open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.IO.Compression
open System.Reflection
open System.Security.Cryptography
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Interactivity
open Avalonia.LogicalTree
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform.Storage
open Avalonia.Threading
open WindowsCleaner

type PickerProxy() =
    inherit DispatchProxy()
    member val Image = "" with get, set
    member val Folder = "" with get, set
    override this.Invoke(methodInfo, _) =
        match methodInfo.Name with
        | "get_CanOpen" | "get_CanPickFolder" -> box true
        | "get_Path" -> box (Uri(this.Image))
        | "get_Name" -> box (Path.GetFileName(this.Image))
        | "OpenFilePickerAsync" ->
            let file = DispatchProxy.Create<IStorageFile, PickerProxy>()
            (box file :?> PickerProxy).Image <- this.Image
            box (Task.FromResult([| file |] :> IReadOnlyList<IStorageFile>))
        | "OpenFolderPickerAsync" ->
            let folder = DispatchProxy.Create<IStorageFolder, PickerProxy>()
            (box folder :?> PickerProxy).Image <- this.Folder
            box (Task.FromResult([| folder |] :> IReadOnlyList<IStorageFolder>))
        | "Dispose" -> null
        | name -> failwithf "Unexpected picker call: %s" name

let check condition message = if not condition then failwith message
let descendants<'T when 'T :> Control> (root: Control) = root.GetLogicalDescendants() |> Seq.choose (function :? 'T as item -> Some item | _ -> None)
let button text root = descendants<Button> root |> Seq.find (fun b -> b.Content :? string && string b.Content = text)
let click (b: Button) = check b.IsEnabled (sprintf "Disabled button: %O" b.Content); b.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
let hasText (part: string) root = descendants<TextBlock> root |> Seq.exists (fun t -> not (isNull t.Text) && t.Text.Contains(part))
let waitUntil label predicate =
    let watch = Stopwatch.StartNew()
    while not (predicate()) && watch.Elapsed < TimeSpan.FromSeconds(25.0) do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep(5)
    Dispatcher.UIThread.RunJobs()
    check (predicate()) ("Timed out: " + label)
let awaitTask (work: Task<'T>) = waitUntil "worker reply" (fun () -> work.IsCompleted); work.GetAwaiter().GetResult()
let request (service: RecoveryService) command fields = service.Request(command, fields) |> awaitTask

// This test-only child implements the same protocol with controlled result
// waves. It exercises streaming UI updates without scanning another host drive.
let runStreamWorker () =
    let emit fields = Console.WriteLine(JsonSerializer.Serialize(dict fields)); Console.Out.Flush()
    let reply id fields = emit [ "id", box id; "data", box (dict fields) ]
    let mutable count = 0
    let mutable scanId = 0L
    let append amount =
        for _ = 1 to amount do
            let index = count
            emit [ "event", box "found"; "index", box index; "name", box (sprintf "Fixture %05d.pdf" index)
                   "kind", box "pdf"; "size", box (int64 (index + 1) * 32L); "offset", box (int64 index * 512L)
                   "category", box 2; "validation", box "Unverified candidate"; "metadata", null ]
            count <- count + 1
    let mutable line = Console.ReadLine()
    while not (isNull line) do
        use document = JsonDocument.Parse(line)
        let value = document.RootElement
        let id = RecoveryJson.number "id" value
        match RecoveryJson.text "cmd" value with
        | "scan" -> scanId <- id; count <- 0; append 3
        | "fixtureAppend" -> append (int (RecoveryJson.number "count" value)); reply id [ "count", box count ]
        | "fixtureSaved" ->
            emit [ "event", box "recovered"; "index", box 0; "path", box "fixture-saved.pdf" ]
            reply id []
        | "fixtureFailed" ->
            emit [ "event", box "recoveryFailed"; "index", box 0; "error", box "Fixture read failure" ]
            reply id []
        | "cancel" ->
            reply id []
            reply scanId [ "cancelled", box true ]
        | _ -> reply id []
        line <- Console.ReadLine()
    0

let streamingUiRegression source output =
    let provider = DispatchProxy.Create<IStorageProvider, PickerProxy>()
    let picker = box provider :?> PickerProxy
    picker.Image <- source
    picker.Folder <- output
    use service = new RecoveryService(workerPath = Environment.ProcessPath)
    let flag = "PLUMBYR_RECOVERY_STREAM_FIXTURE"
    let previousFlag = Environment.GetEnvironmentVariable(flag)
    let started =
        try
            Environment.SetEnvironmentVariable(flag, "1")
            service.Request("discover", [])
        finally Environment.SetEnvironmentVariable(flag, previousFlag)
    awaitTask started |> ignore
    use view = new RecoveryView(ignore, provider, ignore, service = service)
    let window = Window(Width = 1040.0, Height = 820.0, Content = view.SourcesPage)
    window.Show()
    try
        click (button "Open disk image" view.SourcesPage)
        click (button "Start scanning" view.SourcesPage)
        window.Content <- view.ResultsPage
        waitUntil "initial streaming rows" (fun () -> hasText "3 found" view.ResultsPage)
        let panel = descendants<StackPanel> view.ResultsPage |> Seq.find (fun p -> p.Name = "RecoveryFileRows")
        let firstRows = panel.Children |> Seq.toArray
        let mutable mutations = 0
        panel.Children.CollectionChanged.Add(fun _ -> mutations <- mutations + 1)
        let append amount =
            let result = request service "fixtureAppend" [ "count", box amount ]
            let total = int (RecoveryJson.number "count" result)
            waitUntil "streaming result batch" (fun () -> hasText (total.ToString("N0") + " found") view.ResultsPage)
            total
        append 7 |> ignore
        check (mutations = 7) "Appending seven files rebuilt existing rows"
        check (firstRows |> Array.mapi (fun i row -> Object.ReferenceEquals(row, panel.Children[i])) |> Array.forall id) "Initial row instances were replaced"
        append 90 |> ignore
        let fullPage = panel.Children |> Seq.toArray
        let firstCheck = descendants<CheckBox> fullPage[0] |> Seq.exactlyOne
        firstCheck.IsChecked <- Nullable true
        let focused = descendants<Button> fullPage[1] |> Seq.find (fun item -> item.Content :? StackPanel)
        check (focused.Focus()) "Could not focus a result row"
        let scroll = descendants<ScrollViewer> view.ResultsPage |> Seq.find (fun s -> s.Name = "RecoveryFileScroll")
        Dispatcher.UIThread.RunJobs()
        scroll.Offset <- Vector(0.0, 150.0)
        Dispatcher.UIThread.RunJobs()
        let offset = scroll.Offset
        let before = mutations
        for _ = 1 to 5 do append 1000 |> ignore
        check (mutations = before) "Off-page results mutated the existing row collection"
        check (fullPage |> Array.mapi (fun i row -> Object.ReferenceEquals(row, panel.Children[i])) |> Array.forall id) "Streaming replaced a visible row"
        check (firstCheck.IsChecked.GetValueOrDefault() && hasText "1 selected" view.ResultsPage) "Streaming lost selection"
        check focused.IsFocused "Streaming lost keyboard focus"
        check (scroll.Offset = offset) "Streaming reset the scroll offset"
        request service "fixtureSaved" [] |> ignore
        waitUntil "status patched in place" (fun () -> hasText "Saved" fullPage[0])
        check (mutations = before && Object.ReferenceEquals(fullPage[0], panel.Children[0])) "A status update rebuilt a row"
        request service "fixtureFailed" [] |> ignore
        waitUntil "failure patched in place" (fun () -> hasText "Recovery failed" fullPage[0])
        check (mutations = before) "A failure status rebuilt a row"

        let sort = descendants<ComboBox> view.ResultsPage |> Seq.find (fun s -> s.Name = "RecoverySort")
        sort.SelectedIndex <- 2
        let oldLargest = panel.Children |> Seq.toArray
        let beforeInsert = mutations
        let total = append 1
        check (mutations - beforeInsert = 2) "One larger result rebuilt the sorted page"
        check (oldLargest |> Seq.take 99 |> Seq.mapi (fun i row -> Object.ReferenceEquals(row, panel.Children[i + 1])) |> Seq.forall id) "Sorted insertion replaced surviving rows"
        let actual = panel.Children |> Seq.map (fun row -> unbox<int> row.Tag) |> Seq.toArray
        check (actual = [| total - 1 .. -1 .. total - 100 |]) "Incremental sorting differs from a full sort"
        let search = descendants<TextBox> view.ResultsPage |> Seq.exactlyOne
        search.Text <- "Fixture 0000"
        waitUntil "filtered projection" (fun () -> panel.Children.Count = 10 && hasText "10 matching" view.ResultsPage)
        let beforeFiltered = mutations
        append 100 |> ignore
        check (mutations = beforeFiltered && panel.Children.Count = 10) "Nonmatching files changed filtered rows"
        let selectedRow = panel.Children |> Seq.find (fun row -> unbox<int> row.Tag = 0)
        check ((descendants<CheckBox> selectedRow |> Seq.exactlyOne).IsChecked.GetValueOrDefault()) "Selection was not restored after filtering"
        click (button "Stop scanning" view.ResultsPage)
        waitUntil "fixture scan stop" (fun () -> hasText "Scan stopped:" view.ResultsPage)
        printfn "PASS: 5,000 streamed candidates leave a full page's row objects, selection, focus and scroll unchanged; sorted insertion and status updates patch only changed rows."
    finally window.Close()

let runTests () =
    let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let fixture = Path.Combine(root, ".build", "recovery-" + Guid.NewGuid().ToString("N"))
    let output = Path.Combine(fixture, "recovered")
    Directory.CreateDirectory(output) |> ignore
    let source = Path.Combine(fixture, "source.img")
    let original = Path.Combine(root, "plumbyr/Reclaim/tests/fixtures")
    let picture = File.ReadAllBytes(Path.Combine(original, "real.png"))
    let pdf = Text.Encoding.ASCII.GetBytes("%PDF-1.7\nfixture document\n%%EOF\n")
    let image = Array.concat [ Array.zeroCreate (1024 * 1024 - 3); picture; Array.zeroCreate 511; pdf; Array.zeroCreate 1024 ]
    File.WriteAllBytes(source, image)
    let initialHash = SHA256.HashData(image)
    try
        AppBuilder.Configure<App>().UseSkia().UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false)).SetupWithoutStarting() |> ignore
        // Exercise the real bundled worker, not a mock of scanning/export.
        use service = new RecoveryService()
        let found = ConcurrentQueue<RecoveryFile>()
        use subscription = service.Events.Subscribe(fun message ->
            let e = message.Payload
            if RecoveryJson.text "event" e = "found" then found.Enqueue(RecoveryJson.file e))
        let result = request service "scan" [ "path", box source; "unusedOnly", box false ]
        check (not (RecoveryJson.flag "cancelled" result)) "Unexpected scan cancellation"
        check (found.Count = 2) "Expected PNG and PDF across the scanner boundary"
        let files = found.ToArray()
        let png = files |> Array.find (fun f -> f.Kind = "png")
        let doc = files |> Array.find (fun f -> f.Kind = "pdf")
        check (png.Offset = int64 (1024 * 1024 - 3) && png.Size = int64 picture.Length) "Wrong picture range"
        let preview = request service "preview" [ "index", box png.Index ]
        use previewStream = new MemoryStream(Convert.FromBase64String(RecoveryJson.text "png" preview))
        use decoded = new Bitmap(previewStream)
        check (decoded.PixelSize.Width > 0) "Picture preview was not decoded"
        let save () = request service "recover" [ "folder", box output; "indices", box [| png.Index; doc.Index |] ]
        let saved = save()
        check (RecoveryJson.number "saved" saved = 2L && RecoveryJson.number "failed" saved = 0L) "Recovery failed"
        check (File.ReadAllBytes(Path.Combine(output, png.Name)) = picture) "Recovered PNG differs"
        check (File.ReadAllBytes(Path.Combine(output, doc.Name)) = pdf) "Recovered PDF differs"
        save() |> ignore
        check ((Directory.GetFiles(output)).Length = 4) "Repeated recovery overwrote files"
        check (SHA256.HashData(File.ReadAllBytes(source)) = initialHash) "Scanning/recovery changed the source"
        printfn "PASS: bundled worker, boundary carving, decoded preview, byte-exact export, no overwrite, read-only source."

        // The default mode must refuse an unformatted image, keeping occupied data out.
        let mutable rejected = false
        try request service "scan" [ "path", box source; "unusedOnly", box true ] |> ignore
        with ex -> rejected <- ex.Message.Contains("formatted") || ex.Message.Contains("supported")
        check rejected "Unused-space mode silently fell back to all bytes"
        use compressed = File.OpenRead(Path.Combine(original, "deleted-journal-ext4.img.gz"))
        use gzip = new GZipStream(compressed, CompressionMode.Decompress)
        let volume = Path.Combine(fixture, "deleted.img")
        do use target = File.Create(volume) in gzip.CopyTo(target)
        while not found.IsEmpty do found.TryDequeue() |> ignore
        request service "scan" [ "path", box volume; "unusedOnly", box true ] |> ignore
        check (found.Count > 0) "No deleted files found in a formatted volume fixture"
        printfn "PASS: unused-space scanning rejects unformatted input and finds deleted data in a formatted volume image."

        while not found.IsEmpty do found.TryDequeue() |> ignore
        request service "scan" [ "path", box source; "unusedOnly", box false ] |> ignore
        let before = Directory.GetFiles(output).Length
        File.WriteAllBytes(source, Array.append image [| 0uy |])
        let mutable changedSourceRejected = false
        try request service "recover" [ "folder", box output; "indices", box [| 0 |] ] |> ignore
        with ex -> changedSourceRejected <- ex.Message.Contains("Source changed")
        check (changedSourceRejected && Directory.GetFiles(output).Length = before) "Changed source was exported"
        File.WriteAllBytes(source, image)
        printfn "PASS: changed sources are rejected before creating recovery files."

        // A private worker executable lets the fixture terminate only its own
        // process and exercise actual broken-pipe/restart behavior safely.
        let workerStem = "recovery-fixture-" + Guid.NewGuid().ToString("N")
        let workerPath = Path.Combine(fixture, workerStem + ".exe")
        File.Copy(AppFiles.get "Reclaim/plumbyr-recovery.exe", workerPath)
        use crashService = new RecoveryService(workerPath = workerPath)
        let crashEvents = ConcurrentQueue<RecoveryEvent>()
        use crashSubscription = crashService.Events.Subscribe(crashEvents.Enqueue)
        let largeImage = Path.Combine(fixture, "concurrent.img")
        do
            use target = File.Create(largeImage)
            target.Write(picture)
            target.Write(Array.zeroCreate<byte> 1024)
            target.Write(pdf)
            target.SetLength(512L * 1024L * 1024L)
        let liveScan = crashService.Request("scan", [ "path", box largeImage; "unusedOnly", box false ])
        let liveFiles () = crashEvents.ToArray() |> Array.filter (fun e -> RecoveryJson.text "event" e.Payload = "found") |> Array.map (fun e -> RecoveryJson.file e.Payload)
        waitUntil "live candidates" (fun () -> (liveFiles()).Length = 2)
        check (not liveScan.IsCompleted) "Fixture finished before concurrent recovery could be tested"
        let livePng = liveFiles() |> Array.find (fun f -> f.Kind = "png")
        let livePreview = request crashService "preview" [ "index", box livePng.Index ]
        check ((RecoveryJson.text "png" livePreview).Length > 0) "Preview failed during an active scan"
        let liveSaved = request crashService "recover" [ "folder", box output; "indices", box [| livePng.Index |] ]
        check (RecoveryJson.number "saved" liveSaved = 1L) "Recovery failed during an active scan"
        request crashService "cancel" [] |> ignore
        let cancelled = awaitTask liveScan
        check (RecoveryJson.flag "cancelled" cancelled) "Scan did not stop after concurrent preview/recovery"
        printfn "PASS: picture preview and recovery run concurrently with scanning without moving its cursor."

        while not crashEvents.IsEmpty do crashEvents.TryDequeue() |> ignore
        let interrupted = crashService.Request("scan", [ "path", box largeImage; "unusedOnly", box false ])
        waitUntil "candidates before worker failure" (fun () -> (liveFiles()).Length = 2)
        use crashed = Process.GetProcessesByName(workerStem) |> Seq.exactlyOne
        crashed.Kill()
        check (crashed.WaitForExit(5000)) "Fixture worker did not exit"
        waitUntil "failed worker request completes" (fun () -> interrupted.IsCompleted)
        check interrupted.IsFaulted "Worker failure was reported as a successful scan"
        let firstGeneration = crashService.Generation
        crashService.DiscoverAsync() |> awaitTask |> ignore
        check (crashService.Generation > firstGeneration) "Worker was not restarted"
        let restartedScan = request crashService "scan" [ "path", box source; "unusedOnly", box false ]
        check (not (RecoveryJson.flag "cancelled" restartedScan)) "Old connection failure poisoned the new session"
        use restarted = Process.GetProcessesByName(workerStem) |> Seq.exactlyOne
        let disposal = Task.Run(fun () -> (crashService :> IDisposable).Dispose())
        waitUntil "worker disposal" (fun () -> disposal.IsCompleted)
        disposal.GetAwaiter().GetResult()
        check (restarted.WaitForExit(5000)) "Disposal left a worker running"
        printfn "PASS: worker crashes fail pending requests, restart cleanly, and dispose without a deadlock or orphan process."

        let provider = DispatchProxy.Create<IStorageProvider, PickerProxy>()
        let picker = box provider :?> PickerProxy
        picker.Image <- source
        picker.Folder <- output
        let logs = ResizeArray<string>()
        let mutable shown = false
        let uiService = new RecoveryService(workerPath = workerPath)
        use view = new RecoveryView(logs.Add, provider, (fun () -> shown <- true), service = uiService)
        let window = Window(Width = 1040.0, Height = 820.0, Content = view.SourcesPage,
                            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Background = SolidColorBrush.Parse("#080808"))
        window.Show()
        Dispatcher.UIThread.RunJobs()
        click (button "Refresh" view.SourcesPage)
        waitUntil "drive discovery" (fun () -> (button "Refresh" view.SourcesPage).IsEnabled)
        click (button "Open disk image" view.SourcesPage)
        waitUntil "image selection" (fun () -> (button "Start scanning" view.SourcesPage).IsEnabled)
        for width in [ 1040.0; 800.0 ] do
            window.Width <- width
            Dispatcher.UIThread.RunJobs()
            use frame = window.CaptureRenderedFrame()
            frame.Save(Path.Combine(root, ".build", sprintf "recovery-sources-%d.png" (int width)), PngBitmapEncoderOptions.Default)
        let modes = descendants<ComboBox> view.SourcesPage |> Seq.head
        modes.SelectedIndex <- 1
        click (button "Start scanning" view.SourcesPage)
        check shown "Scan did not navigate to results"
        window.Content <- view.ResultsPage
        waitUntil "UI scan" (fun () -> hasText "Scan complete:" view.ResultsPage)
        check (hasText ".png" view.ResultsPage && hasText ".pdf" view.ResultsPage) "Live result rows are missing"
        let row = descendants<Button> view.ResultsPage |> Seq.find (fun b -> hasText ".png" b)
        click row
        waitUntil "UI preview" (fun () -> descendants<Image> view.ResultsPage |> Seq.exists (fun i -> not (isNull i.Source)))
        let categories = descendants<ComboBox> view.ResultsPage |> Seq.find (fun c -> c.Name = "RecoveryCategory")
        categories.SelectedIndex <- 1
        waitUntil "category filtering" (fun () -> hasText "1 matching" view.ResultsPage)
        click (button "Select matching" view.ResultsPage)
        waitUntil "selection" (fun () -> (button "Recover selected" view.ResultsPage).IsEnabled)
        categories.SelectedIndex <- 2
        waitUntil "hidden selection" (fun () -> hasText "1 selected" view.ResultsPage && not (hasText ".png" (descendants<StackPanel> view.ResultsPage |> Seq.find (fun p -> p.Name = "RecoveryFileRows"))))
        categories.SelectedIndex <- 0
        picker.Folder <- Path.Combine(fixture, "missing-folder")
        click (button "Recover selected" view.ResultsPage)
        waitUntil "invalid destination rejected" (fun () -> hasText "Choose an existing recovery folder" view.ResultsPage && (button "Recover selected" view.ResultsPage).IsEnabled)
        picker.Folder <- output
        click (button "Recover selected" view.ResultsPage)
        waitUntil "UI export" (fun () -> hasText "Recovery complete:" view.ResultsPage)
        check (hasText "1 saved, 0 failed" view.ResultsPage) "UI exported the wrong selection"
        check (hasText "Saved:" view.ResultsPage) "Focused file details did not refresh after export"
        for width in [ 1040.0; 800.0 ] do
            window.Width <- width
            Dispatcher.UIThread.RunJobs()
            use frame = window.CaptureRenderedFrame()
            frame.Save(Path.Combine(root, ".build", sprintf "recovery-files-%d.png" (int width)), PngBitmapEncoderOptions.Default)
        printfn "PASS: native Sources/results, discovery, image picker, filters, persistent selection, picture preview and selective recovery."

        // Reuse the selection, request close during export and wait for graceful completion.
        click (button "Recover selected" view.ResultsPage)
        let mutable closeReady = false
        check (not (view.TryClose(fun () -> closeReady <- true))) "Close should wait during recovery"
        waitUntil "close after recovery" (fun () -> closeReady)
        let big = Path.Combine(fixture, "large.img")
        do use target = File.Create(big) in target.SetLength(256L * 1024L * 1024L)
        picker.Image <- big
        window.Content <- view.SourcesPage
        click (button "Open disk image" view.SourcesPage)
        click (button "Start scanning" view.SourcesPage)
        click (button "Stop scanning" view.SourcesPage)
        waitUntil "cancel scan" (fun () -> hasText "Scan stopped:" view.SourcesPage)
        let many = Path.Combine(fixture, "many.img")
        File.WriteAllBytes(many, Array.concat [| for i in 1 .. 125 -> Array.append pdf (Array.zeroCreate i) |])
        picker.Image <- many
        click (button "Open disk image" view.SourcesPage)
        click (button "Start scanning" view.SourcesPage)
        waitUntil "many-result scan" (fun () -> hasText "Scan complete:" view.SourcesPage)
        window.Content <- view.ResultsPage
        let fileRows () = descendants<StackPanel> view.ResultsPage |> Seq.find (fun p -> p.Name = "RecoveryFileRows")
        check ((fileRows()).Children.Count = 100) "Result page exceeded its row bound"
        click (button "Next" view.ResultsPage)
        check ((fileRows()).Children.Count = 25 && hasText "Page 2 of 2" view.ResultsPage) "Remaining results are inaccessible"
        let search = descendants<TextBox> view.ResultsPage |> Seq.head
        search.Text <- "Document 0001"
        waitUntil "name search" (fun () -> hasText "1 matching" view.ResultsPage)
        check ((fileRows()).Children.Count = 1) "Search did not reset pagination"
        search.Text <- "pdf"
        waitUntil "type search" (fun () -> hasText "125 matching" view.ResultsPage)
        click (button "Select matching" view.ResultsPage)
        check (hasText "125 selected" view.ResultsPage) "Select matching omitted filtered results on other pages"
        click (button "Clear selection" view.ResultsPage)
        // Losing the worker also loses its candidate handles. Those results
        // must be cleared instead of remaining selectable with stale IDs.
        use uiWorker = Process.GetProcessesByName(workerStem) |> Seq.exactlyOne
        uiWorker.Kill()
        check (uiWorker.WaitForExit(5000)) "UI fixture worker did not exit"
        waitUntil "stale UI results cleared" (fun () -> hasText "worker stopped" view.ResultsPage && hasText "0 found" view.ResultsPage)
        check (not (button "Recover selected" view.ResultsPage).IsEnabled) "Stale recovery remained available"
        window.Content <- view.SourcesPage
        click (button "Start scanning" view.SourcesPage)
        waitUntil "UI restart and rescan" (fun () -> hasText "Scan complete:" view.SourcesPage && hasText "125 found" view.ResultsPage)
        check (SHA256.HashData(File.ReadAllBytes(source)) = initialHash) "UI changed the source"
        window.Close()
        printfn "PASS: cancellation, graceful close during recovery, bounded pagination, name/type search and cross-page selection."
        streamingUiRegression source output
        0
    finally
        // Delete only files created inside the verified, unique fixture directory.
        check (Path.GetFullPath(fixture).StartsWith(Path.Combine(root, ".build") + string Path.DirectorySeparatorChar)) "Unsafe fixture cleanup target"
        for path in Directory.GetFiles(output) do File.Delete(path)
        Directory.Delete(output)
        for path in Directory.GetFiles(fixture) do File.Delete(path)
        Directory.Delete(fixture)

[<STAThread; EntryPoint>]
let main _ =
    if Environment.GetEnvironmentVariable("PLUMBYR_RECOVERY_STREAM_FIXTURE") = "1" then runStreamWorker()
    else runTests()
