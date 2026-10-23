namespace WindowsCleaner

open System
open System.Collections.Generic
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Threading
open Avalonia.Styling
open Synthora.Controls
open Synthora.Overlays

// -----------------------------------------------------------------------
// OptimizeView UserControl — Windows tweaks ported from Swept
// -----------------------------------------------------------------------

type OptimizeView(writeToLog: string -> unit) as this =
    inherit UserControl()

    let mutable activeCts : CancellationTokenSource option = None

    // ----- theme resources (Synthora; app runs Dark-only so a static lookup is fine) -----
    let brush (key: string) = Application.Current.FindResource(key) :?> IBrush
    // Matches Plumbyr's near-black window background elsewhere in the app.
    let cardBg = SolidColorBrush.Parse("#121212") :> IBrush
    let textLow = brush "ThemeForegroundLowBrush"
    let primary = brush "PrimaryBrush"
    let success = brush "SuccessBrush"
    let warning = brush "WarningBrush"
    let error   = brush "ErrorBrush"
    let solidButtonTheme = Application.Current.FindResource("SolidButtonTheme") :?> ControlTheme

    // ----- UI controls -----
    let itemsList = StackPanel(Spacing = 6.0)

    let statusLabel =
        TextBlock(Text = "Ready. Check the tweaks to apply, then press Apply selected.",
                  Foreground = textLow, FontSize = 13.0,
                  TextWrapping = TextWrapping.Wrap)

    let progressBar =
        ProgressBar(Minimum = 0.0, Maximum = 100.0, Value = 0.0,
                    Height = 6.0, IsVisible = false)

    let restoreFirstBox =
        CheckBox(Content = "Create a restore point first",
                 IsChecked = Nullable<bool>(true))

    let selectionTxt =
        TextBlock(Foreground = textLow, FontSize = 12.0)

    // Per-item checkbox and result line, keyed by Optimization.Key.
    let checks  = Dictionary<string, CheckBox>()
    let results = Dictionary<string, TextBlock>()

    // ----- helpers -----

    let setStatus (msg: string) =
        Dispatcher.UIThread.Post(fun () -> statusLabel.Text <- msg)

    let logAndStatus msg =
        setStatus msg
        writeToLog msg

    let setProgress (done': int) (total: int) =
        Dispatcher.UIThread.Post(fun () ->
            progressBar.Value     <- if total = 0 then 0.0 else 100.0 * float done' / float total
            progressBar.IsVisible <- done' < total)

    let setResult (key: string) (text: string) (fg: IBrush) =
        Dispatcher.UIThread.Post(fun () ->
            let r = results[key]
            r.Text       <- text
            r.Foreground <- fg
            r.IsVisible  <- text <> "")

    let isChecked (cb: CheckBox) = cb.IsChecked.HasValue && cb.IsChecked.Value

    let selected () =
        OptimizeService.optimizations |> List.filter (fun o -> isChecked checks[o.Key])

    let updateSelection () =
        selectionTxt.Text <- sprintf "SELECTED: %d / %d" (selected()).Length OptimizeService.optimizations.Length

    let setChecks (pick: Optimization -> bool) =
        for o in OptimizeService.optimizations do
            checks[o.Key].IsChecked <- Nullable<bool>(pick o)

    let isBusy () = activeCts.IsSome

    let runTask (label: string) (work: CancellationToken -> unit) =
        if isBusy() then
            logAndStatus "A task is already running. Cancel it first."
        else
            let cts = new CancellationTokenSource()
            activeCts <- Some cts
            Dispatcher.UIThread.Post(fun () ->
                progressBar.IsVisible <- true
                progressBar.Value     <- 0.0)
            logAndStatus (sprintf "--> Starting: %s" label)
            ThreadPool.QueueUserWorkItem(fun _ ->
                try work cts.Token
                with ex -> logAndStatus (sprintf "[ERROR] %s: %s" label ex.Message)
                activeCts <- None
                Dispatcher.UIThread.Post(fun () -> progressBar.IsVisible <- false)
            ) |> ignore

    // Applies items in order, marking each card as it goes. Every item is isolated:
    // a failure is reported and the batch moves on.
    let applyItems (items: Optimization list) (createRestorePoint: bool) (ct: CancellationToken) =
        writeToLog "========== WINDOWS OPTIMIZATION =========="
        writeToLog (sprintf "OS: %s" (OptimizeService.windowsInfo()))
        if createRestorePoint then
            setStatus "Creating restore point..."
            if not (DriverService.createRestorePoint "Plumbyr optimization" writeToLog) then
                writeToLog "Restore point not created — continuing with optimizations."
        let total = items.Length
        let mutable applied = 0
        let mutable protectedCount = 0
        let mutable failed = 0
        for (index, item) in List.indexed items do
            if ct.IsCancellationRequested then
                setResult item.Key "— not run (cancelled)" textLow
            else
                setProgress index total
                setStatus (sprintf "Applying %d/%d: %s" (index + 1) total item.Title)
                setResult item.Key "⏳ Applying..." primary
                writeToLog (sprintf "--- %s ---" item.Title)
                match OptimizeService.apply item with
                | Applied ->
                    applied <- applied + 1
                    setResult item.Key "✔ Applied" success
                    writeToLog "  ✔ done"
                | Protected reason ->
                    protectedCount <- protectedCount + 1
                    setResult item.Key (sprintf "⛔ Unavailable — %s" reason) warning
                    writeToLog (sprintf "  ⛔ unavailable: %s" reason)
                | Failed reason ->
                    failed <- failed + 1
                    setResult item.Key (sprintf "⚠ Error — %s" reason) error
                    writeToLog (sprintf "  ⚠ error: %s" reason)
        setProgress total total
        let summary =
            sprintf "Optimization finished: %d applied, %d unavailable, %d failed%s."
                applied protectedCount failed
                (if ct.IsCancellationRequested then " (cancelled)" else "")
        logAndStatus summary

    // ----- build UI -----

    do
        // Left: grouped tweak cards
        for group in [ Basics; Debloat ] do
            let header =
                TextBlock(Text = (OptimizeService.groupTitle group).ToUpperInvariant(),
                          FontSize = 12.0, FontWeight = FontWeight.Bold,
                          Foreground = primary, Margin = Thickness(0.0, 10.0, 0.0, 2.0))
            itemsList.Children.Add(header)
            for o in OptimizeService.optimizations |> List.filter (fun o -> o.Group = group) do
                let card = Border(Background = cardBg, CornerRadius = CornerRadius(8.0), Padding = Thickness(14.0, 10.0))
                let textStack = StackPanel(Spacing = 3.0, Margin = Thickness(10.0, 0.0, 0.0, 0.0))
                textStack.Children.Add(TextBlock(Text = o.Title, FontWeight = FontWeight.Bold, FontSize = 14.0, TextWrapping = TextWrapping.Wrap))
                textStack.Children.Add(TextBlock(Text = o.Description, FontSize = 11.0, Foreground = textLow, TextWrapping = TextWrapping.Wrap))
                let result = TextBlock(FontSize = 11.0, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap, IsVisible = false)
                textStack.Children.Add(result)
                let cb = CheckBox(Content = textStack, IsChecked = Nullable<bool>(o.SafeDefault))
                cb.IsCheckedChanged.Add(fun _ -> updateSelection())
                checks[o.Key]  <- cb
                results[o.Key] <- result
                card.Child <- cb
                itemsList.Children.Add(card)
        updateSelection()

        let scroll = ScrollViewer(Content = itemsList)

        // Right: controls
        let mkBtn (text: string) (variant: string) =
            let btn =
                Button(Content = text, Height = 52.0,
                       HorizontalAlignment = HorizontalAlignment.Stretch,
                       FontWeight = FontWeight.Bold, FontSize = 15.0,
                       Theme = solidButtonTheme)
            if variant <> "" then btn.Classes.Add(variant)
            btn

        let applyBtn    = mkBtn "Apply selected"     "Success"
        let safeBtn     = mkBtn "Mark safe defaults" "Primary"
        let noneBtn     = mkBtn "Select none"        ""
        let restoreBtn  = mkBtn "Restore point"      ""
        let cancelBtn   = mkBtn "Cancel"             "Error"

        let infoPanel = Border(Background = cardBg, CornerRadius = CornerRadius(8.0), Padding = Thickness(16.0))
        let infoStack = StackPanel(Spacing = 8.0)
        infoStack.Children.Add(TextBlock(Text = "System", FontWeight = FontWeight.Bold, FontSize = 14.0))
        infoStack.Children.Add(TextBlock(Text = OptimizeService.windowsInfo(), FontSize = 12.0, Foreground = textLow, TextWrapping = TextWrapping.Wrap))
        infoStack.Children.Add(selectionTxt)
        infoStack.Children.Add(restoreFirstBox)
        infoPanel.Child <- infoStack

        let statusPanel = Border(Background = cardBg, CornerRadius = CornerRadius(8.0), Padding = Thickness(16.0))
        let statusStack = StackPanel(Spacing = 8.0)
        statusStack.Children.AddRange [progressBar; statusLabel]
        statusPanel.Child <- statusStack

        let actionPanel = StackPanel(Spacing = 14.0, Margin = Thickness(24.0, 0.0, 0.0, 0.0))
        actionPanel.Children.AddRange [infoPanel; applyBtn; safeBtn; noneBtn; restoreBtn; cancelBtn; statusPanel]

        // Header
        let heading = StackPanel(Spacing = 4.0, Margin = Thickness(0.0, 0.0, 0.0, 20.0))
        heading.Children.Add(TextBlock(Text = "Windows Optimization",
                                        FontSize   = 32.0,
                                        FontWeight = FontWeight.Black))
        heading.Children.Add(TextBlock(Text = "Safe, conservative Windows 10/11 tweaks. Settings only — no system files, drivers or services are removed.",
                                        FontSize     = 14.0,
                                        Foreground   = textLow,
                                        TextWrapping = TextWrapping.Wrap))

        let body = Grid(ColumnDefinitions = ColumnDefinitions("*,320"))
        Grid.SetColumn(scroll, 0);      body.Children.Add(scroll)
        Grid.SetColumn(actionPanel, 1); body.Children.Add(actionPanel)

        let root = Grid(RowDefinitions = RowDefinitions("Auto,*"), Margin = Thickness(30.0))
        Grid.SetRow(heading, 0); root.Children.Add(heading)
        Grid.SetRow(body, 1);    root.Children.Add(body)

        this.Content <- root

        // ----- event handlers -----

        safeBtn.Click.Add(fun _ -> setChecks (fun o -> o.SafeDefault))
        noneBtn.Click.Add(fun _ -> setChecks (fun _ -> false))

        applyBtn.Click.Add(fun _ ->
            let items = selected()
            if items.IsEmpty then
                setStatus "Select at least one optimization to apply."
            elif not (isBusy()) then
                async {
                    let! result =
                        AlertDialog.ShowAsync(
                            sprintf "This will change %d Windows setting(s). Changes can be undone with the system restore point." items.Length,
                            "Confirm Optimization",
                            DialogButton.YesNo,
                            IconType.Warning) |> Async.AwaitTask
                    if result = DialogResult.Yes then
                        for o in OptimizeService.optimizations do setResult o.Key "" textLow
                        let createRestorePoint = isChecked restoreFirstBox
                        runTask "Windows Optimization" (applyItems items createRestorePoint)
                } |> Async.StartImmediate
            else
                setStatus "A task is already running. Cancel it first.")

        restoreBtn.Click.Add(fun _ ->
            runTask "Create Restore Point" (fun _ ->
                if DriverService.createRestorePoint "Plumbyr optimization" writeToLog then
                    setStatus "Restore point created successfully."
                else
                    setStatus "Failed to create restore point. See Command Center log."))

        cancelBtn.Click.Add(fun _ ->
            match activeCts with
            | Some cts ->
                cts.Cancel()
                logAndStatus "[CANCELLED] Remaining optimizations will be skipped."
            | None ->
                setStatus "No active task to cancel.")
