namespace WindowsCleaner

open System
open System.IO
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
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
    member _.Item          = item

// -----------------------------------------------------------------------
// DriversView UserControl
// -----------------------------------------------------------------------

type DriversView(writeToLog: string -> unit, storage: IStorageProvider) as this =
    inherit UserControl()

    // Cancellation for long-running background tasks
    let mutable activeCts : CancellationTokenSource option = None
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
        TextBlock(Text = "Ready. Use the toolbar to scan or update drivers.",
                  Foreground = textLow, FontSize = 13.0,
                  TextWrapping = TextWrapping.Wrap)

    let progressBar =
        ProgressBar(Minimum = 0.0, Maximum = 100.0, Value = 0.0,
                    Height = 6.0, IsVisible = false)

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
                let prev = if isNull logBox.Text then "" else logBox.Text
                let ts   = DateTime.Now.ToString("HH:mm:ss")
                logBox.Text <- prev + sprintf "[%s] %s\n" ts msg
                logScroll.ScrollToEnd()
            with _ -> ()
        )

    let setProgress (pct: int) =
        Dispatcher.UIThread.Post(fun () ->
            progressBar.Value    <- float (min 100 (max 0 pct))
            progressBar.IsVisible <- pct > 0 && pct < 100)

    let setStatus (msg: string) =
        Dispatcher.UIThread.Post(fun () -> statusLabel.Text <- msg)

    let logAndStatus msg =
        appendLog msg
        setStatus msg
        writeToLog msg

    let isBusy () = activeCts.IsSome

    let cancelActive () =
        activeCts |> Option.iter (fun cts -> cts.Cancel())
        activeCts <- None
        Dispatcher.UIThread.Post(fun () ->
            progressBar.Value    <- 0.0
            progressBar.IsVisible <- false)

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
                Dispatcher.UIThread.Post(fun () ->
                    progressBar.Value    <- 100.0
                    progressBar.IsVisible <- false)
                logAndStatus (sprintf "=== Completed: %s ===" label)
            ) |> ignore

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
        Dispatcher.UIThread.Post(fun () ->
            classFilter.ItemsSource  <- all
            classFilter.SelectedIndex <- 0)

    // ----- build UI -----

    do
        // DataGrid columns
        let col (title: string) (binding: string) (width: float) =
            DataGridTextColumn(Header  = title,
                               Binding = Binding(binding),
                               Width   = DataGridLength(width))
        driverGrid.Columns.Add(col "Device Name"    "DeviceName"    260.0)
        driverGrid.Columns.Add(col "Class"          "DeviceClass"   100.0)
        driverGrid.Columns.Add(col "Manufacturer"   "Manufacturer"  180.0)
        driverGrid.Columns.Add(col "Driver Version" "DriverVersion" 130.0)
        driverGrid.Columns.Add(col "Driver Date"    "DriverDate"    100.0)
        driverGrid.Columns.Add(col "INF File"       "InfName"       180.0)

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
        let installUpdBtn = mkBtn "Install via Windows Update" "Success"
        let backupBtn     = mkBtn "Backup Drivers"             "Warning"
        let installFolBtn = mkBtn "Install from Folder"        "Secondary"
        let restoreBtn    = mkBtn "Restore Point"              ""
        let exportCsvBtn  = mkBtn "Export CSV"                 ""
        let cancelBtn     = mkBtn "Cancel"                     "Error"

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
        statusStack.Children.AddRange [progressBar; statusLabel]
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
        let root = Grid(RowDefinitions = RowDefinitions("Auto,Auto,Auto,Auto,Auto,*"),
                         Margin        = Thickness(30.0))
        Grid.SetRow(heading,     0); root.Children.Add(heading)
        Grid.SetRow(toolbar1,   1); root.Children.Add(toolbar1)
        Grid.SetRow(filterRow,  2); root.Children.Add(filterRow)
        Grid.SetRow(statusStrip,3); root.Children.Add(statusStrip)
        Grid.SetRow(logBorder,  4); root.Children.Add(logBorder)
        Grid.SetRow(driverGrid, 5); root.Children.Add(driverGrid)

        this.Content <- root

        // ----- event handlers -----

        searchBox.TextChanged.Add(fun _      -> applyFilter())
        classFilter.SelectionChanged.Add(fun _ -> applyFilter())

        // Scan drivers
        scanBtn.Click.Add(fun _ ->
            runTask "Scan Installed Drivers" (fun ct ->
                let drivers = DriverService.scanDrivers()
                let rows    = drivers |> List.map DriverRow |> List.toArray
                allDriverRows <- rows
                populateClassFilter rows
                Dispatcher.UIThread.Post(fun () -> applyFilter())
                setProgress 100
                logAndStatus (sprintf "Scan complete — found %d installed drivers." rows.Length)
            )
        )

        // Check driver updates
        checkUpdBtn.Click.Add(fun _ ->
            runTask "Check Driver Updates" (fun ct ->
                let updates = DriverService.checkDriverUpdates ct appendLog
                if updates.IsEmpty then
                    setStatus "No driver updates found. Your system is up to date."
                else
                    setStatus (sprintf "%d driver update(s) found. See log for details." updates.Length)
                setProgress 100
            )
        )

        // Install via Windows Update
        installUpdBtn.Click.Add(fun _ ->
            async {
                let! result =
                    AlertDialog.ShowAsync(
                        "This will search Windows Update for driver updates and install them. Your system may need to restart.",
                        "Confirm Driver Install",
                        DialogButton.YesNo,
                        IconType.Warning) |> Async.AwaitTask
                if result = DialogResult.Yes then
                    runTask "Windows Update Driver Install" (fun ct ->
                        DriverService.runWindowsUpdate ct appendLog setProgress
                    )
            } |> Async.StartImmediate
        )

        // Backup drivers
        backupBtn.Click.Add(fun _ ->
            Dispatcher.UIThread.InvokeAsync(fun () ->
                task {
                    let opts = FolderPickerOpenOptions(Title = "Select folder to save driver backup", AllowMultiple = false)
                    let! folders = storage.OpenFolderPickerAsync(opts)
                    if folders.Count > 0 then
                        let dest = folders.[0].Path.LocalPath.TrimEnd('\\', '/')
                        runTask "Backup Drivers" (fun ct ->
                            DriverService.backupDrivers dest ct appendLog setProgress)
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
                        runTask "Install Drivers from Folder" (fun ct ->
                            DriverService.installDriversFromFolder src ct appendLog setProgress)
                } |> ignore
            ) |> ignore
        )

        // Restore point
        restoreBtn.Click.Add(fun _ ->
            runTask "Create Restore Point" (fun ct ->
                let ok = DriverService.createRestorePoint "Plumbyr Driver Updater Restore Point" appendLog
                if ok then setStatus "Restore point created successfully."
                else      setStatus "Failed to create restore point. See log."
                setProgress 100
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
                logAndStatus "[CANCELLED] Task cancelled by user."
            else
                logAndStatus "No active task to cancel."
        )
