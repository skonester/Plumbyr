open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Interactivity
open Avalonia.LogicalTree
open Avalonia.Media.Imaging
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.VisualTree
open WindowsCleaner

let check condition message = if not condition then failwith message
let repo = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
let scannerFile = Path.Combine(repo, "plumbyr/DriverScan.ps1")
let fixtureScript = Path.Combine(__SOURCE_DIRECTORY__, "ScanWorkerFixture.ps1")
let wait label predicate =
    let timer = Stopwatch.StartNew()
    while not (predicate()) && timer.Elapsed < TimeSpan.FromSeconds(10.0) do Thread.Sleep(5)
    check (predicate()) ("Timed out: " + label)
let processInfo exe args =
    let info = ProcessStartInfo(exe)
    for arg in args do info.ArgumentList.Add(arg)
    info
let runFixture scenario =
    let info = processInfo "powershell.exe" [ "-NoProfile"; "-NonInteractive"; "-ExecutionPolicy"; "Bypass"; "-File"; fixtureScript; "-Scanner"; scannerFile; "-Scenario"; scenario ]
    DriverScanner.runProcess info (TimeSpan.FromSeconds(20.0)) CancellationToken.None
let succeeded = function DriverScanResult.Success value -> value | result -> failwithf "Expected success: %A" result
let failed = function DriverScanResult.Failed _ -> () | result -> failwithf "Expected failure: %A" result

let scannerTests directory =
    let offer = runFixture "offer"
    let empty = runFixture "empty"
    let partial = runFixture "partial"
    let inventory = runFixture "inventory"
    let drivers = DriverScanner.parseInventory inventory
    check (drivers.Length = 1 && drivers.Head.ProblemCode = Some 22 && drivers.Head.HardwareIds.Length = 2) "Inventory metadata was lost"
    let scan = DriverScanner.parseUpdates offer
    let update = scan.Updates.Head
    check (update.Revision = 7 && update.SizeMB = 2.0 && update.MayRequireReboot) "Offer metadata was lost"
    check (update.CurrentDriver |> Option.exists (fun d -> d.DriverVersion = "1.0.0.0")) "Exact compatible ID did not match"
    let duplicate = { drivers.Head with InstanceId = "second-device" }
    check (DriverScanner.matchCurrent update.HardwareId (duplicate :: drivers) = None) "Ambiguous devices must not match"
    check (DriverScanner.matchCurrent "PCI\\VEN_1234" drivers = None) "Hardware prefixes must not match"
    let cache = Path.Combine(directory, "last-scan.json")
    DriverScanner.scanUpdatesWith (fun _ _ -> offer) cache CancellationToken.None |> succeeded |> ignore
    check ((DriverScanner.loadLastScan cache).Value.Updates.Length = 1) "Last scan was not saved"
    let original = File.ReadAllText cache
    for bad in [ ""; "[]"; "unexpected output"; "{}"; offer.Replace("\"resultCode\":2", "\"resultCode\":4"); offer.Replace("\"updates\":[", "\"updates\":null,\"ignored\":[") ] do
        DriverScanner.scanUpdatesWith (fun _ _ -> bad) cache CancellationToken.None |> failed
        check (File.ReadAllText cache = original) "Failed scan replaced cached results"
    let incomplete = DriverScanner.scanUpdatesWith (fun _ _ -> partial) cache CancellationToken.None |> succeeded
    check (not incomplete.Warnings.IsEmpty && (DriverScanner.summary incomplete).Contains("incomplete")) "Partial empty scan claimed success"
    check (File.ReadAllText cache = original) "Incomplete scan replaced last successful scan"
    let noUpdates = DriverScanner.scanUpdatesWith (fun _ _ -> empty) cache CancellationToken.None |> succeeded
    check (noUpdates.Updates.IsEmpty && (DriverScanner.summary noUpdates).Contains("offered no driver updates")) "Valid empty result failed"
    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()
    let before = File.ReadAllText cache
    let result = DriverScanner.scanUpdatesWith (fun _ _ -> failwith "Cancelled scan launched a worker") cache cancelled.Token
    check (result = DriverScanResult.Cancelled && File.ReadAllText cache = before) "Cancellation changed cached results"
    DriverScanner.scanUpdatesWith (fun _ _ -> raise (TimeoutException())) cache CancellationToken.None |> failed
    let readError = runFixture "inventory-error" |> DriverScanner.parseUpdates
    check (not readError.Warnings.IsEmpty) "Inventory failure was hidden"
    DriverScanner.scanUpdatesWith (fun _ _ -> runFixture "search-error") cache CancellationToken.None |> failed
    // The embedded helper must be identical to the reviewed source.
    use embedded = typeof<DriverItem>.Assembly.GetManifestResourceStream("Plumbyr.Payload/Drivers/DriverScan.ps1")
    use reader = new StreamReader(embedded)
    check (reader.ReadToEnd() = File.ReadAllText scannerFile) "Embedded scanner differs from source"
    scan, noUpdates

let processTests directory =
    let self mode marker = processInfo Environment.ProcessPath [ "--fixture-worker"; mode; marker ]
    let isStopped marker =
        let pid = Int32.Parse(File.ReadAllText marker)
        try use proc = Process.GetProcessById(pid) in proc.HasExited with :? ArgumentException -> true
    let cancelledMarker = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-cancel.pid")
    use cancellation = new CancellationTokenSource()
    let work = Task.Run(fun () ->
        try DriverScanner.runProcess (self "silent" cancelledMarker) (TimeSpan.FromSeconds(20.0)) cancellation.Token |> ignore; false
        with :? OperationCanceledException -> true)
    wait "silent worker started" (fun () -> File.Exists cancelledMarker)
    cancellation.Cancel()
    check (work.Wait(10000) && work.Result && isStopped cancelledMarker) "Cancel left the silent worker running"
    let timeoutMarker = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-timeout.pid")
    let timedOut =
        try DriverScanner.runProcess (self "silent" timeoutMarker) (TimeSpan.FromSeconds(2.0)) CancellationToken.None |> ignore; false
        with :? TimeoutException -> true
    check (timedOut && File.Exists timeoutMarker && isStopped timeoutMarker) "Timeout left a worker running"
    let limited =
        try DriverScanner.runProcess (self "huge" "unused") (TimeSpan.FromSeconds(10.0)) CancellationToken.None |> ignore; false
        with :? InvalidDataException -> true
    check limited "Worker output was not bounded"

type TestApp() =
    inherit Application()
    override this.Initialize() =
        this.Styles.Add(Avalonia.Themes.Fluent.FluentTheme())
        this.Styles.Add(Synthora.SynthoraTheme())

let descendants<'T when 'T :> Control> (root: Control) = root.GetLogicalDescendants() |> Seq.choose (function :? 'T as c -> Some c | _ -> None)
let uiWait label predicate =
    let timer = Stopwatch.StartNew()
    while not (predicate()) && timer.Elapsed < TimeSpan.FromSeconds(10.0) do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep(5)
    Dispatcher.UIThread.RunJobs()
    check (predicate()) ("Timed out: " + label)
let uiTests directory scan empty =
    AppBuilder.Configure<TestApp>().UseSkia().UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false)).SetupWithoutStarting() |> ignore
    let mutable calls = 0
    let mutable inventoryCalls = 0
    let mutable next = fun (_: CancellationToken) -> DriverScanResult.Failed "Fixture scan unavailable"
    let mutable installCalls = 0
    let mutable approved = true
    let mutable installedSelection: DriverUpdateInfo list = []
    let mutable installNext: DriverInstallBackend = fun _ _ _ _ -> failwith "Unexpected fixture installation"
    let confirmations = ResizeArray<string>()
    let backend = {
        Inventory = fun _ -> Interlocked.Increment(&inventoryCalls) |> ignore; DriverScanResult.Failed "Fixture inventory unavailable"
        Updates = fun ct -> Interlocked.Increment(&calls) |> ignore; next ct
        LastScan = fun () -> Some scan }
    let logs = ResizeArray<string>()
    use view = new DriversView(logs.Add, Unchecked.defaultof<IStorageProvider>, scanner = backend,
                              installer = (fun updates restore ct onEvent ->
                                  Interlocked.Increment(&installCalls) |> ignore
                                  installedSelection <- updates
                                  installNext updates restore ct onEvent),
                              confirmInstall = (fun text -> confirmations.Add(text); Task.FromResult approved))
    let window = Window(Width = 1200.0, Height = 900.0, Content = view, RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark)
    window.Show()
    Dispatcher.UIThread.RunJobs()
    let button text = descendants<Button> view |> Seq.find (fun b -> string b.Content = text)
    let click text = (button text).RaiseEvent(RoutedEventArgs(Button.ClickEvent))
    let status = descendants<TextBlock> view |> Seq.find (fun b -> b.Name = "DriverTaskStatus")
    let last = descendants<TextBlock> view |> Seq.find (fun b -> b.Name = "DriverLastScan")
    let updates = descendants<DataGrid> view |> Seq.find (fun b -> b.Name = "DriverUpdateResults")
    let busy = descendants<ProgressBar> view |> Seq.find (fun b -> b.Name = "DriverTaskProgress")
    let cachedLabel = last.Text
    click "Check Updates"
    uiWait "failed scan completion" (fun () -> (button "Check Updates").IsEnabled)
    check (status.Text.Contains("[ERROR]") && not (status.Text.Contains("up to date"))) "Failed scan claimed up to date"
    check (last.Text = cachedLabel && updates.IsVisible) "Failure discarded previous scan"
    click "Scan Drivers"
    uiWait "failed inventory completion" (fun () -> (button "Scan Drivers").IsEnabled)
    check (status.Text.Contains("[ERROR]") && not (status.Text.Contains("found 0"))) "Failed inventory claimed zero drivers"
    use started = new ManualResetEventSlim(false)
    use release = new ManualResetEventSlim(false)
    next <- fun _ -> started.Set(); release.Wait(); DriverScanResult.Success empty
    click "Check Updates"
    uiWait "blocked scan started" (fun () -> started.IsSet)
    check (busy.IsIndeterminate && busy.IsVisible) "Scan has false percentage progress"
    click "Cancel"
    check (status.Text.Contains("Cancelling") && not (button "Check Updates").IsEnabled) "Cancel released the task slot early"
    let before = calls
    click "Check Updates" // Explicit routed event also tests the internal guard behind disabled buttons.
    click "Scan Drivers"
    check (calls = before && inventoryCalls = 1) "Another task overlapped cancellation"
    release.Set()
    uiWait "cancelled worker exited" (fun () -> (button "Check Updates").IsEnabled)
    check (status.Text.Contains("cancelled") && last.Text = cachedLabel && updates.IsVisible) "Late success overwrote cancelled results"
    next <- fun _ -> DriverScanResult.Success { empty with Warnings = [ "Fixture incomplete catalog" ] }
    click "Check Updates"
    uiWait "partial scan completion" (fun () -> (button "Check Updates").IsEnabled)
    check (status.Text.Contains("incomplete")) "Partial result claimed a clean scan"
    next <- fun _ -> DriverScanResult.Success empty
    click "Check Updates"
    uiWait "empty scan completion" (fun () -> (button "Check Updates").IsEnabled)
    check (status.Text.Contains("offered no driver updates") && not updates.IsVisible) "Successful empty result did not render correctly"
    next <- fun _ -> DriverScanResult.Success scan
    click "Check Updates"
    uiWait "offers completion" (fun () -> (button "Check Updates").IsEnabled)
    check (updates.IsVisible && status.Text.Contains("offered 1")) "Offer list or scan summary is missing"
    let rows () = updates.ItemsSource |> Seq.cast<DriverUpdateRow> |> Seq.toArray
    let installButton = button "Install selected updates"
    check (not installButton.IsEnabled) "Installation enabled with no selection"
    let availableRows = rows()
    let selectionCheckbox =
        updates.GetVisualDescendants()
        |> Seq.choose (function :? CheckBox as checkbox when (checkbox.DataContext :? DriverUpdateRow) -> Some checkbox | _ -> None)
        |> Seq.head
    selectionCheckbox.IsChecked <- Nullable true
    Dispatcher.UIThread.RunJobs()
    check availableRows[0].Selected "Update checkbox is not bound to its selection"
    check installButton.IsEnabled "Selecting an update did not enable install"
    approved <- false
    click "Install selected updates"
    uiWait "declined install" (fun () -> not view.IsBusy)
    check (installCalls = 0 && installButton.IsEnabled) "Declined installation started a worker"
    approved <- true
    let installStarted = new ManualResetEventSlim(false)
    let finishInstall = new ManualResetEventSlim(false)
    installNext <- fun selected restore ct onEvent ->
        check restore "Restore point default was not passed to installer"
        let update = selected.Head
        onEvent { Stage="install"; UpdateId=update.UpdateId; Revision=update.Revision; Message="Fixture installation running"; Result=None }
        installStarted.Set()
        finishInstall.Wait()
        { Results=[{ UpdateId=update.UpdateId; Revision=update.Revision; State=DriverInstallState.Installed
                     Code=2; HResult="00000000"; RebootRequired=true; Message="Windows confirmed installation" }]
          RebootRequired=true; Cancelled=ct.IsCancellationRequested; RestorePoint="Created and verified"; Error=None }
    click "Install selected updates"
    uiWait "selected install started" (fun () -> installStarted.IsSet)
    check (installedSelection.Length = 1 && installedSelection.Head.UpdateId = scan.Updates.Head.UpdateId && installedSelection.Head.Revision = 7) "UI installed a different identity/revision"
    check (confirmations |> Seq.last |> fun text -> text.Contains(scan.Updates.Head.Title) && text.Contains("license terms")) "Confirmation omitted selected updates or license terms"
    check (not ((button "Check Updates").IsEnabled)) "Scan can overlap installation"
    click "Stop after current operation"
    let mutable closedAfterInstall = false
    check (not (view.TryClose(fun () -> closedAfterInstall <- true)) && not closedAfterInstall) "Window closed during active installation"
    finishInstall.Set()
    uiWait "installation finished before close" (fun () -> not view.IsBusy && closedAfterInstall)
    check (status.Text.Contains("Installed: 1") && status.Text.Contains("Restart required") && status.Text.Contains("Stopped")) "Stopping hid confirmed installation/reboot results"
    let installedRows = rows()
    check (installedRows[0].Status.StartsWith("Installed") && not installedRows[0].CanInstall && not installButton.IsEnabled) "Confirmed update remained installable"
    installStarted.Dispose(); finishInstall.Dispose()
    let second = { scan.Updates.Head with UpdateId="22222222-2222-3333-4444-555555555555"; Revision=4; Title="Second fixture driver" }
    next <- fun _ -> DriverScanResult.Success { scan with Updates=[scan.Updates.Head; second] }
    click "Check Updates"
    uiWait "two update scan" (fun () -> not view.IsBusy)
    click "Select all updates"
    installNext <- fun selected _ _ _ ->
        { Results=selected |> List.mapi (fun i update ->
              { UpdateId=update.UpdateId; Revision=update.Revision; State=(if i=0 then DriverInstallState.Partial else DriverInstallState.Failed)
                Code=(if i=0 then 3 else 4); HResult="80240017"; RebootRequired=false; Message="Fixture result" })
          RebootRequired=false; Cancelled=false; RestorePoint="Created and verified"; Error=None }
    click "Install selected updates"
    uiWait "partial install UI" (fun () -> not view.IsBusy)
    let resultRows = rows()
    check (status.Text.Contains("completed with errors: 1") && status.Text.Contains("failed: 1") && not resultRows[0].CanInstall && resultRows[1].CanInstall) "UI confused partial success and failure"
    for width in [ 1200.0; 920.0 ] do
        window.Width <- width
        Dispatcher.UIThread.RunJobs()
        use image = new RenderTargetBitmap(PixelSize(int width, 900), Vector(96.0, 96.0))
        image.Render(window)
        image.Save(Path.Combine(directory, sprintf "drivers-%d.png" (int width)), PngBitmapEncoderOptions.Default)
    // Closing also requests cancellation without accepting a later result.
    started.Reset(); release.Reset()
    next <- fun _ -> started.Set(); release.Wait(); DriverScanResult.Success empty
    click "Check Updates"
    uiWait "closing scan started" (fun () -> started.IsSet)
    let closingStatus, closingResults = status.Text, last.Text
    (view :> IDisposable).Dispose()
    release.Set()
    uiWait "closed worker settled" (fun () -> not view.IsBusy)
    check (status.Text = closingStatus && last.Text = closingResults) "Disposed view accepted a late result"
    window.Close()

[<STAThread; EntryPoint>]
let main args =
    if args.Length > 0 && args[0] = "--fixture-worker" then
        if args[1] = "huge" then Console.Out.Write(String('x', 5 * 1024 * 1024)); 0
        else File.WriteAllText(args[2], string Environment.ProcessId); Thread.Sleep(Timeout.Infinite); 0
    else
        let directory = Path.Combine(repo, ".build", "driver-smoke")
        Directory.CreateDirectory(directory) |> ignore
        let scan, empty = scannerTests directory
        InstallerChecks.run directory scan.Updates.Head
        processTests directory
        uiTests directory scan empty
        printfn "Driver scanning, installation, cache, worker lifetime, and UI smoke checks passed. Previews: %s" directory
        0
