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
open Avalonia.Themes.Fluent
open Avalonia.Platform.Storage
open AvaloniaEdit
open AvaloniaEdit.Editing
open Serilog
open Serilog.Core
open Serilog.Events
open ByteSizeLib

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

type MainWindow() as this =
    inherit Window()
    
    let scanEngine = ScanEngine()
    let targetsList = StackPanel(Spacing = 8.0)
    let selectedGroups = HashSet<string>()
    let mutable currentScanResults: ScanTargetResult list = []
    let totalFreedTxt = TextBlock(FontSize = 18.0, FontWeight = FontWeight.SemiBold)
    let totalFilesTxt = TextBlock(FontSize = 14.0, Foreground = Brushes.Gray)
    let foundItemsTxt = TextBlock(FontSize = 14.0, Foreground = Brushes.Gray)
    let selectionTxt = TextBlock(FontSize = 14.0, Foreground = Brushes.Gray)
    let actionStatusTxt = TextBlock(FontSize = 13.0, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap)
    
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

    let mutable currentView = "dash"
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
        
        let mainContent = Grid(Background = SolidColorBrush.Parse("#080808"))
        mainContent.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(260.0)))
        mainContent.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        mainContent.IsHitTestVisible <- true
        
        this.Background <- SolidColorBrush.Parse("#080808")
        this.ExtendClientAreaToDecorationsHint <- false
        this.WindowDecorations <- WindowDecorations.Full
        this.TransparencyLevelHint <- [| WindowTransparencyLevel.None |]
        this.TransparencyBackgroundFallback <- Brushes.Black

        this.Content <- mainContent

        // Initialize History
        let historyPath = Path.Combine(AppFiles.dataDirectory(), "plumbyr_history.db")
        CleaningHistory.GetInstance().Initialize(historyPath)

        // Sidebar
        let sidebar = Border(BorderBrush = SolidColorBrush.Parse("#222222"), BorderThickness = Thickness(0.0, 0.0, 1.0, 0.0), Background = SolidColorBrush.Parse("#0c0c0c"), ZIndex = 100)
        let sidebarGrid = Grid(RowDefinitions = RowDefinitions("Auto,*,Auto"))
        
        let header = StackPanel(Margin = Thickness(20.0, 40.0, 20.0, 30.0))
        try
            if File.Exists(iconPath) then
                header.Children.Add(Image(Source = new Bitmap(iconPath), Width = 82.0, Height = 82.0, HorizontalAlignment = HorizontalAlignment.Center, Margin = Thickness(0.0, 0.0, 0.0, 12.0)))
        with ex ->
            Log.Warning(ex, "Unable to load header icon from {IconPath}", iconPath)
        header.Children.Add(TextBlock(Text = "PLUMBYR", FontSize = 24.0, FontWeight = FontWeight.Black, HorizontalAlignment = HorizontalAlignment.Center))
        header.Children.Add(TextBlock(Text = "Plumbing Linked Universal Maintenance & Binary Yield Reclaimer", FontSize = 10.0, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 210.0))
        Grid.SetRow(header, 0); sidebarGrid.Children.Add(header)

        let navStack = StackPanel(Spacing = 8.0, Margin = Thickness(15.0, 0.0, 15.0, 0.0))
        let createNavBtn text svgData =
            let btn = Button(Height = 50.0, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = CornerRadius(12.0), Background = SolidColorBrush.Parse("#0c0c0c"))
            let stack = StackPanel(Orientation = Orientation.Horizontal, Spacing = 15.0, IsHitTestVisible = false)
            let icon = Avalonia.Controls.Shapes.Path(Data = Geometry.Parse(svgData), Fill = Brushes.Gray, Width = 18.0, Height = 18.0, Stretch = Stretch.Uniform)
            stack.Children.Add(icon)
            stack.Children.Add(TextBlock(Text = text, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold))
            btn.Content <- stack
            btn

        let dashBtn = createNavBtn "Dashboard" DASH_SVG
        let termBtn = createNavBtn "Command Center" TERM_SVG
        let sysBtn = createNavBtn "System Cleanup" SYS_SVG
        let browserBtn = createNavBtn "Browsers" BROWSER_SVG
        let appsBtn = createNavBtn "Applications" APPS_SVG
        let gameBtn = createNavBtn "Gaming & GPU" GAME_SVG
        let diagBtn = createNavBtn "System Diagnostics" SYS_SVG
        let histBtn = createNavBtn "Export History" HIST_SVG

        navStack.Children.AddRange [dashBtn; termBtn; sysBtn; browserBtn; appsBtn; gameBtn; diagBtn; histBtn]
        Grid.SetRow(navStack, 1); sidebarGrid.Children.Add(navStack)

        sidebar.Child <- sidebarGrid
        Grid.SetColumn(sidebar, 0); mainContent.Children.Add(sidebar)

        let transition = ContentControl(IsHitTestVisible = true)
        transition.Background <- SolidColorBrush.Parse("#080808")
        Grid.SetColumn(transition, 1)
        mainContent.Children.Add(transition)

        // Dashboard View Setup
        let dashGrid = Grid(RowDefinitions = RowDefinitions("Auto,Auto,*"), Margin = Thickness(30.0))
        let welcomeHeader = StackPanel(Margin = Thickness(0.0, 0.0, 0.0, 30.0))
        welcomeHeader.Children.Add(TextBlock(Text = "System Overview", FontSize = 42.0, FontWeight = FontWeight.Black))
        welcomeHeader.Children.Add(TextBlock(Text = "Real-time hardware status and optimization.", FontSize = 16.0, Foreground = Brushes.Gray))
        Grid.SetRow(welcomeHeader, 0)
        
        let statsRow = Grid(ColumnDefinitions = ColumnDefinitions("*,*,*"), Margin = Thickness(0.0, 0.0, 0.0, 30.0))
        let createStatCard title value color =
            let card = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(12.0), Margin = Thickness(5.0), Padding = Thickness(25.0))
            let stack = StackPanel(Spacing = 8.0)
            stack.Children.Add(TextBlock(Text = title, FontSize = 12.0, FontWeight = FontWeight.Bold, Foreground = Brushes.Gray))
            let valTxt = TextBlock(Text = value, FontSize = 28.0, FontWeight = FontWeight.Black, Foreground = color)
            stack.Children.Add(valTxt)
            card.Child <- stack
            (card, valTxt)

        let (reclaimCard, reclaimVal: TextBlock) = createStatCard "DISK RECLAIMED" "0.00 GB" Brushes.DodgerBlue
        let (filesCard, filesVal: TextBlock) = createStatCard "TOTAL PURGED" "0" Brushes.LimeGreen
        let (healthCard, healthVal: TextBlock) = createStatCard "SYSTEM STATUS" "READY" Brushes.Orange
        
        Grid.SetColumn(reclaimCard, 0); statsRow.Children.Add(reclaimCard)
        Grid.SetColumn(filesCard, 1); statsRow.Children.Add(filesCard)
        Grid.SetColumn(healthCard, 2); statsRow.Children.Add(healthCard)
        Grid.SetRow(statsRow, 1)

        let infoGrid = Grid(ColumnDefinitions = ColumnDefinitions("*,*"))
        let leftInfo = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(12.0), Margin = Thickness(0.0, 0.0, 10.0, 0.0), Padding = Thickness(30.0))
        let hardwareStack = StackPanel(Spacing = 15.0)
        hardwareStack.Children.Add(TextBlock(Text = "HARDWARE SPECIFICATIONS", FontSize = 14.0, FontWeight = FontWeight.Bold, Foreground = Brushes.Gray, Margin = Thickness(0.0, 0.0, 0.0, 15.0)))
        
        let createInfoLine label value =
            let grid = Grid(ColumnDefinitions = ColumnDefinitions("140,*"))
            let lbl = TextBlock(Text = label, Foreground = Brushes.Gray, FontSize = 14.0)
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

        let rightInfo = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(12.0), Margin = Thickness(10.0, 0.0, 0.0, 0.0), Padding = Thickness(30.0))
        let healthStack = StackPanel(Spacing = 20.0, VerticalAlignment = VerticalAlignment.Center)
        healthStack.Children.Add(TextBlock(Text = "OPTIMIZATION GRAPH", FontSize = 14.0, FontWeight = FontWeight.Bold, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center))
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
        let logHeader = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(8.0), Padding = Thickness(20.0), Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        let logHeaderStack = StackPanel(Spacing = 4.0)
        logHeaderStack.Children.Add(TextBlock(Text = "Activity & Diagnostics", FontWeight = FontWeight.Bold, Foreground = Brushes.LimeGreen, FontSize = 18.0))
        logHeaderStack.Children.Add(TextBlock(Text = "Cleaner events, diagnostics reports, and optional Windows command output appear here.", Foreground = Brushes.Gray, FontSize = 13.0, TextWrapping = TextWrapping.Wrap))
        logHeader.Child <- logHeaderStack
        Grid.SetRow(logHeader, 0)

        let terminalActions = StackPanel(Orientation = Orientation.Horizontal, Spacing = 10.0, Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        let createTerminalAction text =
            Button(Content = text, Height = 38.0, MinWidth = 118.0, Padding = Thickness(14.0, 0.0), CornerRadius = CornerRadius(8.0), Background = SolidColorBrush.Parse("#1f2937"), Foreground = Brushes.White)
        let helpQuickBtn = createTerminalAction "Help"
        let diagQuickBtn = createTerminalAction "Diagnostics"
        let networkQuickBtn = createTerminalAction "Network info"
        let windowsQuickBtn = createTerminalAction "Windows version"
        let clearLogBtn = createTerminalAction "Clear"
        terminalActions.Children.AddRange [helpQuickBtn; diagQuickBtn; networkQuickBtn; windowsQuickBtn; clearLogBtn]
        Grid.SetRow(terminalActions, 1)
        
        let logEditor = TextEditor(Background = SolidColorBrush.Parse("#080808"), Foreground = Brushes.LimeGreen, FontSize = 13.0, FontFamily = FontFamily("Consolas"), IsReadOnly = true, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, WordWrap = true)
        logEditor.Options.EnableRectangularSelection <- true
        logEditor.Options.EnableTextDragDrop <- true
        
        Grid.SetRow(logEditor, 2)
        
        let inputRow = Grid(ColumnDefinitions = ColumnDefinitions("*,110"), Margin = Thickness(0.0, 15.0, 0.0, 0.0))
        let inputField = TextBox(PlaceholderText = "help", Height = 45.0, VerticalContentAlignment = VerticalAlignment.Center, Background = SolidColorBrush.Parse("#121212"), Foreground = Brushes.White, BorderThickness = Thickness(1.0), BorderBrush = SolidColorBrush.Parse("#333"))
        Grid.SetColumn(inputField, 0)
        let runCommandBtn = Button(Content = "Run", Height = 45.0, Margin = Thickness(10.0, 0.0, 0.0, 0.0), FontWeight = FontWeight.Bold, CornerRadius = CornerRadius(8.0), Background = SolidColorBrush.Parse("#2563eb"), Foreground = Brushes.White)
        Grid.SetColumn(runCommandBtn, 1)
        inputRow.Children.AddRange [inputField; runCommandBtn]
        Grid.SetRow(inputRow, 3)
        
        logGrid.Children.AddRange [logHeader; terminalActions; logEditor; inputRow]
        terminalView.Content <- logGrid

        // Cleaner View
        let cleanerMain = Grid(RowDefinitions = RowDefinitions("Auto,*,Auto"), Margin = Thickness(30.0))
        let headerGrid = Grid(ColumnDefinitions = ColumnDefinitions("*,Auto"), Margin = Thickness(0.0, 0.0, 0.0, 22.0))
        categoryTitle.Margin <- Thickness(0.0, 0.0, 0.0, 4.0); categoryTitle.FontSize <- 32.0
        let titleStack = StackPanel(Spacing = 4.0)
        titleStack.Children.Add(categoryTitle)
        titleStack.Children.Add(TextBlock(Text = "Select what to analyze, review real sizes, then clean only the checked groups.", Foreground = Brushes.Gray, FontSize = 14.0))
        Grid.SetColumn(titleStack, 0)
        let headerStats = StackPanel(Orientation = Orientation.Horizontal, Spacing = 18.0, VerticalAlignment = VerticalAlignment.Center)
        foundItemsTxt.Text <- "FOUND: 0 ITEMS"
        selectionTxt.Text <- "SELECTED: 0"
        headerStats.Children.AddRange [foundItemsTxt; selectionTxt]
        Grid.SetColumn(headerStats, 1)
        headerGrid.Children.AddRange [titleStack; headerStats]
        Grid.SetRow(headerGrid, 0)

        let cleanGrid = Grid(ColumnDefinitions = ColumnDefinitions("*,320"))
        let listContainer = Grid(RowDefinitions = RowDefinitions("Auto,*"))
        let listHeader = Grid(ColumnDefinitions = ColumnDefinitions("*,110,110"), Margin = Thickness(0.0, 0.0, 8.0, 8.0))
        listHeader.Children.Add(TextBlock(Text = "Cleaner", Foreground = Brushes.Gray, FontWeight = FontWeight.Bold, FontSize = 12.0))
        let sizeHeader = TextBlock(Text = "Size", Foreground = Brushes.Gray, FontWeight = FontWeight.Bold, FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Right)
        Grid.SetColumn(sizeHeader, 1); listHeader.Children.Add(sizeHeader)
        let filesHeader = TextBlock(Text = "Files", Foreground = Brushes.Gray, FontWeight = FontWeight.Bold, FontSize = 12.0, HorizontalAlignment = HorizontalAlignment.Right)
        Grid.SetColumn(filesHeader, 2); listHeader.Children.Add(filesHeader)
        Grid.SetRow(listHeader, 0)
        let scroll = ScrollViewer(Content = targetsList)
        Grid.SetRow(scroll, 1)
        listContainer.Children.AddRange [listHeader; scroll]
        Grid.SetColumn(listContainer, 0)
        
        let actionPanel = StackPanel(Spacing = 14.0, Margin = Thickness(24.0, 0.0, 0.0, 0.0))
        let createPillBtn text (color: IBrush) =
            let btn = Button(Content = text, Height = 52.0, HorizontalAlignment = HorizontalAlignment.Stretch, FontWeight = FontWeight.Bold, FontSize = 15.0, CornerRadius = CornerRadius(8.0), Background = color)
            btn
        let scanBtn = createPillBtn "Analyze" (SolidColorBrush.Parse("#2563eb"))
        let executeBtn = createPillBtn "Clean selected" (SolidColorBrush.Parse("#16a34a"))
        let selectAllBtn = createPillBtn "Select all" (SolidColorBrush.Parse("#27272a"))
        let selectNoneBtn = createPillBtn "Select none" (SolidColorBrush.Parse("#27272a"))
        let summaryPanel = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(8.0), Padding = Thickness(16.0), Margin = Thickness(0.0, 10.0, 0.0, 0.0))
        let summaryStack = StackPanel(Spacing = 8.0)
        summaryStack.Children.Add(TextBlock(Text = "Scan Summary", FontWeight = FontWeight.Bold, FontSize = 14.0))
        actionStatusTxt.Text <- "Analyze first to calculate real reclaimable space."
        summaryStack.Children.Add(actionStatusTxt)
        summaryPanel.Child <- summaryStack
        actionPanel.Children.AddRange [scanBtn; executeBtn; selectAllBtn; selectNoneBtn; summaryPanel]
        Grid.SetColumn(actionPanel, 1)
        cleanGrid.Children.AddRange [listContainer; actionPanel]
        Grid.SetRow(cleanGrid, 1)
        
        let bottomStats = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(8.0), Height = 58.0, Margin = Thickness(0.0, 22.0, 0.0, 0.0))
        let bottomStack = StackPanel(Orientation = Orientation.Horizontal, Spacing = 40.0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center)
        totalFreedTxt.Text <- "RECLAIMABLE: 0 B"; totalFreedTxt.Foreground <- Brushes.DodgerBlue; totalFreedTxt.FontSize <- 16.0; totalFreedTxt.FontWeight <- FontWeight.Bold
        totalFilesTxt.Text <- "FILES: 0"; totalFilesTxt.Foreground <- Brushes.Gray; totalFilesTxt.FontSize <- 16.0; totalFilesTxt.FontWeight <- FontWeight.Bold
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
        writeToLog "--- TERMINAL INITIALIZED AND READY ---"
        writeToLog "Plumbyr prototype terminal ready."
        writeToLog "Type 'help' or use the action buttons above."
        
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

        let showView view cat name content =
            currentView <- view
            currentCategory <- cat
            categoryTitle.Text <- name
            transition.Content <- content
            this.PopulateTargets()

        let browserAnalysis = new BrowserAnalysisView(writeToLog, fun () ->
            showView "clean" (Some BrowserCache) "BROWSER CLEANUP" cleanerView)
        this.Closed.Add(fun _ -> (browserAnalysis :> IDisposable).Dispose())

        let executeShellCommand (cmd: string) =
            Thread(fun () ->
                try
                    let psi = ProcessStartInfo("cmd.exe", sprintf "/c %s" cmd)
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.UseShellExecute <- false
                    psi.CreateNoWindow <- true
                    let proc = Process.Start(psi)
                    proc.OutputDataReceived.Add(fun args -> if box args.Data <> null then writeToLog args.Data)
                    proc.ErrorDataReceived.Add(fun args -> if box args.Data <> null then writeToLog (sprintf "ERROR: %s" args.Data))
                    proc.BeginOutputReadLine()
                    proc.BeginErrorReadLine()
                    proc.WaitForExit()
                    writeToLog (sprintf "--- PROCESS EXITED WITH CODE %d ---" proc.ExitCode)
                with ex -> writeToLog (sprintf "EXECUTION ERROR: %s" ex.Message)
            ).Start()

        let runCommandInput () =
            if not (String.IsNullOrWhiteSpace(inputField.Text)) then
                let cmd = inputField.Text.Trim()
                writeToLog (sprintf "> %s" cmd)
                inputField.Text <- ""
                executeShellCommand cmd

        inputField.KeyDown.Add(fun e ->
            if e.Key = Avalonia.Input.Key.Enter then
                runCommandInput()
        )

        runCommandBtn.Click.Add(fun _ -> runCommandInput())
        helpQuickBtn.Click.Add(fun _ -> writeToLog "> help"; executeShellCommand "help")
        networkQuickBtn.Click.Add(fun _ -> writeToLog "> ipconfig /all"; executeShellCommand "ipconfig /all")
        windowsQuickBtn.Click.Add(fun _ -> writeToLog "> ver"; executeShellCommand "ver")
        clearLogBtn.Click.Add(fun _ ->
            logEditor.Clear()
            writeToLog "--- OUTPUT CLEARED ---")

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
            showView "term" None "COMMAND CENTER" terminalView
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
                            showView "term" None "COMMAND CENTER" terminalView
                            writeToLog (sprintf "Terminal logs exported to: %s" file.Name)
                            writeToLog "--- EXPORT COMPLETE ---")
                    with ex -> 
                        Dispatcher.UIThread.Post(fun () -> writeToLog (sprintf "ERROR EXPORTING LOGS: %s" ex.Message))
            } |> Async.StartImmediate

        dashBtn.Click.Add(fun _ -> writeToLog "Switching to Dashboard..."; showView "dash" None "DASHBOARD" dashboardView)
        termBtn.Click.Add(fun _ -> writeToLog "Switching to Command Center..."; showView "term" None "COMMAND CENTER" terminalView)
        sysBtn.Click.Add(fun _ -> writeToLog "Switching to System Cleanup..."; showView "clean" (Some SystemTemporary) "SYSTEM CLEANUP" cleanerView)
        browserBtn.Click.Add(fun _ -> writeToLog "Switching to Browser Analysis..."; showView "browsers" None "BROWSER ANALYSIS" browserAnalysis)
        appsBtn.Click.Add(fun _ -> writeToLog "Switching to App Cleanup..."; showView "clean" (Some ApplicationCache) "APP CLEANUP" cleanerView)
        gameBtn.Click.Add(fun _ -> writeToLog "Switching to Gaming & GPU..."; showView "clean" (Some GamingCache) "GAMING & GPU" cleanerView)
        diagBtn.Click.Add(fun _ -> runSystemDiagnostics())
        diagQuickBtn.Click.Add(fun _ -> runSystemDiagnostics())
        histBtn.Click.Add(fun _ -> runExportHistory())
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
                        healthVal.Foreground <- Brushes.LimeGreen
                        healthCircle.Value <- 100
                        healthValueTxt.Text <- "100%"
                        currentScanResults <- scanEngine.AnalyzeTargets(targets)
                        this.PopulateTargets()
                    )
                with ex -> writeToLog (sprintf "PURGE ERROR: %s" ex.Message)
            ).Start()
        )

        loadSystemInfo()
        this.PopulateTargets(); showView "dash" None "DASHBOARD" dashboardView

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

        for (key, t, pathCount, bytes, files, folders, exists) in groupedTargets do
            let card = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(8.0), Margin = Thickness(0.0, 0.0, 0.0, 6.0), Padding = Thickness(14.0, 10.0))
            let row = Grid(ColumnDefinitions = ColumnDefinitions("*,110,110"))

            let cb = CheckBox(IsChecked = Nullable<bool>(selectedGroups.Contains(key)), VerticalAlignment = VerticalAlignment.Center)
            let nameStack = StackPanel(Spacing = 3.0, Margin = Thickness(10.0, 0.0, 0.0, 0.0))
            let title = TextBlock(Text = t.Name, FontWeight = FontWeight.Bold, FontSize = 14.0)
            nameStack.Children.Add(title)
            let detail =
                let source = t.SourceFile.Replace(".json", "")
                let status = if currentScanResults.IsEmpty then "Not analyzed" elif exists then "Found" else "Not found"
                sprintf "%s | %s | %d rule paths" (CleanTargets.getCategoryName t.Category) status pathCount
            nameStack.Children.Add(TextBlock(Text = detail, FontSize = 11.0, Foreground = Brushes.Gray))
            if not (String.IsNullOrEmpty(t.Description)) then
                nameStack.Children.Add(TextBlock(Text = t.Description, FontSize = 10.0, Foreground = SolidColorBrush.Parse("#8a8a8a"), TextWrapping = TextWrapping.Wrap, MaxHeight = 34.0))
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
            let sizeBlock = TextBlock(Text = sizeText, Foreground = Brushes.DodgerBlue, FontWeight = FontWeight.Bold, FontSize = 13.0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center)
            Grid.SetColumn(sizeBlock, 1); row.Children.Add(sizeBlock)

            let fileText =
                if currentScanResults.IsEmpty then "-"
                else sprintf "%d" files
            let filesBlock = TextBlock(Text = fileText, Foreground = Brushes.Gray, FontSize = 13.0, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center)
            Grid.SetColumn(filesBlock, 2); row.Children.Add(filesBlock)

            card.Child <- row
            targetsList.Children.Add(card)

type App() =
    inherit Application()
    override this.Initialize() = 
        this.Styles.Add(FluentTheme())
        let gridStyle = Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri = null)
        gridStyle.Source <- Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")
        this.Styles.Add(gridStyle)
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
