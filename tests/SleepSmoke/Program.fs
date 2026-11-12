open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Interactivity
open Avalonia.LogicalTree
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Threading
open WindowsCleaner

let check condition message = if not condition then failwith message
let waitUntil label predicate =
    let timer = Stopwatch.StartNew()
    while not (predicate()) && timer.Elapsed < TimeSpan.FromSeconds(10.0) do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep(5)
    Dispatcher.UIThread.RunJobs()
    check (predicate()) ("Timed out: " + label)

type ManualDelay() =
    let pending = ConcurrentQueue<TimeSpan * TaskCompletionSource<unit>>()
    member _.Wait span (token: CancellationToken) : Task =
        let completion = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        token.Register(fun () -> completion.TrySetCanceled(token) |> ignore) |> ignore
        pending.Enqueue(span, completion)
        completion.Task :> Task
    member _.Pending = pending.Count
    member _.Advance() =
        let mutable item = Unchecked.defaultof<TimeSpan * TaskCompletionSource<unit>>
        check (pending.TryDequeue(&item)) "No pending delay"
        (snd item).TrySetResult(()) |> ignore

type FakePlatform() =
    let calls = ConcurrentQueue<string>()
    let mutable listener: (SleepEvent -> unit) option = None
    let mutable failLaunch = false
    let mutable deferCancellation = false
    let mutable power = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    let mutable tool = TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously)
    member _.Calls = calls.ToArray()
    member _.Emit value = listener |> Option.iter (fun handle -> handle value)
    member _.FailLaunch with set value = failLaunch <- value
    member _.DeferCancellation with set value = deferCancellation <- value
    member _.FinishPower code = power.TrySetResult(code) |> ignore
    member _.FinishTool code = tool.TrySetResult(code) |> ignore
    member _.FailTool() = tool.TrySetException(InvalidOperationException("Fixture collector failure")) |> ignore
    interface ISleepPlatform with
        member _.Subscribe callback =
            calls.Enqueue("subscribe")
            listener <- Some callback
            { new IDisposable with member _.Dispose() = listener <- None; calls.Enqueue("unsubscribe") }
        member _.DisplayOff() = calls.Enqueue("display")
        member _.SuspendApplications() = calls.Enqueue("suspend")
        member _.DisableWakeDevices _ = calls.Enqueue("disable")
        member _.Restore() = calls.Enqueue("restore")
        member _.QuickOff() =
            calls.Enqueue("quickoff")
            if failLaunch then failwith "Injected launch failure"
            power.Task
        member _.RunTool(_, output, ct) = task {
            calls.Enqueue("tool")
            use registration = ct.Register(fun () ->
                calls.Enqueue("cancel-tool")
                if not deferCancellation then tool.TrySetCanceled(ct) |> ignore)
            output "Fixture tool output"
            let! result = tool.Task
            ct.ThrowIfCancellationRequested()
            return result
        }

let advanceSeconds (time: ManualDelay) count =
    for _ in 1 .. count do
        waitUntil "countdown delay" (fun () -> time.Pending > 0)
        time.Advance()

let testDiagnosticClose () =
    let platform = FakePlatform()
    use controller = new SleepController(platform, ignore)
    check (controller.RunTool(Diagnostics, ignore)) "Report collection did not start"
    check (not (controller.TryClose())) "Close skipped collector cleanup"
    check controller.Snapshot.Closing "Close was rejected instead of requesting diagnostic shutdown"
    waitUntil "cancel report before close" (fun () -> controller.Snapshot.Tool.IsNone)
    check (Array.contains "cancel-tool" platform.Calls) "Close did not cancel report collection"
    check (controller.TryClose()) "Collector cleanup did not release Close"
    check (not (controller.Start())) "Closing report controller restarted"
    printfn "PASS: Close cancels an active report and becomes ready after collector cleanup."

let testController () =
    let platform = FakePlatform()
    let time = ManualDelay()
    use controller = new SleepController(platform, ignore, delay = time.Wait)
    check (controller.Start()) "Monitoring did not start"
    check (controller.ScheduleQuickOff(5)) "Countdown did not start"
    check (not (controller.ScheduleQuickOff(5))) "Duplicate countdown accepted"
    check (not (controller.Start())) "Monitoring restarted during countdown"
    check (not (controller.RunTool(Diagnostics, ignore))) "Tool overlapped countdown"
    check (controller.CancelSleep()) "Countdown did not cancel"
    time.Advance()
    Thread.Sleep(20)
    check (not (Array.contains "quickoff" platform.Calls)) "Cancelled countdown launched power command"
    check controller.Snapshot.Monitoring "Cancelling countdown unexpectedly paused monitoring"
    check (controller.ScheduleQuickOff(5)) "Second countdown did not start"
    advanceSeconds time 5
    waitUntil "QuickOff dispatch" (fun () -> controller.Snapshot.QuickOffRunning)
    check (not controller.Snapshot.Monitoring) "Monitoring did not pause before power dispatch"
    let calls = platform.Calls
    check (Array.findIndex ((=) "unsubscribe") calls < Array.findIndex ((=) "quickoff") calls) "Power launched before listener cleanup"
    check (not (controller.TryClose())) "Exit interrupted power command"
    check (not (controller.RunTool(HealthCheck, ignore))) "Tool overlapped dispatched power command"
    platform.FinishPower(0)
    waitUntil "power completion" (fun () -> not controller.Snapshot.QuickOffRunning)
    check (not controller.Snapshot.Monitoring) "Monitoring resumed automatically"
    check (controller.RunTool(HealthRepair, ignore)) "Health tool did not start"
    check (not (controller.ScheduleQuickOff(5))) "Power overlapped health tool"
    check (not (controller.TryClose())) "Exit interrupted a health tool"
    check (not (controller.CancelDiagnostics())) "Diagnostic cancellation interrupted Windows repair"
    platform.FinishTool(3010)
    waitUntil "tool completion" (fun () -> controller.Snapshot.Tool.IsNone)
    check (controller.Snapshot.Status.Contains("restart")) "Restart-required exit was lost"
    check (controller.TryClose()) "Could not close idle controller"
    check (not (controller.Start())) "Closed controller restarted"

    let failed = FakePlatform()
    let failureTime = ManualDelay()
    failed.FailLaunch <- true
    use failureController = new SleepController(failed, ignore, delay = failureTime.Wait)
    failureController.Start() |> ignore
    failureController.ScheduleQuickOff(5) |> ignore
    advanceSeconds failureTime 5
    waitUntil "launch failure" (fun () -> failureController.Snapshot.Status.Contains("Injected"))
    check (not failureController.Snapshot.Monitoring && not failureController.Snapshot.QuickOffRunning) "Launch failure left controller active"

    let closePlatform = FakePlatform()
    let closeTime = ManualDelay()
    use closing = new SleepController(closePlatform, ignore, delay = closeTime.Wait)
    closing.ScheduleQuickOff(5) |> ignore
    check (closing.TryClose()) "Pending countdown blocked close"
    closeTime.Advance()
    Thread.Sleep(20)
    check (not (Array.contains "quickoff" closePlatform.Calls)) "Closing launched pending power command"
    printfn "PASS: countdown timing, duplicate guards, cancellation, close, launch failure, tool exclusion and monitoring pause."

let testEvents () =
    let platform = FakePlatform()
    let time = ManualDelay()
    use controller = new SleepController(platform, ignore, delay = time.Wait)
    controller.UpdateSettings { SleepSettings.defaults() with KeepSleep = true; SuspendProcesses = true; DisableDevices = true }
    check (controller.Start()) "Monitoring did not start"
    platform.Emit EnterSleep
    check (Array.contains "suspend" platform.Calls && Array.contains "disable" platform.Calls) "Sleep actions did not run"
    platform.Emit (Wake "Mouse")
    check (time.Pending = 1) "Blocked wake did not queue return to sleep"
    platform.Emit (Wake "PowerButton")
    time.Advance()
    Thread.Sleep(20)
    check (not (Array.contains "display" platform.Calls)) "Allowed wake did not cancel blocked-wake timer"
    platform.Emit (Wake "Keyboard")
    time.Advance()
    waitUntil "return to sleep" (fun () -> Array.contains "display" platform.Calls)
    platform.Emit (Lid true)
    platform.Emit (Lid false)
    time.Advance()
    Thread.Sleep(20)
    check ((platform.Calls |> Array.filter ((=) "display")).Length = 1) "Opened lid still turned off display"
    platform.Emit (Lid true)
    controller.Pause()
    time.Advance()
    Thread.Sleep(20)
    check ((platform.Calls |> Array.filter ((=) "display")).Length = 1) "Pause did not cancel lid action"
    check (Array.last platform.Calls = "restore") "Pause did not restore changed resources"
    controller.Start() |> ignore
    platform.Emit (MonitorFailed "Injected event-log failure")
    check (not controller.Snapshot.Monitoring && controller.Snapshot.Status.Contains("Injected event-log failure")) "Event-log failure did not stop monitoring"
    check (SleepPlatform.wakeSource 1 = "PowerButton" && SleepPlatform.wakeSource 32 = "Mouse" && SleepPlatform.wakeSource 999 = "Other") "Wake-source mapping failed"
    printfn "PASS: sleep actions, wake-source policy, restoration, lid-open cancellation and Pause cleanup."

let descendants<'T when 'T :> Control> (root: Control) = root.GetLogicalDescendants() |> Seq.choose (function :? 'T as item -> Some item | _ -> None)
let findButton text root = descendants<Button> root |> Seq.find (fun control -> control.Content :? string && string control.Content = text)
let click (button: Button) = button.RaiseEvent(RoutedEventArgs(Button.ClickEvent))

let testReportWindow settingsPath =
    for completion in [ "cancel"; "close"; "finish"; "failure" ] do
        let platform = FakePlatform()
        platform.DeferCancellation <- (completion = "close")
        use controller = new SleepController(platform, ignore)
        use view = new SleepView(controller, ignore, settingsPath)
        let window = Window(Width = 1000.0, Height = 800.0, Content = view)
        use binding = SleepWindowLifetime.attach window controller ignore ignore
        window.Show()
        let tabs = descendants<TabControl> view |> Seq.exactlyOne
        tabs.SelectedIndex <- 1
        Dispatcher.UIThread.RunJobs()
        click (findButton "Collect diagnostics" view)
        waitUntil "report collector launch" (fun () -> Array.contains "tool" platform.Calls)
        waitUntil "report cancel button" (fun () -> (findButton "Cancel diagnostics" view).IsVisible)
        match completion with
        | "cancel" ->
            click (findButton "Cancel diagnostics" view)
            waitUntil "report cancel cleanup" (fun () -> controller.Snapshot.Tool.IsNone)
            check window.IsVisible "Cancel diagnostics closed the window"
            check ((findButton "Start monitoring" view).IsEnabled) "Cancelled report left controls disabled"
            check (not (findButton "Cancel diagnostics" view).IsVisible) "Cancelled report left Cancel visible"
            window.Close()
        | "close" ->
            window.Close()
            check window.IsVisible "Window closed before report cleanup finished"
            check controller.Snapshot.Closing "Window close did not request collector shutdown"
            waitUntil "collector cancellation request" (fun () -> Array.contains "cancel-tool" platform.Calls)
            platform.FinishTool(0)
            waitUntil "automatic window close" (fun () -> not window.IsVisible)
        | "failure" ->
            platform.FailTool()
            waitUntil "collector failure" (fun () -> controller.Snapshot.Tool.IsNone)
            check (controller.Snapshot.Status.Contains("Fixture collector failure")) "Collector failure was hidden"
            window.Close()
        | _ ->
            platform.FinishTool(0)
            waitUntil "finished report" (fun () -> controller.Snapshot.Tool.IsNone)
            window.Close()
        check (not window.IsVisible) "Report left window unable to close"
    printfn "PASS: real window Cancel, Close during collection, automatic close after cleanup, and Close after completion/failure."

let testProcessCancellation () =
    let executable = Environment.ProcessPath
    let output = ConcurrentQueue<string>()
    let completed = SleepPlatform.runProcess executable [ "--fixture-exit" ] output.Enqueue
    waitUntil "collector exit/output" (fun () -> completed.IsCompleted)
    check (completed.GetAwaiter().GetResult() = 7) "Collector exit code was lost"
    check (Array.contains "fixture stdout" (output.ToArray()) && Array.contains "fixture stderr" (output.ToArray())) "Collector output was lost"
    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()
    let preCancelled = SleepPlatform.runProcessWithCancellation executable [ "--fixture-wait" ] ignore cancelled.Token
    try
        preCancelled.GetAwaiter().GetResult() |> ignore
        failwith "Pre-cancelled collector launched"
    with :? OperationCanceledException -> ()
    for mode in [ "--fixture-parent"; "--fixture-inherited-pipe" ] do
        use cts = new CancellationTokenSource()
        let lines = ConcurrentQueue<string>()
        let work = SleepPlatform.runProcessWithCancellation executable [mode] lines.Enqueue cts.Token
        let getPids () = lines.ToArray() |> Array.tryFind (fun line -> line.StartsWith("fixture-pids:"))
        waitUntil "collector descendants" (fun () -> getPids().IsSome)
        let pids = getPids().Value.Substring("fixture-pids:".Length).Split(',') |> Array.map int
        use child = Process.GetProcessById(pids[1])
        let _ = child.Handle // Keep this exact process identity across exit/PID reuse.
        try
            if mode = "--fixture-parent" then
                use parent = Process.GetProcessById(pids[0])
                let _ = parent.Handle
                cts.Cancel()
                waitUntil "collector tree cancellation" (fun () -> work.IsCompleted)
                try
                    work.GetAwaiter().GetResult() |> ignore
                    failwith "Collector cancellation was ignored"
                with :? OperationCanceledException -> ()
                check (parent.HasExited && child.HasExited) "Collector cancellation left a process behind"
            else
                waitUntil "collector exit with inherited output pipe" (fun () -> work.IsCompleted)
                check (work.GetAwaiter().GetResult() = 0) "Inherited output pipe prevented completion"
        finally
            cts.Cancel()
            if not child.HasExited then
                child.Kill(entireProcessTree = true)
                child.WaitForExit(5000) |> ignore
    printfn "PASS: real collector cancellation stops parent/child, pre-cancellation starts nothing, exit/output are preserved, and inherited pipes cannot hang completion."

let testBundledSleepHelpers () =
    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let names = [ "SleepDebug.ps1"; "SystemHealth.ps1"; "SleepRecovery.ps1"; "QuickOff-LICENSE.txt" ]
    // Resolve the compiled application's own payload. Windows' known-folder path
    // need not follow LOCALAPPDATA, and other builds may already be extracted.
    let paths = names |> List.map (fun name ->
        let path = AppFiles.get ("Goodnight/" + name)
        let source = Path.Combine(repoRoot, "plumbyr", "Goodnight", name)
        check (File.Exists(path)) ("Missing embedded sleep payload: " + name)
        check (File.ReadAllBytes(path) = File.ReadAllBytes(source)) ("Embedded sleep payload differs from source: " + name)
        path)
    let powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell/v1.0/powershell.exe")
    use timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
    let work = SleepPlatform.runProcessWithCancellation powershell
                    [ "-NoProfile"; "-NonInteractive"; "-ExecutionPolicy"; "Bypass"; "-File"
                      Path.Combine(repoRoot, "tests", "GoodnightScriptsSmoke.ps1")
                      "-ScriptsDirectory"; Path.GetDirectoryName(paths.Head) ] (printfn "%s") timeout.Token
    check (work.GetAwaiter().GetResult() = 0) "Bundled sleep helper checks failed"
    printfn "PASS: AppFiles resolves the embedded sleep helpers; contents and safe command-plan/recovery checks pass."

let runFixture mode =
    match mode with
    | "--fixture-exit" ->
        Console.WriteLine("fixture stdout")
        Console.Error.WriteLine("fixture stderr")
        7
    | "--fixture-wait" -> Thread.Sleep(120000); 0
    | _ ->
        let info = ProcessStartInfo(Environment.ProcessPath, UseShellExecute = false, CreateNoWindow = true)
        info.ArgumentList.Add("--fixture-wait")
        use child = Process.Start(info)
        Console.WriteLine(sprintf "fixture-pids:%d,%d" Environment.ProcessId child.Id)
        Console.Out.Flush()
        if mode = "--fixture-parent" then Thread.Sleep(120000)
        0

let runTests () =
    AppBuilder.Configure<App>().UseSkia().UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false)).SetupWithoutStarting() |> ignore
    testDiagnosticClose()
    testController()
    testEvents()
    testBundledSleepHelpers()
    let fixture = Path.Combine(Path.GetTempPath(), "Plumbyr-sleep-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory(fixture) |> ignore
    let settingsPath = Path.Combine(fixture, "settings.json")
    try
        SleepSettings.save settingsPath { SleepSettings.defaults() with WakeSources = [| "Keyboard"; "garbage" |]; LidDelaySeconds = -8 }
        let saved = SleepSettings.load settingsPath
        check (saved.LidDelaySeconds = 0 && saved.WakeSources = [| "PowerButton"; "Keyboard" |]) "Settings normalization or persistence failed"
        File.WriteAllText(settingsPath, "invalid-json")
        testReportWindow settingsPath
        testProcessCancellation()
        let platform = FakePlatform()
        let time = ManualDelay()
        use controller = new SleepController(platform, ignore, delay = time.Wait)
        let messages = ResizeArray<string>()
        use view = new SleepView(controller, messages.Add, settingsPath)
        check (messages |> Seq.exists (fun line -> line.Contains("Could not load"))) "Invalid settings were not reported"
        let window = Window(Width = 1040.0, Height = 860.0, Content = view,
                            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark, Background = SolidColorBrush.Parse("#080808"))
        window.Show()
        Dispatcher.UIThread.RunJobs()
        let tabs = descendants<TabControl> view |> Seq.exactlyOne
        check (tabs.Items.Count = 5) "Expected all five Goodnight pages"
        click (findButton "Start monitoring" view)
        Dispatcher.UIThread.RunJobs()
        check controller.Snapshot.Monitoring "Start button did not start monitoring"
        let keyboard = descendants<CheckBox> view |> Seq.find (fun control -> string control.Content = "Keyboard")
        keyboard.IsChecked <- Nullable<bool>(true)
        check (Array.contains "Keyboard" (SleepSettings.load settingsPath).WakeSources) "UI settings did not persist"
        let screenshotRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../../.build"))
        Directory.CreateDirectory(screenshotRoot) |> ignore
        for width in [1040.0; 800.0] do
            window.Width <- width
            for index in 0 .. 4 do
                tabs.SelectedIndex <- index
                Dispatcher.UIThread.RunJobs()
                use frame = window.CaptureRenderedFrame()
                check (not (isNull frame)) "Sleep tab failed to render"
                frame.Save(Path.Combine(screenshotRoot, sprintf "sleep-%d-%d.png" (int width) index), PngBitmapEncoderOptions.Default)
        click (findButton "Sleep with QuickOff" view)
        Dispatcher.UIThread.RunJobs()
        check controller.Snapshot.Countdown.IsSome "UI countdown failed"
        tabs.SelectedIndex <- 0
        Dispatcher.UIThread.RunJobs()
        click (findButton "Cancel sleep" view)
        Dispatcher.UIThread.RunJobs()
        check controller.Snapshot.Countdown.IsNone "Cancel was unavailable after tab change"
        check (not (Array.contains "quickoff" platform.Calls)) "UI test invoked power action"
        window.Close()
        printfn "PASS: settings persistence, corrupt-settings fallback, all five themed pages, two widths, Start and cross-tab countdown cancellation."
        // Register and release real Windows listeners without a controller: events
        // are only collected, so this cannot suspend processes, disable inputs or sleep.
        let native = SleepPlatform.WindowsSleepPlatform(ignore) :> ISleepPlatform
        use listener = native.Subscribe(ignore)
        Thread.Sleep(100)
        printfn "PASS: native Windows power/event subscriptions register and release without power actions."
        0
    finally
        if File.Exists(settingsPath) then File.Delete(settingsPath)
        Directory.Delete(fixture, false)

[<STAThread; EntryPoint>]
let main args =
    if args.Length = 1 && args[0].StartsWith("--fixture-") then runFixture args[0]
    else runTests()
