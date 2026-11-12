#load "../plumbyr/SpaceScanner.fs"

open System
open System.IO
open System.Diagnostics
open System.Threading
open WindowsCleaner

let check condition message = if not condition then failwith message
let fixture = Path.Combine(__SOURCE_DIRECTORY__, "space-map-fixture-" + Guid.NewGuid().ToString("N"))
let createdFiles = ResizeArray<string>()
let createdDirs = ResizeArray<string>()
let createdLinks = ResizeArray<string>()
let mutable expectedBytes = 0L
let mutable expectedFiles = 0L

let rec makeDirectory path =
    if not (Directory.Exists path) then
        if path <> fixture then makeDirectory (Path.GetDirectoryName(path))
        Directory.CreateDirectory(path) |> ignore
        createdDirs.Add(path)

let writeFile relative length =
    let path = Path.Combine(fixture, relative)
    makeDirectory (Path.GetDirectoryName(path))
    use output = File.Create(path)
    output.SetLength(length)
    createdFiles.Add(path)
    expectedBytes <- expectedBytes + length
    expectedFiles <- expectedFiles + 1L
    path

let noProgress = { new IProgress<SpaceScanProgress> with member _.Report(_) = () }
let expectException<'T when 'T :> exn> work =
    let mutable caught = false
    try work() with :? 'T -> caught <- true
    check caught (sprintf "Expected %s" typeof<'T>.Name)

try
    makeDirectory fixture
    let rootFile = writeFile "root.bin" 17L
    writeFile "small/one.bin" 11L |> ignore
    writeFile "large/nested/two.bin" 900L |> ignore
    let hiddenFile = writeFile "hidden-system.bin" 23L
    File.SetAttributes(hiddenFile, FileAttributes.Hidden ||| FileAttributes.System)
    writeFile "empty-file.bin" 0L |> ignore
    makeDirectory (Path.Combine(fixture, "empty-folder"))
    for index = 1 to 60 do writeFile (sprintf "many/file-%02d.bin" index) (int64 index * 100L) |> ignore
    let deep = String.concat "/" (List.replicate 80 "d")
    writeFile (deep + "/bottom.bin") 7L |> ignore

    // A loop must not be traversed or included in the totals.
    let link = Path.Combine(fixture, "loop")
    // NTFS junctions do not require the symbolic-link privilege.
    let quote (value: string) = "'" + value.Replace("'", "''") + "'"
    let start = ProcessStartInfo("powershell.exe", UseShellExecute = false, CreateNoWindow = true)
    start.ArgumentList.Add("-NoProfile")
    start.ArgumentList.Add("-NonInteractive")
    start.ArgumentList.Add("-Command")
    start.ArgumentList.Add("$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path " + quote link + " -Target " + quote fixture + " | Out-Null")
    use createLink = Process.Start(start)
    createLink.WaitForExit()
    check (createLink.ExitCode = 0) "Could not create the junction fixture"
    createdLinks.Add(link)

    let result = SpaceScanner.scan fixture CancellationToken.None noProgress
    check (result.Root.Size = expectedBytes) "Incorrect recursive byte total"
    check (result.Progress.Bytes = expectedBytes && result.Progress.Files = expectedFiles) "Incorrect scan counters"
    check (result.Root.FileBytes = 40L && result.Root.FileCount = 3L) "Direct-file totals lost hidden/system or empty files"
    check (result.Root.Children |> Seq.exists (fun node -> node.Name = "empty-folder" && node.Size = 0L)) "Empty folder was omitted"
    check (result.Root.Children |> Seq.pairwise |> Seq.forall (fun (a, b) -> a.Size >= b.Size)) "Folder rows are not size sorted"
    let large = result.Root.Children |> Seq.find (fun node -> node.Name = "large")
    check (large.Size = 900L && large.Children[0].Size = 900L) "Nested aggregation is incorrect"
    check (large.Parent.Value.FullPath = result.Root.FullPath) "Parent navigation is broken"
    check (result.BigFiles.Length = 40) "Biggest-file list must be bounded to 40"
    check (result.BigFiles[0].Size = 6000L && result.BigFiles[39].Size = 2100L) "Wrong top 40 files"
    check (result.Progress.SkippedEntries = 0L) "Unexpected unreadable entries"
    check (result.Progress.SkippedLinks = 1L) "Junction was not reported as skipped"
    check (result.Root.Children |> Seq.forall (fun node -> node.Name <> "loop")) "Junction entered the tree"
    expectException<IOException> (fun () -> SpaceScanner.scan link CancellationToken.None noProgress |> ignore)
    check (File.ReadAllBytes(rootFile).Length = 17) "Scanner modified file contents"
    check ((File.GetAttributes(hiddenFile)).HasFlag(FileAttributes.Hidden)) "Scanner modified file attributes"
    printfn "PASS: nested/deep trees, hidden/system files, empty folders, sorted sizes, top 40, links, and read-only behavior."

    let small = SpaceScanner.scan (Path.Combine(fixture, "small")) CancellationToken.None noProgress
    check (small.Root.Size = 11L && small.BigFiles.Length = 1) "Small-file scans should show files below Roomy's original 50 MiB threshold"
    let empty = SpaceScanner.scan (Path.Combine(fixture, "empty-folder")) CancellationToken.None noProgress
    check (empty.Root.Size = 0L && empty.BigFiles.Length = 0) "Empty scan is incorrect"
    expectException<DirectoryNotFoundException> (fun () -> SpaceScanner.scan (Path.Combine(fixture, "missing")) CancellationToken.None noProgress |> ignore)
    printfn "PASS: repeat scans, small/empty locations and unavailable paths."

    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()
    expectException<OperationCanceledException> (fun () -> SpaceScanner.scan fixture cancelled.Token noProgress |> ignore)
    use duringScan = new CancellationTokenSource()
    let cancelling = { new IProgress<SpaceScanProgress> with member _.Report(_) = duringScan.Cancel() }
    expectException<OperationCanceledException> (fun () -> SpaceScanner.scan fixture duringScan.Token cancelling |> ignore)
    use atCompletion = new CancellationTokenSource()
    let cancelAfterFiles = { new IProgress<SpaceScanProgress> with member _.Report(p) = if p.Files > 0L then atCompletion.Cancel() }
    expectException<OperationCanceledException> (fun () -> SpaceScanner.scan fixture atCompletion.Token cancelAfterFiles |> ignore)
    printfn "PASS: cancellation before scanning, during scanning and at completion."
finally
    // Delete only recorded fixture entries. Never recursively follow links.
    for path in createdLinks do Directory.Delete(path, false)
    for path in createdFiles do
        File.SetAttributes(path, FileAttributes.Normal)
        File.Delete(path)
    for path in Seq.rev createdDirs do Directory.Delete(path, false)
