namespace WindowsCleaner

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Layout
open Avalonia.Threading
open System
open System.IO
open System.Threading
open System.Diagnostics
open System.Collections.Generic
open Avalonia.Platform.Storage
open AvaloniaEdit
open AvaloniaEdit.Editing
open Serilog
open Serilog.Core
open Serilog.Events
open ByteSizeLib
open Synthora
open Synthora.Controls
open Synthora.Overlays

type TerminalSink(onLog: string -> unit) =
    interface ILogEventSink with
        member this.Emit(le) = onLog (le.RenderMessage())

module AppLogging =
    let private logPath () =
        Path.Combine(AppFiles.dataDirectory(), "plumbyr_log.txt")

    let configure (uiLog: (string -> unit) option) =
        let config =
            LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    logPath(),
                    rollingInterval = RollingInterval.Day,
                    retainedFileCountLimit = Nullable<int>(14),
                    outputTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                .WriteTo.Debug(outputTemplate = "[{Level:u3}] {Message:lj}{NewLine}{Exception}")

        let config =
            match uiLog with
            | Some writer -> config.WriteTo.Sink(TerminalSink(writer))
            | None -> config

        Log.Logger <- config.CreateLogger()

// Identifies which section of the app a TreeMenu nav entry switches to.
type NavKey =
    | NavDashboard
    | NavTerminal
    | NavCleaner of CleanCategory
    | NavBrowsers
    | NavDrivers

type MainWindow() as this =
    inherit Window()

    let scanEngine = ScanEngine()
    let targetsList = StackPanel(Spacing = 8.0)
    let selectedGroups = HashSet<string>()
    let mutable currentScanResults: ScanTargetResult list = []
    let totalFreedTxt = TextBlock(FontSize = 18.0, FontWeight = FontWeight.SemiBold)
    let totalFilesTxt = TextBlock(FontSize = 14.0)
    let foundItemsTxt = TextBlock(FontSize = 14.0)
    let selectionTxt = TextBlock(FontSize = 14.0)
    let actionStatusTxt = TextBlock(FontSize = 13.0, TextWrapping = TextWrapping.Wrap)

    let dashboardView = ContentControl()
    let terminalView = ContentControl()
    let cleanerView = ContentControl()

    // SVG Icons
    let DASH_SVG = "M10 20v-6h4v6h5v-8h3L12 3 2 12h3v8z"
    let TERM_SVG = "M20,19V7H4V19H20M20,5A2,2 0 0,1 22,7V19A2,2 0 0,1 20,21H4A2,2 0 0,1 2,19V7A2,2 0 0,1 4,5H20M13,17V15H18V17H13M9.58,13L5.57,9H8.4L11.7,12.3C12.09,12.69 12.09,13.32 11.7,13.71L8.4,17H5.57L9.58,13Z"
    let CLEAN_SVG = "M19.36 2.72L20.78 4.14L15.06 9.85C16.13 11.39 16.28 13.24 15.38 14.44L9.06 8.12C10.26 7.22 12.11 7.37 13.65 8.44L19.36 2.72M5.93 17.57C3.92 15.56 2.69 13.16 2.35 10.92L7.23 8.83L14.67 16.27L12.58 21.15C10.34 20.81 7.94 19.58 5.93 17.57Z"
    let HIST_SVG = "M13 3c-4.97 0-9 4.03-9 9H1l3.89 3.89.07.14L9 12H6c0-3.87 3.13-7 7-7s7 3.13 7 7-3.13 7-7 7c-1.93 0-3.68-.79-4.94-2.06l-1.42 1.42C8.27 19.99 10.51 21 13 21c4.97 0 9-4.03 9-9s-4.03-9-9-9zm-1 5v5l4.28 2.54.72-1.21-3.5-2.08V8H12z"
    let SYS_SVG = "M20 18c1.1 0 1.99-.9 1.99-2L22 6c0-1.1-.9-2-2-2H4c-1.1 0-2 .9-2 2v10c0 1.1.9 2 2 2H0v2h24v-2h-4zM4 6h16v10H4V6z"
    let BROWSER_SVG = "M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm-1 17.93c-3.95-.49-7-3.85-7-7.93 0-.62.08-1.21.21-1.79L9 15v1c0 1.1.9 2 2 2v1.93zm6.9-2.54c-.26-.81-1-1.39-1.9-1.39h-1v-3c0-.55-.45-1-1-1H8v-2h2c.55 0 1-.45 1-1V7h2c1.1 0 2-.9 2-2v-.41c2.93 1.19 5 4.06 5 7.41 0 2.08-.8 3.97-2.1 5.39z"
    let APPS_SVG = "M4 8h4V4H4v4zm6 12h4v-4h-4v4zm-6 0h4v-4H4v4zm0-6h4v-4H4v4zm6 0h4v-4h-4v4zm6-10v4h4V4h-4zm-6 4h4V4h-4v4zm6 6h4v-4h-4v4zm0 6h4v-4h-4v4z"
    let GAME_SVG = "M21 6H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm-10 7H8v3H6v-3H3v-2h3V8h2v3h3v2zm4.5 2c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zm4-3c-.83 0-1.5-.67-1.5-1.5S18.67 9 19.5 9s1.5.67 1.5 1.5-.67 1.5-1.5 1.5z"
    let DRIVER_SVG = "M22.7 19l-9.1-9.1c.9-2.3.4-5-1.5-6.9-2-2-5-2.4-7.4-1.3L9 6 6 9 1.6 4.7C.4 7.1.9 10.1 2.9 12.1c1.9 1.9 4.6 2.4 6.9 1.5l9.1 9.1c.4.4 1 .4 1.4 0l2.3-2.3c.5-.4.5-1.1.1-1.4z"

    let mutable currentCategory: CleanCategory option = None
    let categoryTitle = TextBlock(FontSize = 28.0, FontWeight = FontWeight.Black)

    let groupKey (target: CleanTarget) =
        sprintf "%A|%s|%s" target.Category target.SourceFile (target.Name.Trim().ToLowerInvariant())

    let formatBytes bytes =
        ByteSize.FromBytes(float bytes).ToString()

    let targetsForCurrentCategory () =
        match currentCategory with
        | Some cat -> CleanTargets.getAllTargets() |> List.filter (fun t -> t.Category = cat)
        | None -> CleanTargets.getAllTargets()

    let selectedTargetsForCurrentCategory () =
        targetsForCurrentCategory()
        |> List.filter (fun t -> selectedGroups.Contains(groupKey t))

    do
        this.Title <- "Plumbyr"
        this.RequestedThemeVariant <- Avalonia.Styling.ThemeVariant.Dark
        let iconPath = AppFiles.get "Assets/icon.png"
        try
            if File.Exists(iconPath) then
                this.Icon <- WindowIcon(iconPath)
        with ex ->
            Log.Warning(ex, "Unable to load window icon from {IconPath}", iconPath)
        this.Width <- 1200.0; this.Height <- 850.0
        this.WindowStartupLocation <- WindowStartupLocation.CenterScreen

        // Resolve Synthora theme resources once (the app always runs in Dark mode,
        // so a one-time static lookup is sufficient - no need for reactive DynamicResource
        // bindings on every element).
        let brush (key: string) = Application.Current.FindResource(key) :?> IBrush
        // Plumbyr's near-black backdrop is a deliberate brand choice (matches the terminal
        // panels), kept explicit rather than sourced from Synthora's (lighter) gray palette.
        let windowBg   = SolidColorBrush.Parse("#080808") :> IBrush
        let cardBg     = SolidColorBrush.Parse("#121212") :> IBrush
        let textLow    = brush "ThemeForegroundLowBrush"
        let primary    = brush "PrimaryBrush"
        let success    = brush "SuccessBrush"
        let warning    = brush "WarningBrush"
        let solidButtonTheme = Application.Current.FindResource("SolidButtonTheme") :?> Avalonia.Styling.ControlTheme
        let borderlessButtonTheme = Application.Current.FindResource("BorderlessButtonTheme") :?> Avalonia.Styling.ControlTheme

        this.Background <- windowBg
        this.ExtendClientAreaToDecorationsHint <- false
        this.WindowDecorations <- WindowDecorations.Full
        this.TransparencyLevelHint <- [| WindowTransparencyLevel.None |]
        this.TransparencyBackgroundFallback <- Brushes.Black

        // Initialize History
        let historyPath = Path.Combine(AppFiles.dataDirectory(), "plumbyr_history.db")
        CleaningHistory.GetInstance().Initialize(historyPath)

        // Pane header: logo, title, subtitle
        let header = StackPanel(Margin = Thickness(20.0, 40.0, 20.0, 30.0))
        try
            if File.Exists(iconPath) then
                header.Children.Add(Image(Source = new Bitmap(iconPath), Width = 82.0, Height = 82.0, HorizontalAlignment = HorizontalAlignment.Center, Margin = Thickness(0.0, 0.0, 0.0, 12.0)))
        with ex ->
            Log.Warning(ex, "Unable to load header icon from {IconPath}", iconPath)
        header.Children.Add(TextBlock(Text = "PLUMBYR", FontSize = 24.0, FontWeight = FontWeight.Black, HorizontalAlignment = HorizontalAlignment.Center))
        let subtitle = TextBlock(Text = "Plumbing Linked Universal Maintenance & Binary Yield Reclaimer", FontSize = 10.0, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 210.0)
        subtitle.Foreground <- textLow
        header.Children.Add(subtitle)

        let makeNavHeader (text: string) (svgData: string) =
            let stack = StackPanel(Orientation = Orientation.Horizontal, Spacing = 12.0)
            stack.Children.Add(PathIcon(Data = Geometry.Parse(svgData), Width = 18.0, Height = 18.0))
            stack.Children.Add(TextBlock(Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold))
            stack :> obj

        let makeNavItem (text: string) (svgData: string) (content: Control) (key: NavKey) =
            let item = TreeMenuItem(Content = content)
            item.Header <- makeNavHeader text svgData
            item.Tag <- box key
            item

        // Dashboard View Setup
        let dashGrid = Grid(RowDefinitions = RowDefinitions("Auto,Auto,*"), Margin = Thickness(30.0))
        let welcomeHeader = StackPanel(Margin = Thickness(0.0, 0.0, 0.0, 30.0))
        welcomeHeader.Children.Add(TextBlock(Text = "System Overview", FontSize = 42.0, FontWeight = FontWeight.Black))
        let welcomeSub = TextBlock(Text = "Real-time hardware status and optimization.", FontSize = 16.0)
        welcomeSub.Foreground <- textLow
        welcomeHeader.Children.Add(welcomeSub)
        Grid.SetRow(welcomeHeader, 0)

        let statsRow = Grid(ColumnDefinitions = ColumnDefinitions("*,*,*"), Margin = Thickness(0.0, 0.0, 0.0, 30.0))
        let createStatCard title value (color: IBrush) =
            let card = Border(Background = cardBg, CornerRadius = CornerRadius(12.0), Margin = Thickness(5.0), Padding = Thickness(25.0))
            let stack = StackPanel(Spacing = 8.0)
            let titleTxt = TextBlock(Text = title, FontSize = 12.0, FontWeight = FontWeight.Bold)
            titleTxt.Foreground <- textLow
            stack.Children.Add(titleTxt)
            let valTxt = TextBlock(Text = value, FontSize = 28.0, FontWeight = FontWeight.Black, Foreground = color)
            stack.Children.Add(valTxt)
            card.Child <- stack
            (card, valTxt)

        let (reclaimCard, reclaimVal: TextBlock) = createStatCard "DISK RECLAIMED" "0.00 GB" primary
        let (filesCard, filesVal: TextBlock) = createStatCard "TOTAL PURGED" "0" success
        let (healthCard, healthVal: TextBlock) = createStatCard "SYSTEM STATUS" "READY" warning

        Grid.SetColumn(reclaimCard, 0); statsRow.Children.Add(reclaimCard)
        Grid.SetColumn(filesCard, 1); statsRow.Children.Add(filesCard)
        Grid.SetColumn(healthCard, 2); statsRow.Children.Add(healthCard)
        Grid.SetRow(statsRow, 1)

        let infoGrid = Grid(ColumnDefinitions = ColumnDefinitions("*,*"))
        let leftInfo = Border(Background = cardBg, CornerRadius = CornerRadius(12.0), Margin = Thickness(0.0, 0.0, 10.0, 0.0), Padding = Thickness(30.0))
        let hardwareStack = StackPanel(Spacing = 15.0)
        let hardwareTitle = TextBlock(Text = "HARDWARE SPECIFICATIONS", FontSize = 14.0, FontWeight = FontWeight.Bold, Margin = Thickness(0.0, 0.0, 0.0, 15.0))
        hardwareTitle.Foreground <- textLow
        hardwareStack.Children.Add(hardwareTitle)

        let createInfoLine label value =
            let grid = Grid(ColumnDefinitions = ColumnDefinitions("140,*"))
            let lbl = TextBlock(Text = label, FontSize = 14.0)
            lbl.Foreground <- textLow
            let valTxt = TextBlock(Text = value, FontWeight = FontWeight.SemiBold, FontSize = 14.0)
            Grid.SetColumn(lbl, 0); grid.Children.Add(lbl)
            Grid.SetColumn(valTxt, 1); grid.Children.Add(valTxt)
            (grid, valTxt)

        let (osGrid, osVal) = createInfoLine "OS Version" "Windows 11"
        let (cpuGrid, cpuVal) = createInfoLine "CPU" "Loading..."
        let (ramGrid, ramVal) = createInfoLine "Memory" "Loading..."
        let (gpuGrid, gpuVal) = createInfoLine "GPU" "Loading..."

        let dumpBtn = Button(Content = "GENERATE FULL DXDIAG DUMP", HorizontalAlignment = HorizontalAlignment.Stretch, Height = 60.0, Margin = Thickness(0.0, 30.0, 0.0, 0.0))

        hardwareStack.Children.AddRange [osGrid; cpuGrid; ramGrid; gpuGrid; dumpBtn]
        leftInfo.Child <- hardwareStack
        Grid.SetColumn(leftInfo, 0)

        let rightInfo = Border(Background = cardBg, CornerRadius = CornerRadius(12.0), Margin = Thickness(10.0, 0.0, 0.0, 0.0), Padding = Thickness(30.0))
        let healthStack = StackPanel(Spacing = 20.0, VerticalAlignment = VerticalAlignment.Center)
        let graphTitle = TextBlock(Text = "OPTIMIZATION GRAPH", FontSize = 14.0, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center)
        graphTitle.Foreground <- textLow
        healthStack.Children.Add(graphTitle)
        let healthCircle = ProgressBar(Width = 220.0, Height = 12.0, Minimum = 0.0, Maximum = 100.0, Value = 0.0)
        let healthValueTxt: TextBlock = TextBlock(Text = "0%", FontSize = 32.0, FontWeight = FontWeight.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
        let circleContainer = StackPanel(Spacing = 12.0, HorizontalAlignment = HorizontalAlignment.Center)
        circleContainer.Children.AddRange [healthValueTxt; healthCircle]
        healthStack.Children.Add(circleContainer)
        healthStack.Children.Add(TextBlock(Text = "System Health Score", FontSize = 16.0, HorizontalAlignment = HorizontalAlignment.Center))
        rightInfo.Child <- healthStack
        Grid.SetColumn(rightInfo, 1)

        Grid.SetRow(infoGrid, 2)
        dashGrid.Children.AddRange [welcomeHeader; statsRow; infoGrid]
        dashboardView.Content <- dashGrid

        // Terminal View
        let logGrid = Grid(RowDefinitions = RowDefinitions("Auto,Auto,*,Auto"), Margin = Thickness(30.0))
        let logHeader = Border(Background = cardBg, CornerRadius = CornerRadius(8.0), Padding = Thickness(20.0), Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        let logHeaderStack = StackPanel(Spacing = 6.0)
        let titleRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = 12.0)
        titleRow.Children.Add(TextBlock(Text = "Command Center & Maintenance Console", FontWeight = FontWeight.Bold, FontSize = 18.0))
        let adminBadge = Tag(Content = "ELEVATED ADMIN", TagType = TagType.Success, IsSolid = true, VerticalAlignment = VerticalAlignment.Center)
        titleRow.Children.Add(adminBadge)
        logHeaderStack.Children.Add(titleRow)
        let consoleDesc = TextBlock(Text = "Execute elevated Windows repairs, native PowerShell cmdlets, network resets, and PC maintenance routines.", FontSize = 13.0, TextWrapping = TextWrapping.Wrap)
        consoleDesc.Foreground <- textLow
        logHeaderStack.Children.Add(consoleDesc)
        logHeader.Child <- logHeaderStack
        Grid.SetRow(logHeader, 0)

        let terminalActions = StackPanel(Orientation = Orientation.Horizontal, Spacing = 10.0, Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        let createTerminalAction text =
            Button(Content = text, Height = 38.0, MinWidth = 118.0, Padding = Thickness(14.0, 0.0))
        let helpQuickBtn = createTerminalAction "📖 Help Guide"
        let flushDnsQuickBtn = createTerminalAction "⚡ Flush DNS"
        let dismHealthQuickBtn = createTerminalAction "🛡️ DISM Health"
        let topProcQuickBtn = createTerminalAction "🔍 Top Processes"
        let batteryQuickBtn = createTerminalAction "🔋 Battery Report"
        let clearLogBtn = createTerminalAction "🧹 Clear"
        terminalActions.Children.AddRange [helpQuickBtn; flushDnsQuickBtn; dismHealthQuickBtn; topProcQuickBtn; batteryQuickBtn; clearLogBtn]
        Grid.SetRow(terminalActions, 1)

        let logEditor = TextEditor(Background = SolidColorBrush.Parse("#080808"), Foreground = Brushes.LimeGreen, FontSize = 13.0, FontFamily = FontFamily("Consolas"), IsReadOnly = true, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, WordWrap = true)
        logEditor.Options.EnableRectangularSelection <- true
        logEditor.Options.EnableTextDragDrop <- true

        Grid.SetRow(logEditor, 2)

        let inputRow = Grid(ColumnDefinitions = ColumnDefinitions("Auto,*,110"), Margin = Thickness(0.0, 15.0, 0.0, 0.0))
        let promptBadge = Border(
            Background = SolidColorBrush.Parse("#1a1a1a"),
            BorderBrush = SolidColorBrush.Parse("#333"),
            BorderThickness = Thickness(1.0, 1.0, 0.0, 1.0),
            CornerRadius = CornerRadius(8.0, 0.0, 0.0, 8.0),
            Padding = Thickness(12.0, 0.0),
            Height = 45.0)
        let promptTxt = TextBlock(
            Text = "ADMIN >",
            FontFamily = FontFamily("Consolas"),
            FontWeight = FontWeight.Bold,
            Foreground = Brushes.LimeGreen,
            VerticalAlignment = VerticalAlignment.Center)
        promptBadge.Child <- promptTxt
        Grid.SetColumn(promptBadge, 0)

        let inputField = TextBox(PlaceholderText = "Type a command (e.g. 'help', 'flushdns', 'ps Get-Process', 'sfc')...", Height = 45.0, VerticalContentAlignment = VerticalAlignment.Center, Background = SolidColorBrush.Parse("#121212"), Foreground = Brushes.White, BorderThickness = Thickness(0.0, 1.0, 1.0, 1.0), CornerRadius = CornerRadius(0.0, 8.0, 8.0, 0.0), BorderBrush = SolidColorBrush.Parse("#333"))
        Grid.SetColumn(inputField, 1)
        let runCommandBtn = Button(Content = "Run", Height = 45.0, Margin = Thickness(10.0, 0.0, 0.0, 0.0), FontWeight = FontWeight.Bold, Theme = solidButtonTheme)
        runCommandBtn.Classes.Add("Primary")
        Grid.SetColumn(runCommandBtn, 2)
        inputRow.Children.AddRange [promptBadge; inputField; runCommandBtn]
        Grid.SetRow(inputRow, 3)

        logGrid.Children.AddRange [logHeader; terminalActions; logEditor; inputRow]
        terminalView.Content <- logGrid

        // Cleaner View
        let cleanerMain = Grid(RowDefinitions = RowDefinitions("Auto,*,Auto"), Margin = Thickness(30.0))
        let headerGrid = Grid(ColumnDefinitions = ColumnDefinitions("*,Auto"), Margin = Thickness(0.0, 0.0, 0.0, 22.0))
        categoryTitle.Margin <- Thickness(0.0, 0.0, 0.0, 4.0); categoryTitle.FontSize <- 32.0
        let titleStack = StackPanel(Spacing = 4.0)
        titleStack.Children.Add(categoryTitle)
        let cleanerSub = TextBlock(Text = "Select what to analyze, review real sizes, then clean only the checked groups.", FontSize = 14.0)
        cleanerSub.Foreground <- textLow
        titleStack.Children.Add(cleanerSub)
        Grid.SetColumn(titleStack, 0)
        let headerStats = StackPanel(Orientation = Orientation.Horizontal, Spacing = 18.0, VerticalAlignment = VerticalAlignment.Center)
        foundItemsTxt.Text <- "FOUND: 0 ITEMS"
        foundItemsTxt.Foreground <- textLow
        selectionTxt.Text <- "SELECTED: 0"
        selectionTxt.Foreground <- textLow
        headerStats.Children.AddRange [foundItemsTxt; selectionTxt]
        Grid.SetColumn(headerStats, 1)
        headerGrid.Children.AddRange [titleStack; headerStats]
        Grid.SetRow(headerGrid, 0)

        let cleanGrid = Grid(ColumnDefinitions = ColumnDefinitions("*,320"))
        let listContainer = Grid(RowDefinitions = RowDefinitions("Auto,*"))
        let listHeader = Grid(ColumnDefinitions = ColumnDefinitions("*,110,110"), Margin = Thickness(0.0, 0.0, 8.0, 8.0))
        let cleanerColTxt = TextBlock(Text = "Cleaner", FontWeight = FontWeight.Bold, FontSize = 12.0)
        cleanerColTxt.Foreground <- textLow
        listHeader.Children.Add(cleanerColTxt)
        let sizeHeader = TextBlock(Text = "Size", FontWeight = FontWeight.Bold, FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Right)
        sizeHeader.Foreground <- textLow
        Grid.SetColumn(sizeHeader, 1); listHeader.Children.Add(sizeHeader)
        let filesHeader = TextBlock(Text = "Files", FontWeight = FontWeight.Bold, FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Right)
        filesHeader.Foreground <- textLow
        Grid.SetColumn(filesHeader, 2); listHeader.Children.Add(filesHeader)
        Grid.SetRow(listHeader, 0)
        let scroll = ScrollViewer(Content = targetsList)
        Grid.SetRow(scroll, 1)
        listContainer.Children.AddRange [listHeader; scroll]
        Grid.SetColumn(listContainer, 0)

        let actionPanel = StackPanel(Spacing = 14.0, Margin = Thickness(24.0, 0.0, 0.0, 0.0))
        let createPillBtn text (variant: string) =
            let btn = Button(Content = text, Height = 52.0, HorizontalAlignment = HorizontalAlignment.Stretch, FontWeight = FontWeight.Bold, FontSize = 15.0, Theme = solidButtonTheme)
            if variant <> "" then btn.Classes.Add(variant)
            btn
        let scanBtn = createPillBtn "Analyze" "Primary"
        let executeBtn = createPillBtn "Clean selected" "Success"
        let selectAllBtn = createPillBtn "Select all" ""
        let selectNoneBtn = createPillBtn "Select none" ""
        let summaryPanel = Border(Background = cardBg, CornerRadius = CornerRadius(8.0), Padding = Thickness(16.0), Margin = Thickness(0.0, 10.0, 0.0, 0.0))
        let summaryStack = StackPanel(Spacing = 8.0)
        summaryStack.Children.Add(TextBlock(Text = "Scan Summary", FontWeight = FontWeight.Bold, FontSize = 14.0))
        actionStatusTxt.Text <- "Analyze first to calculate real reclaimable space."
        actionStatusTxt.Foreground <- textLow
        summaryStack.Children.Add(actionStatusTxt)
        summaryPanel.Child <- summaryStack
        actionPanel.Children.AddRange [scanBtn; executeBtn; selectAllBtn; selectNoneBtn; summaryPanel]
        Grid.SetColumn(actionPanel, 1)
        cleanGrid.Children.AddRange [listContainer; actionPanel]
        Grid.SetRow(cleanGrid, 1)

        let bottomStats = Border(Background = cardBg, CornerRadius = CornerRadius(8.0), Height = 58.0, Margin = Thickness(0.0, 22.0, 0.0, 0.0))
        let bottomStack = StackPanel(Orientation = Orientation.Horizontal, Spacing = 40.0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center)
        totalFreedTxt.Text <- "RECLAIMABLE: 0 B"; totalFreedTxt.Foreground <- primary; totalFreedTxt.FontSize <- 16.0; totalFreedTxt.FontWeight <- FontWeight.Bold
        totalFilesTxt.Text <- "FILES: 0"; totalFilesTxt.Foreground <- textLow; totalFilesTxt.FontSize <- 16.0; totalFilesTxt.FontWeight <- FontWeight.Bold
        bottomStack.Children.AddRange [totalFreedTxt; totalFilesTxt]
        bottomStats.Child <- bottomStack
        Grid.SetRow(bottomStats, 2)
        cleanerMain.Children.AddRange [headerGrid; cleanGrid; bottomStats]
        cleanerView.Content <- cleanerMain

        // UI Logic
        let writeToLog (msg: string) =
            Dispatcher.UIThread.Post(fun () ->
                try
                    logEditor.AppendText(sprintf "[%s] %s\n" (DateTime.Now.ToString("HH:mm:ss")) msg)
                    logEditor.ScrollToEnd()
                with _ -> ()
            )

        scanEngine.OnFileProcessed.Add(writeToLog)

        AppLogging.configure (Some writeToLog)

        Log.Information("Plumbyr Started")
        writeToLog "--- COMMAND CENTER INITIALIZED (ELEVATED ADMIN) ---"
        writeToLog "Plumbyr Power-User Terminal ready. CMD & PowerShell enabled."
        writeToLog "Type 'help' for available commands or use the quick actions above."

        // Real-time Stats during Purge
        scanEngine.OnFileProcessed.Add(fun _ ->
            Dispatcher.UIThread.Post(fun () ->
                let s = scanEngine.GetStats()
                let freedSize = ByteSize.FromBytes(float s.BytesFreed)
                reclaimVal.Text <- sprintf "%s" (freedSize.ToString())
                filesVal.Text <- string s.FilesDeleted
                totalFreedTxt.Text <- sprintf "RECLAIMED: %s" (freedSize.ToString())
                totalFilesTxt.Text <- sprintf "FILES PURGED: %d" s.FilesDeleted
            )
        )

        // BrowserAnalysisView's "Open cleanup rules" button needs to jump into the cleaner
        // view pre-filtered to browser-cache targets; the nav items it depends on don't exist
        // yet, so it's wired up via this mutable indirection once they're built below.
        let mutable openCleanupFromBrowsers : unit -> unit = fun () -> ()
        let browserAnalysis = new BrowserAnalysisView(writeToLog, fun () -> openCleanupFromBrowsers())
        this.Closed.Add(fun _ -> (browserAnalysis :> IDisposable).Dispose())

        let driversView = new DriversView(writeToLog, this.StorageProvider)

        // Navigation: a Synthora TreeMenu replaces the hand-rolled sidebar + view switcher.
        // Selecting an item both shows its Content and (via SelectionChanged below) triggers
        // the equivalent side effects the old button-click handlers used to run.
        let dashItem    = makeNavItem "Dashboard" DASH_SVG dashboardView NavDashboard
        let termItem    = makeNavItem "Command Center" TERM_SVG terminalView NavTerminal
        let sysItem     = makeNavItem "System Cleanup" SYS_SVG cleanerView (NavCleaner SystemTemporary)
        let browserItem = makeNavItem "Browsers" BROWSER_SVG (browserAnalysis :> Control) NavBrowsers
        let appsItem    = makeNavItem "Applications" APPS_SVG cleanerView (NavCleaner ApplicationCache)
        let gameItem    = makeNavItem "Gaming & GPU" GAME_SVG cleanerView (NavCleaner GamingCache)
        let driverItem  = makeNavItem "Drivers" DRIVER_SVG (driversView :> Control) NavDrivers

        let treeMenu = TreeMenu(Header = "PLUMBYR")
        treeMenu.PaneHeader <- header
        treeMenu.Items.Add(dashItem) |> ignore
        treeMenu.Items.Add(termItem) |> ignore
        treeMenu.Items.Add(sysItem) |> ignore
        treeMenu.Items.Add(browserItem) |> ignore
        treeMenu.Items.Add(appsItem) |> ignore
        treeMenu.Items.Add(gameItem) |> ignore
        treeMenu.Items.Add(driverItem) |> ignore

        let runExportHistory () =
            async {
                let options = FilePickerSaveOptions(
                    Title = "Export Terminal Logs",
                    SuggestedFileName = "plumbyr_logs.txt",
                    DefaultExtension = "txt",
                    FileTypeChoices = [| FilePickerFileType("Text Files", Patterns = [| "*.txt" |]) |])

                let! file = this.StorageProvider.SaveFilePickerAsync(options) |> Async.AwaitTask

                if box file <> null then
                    try
                        let logs = logEditor.Text
                        use! stream = file.OpenWriteAsync() |> Async.AwaitTask
                        use sw = new StreamWriter(stream)
                        sw.WriteLine("PLUMBYR TERMINAL EXPORT")
                        sw.WriteLine(sprintf "Generated on: %O" DateTime.Now)
                        sw.WriteLine("==========================================")
                        sw.Write(logs)

                        Dispatcher.UIThread.Post(fun () ->
                            treeMenu.SelectedItem <- termItem
                            writeToLog (sprintf "Terminal logs exported to: %s" file.Name)
                            writeToLog "--- EXPORT COMPLETE ---")
                    with ex ->
                        Dispatcher.UIThread.Post(fun () -> writeToLog (sprintf "ERROR EXPORTING LOGS: %s" ex.Message))
            } |> Async.StartImmediate

        // Export History lives in the pane footer since it's an action, not a page.
        let exportBtn = Button(Theme = borderlessButtonTheme, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Margin = Thickness(15.0, 6.0, 15.0, 15.0))
        exportBtn.Content <- makeNavHeader "Export History" HIST_SVG
        treeMenu.PaneFooter <- exportBtn
        exportBtn.Click.Add(fun _ -> writeToLog "> export"; runExportHistory())

        openCleanupFromBrowsers <- fun () ->
            treeMenu.SelectedItem <- sysItem
            currentCategory <- Some BrowserCache
            categoryTitle.Text <- "BROWSER CLEANUP"
            this.PopulateTargets()

        let categoryTitleFor = function
            | SystemTemporary -> "SYSTEM CLEANUP"
            | ApplicationCache -> "APP CLEANUP"
            | GamingCache -> "GAMING & GPU"
            | BrowserCache -> "BROWSER CLEANUP"
            | _ -> "SYSTEM CLEANUP"

        treeMenu.SelectionChanged.Add(fun _ ->
            match treeMenu.SelectedItem with
            | :? TreeMenuItem as item ->
                match item.Tag with
                | :? NavKey as key ->
                    match key with
                    | NavDashboard ->
                        currentCategory <- None
                        categoryTitle.Text <- "DASHBOARD"
                        writeToLog "Switching to Dashboard..."
                    | NavTerminal ->
                        currentCategory <- None
                        categoryTitle.Text <- "COMMAND CENTER"
                        writeToLog "Switching to Command Center..."
                    | NavCleaner cat ->
                        currentCategory <- Some cat
                        categoryTitle.Text <- categoryTitleFor cat
                        writeToLog (sprintf "Switching to %s..." (categoryTitleFor cat))
                        this.PopulateTargets()
                    | NavBrowsers ->
                        currentCategory <- None
                        categoryTitle.Text <- "BROWSER ANALYSIS"
                        writeToLog "Switching to Browser Analysis..."
                    | NavDrivers ->
                        currentCategory <- None
                        categoryTitle.Text <- "DRIVERS"
                        writeToLog "Switching to Drivers..."
                | _ -> ()
            | _ -> ())

        let selectNav (item: TreeMenuItem) = treeMenu.SelectedItem <- item

        let printHelpGuide (topic: string) =
            match topic.Trim().ToLowerInvariant() with
            | "repair" | "dism" | "sfc" ->
                writeToLog "======================================================================"
                writeToLog "                   SYSTEM REPAIR & SERVICING COMMANDS                 "
                writeToLog "======================================================================"
                writeToLog "  sfc /scannow       - Scan & repair corrupted Windows system files"
                writeToLog "  dism-health        - Fast health check of Windows component store"
                writeToLog "  dism-scan          - Deep scan for component store corruption"
                writeToLog "  dism /online /cleanup-image /restorehealth"
                writeToLog "                     - Download & restore damaged components via WU"
                writeToLog "  dism-clean         - Purge superseded updates & reclaim disk space"
                writeToLog "  chkdsk C: /scan    - Online NTFS file system scan without reboot"
                writeToLog "======================================================================"
            | "net" | "network" ->
                writeToLog "======================================================================"
                writeToLog "                    NETWORK & CONNECTIVITY COMMANDS                   "
                writeToLog "======================================================================"
                writeToLog "  flushdns           - Flush and reset DNS resolver cache"
                writeToLog "  netreset           - Reset Winsock & TCP/IP stack to factory defaults"
                writeToLog "  ipconfig /all      - Detailed network adapter configuration"
                writeToLog "  netstat -ano       - Display active listening ports & PID owners"
                writeToLog "  ping 8.8.8.8       - Test connection and latency to Google DNS"
                writeToLog "  ps Test-NetConnection -ComputerName google.com -Port 443"
                writeToLog "                     - Test TCP port reachability via PowerShell"
                writeToLog "======================================================================"
            | "ps" | "powershell" ->
                writeToLog "======================================================================"
                writeToLog "                     POWERSHELL POWER-USER CMDLETS                    "
                writeToLog "======================================================================"
                writeToLog "  ps <script>        - Execute any PowerShell command as Administrator"
                writeToLog "  top-cpu            - Show top 10 CPU-consuming processes"
                writeToLog "  top-ram            - Show top 10 RAM-consuming processes"
                writeToLog "  disk-health        - Check physical disk health status & media type"
                writeToLog "  ps Get-Service | Where-Object Status -eq 'Running'"
                writeToLog "                     - List all currently running background services"
                writeToLog "  ps Get-ComputerInfo | Select-Object WindowsProductName, BiosBIOSVersion"
                writeToLog "                     - Display motherboard BIOS & OS build info"
                writeToLog "======================================================================"
            | "power" | "disk" ->
                writeToLog "======================================================================"
                writeToLog "                      HARDWARE & STORAGE POWER TOOLS                  "
                writeToLog "======================================================================"
                writeToLog "  battery            - Generate detailed battery health report (HTML)"
                writeToLog "  trim               - Send TRIM command to SSD (defrag C: /O)"
                writeToLog "  hibernation-off    - Disable hibernation & delete hiberfil.sys (frees GBs)"
                writeToLog "  hibernation-on     - Re-enable hibernation"
                writeToLog "  reboot-bios        - Reboot directly into UEFI / BIOS settings"
                writeToLog "======================================================================"
            | _ ->
                writeToLog "======================================================================"
                writeToLog "                PLUMBYR ELEVATED COMMAND CENTER GUIDE                 "
                writeToLog "======================================================================"
                writeToLog "  [ELEVATED ADMIN] Running with full Administrator privileges."
                writeToLog "  Supports CMD commands, PowerShell cmdlets, and built-in shortcuts."
                writeToLog ""
                writeToLog "[QUICK POWER SHORTCUTS]"
                writeToLog "  flushdns           - Flush DNS resolver cache (ipconfig /flushdns)"
                writeToLog "  netreset           - Reset Winsock & TCP/IP network stack"
                writeToLog "  dism-health        - Fast check for Windows component store corruption"
                writeToLog "  dism-clean         - Purge old Windows Update packages (frees GBs)"
                writeToLog "  sfc                - Scan & fix corrupted Windows system files"
                writeToLog "  trim               - Optimize / TRIM SSD drives (defrag C: /O)"
                writeToLog "  battery            - Generate battery wear & health HTML report"
                writeToLog "  top-cpu / top-ram  - View top 10 processes by CPU or RAM usage"
                writeToLog "  disk-health        - Query physical disk health & media types"
                writeToLog "  reboot-bios        - Reboot computer directly into UEFI / BIOS"
                writeToLog ""
                writeToLog "[POWERSHELL SUPPORT]"
                writeToLog "  Prefix any command with 'ps ' or run cmdlets directly:"
                writeToLog "  e.g.: ps Get-Service | Where-Object Status -eq 'Running'"
                writeToLog "  e.g.: ps Test-NetConnection -ComputerName google.com -Port 443"
                writeToLog ""
                writeToLog "[PLUMBYR SHORTCUTS]"
                writeToLog "  drivers            - Open Driver Management & Updates view"
                writeToLog "  clean              - Open System Cleanup view"
                writeToLog "  dashboard          - Return to System Overview dashboard"
                writeToLog "  export             - Export terminal logs to a text file"
                writeToLog "  clear / cls        - Clear the terminal screen"
                writeToLog ""
                writeToLog "Sub-guides: 'help repair', 'help net', 'help ps', 'help power'"
                writeToLog "For raw Windows DOS command index, type: doshelp"
                writeToLog "======================================================================"

        let executeShellCommand (cmd: string) (isPowerShell: bool) =
            Thread(fun () ->
                try
                    let psi =
                        if isPowerShell then
                            let psi = ProcessStartInfo("powershell.exe")
                            psi.ArgumentList.Add("-NoProfile")
                            psi.ArgumentList.Add("-NonInteractive")
                            psi.ArgumentList.Add("-Command")
                            psi.ArgumentList.Add(cmd)
                            psi
                        else
                            let psi = ProcessStartInfo("cmd.exe")
                            psi.ArgumentList.Add("/c")
                            psi.ArgumentList.Add(cmd)
                            psi
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.UseShellExecute <- false
                    psi.CreateNoWindow <- true
                    let proc = Process.Start(psi)
                    proc.OutputDataReceived.Add(fun args -> if box args.Data <> null then writeToLog args.Data)
                    proc.ErrorDataReceived.Add(fun args -> if box args.Data <> null then writeToLog (sprintf "ERR: %s" args.Data))
                    proc.BeginOutputReadLine()
                    proc.BeginErrorReadLine()
                    proc.WaitForExit()
                    writeToLog (sprintf "--- PROCESS EXITED WITH CODE %d ---" proc.ExitCode)
                with ex -> writeToLog (sprintf "EXECUTION ERROR: %s" ex.Message)
            ).Start()

        let rec executeCommand (input: string) =
            let trimmed = input.Trim()
            if not (String.IsNullOrWhiteSpace trimmed) then
                let lower = trimmed.ToLowerInvariant()
                if lower = "drivers" || lower = "driver" then
                    writeToLog "> drivers"
                    selectNav driverItem
                elif lower = "clean" || lower = "cleanup" then
                    writeToLog "> clean"
                    selectNav sysItem
                elif lower = "dashboard" || lower = "dash" then
                    writeToLog "> dashboard"
                    selectNav dashItem
                elif lower = "browsers" || lower = "browser" then
                    writeToLog "> browsers"
                    selectNav browserItem
                elif lower = "export" then
                    writeToLog "> export"
                    runExportHistory()
                elif lower = "clear" || lower = "cls" then
                    logEditor.Clear()
                    writeToLog "--- OUTPUT CLEARED ---"
                elif lower = "help" then
                    writeToLog "> help"
                    printHelpGuide ""
                elif lower.StartsWith("help ") then
                    let topic = trimmed.Substring(5).Trim()
                    writeToLog (sprintf "> help %s" topic)
                    printHelpGuide topic
                elif lower = "doshelp" then
                    writeToLog "> doshelp"
                    executeShellCommand "help" false
                else
                    let (cmdToRun, isPowerShell) =
                        match lower with
                        | "flushdns" -> ("ipconfig /flushdns", false)
                        | "netreset" -> ("netsh winsock reset && netsh int ip reset", false)
                        | "trim" | "trim-ssd" -> ("defrag C: /O", false)
                        | "dism-health" -> ("dism /online /cleanup-image /checkhealth", false)
                        | "dism-scan" -> ("dism /online /cleanup-image /scanhealth", false)
                        | "dism-clean" -> ("dism /online /cleanup-image /startcomponentcleanup", false)
                        | "sfc" | "repair-system" -> ("sfc /scannow", false)
                        | "hibernation-off" -> ("powercfg /hibernate off", false)
                        | "hibernation-on" -> ("powercfg /hibernate on", false)
                        | "battery" | "batteryreport" ->
                            let reportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "battery_report.html")
                            (sprintf "powercfg /batteryreport /output \"%s\"" reportPath, false)
                        | "reboot-bios" | "uefi" -> ("shutdown /r /fw /t 5", false)
                        | "top-cpu" ->
                            ("Get-Process | Sort-Object CPU -Descending | Select-Object -First 10 -Property Id, ProcessName, @{Name='CPU(s)';Expression={'{0:N2}' -f $_.CPU}}, @{Name='RAM(MB)';Expression={'{0:N1}' -f ($_.WorkingSet64/1MB)}} | Format-Table -AutoSize", true)
                        | "top-ram" ->
                            ("Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 10 -Property Id, ProcessName, @{Name='RAM(MB)';Expression={'{0:N1}' -f ($_.WorkingSet64/1MB)}} | Format-Table -AutoSize", true)
                        | "disk-health" ->
                            ("Get-PhysicalDisk | Format-Table -AutoSize FriendlyName, MediaType, HealthStatus, OperationalStatus, @{Name='Size(GB)';Expression={'{0:N1}' -f ($_.Size/1GB)}}", true)
                        | _ ->
                            if trimmed.StartsWith("ps ", StringComparison.OrdinalIgnoreCase) then
                                (trimmed.Substring(3).Trim(), true)
                            elif trimmed.StartsWith("powershell ", StringComparison.OrdinalIgnoreCase) then
                                (trimmed.Substring(11).Trim(), true)
                            elif trimmed.StartsWith("Get-", StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("Set-", StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("Test-", StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("Restart-", StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("Start-", StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("Stop-", StringComparison.OrdinalIgnoreCase) ||
                                 trimmed.StartsWith("$") then
                                (trimmed, true)
                            else
                                (trimmed, false)

                    let promptPrefix = if isPowerShell then "PS >" else "CMD >"
                    writeToLog (sprintf "%s %s" promptPrefix trimmed)
                    executeShellCommand cmdToRun isPowerShell

        let runCommandInput () =
            if not (String.IsNullOrWhiteSpace(inputField.Text)) then
                let cmd = inputField.Text.Trim()
                inputField.Text <- ""
                executeCommand cmd

        inputField.KeyDown.Add(fun e ->
            if e.Key = Avalonia.Input.Key.Enter then
                runCommandInput()
        )

        runCommandBtn.Click.Add(fun _ -> runCommandInput())
        helpQuickBtn.Click.Add(fun _ -> executeCommand "help")
        flushDnsQuickBtn.Click.Add(fun _ -> executeCommand "flushdns")
        dismHealthQuickBtn.Click.Add(fun _ -> executeCommand "dism-health")
        topProcQuickBtn.Click.Add(fun _ -> executeCommand "top-cpu")
        batteryQuickBtn.Click.Add(fun _ -> executeCommand "battery")
        clearLogBtn.Click.Add(fun _ -> executeCommand "clear")

        let loadSystemInfo () =
            try
                let info = SystemInfo.getFullReport()
                Dispatcher.UIThread.Invoke(fun () ->
                    osVal.Text <- info.OS
                    cpuVal.Text <- info.Processor
                    ramVal.Text <- info.Memory
                    gpuVal.Text <- info.GPU
                    healthVal.Text <- "READY"
                    healthCircle.Value <- 85
                    healthValueTxt.Text <- "85%"
                )
            with _ -> ()

        // Real-time Hardware Monitor Timer
        let statsTimer = new System.Timers.Timer(2000.0)
        statsTimer.Elapsed.Add(fun _ -> loadSystemInfo())
        statsTimer.AutoReset <- true
        statsTimer.Enabled <- true
        loadSystemInfo()

        let runSystemDiagnostics () =
            selectNav termItem
            writeToLog "--- GENERATING SYSTEM DIAGNOSTICS ---"
            Thread(fun () ->
                try
                    for line in SystemInfo.formatDiagnosticsReport() do
                        writeToLog line
                    writeToLog "--- DIAGNOSTICS COMPLETE ---"
                with ex ->
                    Log.Error(ex, "System diagnostics failed")
                    writeToLog (sprintf "DIAGNOSTICS ERROR: %s" ex.Message)
            ).Start()

        dumpBtn.Click.Add(fun _ -> runSystemDiagnostics())

        selectAllBtn.Click.Add(fun _ ->
            for target in targetsForCurrentCategory() do
                selectedGroups.Add(groupKey target) |> ignore
            this.PopulateTargets()
        )

        selectNoneBtn.Click.Add(fun _ ->
            for target in targetsForCurrentCategory() do
                selectedGroups.Remove(groupKey target) |> ignore
            this.PopulateTargets()
        )

        scanBtn.Click.Add(fun _ ->
            actionStatusTxt.Text <- "Analyzing selected cleanup groups..."
            writeToLog "--- STARTING CLEANER ANALYSIS ---"
            let targets = selectedTargetsForCurrentCategory()
            Thread(fun () ->
                try
                    let results = scanEngine.AnalyzeTargets(targets)
                    let bytes = results |> List.sumBy (fun r -> r.EstimatedBytes)
                    let files = results |> List.sumBy (fun r -> r.FileCount)
                    let found = results |> List.filter (fun r -> r.Exists) |> List.length
                    writeToLog (sprintf "ANALYSIS COMPLETE. Found %d rule paths, %d files, %s reclaimable." found files (formatBytes bytes))
                    Dispatcher.UIThread.Post(fun () ->
                        currentScanResults <- results
                        totalFreedTxt.Text <- sprintf "RECLAIMABLE: %s" (formatBytes bytes)
                        totalFilesTxt.Text <- sprintf "FILES: %d" files
                        foundItemsTxt.Text <- sprintf "FOUND: %d PATHS" found
                        actionStatusTxt.Text <- sprintf "%s reclaimable across %d files." (formatBytes bytes) files
                        reclaimVal.Text <- formatBytes bytes
                        filesVal.Text <- string files
                        healthVal.Text <- "REVIEW"
                        healthCircle.Value <- 72
                        healthValueTxt.Text <- "72%"
                        this.PopulateTargets()
                    )
                with ex -> writeToLog (sprintf "ANALYSIS ERROR: %s" ex.Message)
            ).Start()
        )

        executeBtn.Click.Add(fun _ ->
            let targets = selectedTargetsForCurrentCategory()
            if targets.IsEmpty then
                actionStatusTxt.Text <- "Select at least one item to clean."
            else
                async {
                    let! result =
                        AlertDialog.ShowAsync(
                            sprintf "This will permanently delete files from %d selected group(s). This cannot be undone." targets.Length,
                            "Confirm Cleanup",
                            DialogButton.YesNo,
                            IconType.Warning) |> Async.AwaitTask
                    if result = DialogResult.Yes then
                        writeToLog (sprintf "--- CLEANING %d SELECTED RULE PATHS ---" targets.Length)
                        actionStatusTxt.Text <- "Cleaning selected items..."
                        Thread(fun () ->
                            try
                                scanEngine.CleanTargets(targets)
                                scanEngine.SaveStatsToDatabase()
                                let stats = scanEngine.GetStats()
                                Dispatcher.UIThread.Post(fun () ->
                                    let freedSize = formatBytes stats.BytesFreed
                                    totalFreedTxt.Text <- sprintf "RECLAIMED: %s" freedSize
                                    totalFilesTxt.Text <- sprintf "FILES PURGED: %d" stats.FilesDeleted
                                    actionStatusTxt.Text <- sprintf "Cleaned %d files and reclaimed %s." stats.FilesDeleted freedSize
                                    reclaimVal.Text <- freedSize
                                    filesVal.Text <- string stats.FilesDeleted
                                    writeToLog (sprintf "CLEAN COMPLETE. Total reclaimed: %s" freedSize)
                                    healthVal.Text <- "CLEAN"
                                    healthVal.Foreground <- success
                                    healthCircle.Value <- 100
                                    healthValueTxt.Text <- "100%"
                                    currentScanResults <- scanEngine.AnalyzeTargets(targets)
                                    this.PopulateTargets()
                                )
                            with ex -> writeToLog (sprintf "PURGE ERROR: %s" ex.Message)
                        ).Start()
                } |> Async.StartImmediate
        )

        this.Content <- AlertDialogHost(Content = treeMenu)

        loadSystemInfo()
        this.PopulateTargets()
        dashItem.IsSelected <- true

    member private this.PopulateTargets() =
        targetsList.Children.Clear()
        let targets = targetsForCurrentCategory()

        if selectedGroups.Count = 0 then
            for target in targets do
                selectedGroups.Add(groupKey target) |> ignore

        let resultLookup =
            currentScanResults
            |> List.groupBy (fun r -> groupKey r.Target)
            |> Map.ofList

        let groupedTargets =
            targets
            |> List.groupBy groupKey
            |> List.map (fun (key, items) ->
                let first = items.Head
                let results = defaultArg (Map.tryFind key resultLookup) []
                let bytes = results |> List.sumBy (fun r -> r.EstimatedBytes)
                let files = results |> List.sumBy (fun r -> r.FileCount)
                let folders = results |> List.sumBy (fun r -> r.FolderCount)
                let exists = results |> List.exists (fun r -> r.Exists)
                (key, first, items.Length, bytes, files, folders, exists))
            |> List.sortBy (fun (_, target, _, _, _, _, _) -> target.Name)

        let selectedVisible =
            groupedTargets |> List.filter (fun (key, _, _, _, _, _, _) -> selectedGroups.Contains(key)) |> List.length
        selectionTxt.Text <- sprintf "SELECTED: %d / %d" selectedVisible groupedTargets.Length

        let cardBg = SolidColorBrush.Parse("#121212") :> IBrush
        let textLow = Application.Current.FindResource("ThemeForegroundLowBrush") :?> IBrush
        let primary = Application.Current.FindResource("PrimaryBrush") :?> IBrush

        for (key, t, pathCount, bytes, files, folders, exists) in groupedTargets do
            let card = Border(Background = cardBg, CornerRadius = CornerRadius(8.0), Margin = Thickness(0.0, 0.0, 0.0, 6.0), Padding = Thickness(14.0, 10.0))
            let row = Grid(ColumnDefinitions = ColumnDefinitions("*,110,110"))

            let cb = CheckBox(IsChecked = Nullable<bool>(selectedGroups.Contains(key)), VerticalAlignment = VerticalAlignment.Center)
            let nameStack = StackPanel(Spacing = 3.0, Margin = Thickness(10.0, 0.0, 0.0, 0.0))
            let title = TextBlock(Text = t.Name, FontWeight = FontWeight.Bold, FontSize = 14.0)
            nameStack.Children.Add(title)
            let detail =
                let source = t.SourceFile.Replace(".json", "")
                let status = if currentScanResults.IsEmpty then "Not analyzed" elif exists then "Found" else "Not found"
                sprintf "%s | %s | %d rule paths" (CleanTargets.getCategoryName t.Category) status pathCount
            let detailTxt = TextBlock(Text = detail, FontSize = 11.0)
            detailTxt.Foreground <- textLow
            nameStack.Children.Add(detailTxt)
            if not (String.IsNullOrEmpty(t.Description)) then
                let descTxt = TextBlock(Text = t.Description, FontSize = 10.0, TextWrapping = TextWrapping.Wrap, MaxHeight = 34.0)
                descTxt.Foreground <- textLow
                nameStack.Children.Add(descTxt)
            cb.Content <- nameStack
            cb.IsCheckedChanged.Add(fun _ ->
                match cb.IsChecked with
                | v when v.HasValue && v.Value -> selectedGroups.Add(key) |> ignore
                | _ -> selectedGroups.Remove(key) |> ignore
                selectionTxt.Text <- sprintf "SELECTED: %d / %d" (groupedTargets |> List.filter (fun (k, _, _, _, _, _, _) -> selectedGroups.Contains(k)) |> List.length) groupedTargets.Length
            )
            Grid.SetColumn(cb, 0); row.Children.Add(cb)

            let sizeText =
                if currentScanResults.IsEmpty then "Analyze"
                else formatBytes bytes
            let sizeBlock = TextBlock(Text = sizeText, Foreground = primary, FontWeight = FontWeight.Bold, FontSize = 13.0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center)
            Grid.SetColumn(sizeBlock, 1); row.Children.Add(sizeBlock)

            let fileText =
                if currentScanResults.IsEmpty then "-"
                else sprintf "%d" files
            let filesBlock = TextBlock(Text = fileText, FontSize = 13.0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center)
            filesBlock.Foreground <- textLow
            Grid.SetColumn(filesBlock, 2); row.Children.Add(filesBlock)

            card.Child <- row
            targetsList.Children.Add(card)

type App() =
    inherit Application()
    override this.Initialize() =
        // FluentTheme must load first: AvaloniaEdit's Fluent theme (and its SearchPanel)
        // reads base tokens like ControlContentThemeFontSize straight from it and crashes
        // without them. SynthoraTheme loads after, so its control themes still win.
        this.Styles.Add(Avalonia.Themes.Fluent.FluentTheme())
        this.Styles.Add(SynthoraTheme())
        let editStyle = Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri = null)
        editStyle.Source <- Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        this.Styles.Add(editStyle)
    override this.OnFrameworkInitializationCompleted() =
        match this.ApplicationLifetime with
        | :? IClassicDesktopStyleApplicationLifetime as d -> d.MainWindow <- MainWindow()
        | _ -> ()

module Main =
    [<STAThread>]
    [<EntryPoint>]
    let main argv =
        try
            AppLogging.configure None
            AppDomain.CurrentDomain.UnhandledException.Add(fun args ->
                match args.ExceptionObject with
                | :? Exception as ex -> Log.Fatal(ex, "Unhandled AppDomain exception")
                | other -> Log.Fatal("Unhandled AppDomain exception: {ExceptionObject}", other)
                Log.CloseAndFlush())

            AppFiles.initialize()
            let exitCode =
                if argv.Length = 2 && argv[0] = "--analyze-browsers" then
                    let report = KuduBridge.analyze CancellationToken.None |> fun work -> work.GetAwaiter().GetResult()
                    File.WriteAllText(Path.GetFullPath(argv[1]), System.Text.Json.JsonSerializer.Serialize(report))
                    0
                else
                    AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(argv)
            Log.Information("Plumbyr exited with code {ExitCode}", exitCode)
            Log.CloseAndFlush()
            exitCode
        with ex ->
            Log.Fatal(ex, "Plumbyr failed during startup")
            try File.WriteAllText(Path.Combine(AppFiles.dataDirectory(), "crash_log.txt"), ex.ToString()) with _ -> ()
            Log.CloseAndFlush()
            printfn "CRASH: %s" ex.Message
            1
