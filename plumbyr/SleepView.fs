namespace WindowsCleaner

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Styling
open Avalonia.Threading
open Synthora.Controls
open Synthora.Overlays

type SleepView(controller: SleepController, writeToLog: string -> unit, settingsPath: string) as this =
    inherit UserControl()

    let textLow = Application.Current.FindResource("ThemeForegroundLowBrush") :?> IBrush
    let primary = Application.Current.FindResource("PrimaryBrush") :?> IBrush
    let buttonTheme = Application.Current.FindResource("SolidButtonTheme") :?> ControlTheme
    let mutable disposed = false
    let mutable loading = true
    let checks = ResizeArray<CheckBox>()
    let status = TextBlock(TextWrapping = TextWrapping.Wrap, FontSize = 13.0, Foreground = textLow)
    let monitor = Button(Content = "Start monitoring", Theme = buttonTheme)
    let cancel = Button(Content = "Cancel sleep", Theme = buttonTheme, IsVisible = false)
    let cancelReport = Button(Content = "Cancel diagnostics", Theme = buttonTheme, IsVisible = false)
    let quick = Button(Content = "Sleep with QuickOff", Theme = buttonTheme)
    let delay = ComboBox(ItemsSource = [| "5 seconds"; "15 seconds"; "30 seconds" |], SelectedIndex = 0, MinWidth = 160.0)
    let lidDelay = NumericUpDown(Minimum = 0M, Maximum = 3600M,
                                Value = Nullable<decimal>(40M), Increment = 5M, Width = 130.0)
    let output = TextBox(IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                         MinHeight = 180.0, MaxHeight = 300.0, FontFamily = FontFamily("Consolas"), IsVisible = false)
    let toolButtons = ResizeArray<Button>()
    let startup = CheckBox(Content = "Launch Plumbyr and start sleep monitoring at sign-in")
    let outputQueue = ConcurrentQueue<string>()
    let outputTimer = DispatcherTimer(Interval = TimeSpan.FromMilliseconds(100.0))

    let paragraph text = TextBlock(Text = text, Foreground = textLow, FontSize = 13.0, TextWrapping = TextWrapping.Wrap)
    let heading text = TextBlock(Text = text, FontSize = 20.0, FontWeight = FontWeight.Bold)
    let panel () = StackPanel(Spacing = 16.0, Margin = Thickness(0.0, 20.0, 0.0, 10.0))
    let button text = Button(Content = text, Theme = buttonTheme)
    let check text value =
        let control = CheckBox(Content = text, IsChecked = Nullable<bool>(value))
        checks.Add(control)
        control
    let checked' (control: CheckBox) = control.IsChecked.GetValueOrDefault()

    let render (state: SleepSnapshot) =
        if not disposed then
            status.Text <-
                match state.Countdown with
                | Some seconds -> sprintf "QuickOff sleep in %d seconds. Cancel sleep is available on every tab and in the sidebar." seconds
                | None -> state.Status
            monitor.Content <- if state.Monitoring then "Pause monitoring" else "Start monitoring"
            let busy = state.Tool.IsSome || state.QuickOffRunning || state.Countdown.IsSome
            monitor.IsEnabled <- not busy && not state.Closing
            quick.IsEnabled <- not busy && not state.Closing
            delay.IsEnabled <- not busy
            cancel.IsVisible <- state.Countdown.IsSome
            cancelReport.IsVisible <- state.Tool |> Option.exists SleepTool.canCancel
            cancelReport.IsEnabled <- not state.CancellingTool && not state.Closing
            for control in checks do control.IsEnabled <- not busy
            lidDelay.IsEnabled <- not busy
            for control in toolButtons do control.IsEnabled <- not busy

    let subscription = controller.Changed.Subscribe(fun state -> Dispatcher.UIThread.Post(fun () -> render state))

    let flushOutput () =
        if not disposed then
            let lines = ResizeArray<string>()
            let mutable line = ""
            while lines.Count < 200 && outputQueue.TryDequeue(&line) do lines.Add(line)
            if lines.Count > 0 then
                let batch = String.Join(Environment.NewLine, lines)
                writeToLog batch
                let text = (if isNull output.Text then "" else output.Text) + batch + Environment.NewLine
                output.Text <- if text.Length > 64000 then text.Substring(text.Length - 64000) else text
                output.CaretIndex <- output.Text.Length

    // A burst of report tables must not flood the dispatcher ahead of Close/Cancel.
    let appendOutput line = outputQueue.Enqueue(line)

    let runTool tool =
        outputQueue.Clear()
        output.Text <- ""
        output.IsVisible <- true
        if not (controller.RunTool(tool, appendOutput)) then status.Text <- "Finish or cancel the current sleep action first."

    do
        outputTimer.Tick.Add(fun _ -> flushOutput())
        outputTimer.Start()
        let settings =
            try SleepSettings.load settingsPath
            with ex ->
                writeToLog ("Could not load sleep settings; using defaults: " + ex.Message)
                SleepSettings.defaults()
        controller.UpdateSettings settings
        let behavior = panel()
        behavior.Children.Add(heading "Sleep behavior")
        behavior.Children.Add(paragraph "These controls follow Goodnight's Modern Standby events. Return to sleep turns the display off; Windows and your hardware decide whether the PC enters sleep.")
        let keep = check "Return to sleep after a blocked wake event" settings.KeepSleep
        let suspend = check "Suspend background applications while sleeping" settings.SuspendProcesses
        let devices = check "Disable devices excluded by allowed wake sources while sleeping" settings.DisableDevices
        let log = check "Log wake and power events in Command Center" settings.WakeLog
        behavior.Children.AddRange [keep; suspend; devices; log]
        behavior.Children.Add(paragraph "Applications and input devices are restored on resume, Pause, or exit. Device control may disconnect network and Bluetooth devices. Use the power button to wake the PC.")
        behavior.Children.Add(heading "Allowed wake sources")
        behavior.Children.Add(paragraph "Power button is always allowed. Select additional wake sources.")
        let sources =
            SleepSettings.sources |> List.map (fun (key, title) ->
                let control = check title (Array.contains key settings.WakeSources)
                behavior.Children.Add(control)
                key, control)
        let lidRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = 12.0)
        lidRow.Children.Add(TextBlock(Text = "Display off after lid closes (seconds; 0 disables)", VerticalAlignment = VerticalAlignment.Center))
        lidDelay.Value <- Nullable<decimal>(decimal settings.LidDelaySeconds)
        lidRow.Children.Add(lidDelay)
        behavior.Children.Add(lidRow)
        behavior.Children.Add(startup)
        let applySettings () =
            if not loading then
                let value = {
                    KeepSleep = checked' keep; SuspendProcesses = checked' suspend; DisableDevices = checked' devices
                    WakeLog = checked' log; WakeSources = sources |> List.choose (fun (key, control) -> if checked' control then Some key else None) |> List.toArray
                    LidDelaySeconds = int (lidDelay.Value.GetValueOrDefault()) }
                try
                    SleepSettings.save settingsPath value
                    controller.UpdateSettings value
                with ex -> status.Text <- "Could not save sleep settings: " + ex.Message
        for control in checks do control.IsCheckedChanged.Add(fun _ -> applySettings())
        lidDelay.ValueChanged.Add(fun _ -> applySettings())

        // Startup is an explicit action; merely opening this page never changes tasks.
        let startupButton = button "Apply sign-in preference"
        behavior.Children.Add(startupButton)
        startupButton.Click.Add(fun _ ->
            let enabled = checked' startup
            startupButton.IsEnabled <- false
            async {
                try
                    let! code = SleepPlatform.setStartup enabled appendOutput |> Async.AwaitTask
                    status.Text <- if code = 0 then "Sign-in preference saved." else "Could not change sign-in preference; see Command Center."
                with ex -> status.Text <- "Could not change sign-in preference: " + ex.Message
                startupButton.IsEnabled <- true
            } |> Async.StartImmediate)

        let diagnostics = panel()
        diagnostics.Children.Add(heading "Find out what interrupts sleep")
        diagnostics.Children.Add(paragraph "Collect power settings, wake sources, drivers, recent system events, a sleep study (where supported), and a 60-second energy analysis. Collection takes at least 60 seconds. Cancel diagnostics or close Plumbyr to stop collection; partial reports are kept.")
        let collect = button "Collect diagnostics"
        toolButtons.Add(collect)
        collect.Click.Add(fun _ -> runTool Diagnostics)
        diagnostics.Children.Add(collect)

        let health = panel()
        health.Children.Add(heading "Check and repair Windows")
        health.Children.Add(paragraph "Check runs DISM CheckHealth and ScanHealth, then SFC verification. Repair also runs DISM RestoreHealth and SFC /scannow. Repair can replace Windows files and download repair content. Leave Windows tools running until they finish.")
        let healthActions = WrapPanel(Orientation = Orientation.Horizontal)
        let healthCheck = button "Check Windows"
        let repair = button "Repair Windows"
        toolButtons.Add(healthCheck)
        toolButtons.Add(repair)
        healthCheck.Click.Add(fun _ -> runTool HealthCheck)
        repair.Click.Add(fun _ ->
            async {
                let! result = AlertDialog.ShowAsync(
                    "Run DISM RestoreHealth and SFC /scannow? These tools can replace damaged Windows files, download repair content, and require a restart.",
                    "Repair Windows", DialogButton.YesNo, IconType.Warning) |> Async.AwaitTask
                if result = DialogResult.Yes then runTool HealthRepair
            } |> Async.StartImmediate)
        healthActions.Children.AddRange [healthCheck; repair]
        health.Children.Add(healthActions)

        let modes = panel()
        modes.Children.Add(heading "Supported sleep modes")
        modes.Children.Add(paragraph "Windows reports the states supported by your hardware, firmware, and current configuration. Modern Standby (S0) requires platform support. This page does not change power plans or force a different sleep mode.")
        let refresh = button "Refresh capabilities"
        toolButtons.Add(refresh)
        refresh.Click.Add(fun _ -> runTool Capabilities)
        modes.Children.Add(refresh)

        let quickPage = panel()
        quickPage.Children.Add(heading "Sleep with QuickOff")
        quickPage.Children.Add(paragraph "Use Goodnight's QuickOff power command. Depending on your PC, it may hibernate. Monitoring pauses when the countdown ends and stays paused when you return. Press Start monitoring to resume your rules.")
        quickPage.Children.Add(paragraph "Choose a delay. Closing the window cancels a pending countdown.")
        quickPage.Children.Add(delay)
        quickPage.Children.Add(quick)
        quick.Click.Add(fun _ ->
            let seconds = [| 5; 15; 30 |][Math.Clamp(delay.SelectedIndex, 0, 2)]
            if not (controller.ScheduleQuickOff seconds) then status.Text <- "Finish or cancel the current action first.")
        monitor.Click.Add(fun _ ->
            if controller.Snapshot.Monitoring then controller.Pause()
            else controller.Start() |> ignore)
        cancel.Click.Add(fun _ -> controller.CancelSleep() |> ignore)
        cancelReport.Click.Add(fun _ -> controller.CancelDiagnostics() |> ignore)

        let tabs = TabControl()
        for title, content in [ "Sleep settings", behavior; "Diagnostics", diagnostics; "System health", health
                                "Sleep mode", modes; "QuickOff sleep", quickPage ] do
            tabs.Items.Add(TabItem(Header = title, Content = ScrollViewer(Content = content))) |> ignore
        let title = StackPanel(Spacing = 6.0)
        title.Children.Add(TextBlock(Text = "Sleep", FontSize = 32.0, FontWeight = FontWeight.Black))
        title.Children.Add(paragraph "Goodnight's sleep controls and diagnostics, built into Plumbyr.")
        let actions = WrapPanel(Orientation = Orientation.Horizontal)
        actions.Children.AddRange [monitor; cancel; cancelReport]
        title.Children.Add(actions)
        title.Children.Add(status)
        let reports = button "Open sleep reports"
        reports.Click.Add(fun _ ->
            try Process.Start(ProcessStartInfo(SleepPlatform.reportsDirectory(), UseShellExecute = true)) |> ignore
            with ex -> status.Text <- "Could not open reports: " + ex.Message)
        let footer = StackPanel(Spacing = 8.0, Margin = Thickness(0.0, 12.0, 0.0, 0.0))
        footer.Children.Add(output)
        footer.Children.Add(reports)
        let root = Grid(RowDefinitions = RowDefinitions("Auto,*,Auto"), Margin = Thickness(30.0))
        Grid.SetRow(title, 0)
        Grid.SetRow(tabs, 1)
        Grid.SetRow(footer, 2)
        root.Children.AddRange [title; tabs; footer]
        this.Content <- root
        loading <- false
        render controller.Snapshot

    member _.Controller = controller
    member _.SetStartupState(enabled) = startup.IsChecked <- Nullable<bool>(enabled)
    interface IDisposable with
        member _.Dispose() =
            flushOutput()
            disposed <- true
            outputTimer.Stop()
            outputQueue.Clear()
            subscription.Dispose()
            (controller :> IDisposable).Dispose()

/// Keep the actual window close/retry wiring shared with the headless regression.
module SleepWindowLifetime =
    let attach (window: Window) (controller: SleepController) (showSleep: unit -> unit) (writeToLog: string -> unit) =
        let closing = window.Closing.Subscribe(fun args ->
            if not (controller.TryClose()) then
                args.Cancel <- true
                showSleep()
                if not controller.Snapshot.Closing then
                    writeToLog "Windows health tools or the power command are still running. Wait for them to finish before closing Plumbyr.")
        let mutable detached = false
        let ready = controller.Changed.Subscribe(fun state ->
            if state.Closing && state.Tool.IsNone && not state.QuickOffRunning then
                Dispatcher.UIThread.Post(fun () -> if not detached then window.Close()))
        { new IDisposable with
            member _.Dispose() =
                detached <- true
                closing.Dispose()
                ready.Dispose() }
