namespace WindowsCleaner

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open System.ComponentModel
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Templates
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Data
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.Styling
open Synthora.Controls
open Synthora.Overlays

// -----------------------------------------------------------------------
// DataGrid row view-model
// -----------------------------------------------------------------------

type DriverRow(item: DriverItem) =
    member _.DeviceName    = if String.IsNullOrWhiteSpace item.DeviceName    then "(Unknown Device)" else item.DeviceName
    member _.DeviceClass   = if String.IsNullOrWhiteSpace item.DeviceClass   then "—"               else item.DeviceClass
    member _.Manufacturer  = if String.IsNullOrWhiteSpace item.Manufacturer  then "—"               else item.Manufacturer
    member _.DriverVersion = if String.IsNullOrWhiteSpace item.DriverVersion then "—"               else item.DriverVersion
    member _.DriverDate    = if String.IsNullOrWhiteSpace item.DriverDate    then "—"               else item.DriverDate
    member _.InfName       = if String.IsNullOrWhiteSpace item.InfName       then "—"               else item.InfName
    member _.Provider      = if String.IsNullOrWhiteSpace item.Provider      then "—"               else item.Provider
    member _.Status        = match item.ProblemCode with None -> "Unknown" | Some 0 -> "OK" | Some 22 -> "Disabled" | Some code -> sprintf "Problem %d" code
    member _.Item          = item

type DriverUpdateRow(item: DriverUpdateInfo, selectionChanged: unit -> unit) as this =
    let changed = Event<PropertyChangedEventHandler, PropertyChangedEventArgs>()
    let mutable selected = false
    let mutable status = "Available"
    let mutable available = true
    let notify name = changed.Trigger(this, PropertyChangedEventArgs(name))
    member _.Item = item
    member _.Device = if String.IsNullOrWhiteSpace item.Model then item.Title else item.Model
    member _.Title = item.Title
    member _.Provider = item.Provider
    member _.Date = item.DriverDate
    member _.Size = sprintf "%.1f MB" item.SizeMB
    member _.Current = item.CurrentDriver |> Option.map (fun d -> d.DriverVersion) |> Option.defaultValue "Unknown / ambiguous"
    member _.Restart = if item.MayRequireReboot then "May require restart" else "Not expected"
    member _.Status with get() = status and set value = status <- value; notify "Status"
    member _.CanInstall = available
    member _.Selected
        with get() = selected
        and set value =
            selected <- value && available
            notify "Selected"
            selectionChanged()
    member _.ApplyResult(result: DriverInstallResult) =
        available <- result.State = DriverInstallState.Failed || result.State = DriverInstallState.NotAttempted
        this.Status <-
            match result.State with
            | DriverInstallState.Installed -> if result.RebootRequired then "Installed — restart required" else "Installed"
            | DriverInstallState.Partial -> "Completed with errors"
            | DriverInstallState.Failed -> "Failed" + (if result.HResult = "" then "" else " (0x" + result.HResult + ")")
            | DriverInstallState.Unavailable -> "No longer offered — scan again"
            | DriverInstallState.NotAttempted -> "Not attempted"
            | DriverInstallState.Unknown -> "Unconfirmed — check history"
        notify "CanInstall"
        this.Selected <- false
    interface INotifyPropertyChanged with
        [<CLIEvent>] member _.PropertyChanged = changed.Publish

// -----------------------------------------------------------------------
// DriversView UserControl
// -----------------------------------------------------------------------

type DriversView(writeToLog: string -> unit, storage: IStorageProvider,
                 ?scanner: DriverScanBackend, ?installer: DriverInstallBackend,
                 ?confirmInstall: string -> Task<bool>) as this =
    inherit UserControl()

    // Cancellation for long-running background tasks
    let mutable activeCts : CancellationTokenSource option = None
    let mutable disposed = false
    let mutable installing = false
    let mutable confirming = false
    let mutable closeAfterInstall: (unit -> unit) option = None
    let mutable updateRows: DriverUpdateRow array = [||]
    let mutable refreshInstallState = fun () -> ()
    let backend = defaultArg scanner DriverService.scanner
    let installBackend = defaultArg installer DriverService.installer
    let confirmInstall = defaultArg confirmInstall (fun text -> task {
        let! result = AlertDialog.ShowAsync(text, "Install selected drivers", DialogButton.YesNo, IconType.Warning)
        return result = DialogResult.Yes })
    let taskButtons = ResizeArray<Button>()
    let cancelButton = Button(Content = "Cancel", IsEnabled = false)
    let installButton = Button(Content = "Install selected updates", IsEnabled = false)
    let restoreOption = CheckBox(Name = "DriverInstallRestore", Content = "Create a restore point first", IsChecked = Nullable true)
    let selectionLabel = TextBlock(VerticalAlignment = VerticalAlignment.Center)
    let installOptions = WrapPanel(IsVisible = false, Margin = Thickness(0.0, 0.0, 0.0, 8.0))
    let mutable allDriverRows : DriverRow array = [||]
    let mutable classClasses  : string array     = [||]

    // ----- theme resources (Synthora; app runs Dark-only so a static lookup is fine) -----
    let brush (key: string) = Application.Current.FindResource(key) :?> IBrush
    // Matches Plumbyr's near-black window background elsewhere in the app.
    let cardBg = SolidColorBrush.Parse("#121212") :> IBrush
    let textLow = brush "ThemeForegroundLowBrush"
    let solidButtonTheme = Application.Current.FindResource("SolidButtonTheme") :?> ControlTheme

    // ----- UI controls -----
    let statusLabel =
        TextBlock(Name = "DriverTaskStatus", Text = "Ready. Use the toolbar to scan or update drivers.",
                  Foreground = textLow, FontSize = 13.0,
                  TextWrapping = TextWrapping.Wrap)

    let progressBar =
        ProgressBar(Name = "DriverTaskProgress", Minimum = 0.0, Maximum = 100.0, Value = 0.0,
                    Height = 6.0, IsVisible = false)

    let lastScanLabel = TextBlock(Name = "DriverLastScan", Foreground = textLow, FontSize = 12.0, TextWrapping = TextWrapping.Wrap)
    let updateGrid = DataGrid(Name = "DriverUpdateResults", AutoGenerateColumns = false, IsReadOnly = false,
                              CanUserResizeColumns = true, MaxHeight = 180.0, MinHeight = 90.0,
                              IsVisible = false, BorderThickness = Thickness(0.0))

    let showUpdateScan (scan: DriverUpdateScan) =
        lastScanLabel.Text <- sprintf "%s: %s — %d update(s)."
            (if scan.Warnings.IsEmpty then "Last successful scan" else "Incomplete scan")
            (scan.CheckedAt.ToLocalTime().ToString("g")) scan.Updates.Length
        updateRows <- scan.Updates |> List.map (fun u -> DriverUpdateRow(u, fun () -> refreshInstallState())) |> List.toArray
        updateGrid.ItemsSource <- updateRows
        updateGrid.IsVisible <- not scan.Updates.IsEmpty
        installOptions.IsVisible <- not scan.Updates.IsEmpty
        refreshInstallState()

    // Kept as an explicit near-black / green console skin — a deliberate "terminal" look,
    // not a stray hardcoded chrome color, so it isn't sourced from the theme palette.
    let logBox =
        TextBox(AcceptsReturn = true, IsReadOnly = true,
                Background   = SolidColorBrush.Parse("#0a0a0a"),
                Foreground   = Brushes.LimeGreen,
                FontFamily   = FontFamily("Consolas"),
                FontSize     = 12.0,
                TextWrapping = TextWrapping.Wrap)

    // We hold a reference to the scroll viewer wrapping logBox so we can scroll it
    let logScroll =
        ScrollViewer(HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                     VerticalScrollBarVisibility   = ScrollBarVisibility.Auto)

    let driverGrid =
        DataGrid(AutoGenerateColumns  = false,
                 IsReadOnly           = true,
                 CanUserResizeColumns = true,
                 GridLinesVisibility  = DataGridGridLinesVisibility.Horizontal,
                 BorderThickness      = Thickness(0.0))

    let searchBox =
        TextBox(PlaceholderText   = "Search devices, drivers, manufacturers...",
                Height            = 36.0, MinWidth = 280.0,
                Padding           = Thickness(8.0, 0.0))

    let classFilter =
        ComboBox(MinWidth       = 160.0,
                 Height         = 36.0)

    let driverCountTxt =
        TextBlock(Foreground          = textLow,
                  FontSize            = 12.0,
                  VerticalAlignment   = VerticalAlignment.Center)

    // ----- helpers -----

    let appendLog (msg: string) =
        Dispatcher.UIThread.Post(fun () ->
            try
                if not disposed then
                    let prev = if isNull logBox.Text then "" else logBox.Text
                    let ts = DateTime.Now.ToString("HH:mm:ss")
                    logBox.Text <- prev + sprintf "[%s] %s\n" ts msg
                    logScroll.ScrollToEnd()
            with _ -> ()
        )

    let setProgress (pct: int) =
        Dispatcher.UIThread.Post(fun () ->
            if not disposed && (activeCts |> Option.exists (fun c -> not c.IsCancellationRequested)) then
                progressBar.IsIndeterminate <- false
                progressBar.Value <- float (min 100 (max 0 pct)))

    let setStatus (msg: string) =
        let apply () = if not disposed then statusLabel.Text <- msg
        if Dispatcher.UIThread.CheckAccess() then apply()
        else Dispatcher.UIThread.Post(apply)

    let logAndStatus msg =
        appendLog msg
        setStatus msg
        writeToLog msg

    let isBusy () = activeCts.IsSome || confirming

    do refreshInstallState <- fun () ->
        let selected = updateRows |> Array.filter (fun row -> row.Selected && row.CanInstall)
        selectionLabel.Text <- sprintf "%d selected (%.1f MB)" selected.Length (selected |> Array.sumBy (fun row -> row.Item.SizeMB))
        installButton.IsEnabled <- not disposed && not (isBusy()) && selected.Length > 0

    let cancelActive () =
        activeCts |> Option.iter (fun cts -> cts.Cancel())
        // The worker owns this slot until it actually exits, even if it ignores cancellation.
        cancelButton.IsEnabled <- false
        statusLabel.Text <-
            if installing then "Stopping after the current Windows Update operation finishes. Completed driver changes will be kept."
            else "Cancelling — waiting for the active driver task to stop…"

    let runTask (label: string) (indeterminate: bool) (isInstallation: bool) (work: CancellationToken -> string) =
        if disposed then ()
        elif isBusy() then
            appendLog "A driver task is still running. Wait for it to finish stopping."
        else
            let cts = new CancellationTokenSource()
            activeCts <- Some cts
            installing <- isInstallation
            for button in taskButtons do button.IsEnabled <- false
            updateGrid.IsEnabled <- false
            restoreOption.IsEnabled <- false
            cancelButton.IsEnabled <- true
            cancelButton.Content <- if isInstallation then "Stop after current operation" else "Cancel"
            progressBar.IsVisible <- true
            progressBar.IsIndeterminate <- indeterminate
            progressBar.Value <- 0.0
            logAndStatus (sprintf "--> Starting: %s" label)
            Task.Run(fun () ->
                let result =
                    try Ok(work cts.Token)
                    with
                    | :? OperationCanceledException -> Error "cancelled"
                    | ex -> Error ex.Message
                Dispatcher.UIThread.Post(fun () ->
                    if activeCts |> Option.exists (fun current -> obj.ReferenceEquals(current, cts)) then
                        activeCts <- None
                        installing <- false
                        if not disposed then
                            for button in taskButtons do button.IsEnabled <- true
                            updateGrid.IsEnabled <- true
                            restoreOption.IsEnabled <- true
                            refreshInstallState()
                            cancelButton.IsEnabled <- false
                            cancelButton.Content <- "Cancel"
                            progressBar.IsVisible <- false
                            progressBar.IsIndeterminate <- false
                            if cts.IsCancellationRequested && not isInstallation then logAndStatus (label + " cancelled.")
                            else
                                match result with
                                | Ok message -> logAndStatus message
                                | Error "cancelled" -> logAndStatus (label + " cancelled.")
                                | Error message -> logAndStatus ("[ERROR] " + message)
                    cts.Dispose()
                    let close = closeAfterInstall
                    closeAfterInstall <- None
                    close |> Option.iter (fun close -> close()))
            ) |> ignore

    let publish (ct: CancellationToken) action =
        Dispatcher.UIThread.Post(fun () ->
            if not disposed && not ct.IsCancellationRequested then action())

    // ----- filtering -----

    let applyFilter () =
        let text =
            if isNull searchBox.Text then "" else searchBox.Text.Trim().ToLowerInvariant()
        let cls =
            match classFilter.SelectedItem with
            | :? string as s when s <> "All Classes" -> s.ToLowerInvariant()
            | _ -> ""
        let filtered =
            allDriverRows
            |> Array.filter (fun r ->
                let matchText =
                    text = "" ||
                    r.DeviceName.ToLowerInvariant().Contains(text) ||
                    r.Manufacturer.ToLowerInvariant().Contains(text) ||
                    r.Provider.ToLowerInvariant().Contains(text) ||
                    r.DeviceClass.ToLowerInvariant().Contains(text) ||
                    r.InfName.ToLowerInvariant().Contains(text) ||
                    r.DriverVersion.ToLowerInvariant().Contains(text)
                let matchClass =
                    cls = "" || r.DeviceClass.ToLowerInvariant().Contains(cls)
                matchText && matchClass)
        driverGrid.ItemsSource   <- filtered
        driverCountTxt.Text      <- sprintf "%d / %d drivers" filtered.Length allDriverRows.Length

    let populateClassFilter (rows: DriverRow array) =
        let classes =
            rows
            |> Array.map (fun r -> r.DeviceClass)
            |> Array.filter (fun s -> s <> "—" && s <> "")
            |> Array.distinct
            |> Array.sort
        let all = Array.append [| "All Classes" |] classes
        classClasses <- all
        classFilter.ItemsSource <- all
        classFilter.SelectedIndex <- 0

    // ----- build UI -----

    do
        // DataGrid columns
        let col (title: string) (binding: string) (width: float) =
            DataGridTextColumn(Header  = title,
                               IsReadOnly = true,
                               Binding = Binding(binding),
                               Width   = DataGridLength(width))
        driverGrid.Columns.Add(col "Device Name"    "DeviceName"    260.0)
        driverGrid.Columns.Add(col "Class"          "DeviceClass"   100.0)
        driverGrid.Columns.Add(col "Manufacturer"   "Manufacturer"  180.0)
        driverGrid.Columns.Add(col "Driver Version" "DriverVersion" 130.0)
        driverGrid.Columns.Add(col "Driver Date"    "DriverDate"    100.0)
        driverGrid.Columns.Add(col "INF File"       "InfName"       180.0)
        driverGrid.Columns.Add(col "Provider"       "Provider"      160.0)
        driverGrid.Columns.Add(col "Device status"  "Status"        110.0)
        let selectionTemplate = FuncDataTemplate<DriverUpdateRow>((fun _ _ ->
            let checkbox = CheckBox(HorizontalAlignment = HorizontalAlignment.Center)
            checkbox.Bind(CheckBox.IsCheckedProperty, Binding("Selected", Mode = BindingMode.TwoWay)) |> ignore
            checkbox.Bind(CheckBox.IsEnabledProperty, Binding("CanInstall")) |> ignore
            checkbox :> Control), true)
        updateGrid.Columns.Add(DataGridTemplateColumn(Header = "Install", CellTemplate = selectionTemplate, Width = DataGridLength(60.0)))
        updateGrid.Columns.Add(col "Device / model" "Device" 230.0)
        updateGrid.Columns.Add(col "Result" "Status" 210.0)
        updateGrid.Columns.Add(col "Installed version" "Current" 145.0)
        updateGrid.Columns.Add(col "Windows Update offer" "Title" 310.0)
        updateGrid.Columns.Add(col "Provider" "Provider" 130.0)
        updateGrid.Columns.Add(col "Driver date" "Date" 100.0)
        updateGrid.Columns.Add(col "Download" "Size" 85.0)
        updateGrid.Columns.Add(col "Restart" "Restart" 150.0)

        // Initialize class filter
        classFilter.ItemsSource   <- [| "All Classes" |]
        classFilter.SelectedIndex <- 0

        // Action buttons — solid Synthora button theme with semantic color classes
        // instead of ad hoc hex, so they get proper hover/pressed states for free.
        let mkBtn (text: string) (variant: string) =
            let btn =
                Button(Content      = text,
                       Height       = 38.0,
                       Padding      = Thickness(16.0, 0.0),
                       FontWeight   = FontWeight.SemiBold,
                       Margin       = Thickness(0.0, 0.0, 8.0, 0.0),
                       Theme        = solidButtonTheme)
            if variant <> "" then btn.Classes.Add(variant)
            btn

        let scanBtn       = mkBtn "Scan Drivers"               "Primary"
        let checkUpdBtn   = mkBtn "Check Updates"              "Question"
        let installUpdBtn = installButton
        installUpdBtn.Height <- 38.0
        installUpdBtn.Padding <- Thickness(16.0, 0.0)
        installUpdBtn.Margin <- Thickness(0.0, 0.0, 8.0, 0.0)
        installUpdBtn.Theme <- solidButtonTheme
        installUpdBtn.Classes.Add("Success")
        let backupBtn     = mkBtn "Backup Drivers"             "Warning"
        let installFolBtn = mkBtn "Install from Folder"        "Secondary"
        let restoreBtn    = mkBtn "Restore Point"              ""
        let exportCsvBtn  = mkBtn "Export CSV"                 ""
        let cancelBtn = cancelButton
        cancelBtn.Height <- 38.0
        cancelBtn.Padding <- Thickness(16.0, 0.0)
        cancelBtn.Theme <- solidButtonTheme
        cancelBtn.Classes.Add("Error")
        taskButtons.AddRange [scanBtn; checkUpdBtn; installUpdBtn; backupBtn; installFolBtn; restoreBtn; exportCsvBtn]
        let selectAll = mkBtn "Select all updates" ""
        let clear = mkBtn "Clear selection" ""
        taskButtons.AddRange [selectAll; clear]
        selectAll.Click.Add(fun _ -> if not (isBusy()) then for row in updateRows do row.Selected <- row.CanInstall)
        clear.Click.Add(fun _ -> if not (isBusy()) then for row in updateRows do row.Selected <- false)
        restoreOption.Margin <- Thickness(8.0, 0.0, 16.0, 0.0)
        installOptions.Children.AddRange [selectAll; clear; restoreOption; selectionLabel]

        // Toolbar row 1
        let toolbar1 = WrapPanel(Orientation = Orientation.Horizontal,
                                  Margin     = Thickness(0.0, 0.0, 0.0, 8.0))
        toolbar1.Children.AddRange [scanBtn; checkUpdBtn; installUpdBtn; backupBtn; installFolBtn; restoreBtn; exportCsvBtn; cancelBtn]

        // Filter row
        let filterRow =
            StackPanel(Orientation        = Orientation.Horizontal,
                       Spacing           = 10.0,
                       VerticalAlignment = VerticalAlignment.Center,
                       Margin            = Thickness(0.0, 0.0, 0.0, 12.0))
        filterRow.Children.AddRange [searchBox; classFilter; driverCountTxt]

        // Header
        let heading = StackPanel(Spacing = 4.0, Margin = Thickness(0.0, 0.0, 0.0, 20.0))
        heading.Children.Add(TextBlock(Text = "Driver Management & Updates",
                                        FontSize   = 32.0,
                                        FontWeight = FontWeight.Black))
        heading.Children.Add(TextBlock(Text = "Scan installed drivers, check for updates, backup, install, and manage restore points.",
                                        FontSize    = 14.0,
                                        Foreground  = textLow,
                                        TextWrapping = TextWrapping.Wrap))

        // Status / progress strip
        let statusStrip =
            Border(Background   = cardBg,
                   CornerRadius = CornerRadius(8.0),
                   Padding      = Thickness(16.0, 10.0),
                   Margin       = Thickness(0.0, 0.0, 0.0, 8.0))
        let statusStack = StackPanel(Spacing = 6.0)
        statusStack.Children.AddRange [progressBar; statusLabel; lastScanLabel]
        statusStrip.Child <- statusStack

        // Log box — kept as the same deliberate terminal skin as logBox above.
        logScroll.Content <- logBox
        let logBorder =
            Border(Background       = SolidColorBrush.Parse("#0a0a0a"),
                   CornerRadius     = CornerRadius(8.0),
                   BorderBrush      = SolidColorBrush.Parse("#222"),
                   BorderThickness  = Thickness(1.0),
                   Margin           = Thickness(0.0, 0.0, 0.0, 12.0),
                   MaxHeight        = 200.0)
        logBorder.Child <- logScroll

        // Root grid
        let root = Grid(RowDefinitions = RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto,*"),
                         Margin        = Thickness(30.0))
        Grid.SetRow(heading,     0); root.Children.Add(heading)
        Grid.SetRow(toolbar1,   1); root.Children.Add(toolbar1)
        Grid.SetRow(filterRow,  2); root.Children.Add(filterRow)
        Grid.SetRow(statusStrip,3); root.Children.Add(statusStrip)
        Grid.SetRow(installOptions,4); root.Children.Add(installOptions)
        Grid.SetRow(updateGrid, 5); root.Children.Add(updateGrid)
        Grid.SetRow(logBorder,  6); root.Children.Add(logBorder)
        Grid.SetRow(driverGrid, 7); root.Children.Add(driverGrid)

        this.Content <- root
        backend.LastScan() |> Option.iter showUpdateScan

        // ----- event handlers -----

        searchBox.TextChanged.Add(fun _      -> applyFilter())
        classFilter.SelectionChanged.Add(fun _ -> applyFilter())

        // Scan drivers
        scanBtn.Click.Add(fun _ ->
            runTask "Scan Installed Drivers" true false (fun ct ->
                match backend.Inventory ct with
                | DriverScanResult.Failed message -> failwith message
                | DriverScanResult.Cancelled -> raise (OperationCanceledException(ct))
                | DriverScanResult.Success drivers ->
                    let rows = drivers |> List.map DriverRow |> List.toArray
                    publish ct (fun () ->
                        allDriverRows <- rows
                        populateClassFilter rows
                        applyFilter())
                    sprintf "Scan complete — found %d installed drivers." rows.Length
            )
        )

        // Check driver updates
        checkUpdBtn.Click.Add(fun _ ->
            runTask "Check Driver Updates" true false (fun ct ->
                match backend.Updates ct with
                | DriverScanResult.Failed message -> failwith message
                | DriverScanResult.Cancelled -> raise (OperationCanceledException(ct))
                | DriverScanResult.Success scan ->
                    publish ct (fun () -> showUpdateScan scan)
                    for update in scan.Updates do
                        ct.ThrowIfCancellationRequested()
                        appendLog (sprintf "%s | %s | %s | %.1f MB | %s"
                            update.Title update.Provider update.DriverDate update.SizeMB
                            (if update.MayRequireReboot then "Restart may be required" else "Restart not expected"))
                        appendLog (sprintf "  Update ID: %s, revision %d; hardware ID: %s"
                            update.UpdateId update.Revision update.HardwareId)
                        match update.CurrentDriver with
                        | Some driver -> appendLog (sprintf "  Installed: %s (%s), version %s" driver.DeviceName driver.InstanceId driver.DriverVersion)
                        | None -> appendLog "  Installed version: unknown or multiple matching devices."
                    DriverScanner.summary scan
            )
        )

        // Install exactly the selected identities/revisions, using StellarHub's WUA flow.
        installUpdBtn.Click.Add(fun _ ->
            if not (isBusy()) && not disposed then
                let selected = updateRows |> Array.filter (fun row -> row.Selected && row.CanInstall) |> Array.map (fun row -> row.Item) |> Array.toList
                if selected.IsEmpty then logAndStatus "Select the driver updates you want to install first."
                else
                    let restore = restoreOption.IsChecked.GetValueOrDefault()
                    let names = selected |> List.truncate 12 |> List.map (fun u -> "• " + u.Title) |> String.concat "\n"
                    let prompt =
                        sprintf "Install these %d selected driver update(s) from Windows Update?\n\n%s%s\n\nWindows Update software license terms for the selected updates will be accepted. A restart may be required.\n\n%s"
                            selected.Length names (if selected.Length > 12 then "\n…" else "")
                            (if restore then "A new restore point must be created before installation. If that fails, no drivers will be installed." else "No restore point will be created.")
                    confirming <- true
                    for button in taskButtons do button.IsEnabled <- false
                    updateGrid.IsEnabled <- false
                    restoreOption.IsEnabled <- false
                    task {
                        let! approved = task {
                            try
                                try return! confirmInstall prompt
                                with ex -> logAndStatus ("[ERROR] Could not confirm driver installation: " + ex.Message); return false
                            finally
                                confirming <- false
                                if not disposed then
                                    for button in taskButtons do button.IsEnabled <- true
                                    updateGrid.IsEnabled <- true
                                    restoreOption.IsEnabled <- true
                                    refreshInstallState() }
                        if approved && not disposed then
                            runTask "Install Selected Driver Updates" true true (fun ct ->
                                let report = installBackend selected restore ct (fun event ->
                                    let detail = event.Result |> Option.map (fun r -> sprintf " [%A, code %d%s]" r.State r.Code (if r.HResult = "" then "" else ", 0x" + r.HResult)) |> Option.defaultValue ""
                                    appendLog (sprintf "%s%s%s" (if event.UpdateId = "" then "" else event.UpdateId + ": ") event.Message detail)
                                    Dispatcher.UIThread.Post(fun () ->
                                        if not disposed then
                                            if not ct.IsCancellationRequested then statusLabel.Text <- event.Message
                                            for row in updateRows do
                                                if String.Equals(row.Item.UpdateId, event.UpdateId, StringComparison.OrdinalIgnoreCase) && row.Item.Revision = event.Revision then
                                                    match event.Result with
                                                    | Some result -> row.ApplyResult(result)
                                                    | None -> row.Status <- event.Message))
                                Dispatcher.UIThread.Post(fun () ->
                                    if not disposed then
                                        for result in report.Results do
                                            updateRows |> Array.tryFind (fun row -> String.Equals(row.Item.UpdateId, result.UpdateId, StringComparison.OrdinalIgnoreCase) && row.Item.Revision = result.Revision)
                                            |> Option.iter (fun row -> row.ApplyResult(result))
                                            appendLog (sprintf "%s: %A — %s%s" result.UpdateId result.State result.Message (if result.HResult = "" then "" else " (0x" + result.HResult + ")"))
                                        lastScanLabel.Text <- "Installation attempt finished. Scan again to refresh available updates.")
                                DriverInstaller.summary report)
                    } |> ignore
        )

        // Backup drivers
        backupBtn.Click.Add(fun _ ->
            Dispatcher.UIThread.InvokeAsync(fun () ->
                task {
                    let opts = FolderPickerOpenOptions(Title = "Select folder to save driver backup", AllowMultiple = false)
                    let! folders = storage.OpenFolderPickerAsync(opts)
                    if folders.Count > 0 then
                        let dest = folders.[0].Path.LocalPath.TrimEnd('\\', '/')
                        runTask "Backup Drivers" false false (fun ct ->
                            DriverService.backupDrivers dest ct appendLog setProgress
                            "Driver backup task finished. See log for its results.")
                } |> ignore
            ) |> ignore
        )

        // Install from folder
        installFolBtn.Click.Add(fun _ ->
            Dispatcher.UIThread.InvokeAsync(fun () ->
                task {
                    let opts = FolderPickerOpenOptions(Title = "Select folder containing .inf driver files", AllowMultiple = false)
                    let! folders = storage.OpenFolderPickerAsync(opts)
                    if folders.Count > 0 then
                        let src = folders.[0].Path.LocalPath.TrimEnd('\\', '/')
                        runTask "Install Drivers from Folder" false false (fun ct ->
                            DriverService.installDriversFromFolder src ct appendLog setProgress
                            "Driver installation task finished. See log for its results.")
                } |> ignore
            ) |> ignore
        )

        // Restore point
        restoreBtn.Click.Add(fun _ ->
            runTask "Create Restore Point" false false (fun ct ->
                ct.ThrowIfCancellationRequested()
                let ok = DriverService.createRestorePoint "Plumbyr Driver Updater Restore Point" appendLog
                if ok then "Restore point created successfully."
                else "Failed to create restore point. See log."
            )
        )

        // Export CSV
        exportCsvBtn.Click.Add(fun _ ->
            if allDriverRows.Length = 0 then
                logAndStatus "No driver data to export. Run 'Scan Drivers' first."
            else
                Dispatcher.UIThread.InvokeAsync(fun () ->
                    task {
                        let opts =
                            FilePickerSaveOptions(
                                Title             = "Export Driver Inventory",
                                SuggestedFileName = sprintf "drivers_%s.csv" (DateTime.Now.ToString("yyyyMMdd_HHmmss")),
                                DefaultExtension  = "csv",
                                FileTypeChoices   = [| FilePickerFileType("CSV", Patterns = [| "*.csv" |]) |])
                        let! file = storage.SaveFilePickerAsync(opts)
                        if not (isNull file) then
                            try
                                let drivers = allDriverRows |> Array.map (fun r -> r.Item) |> Array.toList
                                DriverService.exportDriversCsv drivers file.Path.LocalPath
                                logAndStatus (sprintf "Exported %d drivers to: %s" drivers.Length file.Name)
                            with ex ->
                                logAndStatus (sprintf "[ERROR] Export failed: %s" ex.Message)
                    } |> ignore
                ) |> ignore
        )

        // Cancel
        cancelBtn.Click.Add(fun _ ->
            if isBusy() then
                cancelActive()
                appendLog "Cancellation requested; waiting for the active task to stop."
            else
                logAndStatus "No active task to cancel."
        )

    member _.IsBusy = isBusy()

    member _.TryClose(close: unit -> unit) =
        if installing then
            closeAfterInstall <- Some close
            cancelActive()
            false
        else true

    interface IDisposable with
        member _.Dispose() =
            disposed <- true
            activeCts |> Option.iter (fun cts -> cts.Cancel())
