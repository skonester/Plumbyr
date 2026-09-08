#r "../plumbyr/bin/Release/net10.0-windows/win-x64/Plumbyr.dll"

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open WindowsCleaner

let check condition message = if not condition then failwith message
let fixture = Path.Combine(__SOURCE_DIRECTORY__, "fixture " + Guid.NewGuid().ToString("N"))
let createdFiles = ResizeArray<string>()
let createdDirs = ResizeArray<string>()
let rec makeDirectory path =
    if not (Directory.Exists path) then
        let parent = Path.GetDirectoryName(path)
        if path <> fixture then makeDirectory parent
        Directory.CreateDirectory(path) |> ignore
        createdDirs.Add(path)

let writeFile relative (text: string) =
    let path = Path.Combine(fixture, relative)
    makeDirectory (Path.GetDirectoryName(path))
    File.WriteAllText(path, text, Text.UTF8Encoding(false))
    createdFiles.Add(path)
    path

let settings = KuduBridge.defaultSettings()
let expectFailure predicate (work: unit -> Task<BrowserAnalysis>) = task {
    let! outcome = task {
        try
            let! report = work()
            return Ok report
        with ex -> return Error ex
    }
    match outcome with
    | Error ex when predicate ex -> ()
    | Error ex -> failwithf "Unexpected error: %O" ex
    | Ok _ -> failwith "Expected a failure"
}

let oldLocal = Environment.GetEnvironmentVariable("LOCALAPPDATA")
let oldRoaming = Environment.GetEnvironmentVariable("APPDATA")
let run () = task {
    try
        makeDirectory fixture
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(fixture, "Local"))
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(fixture, "Roaming"))
        let cacheFile = writeFile "Local/Google/Chrome/User Data/Default/Cache/Cache_Data/cache.bin" "abc"
        let profileFile = writeFile "Local/Google/Chrome/User Data/Profile 2/Code Cache/code.bin" "12345"
        let sharedFile = writeFile "Local/Google/Chrome/User Data/ShaderCache/shader.bin" "12"
        let operaFile = writeFile "Roaming/Opera Software/Opera Stable/Code Cache/code.bin" "1234"
        let! result = KuduBridge.analyzeWith settings CancellationToken.None
        check (result.Targets.Length = 4) "Expected exactly 4 cache folders"
        check (result.Targets |> List.sumBy _.Bytes = 14L) "Expected 14 logical bytes"
        check (result.Targets |> List.sumBy _.Files = 4L) "Expected 4 files"
        check (result.Targets |> List.sumBy _.Skipped = 0L) "Unexpected skipped entries"
        check result.Warnings.IsEmpty "Unexpected warnings"
        check (result.Targets |> List.exists (fun t -> t.Label.Contains("Profile 2"))) "Missing second profile"
        check (result.Targets |> List.exists (fun t -> t.Browser = "Opera")) "Missing profile-less Opera"
        for path, expected in [ cacheFile, "abc"; profileFile, "12345"; sharedFile, "12"; operaFile, "1234" ] do
            check (File.ReadAllText(path) = expected) "Analysis changed a fixture file"
        printfn "PASS: packaged Kudu discovery and F# adapter measured 4 caches / 4 files / 14 bytes without modifying files."

        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(fixture, "AbsentLocal"))
        Environment.SetEnvironmentVariable("APPDATA", Path.Combine(fixture, "AbsentRoaming"))
        let! empty = KuduBridge.analyzeWith settings CancellationToken.None
        check empty.Targets.IsEmpty "Expected no installed browser caches"
        printfn "PASS: missing browsers return an empty result."

        do! expectFailure (fun ex -> ex.Message.Contains("bundled Node runtime could not be started")) (fun () ->
            KuduBridge.analyzeWith { settings with NodeExecutable = Path.Combine(fixture, "missing-node.exe") } CancellationToken.None)
        do! expectFailure (fun ex -> ex.Message.Contains("files are missing")) (fun () ->
            KuduBridge.analyzeWith { settings with WorkerPath = Path.Combine(fixture, "missing-worker.mjs") } CancellationToken.None)
        printfn "PASS: missing runtime and missing worker produce actionable errors."

        let stalled = writeFile "stalled.mjs" "process.stdin.resume(); setInterval(() => {}, 1000)"
        use cancellation = new CancellationTokenSource()
        let cancelled = KuduBridge.analyzeWith { settings with WorkerPath = stalled } cancellation.Token
        cancellation.CancelAfter(250)
        do! expectFailure (fun ex -> ex :? OperationCanceledException) (fun () -> cancelled)
        do! expectFailure (fun ex -> ex :? TimeoutException) (fun () ->
            KuduBridge.analyzeWith { settings with WorkerPath = stalled; Timeout = TimeSpan.FromMilliseconds(250.0) } CancellationToken.None)
        printfn "PASS: a running worker can be cancelled and a stalled worker times out."

        let oversized = writeFile "oversized.mjs" "process.stdin.resume(); process.stdout.write('x'.repeat(5 * 1024 * 1024)); setInterval(() => {}, 1000)"
        do! expectFailure (fun ex -> ex.Message.Contains("size limit")) (fun () ->
            KuduBridge.analyzeWith { settings with WorkerPath = oversized } CancellationToken.None)
        let failed = writeFile "failed.mjs" "process.stdin.resume(); process.stdout.write(JSON.stringify({version:1,ok:false,error:'fixture failure'})); process.exitCode=1"
        do! expectFailure (fun ex -> ex.Message = "fixture failure") (fun () ->
            KuduBridge.analyzeWith { settings with WorkerPath = failed } CancellationToken.None)
        printfn "PASS: oversized output terminates the worker and structured errors reach F#."
    finally
        Environment.SetEnvironmentVariable("LOCALAPPDATA", oldLocal)
        Environment.SetEnvironmentVariable("APPDATA", oldRoaming)
        // Delete only our recorded files and empty directories, never recursively.
        for path in createdFiles do File.Delete(path)
        for path in Seq.rev createdDirs do Directory.Delete(path, false)
}
run().GetAwaiter().GetResult()
