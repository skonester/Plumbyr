namespace WindowsCleaner

open Avalonia
open Avalonia.Controls
open Avalonia.Controls.ApplicationLifetimes
open Avalonia.Media
open Avalonia.Layout
open Avalonia.Threading
open System
open System.IO
open System.Threading
open System.Diagnostics
open System.Collections.ObjectModel
open Avalonia.Themes.Fluent
open SukiUI
open SukiUI.Controls
open SukiUI.Theme
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

type MainWindow() as this =
    inherit Window()
    
    let scanEngine = ScanEngine()
    let targetsList = StackPanel(Spacing = 8.0)
    let liveLogs = ObservableCollection<string>()
    let totalFreedTxt = TextBlock(FontSize = 18.0, FontWeight = FontWeight.SemiBold)
    let totalFilesTxt = TextBlock(FontSize = 14.0, Foreground = Brushes.Gray)
    
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

    do
        this.Title <- "Plumbyr - Advanced System Maintenance"
        try this.Icon <- WindowIcon("Assets/icon.png") with _ -> ()
        this.Width <- 1200.0; this.Height <- 850.0
        this.WindowStartupLocation <- WindowStartupLocation.CenterScreen
        
        let mainContent = Grid(Background = SolidColorBrush.Parse("#080808"))
        mainContent.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(260.0)))
        mainContent.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Star))
        mainContent.IsHitTestVisible <- true
        
        this.Background <- SolidColorBrush.Parse("#080808")
        this.ExtendClientAreaToDecorationsHint <- false
        this.SystemDecorations <- SystemDecorations.Full
        this.TransparencyLevelHint <- [| WindowTransparencyLevel.None |]
        this.TransparencyBackgroundFallback <- Brushes.Black

        let host = SukiMainHost()
        host.Background <- SolidColorBrush.Parse("#080808")
        host.Content <- mainContent
        this.Content <- host

        // Initialize History
        let historyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plumbyr_history.db")
        CleaningHistory.GetInstance().Initialize(historyPath)

        // Sidebar
        let sidebar = Border(BorderBrush = SolidColorBrush.Parse("#222222"), BorderThickness = Thickness(0.0, 0.0, 1.0, 0.0), Background = SolidColorBrush.Parse("#0c0c0c"), ZIndex = 100)
        let sidebarGrid = Grid(RowDefinitions = RowDefinitions("Auto,*,Auto"))
        
        let header = StackPanel(Margin = Thickness(20.0, 40.0, 20.0, 30.0))
        header.Children.Add(TextBlock(Text = "PLUMBYR", FontSize = 24.0, FontWeight = FontWeight.Black, HorizontalAlignment = HorizontalAlignment.Center))
        header.Children.Add(TextBlock(Text = "v1.0.4 PRO", FontSize = 10.0, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center))
        Grid.SetRow(header, 0); sidebarGrid.Children.Add(header)

        let navStack = StackPanel(Spacing = 8.0, Margin = Thickness(15.0, 0.0, 15.0, 0.0))
        let createNavBtn text svgData =
            let btn = Button(Height = 50.0, HorizontalAlignment = HorizontalAlignment.Stretch, CornerRadius = CornerRadius(12.0), Background = SolidColorBrush.Parse("#0c0c0c"))
            btn.Classes.Add("Flat")
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

        let transition = SukiTransitioningContentControl(IsHitTestVisible = true)
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
        dumpBtn.Classes.Add("Primary")
        
        hardwareStack.Children.AddRange [osGrid; cpuGrid; ramGrid; gpuGrid; dumpBtn]
        leftInfo.Child <- hardwareStack
        Grid.SetColumn(leftInfo, 0)

        let rightInfo = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(12.0), Margin = Thickness(10.0, 0.0, 0.0, 0.0), Padding = Thickness(30.0))
        let healthStack = StackPanel(Spacing = 20.0, VerticalAlignment = VerticalAlignment.Center)
        healthStack.Children.Add(TextBlock(Text = "OPTIMIZATION GRAPH", FontSize = 14.0, FontWeight = FontWeight.Bold, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center))
        let healthCircle: CircleProgressBar = CircleProgressBar(Width = 160.0, Height = 160.0, Value = 0)
        let healthValueTxt: TextBlock = TextBlock(Text = "0%", FontSize = 32.0, FontWeight = FontWeight.Black, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center)
        let circleContainer = Grid()
        circleContainer.Children.AddRange [healthCircle; healthValueTxt]
        healthStack.Children.Add(circleContainer)
        healthStack.Children.Add(TextBlock(Text = "System Health Score", FontSize = 16.0, HorizontalAlignment = HorizontalAlignment.Center))
        rightInfo.Child <- healthStack
        Grid.SetColumn(rightInfo, 1)
        
        Grid.SetRow(infoGrid, 2)
        dashGrid.Children.AddRange [welcomeHeader; statsRow; infoGrid]
        dashboardView.Content <- dashGrid

        // Terminal View
        let logGrid = Grid(RowDefinitions = RowDefinitions("Auto,*,Auto"), Margin = Thickness(30.0))
        let logHeader = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(8.0), Padding = Thickness(20.0), Margin = Thickness(0.0, 0.0, 0.0, 15.0))
        logHeader.Child <- TextBlock(Text = "COMMAND CENTER TERMINAL", FontWeight = FontWeight.Bold, Foreground = Brushes.LimeGreen, FontSize = 16.0)
        Grid.SetRow(logHeader, 0)
        
        let logEditor = TextEditor(Background = SolidColorBrush.Parse("#080808"), Foreground = Brushes.LimeGreen, FontSize = 13.0, FontFamily = FontFamily("Consolas"), IsReadOnly = true, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Visible, WordWrap = true)
        logEditor.Options.EnableRectangularSelection <- true
        logEditor.Options.EnableTextDragDrop <- true
        
        Grid.SetRow(logEditor, 1)
        
        let inputField = TextBox(Watermark = "Type a command (e.g. dir, ipconfig, sfc /scannow) and press Enter...", Height = 45.0, VerticalContentAlignment = VerticalAlignment.Center, Margin = Thickness(0.0, 15.0, 0.0, 0.0), Background = SolidColorBrush.Parse("#121212"), Foreground = Brushes.White, BorderThickness = Thickness(1.0), BorderBrush = SolidColorBrush.Parse("#333"))
        Grid.SetRow(inputField, 2)
        
        logGrid.Children.AddRange [logHeader; logEditor; inputField]
        terminalView.Content <- logGrid

        // Cleaner View
        let cleanerMain = Grid(RowDefinitions = RowDefinitions("*,Auto"), Margin = Thickness(30.0))
        let cleanGrid = Grid(ColumnDefinitions = ColumnDefinitions("*,380"))
        let listContainer = Grid(RowDefinitions = RowDefinitions("Auto,*"))
        categoryTitle.Margin <- Thickness(0.0, 0.0, 0.0, 25.0); categoryTitle.FontSize <- 32.0
        Grid.SetRow(categoryTitle, 0)
        let scroll = ScrollViewer(Content = targetsList)
        Grid.SetRow(scroll, 1)
        listContainer.Children.AddRange [categoryTitle; scroll]
        Grid.SetColumn(listContainer, 0)
        
        let actionPanel = StackPanel(Spacing = 20.0, Margin = Thickness(30.0, 60.0, 0.0, 0.0))
        let createPillBtn text (color: IBrush) =
            let btn = Button(Content = text, Height = 70.0, HorizontalAlignment = HorizontalAlignment.Stretch, FontWeight = FontWeight.Black, FontSize = 18.0, CornerRadius = CornerRadius(35.0), Background = color)
            btn
        let scanBtn = createPillBtn "ANALYZE FOLDERS" (SolidColorBrush.Parse("#2563eb"))
        let executeBtn = createPillBtn "EXECUTE PURGE" (SolidColorBrush.Parse("#16a34a"))
        actionPanel.Children.AddRange [scanBtn; executeBtn]
        Grid.SetColumn(actionPanel, 1)
        cleanGrid.Children.AddRange [listContainer; actionPanel]
        Grid.SetRow(cleanGrid, 0)
        
        let bottomStats = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(12.0), Height = 70.0, Margin = Thickness(0.0, 30.0, 0.0, 0.0))
        let bottomStack = StackPanel(Orientation = Orientation.Horizontal, Spacing = 40.0, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center)
        totalFreedTxt.Text <- "RECLAIMED: 0.00 GB"; totalFreedTxt.Foreground <- Brushes.DodgerBlue; totalFreedTxt.FontSize <- 16.0; totalFreedTxt.FontWeight <- FontWeight.Bold
        totalFilesTxt.Text <- "FILES PURGED: 0"; totalFilesTxt.Foreground <- Brushes.Gray; totalFilesTxt.FontSize <- 16.0; totalFilesTxt.FontWeight <- FontWeight.Bold
        bottomStack.Children.AddRange [totalFreedTxt; totalFilesTxt]
        bottomStats.Child <- bottomStack
        Grid.SetRow(bottomStats, 1)
        cleanerMain.Children.AddRange [cleanGrid; bottomStats]
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
        
        // Initialize Serilog with Sink
        Log.Logger <- LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plumbyr_log.txt"), rollingInterval = RollingInterval.Day)
            .WriteTo.Sink(TerminalSink(writeToLog))
            .CreateLogger()
            
        Log.Information("Plumbyr Started")
        writeToLog "--- TERMINAL INITIALIZED AND READY ---"
        writeToLog "Plumbyr Windows Cleaner v1.0.0 (Opaque Edition)"
        writeToLog "Type 'help' for available commands."
        
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

        inputField.KeyDown.Add(fun e ->
            if e.Key = Avalonia.Input.Key.Enter && not (String.IsNullOrWhiteSpace(inputField.Text)) then
                let cmd = inputField.Text
                writeToLog (sprintf "> %s" cmd)
                inputField.Text <- ""
                executeShellCommand cmd
        )

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

        let runDxDiagDump () =
            showView "term" None "COMMAND CENTER" terminalView
            writeToLog "--- INITIATING FULL SYSTEM DUMP (DXDIAG) ---"
            Thread(fun () ->
                try
                    let tempFile = Path.Combine(Path.GetTempPath(), "plumbyr_dxdiag.txt")
                    let proc = Process.Start("dxdiag", sprintf "/t %s" tempFile)
                    proc.WaitForExit()
                    if File.Exists(tempFile) then
                        let lines = File.ReadAllLines(tempFile) |> Array.truncate 200
                        for line in lines do writeToLog line
                        writeToLog "--- DUMP COMPLETE ---"
                    else writeToLog "ERROR: DxDiag failed."
                with ex -> writeToLog (sprintf "EXCEPTION: %s" ex.Message)
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
        browserBtn.Click.Add(fun _ -> writeToLog "Switching to Browser Cleanup..."; showView "clean" (Some BrowserCache) "BROWSER CLEANUP" cleanerView)
        appsBtn.Click.Add(fun _ -> writeToLog "Switching to App Cleanup..."; showView "clean" (Some ApplicationCache) "APP CLEANUP" cleanerView)
        gameBtn.Click.Add(fun _ -> writeToLog "Switching to Gaming & GPU..."; showView "clean" (Some GamingCache) "GAMING & GPU" cleanerView)
        diagBtn.Click.Add(fun _ -> runDxDiagDump())
        histBtn.Click.Add(fun _ -> runExportHistory())
        dumpBtn.Click.Add(fun _ -> runDxDiagDump())
        
        scanBtn.Click.Add(fun _ -> 
            showView "term" None "ANALYZING..." terminalView
            writeToLog "--- STARTING SYSTEM ANALYSIS ---"
            Thread(fun () -> 
                try
                    scanEngine.DetectApplications()
                    writeToLog "ANALYSIS COMPLETE."
                    Dispatcher.UIThread.Post(fun () -> 
                        healthVal.Text <- "OPTIMIZING"
                        healthCircle.Value <- 60
                        healthValueTxt.Text <- "60%"
                    )
                with ex -> writeToLog (sprintf "ANALYSIS ERROR: %s" ex.Message)
            ).Start()
        )
        
        executeBtn.Click.Add(fun _ -> 
            showView "term" None "PURGING..." terminalView
            writeToLog "--- INITIATING SYSTEM PURGE ---"
            Thread(fun () -> 
                try
                    scanEngine.CleanAll()
                    scanEngine.SaveStatsToDatabase()
                    let stats = scanEngine.GetStats()
                    Dispatcher.UIThread.Post(fun () ->
                        let freedSize = ByteSize.FromBytes(float stats.BytesFreed)
                        totalFreedTxt.Text <- sprintf "RECLAIMED: %s" (freedSize.ToString())
                        totalFilesTxt.Text <- sprintf "FILES PURGED: %d" stats.FilesDeleted
                        reclaimVal.Text <- sprintf "%s" (freedSize.ToString())
                        filesVal.Text <- string stats.FilesDeleted
                        writeToLog (sprintf "PURGE COMPLETE. Total Reclaimed: %s" (freedSize.ToString()))
                        healthVal.Text <- "CLEAN"
                        healthVal.Foreground <- Brushes.LimeGreen
                        healthCircle.Value <- 100
                        healthValueTxt.Text <- "100%"
                    )
                with ex -> writeToLog (sprintf "PURGE ERROR: %s" ex.Message)
            ).Start()
        )

        loadSystemInfo()
        this.PopulateTargets(); showView "dash" None "DASHBOARD" dashboardView

    member private this.PopulateTargets() =
        targetsList.Children.Clear()
        let targets = 
            match currentCategory with
            | Some cat -> CleanTargets.getAllTargets() |> List.filter (fun t -> t.Category = cat)
            | None -> CleanTargets.getAllTargets()
        for t in targets do
            let card = Border(Background = SolidColorBrush.Parse("#121212"), CornerRadius = CornerRadius(8.0), Margin = Thickness(0.0, 0.0, 0.0, 5.0), Padding = Thickness(15.0, 10.0))
            let stack = DockPanel()
            let cb = CheckBox(IsChecked = Nullable<bool>(true), VerticalAlignment = VerticalAlignment.Center)
            let nameStack = StackPanel(Spacing = 2.0, Margin = Thickness(10.0, 0.0, 0.0, 0.0))
            nameStack.Children.Add(TextBlock(Text = t.Name, FontWeight = FontWeight.Bold, FontSize = 14.0))
            if not (String.IsNullOrEmpty(t.Description)) then
                nameStack.Children.Add(TextBlock(Text = t.Description, FontSize = 10.0, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap))
            cb.Content <- nameStack
            DockPanel.SetDock(cb, Dock.Left)
            let infoStack = StackPanel(HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center)
            if t.NeedsAdmin then
                infoStack.Children.Add(Border(Background = Brushes.DarkRed, CornerRadius = CornerRadius(4.0), Padding = Thickness(5.0, 2.0), Margin = Thickness(0.0, 0.0, 0.0, 5.0), Child = TextBlock(Text = "ADMIN", FontSize = 9.0, FontWeight = FontWeight.Bold)))
            infoStack.Children.Add(TextBlock(Text = t.EstimatedSize, Foreground = Brushes.DodgerBlue, FontSize = 12.0, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Right))
            stack.Children.AddRange [cb; infoStack]
            card.Child <- stack
            targetsList.Children.Add(card)

type App() =
    inherit Application()
    override this.Initialize() = 
        this.Styles.Add(FluentTheme())
        let suki = SukiTheme()
        this.Styles.Add(suki)
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
            AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(argv)
        with ex ->
            try File.WriteAllText("crash_log.txt", ex.ToString()) with _ -> ()
            printfn "CRASH: %s" ex.Message
            1
