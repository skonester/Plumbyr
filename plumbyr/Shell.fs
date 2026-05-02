namespace WindowsCleaner

open Avalonia
open Avalonia.Media
open Avalonia.Layout
open Elmish
open System
open Avalonia.FuncUI.DSL

module Shell =
    
    type TargetItem = {
        Target: CleanTarget
        IsSelected: bool
    }

    type State = {
        IsScanning: bool
        IsCleaning: bool
        Targets: TargetItem list
        OverallStats: AggregatedStats
        CurrentProgress: string
        ActiveTab: string 
    }

    let init () =
        let allTargets = CleanTargets.getAllTargets() |> List.map (fun t -> { Target = t; IsSelected = true })
        let history = CleaningHistory.GetInstance()
        {
            IsScanning = false
            IsCleaning = false
            Targets = allTargets
            OverallStats = history.GetStatsOverall()
            CurrentProgress = "Ready"
            ActiveTab = "Dashboard"
        }, Cmd.none

    type Msg =
        | ToggleTarget of string * bool
        | SelectCategory of CleanCategory * bool
        | SetTab of string
        | StartScan
        | ScanFinished of string list
        | StartClean
        | CleanFinished of CleanStats
        | UpdateProgress of string

    let update msg state =
        match msg with
        | ToggleTarget (name, isSelected) ->
            let newTargets = state.Targets |> List.map (fun t -> if t.Target.Name = name then { t with IsSelected = isSelected } else t)
            { state with Targets = newTargets }, Cmd.none
        
        | SelectCategory (cat, isSelected) ->
            let newTargets = state.Targets |> List.map (fun t -> if t.Target.Category = cat then { t with IsSelected = isSelected } else t)
            { state with Targets = newTargets }, Cmd.none

        | SetTab tab ->
            { state with ActiveTab = tab }, Cmd.none

        | StartScan ->
            { state with IsScanning = true; CurrentProgress = "Analyzing system..." }, Cmd.OfFunc.either (fun _ ->
                let engine = ScanEngine()
                engine.DetectApplications()
                engine.GetDetectedApps()
            ) () ScanFinished (fun _ -> ScanFinished [])

        | ScanFinished apps ->
            { state with IsScanning = false; CurrentProgress = sprintf "Scan complete. %d locations detected." apps.Length }, Cmd.none

        | StartClean ->
            { state with IsCleaning = true; CurrentProgress = "Cleaning..." }, Cmd.OfFunc.either (fun _ ->
                let engine = ScanEngine()
                engine.CleanAll() 
                engine.SaveStatsToDatabase()
                engine.GetStats()
            ) () CleanFinished (fun _ -> CleanFinished { BytesFreed = 0uL; FilesDeleted = 0; FoldersDeleted = 0; Errors = 0; Warnings = 0 })

        | CleanFinished _ ->
            let history = CleaningHistory.GetInstance()
            { state with 
                IsCleaning = false 
                OverallStats = history.GetStatsOverall()
                CurrentProgress = "System Cleaned!" 
            }, Cmd.none

        | UpdateProgress p ->
            { state with CurrentProgress = p }, Cmd.none

    let dashboardView state dispatch =
        StackPanel.create [
            StackPanel.spacing 20.0
            StackPanel.children [
                TextBlock.create [
                    TextBlock.text "System Dashboard"
                    TextBlock.fontSize 32.0
                    TextBlock.fontWeight FontWeight.ExtraBold
                    TextBlock.foreground Brushes.White
                ]
                UniformGrid.create [
                    UniformGrid.columns 3
                    UniformGrid.children [
                        Components.statsBox "TOTAL SPACE FREED" (sprintf "%.2f MB" (float state.OverallStats.TotalBytesFreed / 1048576.0)) "💾"
                        Components.statsBox "FILES DELETED" (string state.OverallStats.TotalFilesDeleted) "📄"
                        Components.statsBox "CLEAN SESSIONS" (string state.OverallStats.SessionCount) "⏱️"
                    ]
                ]
                Border.create [
                    Border.background (SolidColorBrush.Parse "#1e1e1e")
                    Border.cornerRadius 15.0
                    Border.padding 20.0
                    Border.child (
                        StackPanel.create [
                            StackPanel.children [
                                TextBlock.create [ TextBlock.text "System Health"; TextBlock.fontWeight FontWeight.Bold; TextBlock.margin (0.0, 0.0, 0.0, 10.0) ]
                                ProgressBar.create [
                                    ProgressBar.value 85.0
                                    ProgressBar.height 8.0
                                    ProgressBar.foreground Brushes.LimeGreen
                                    ProgressBar.background (SolidColorBrush.Parse "#333")
                                ]
                                TextBlock.create [ TextBlock.text "Your system is in good condition."; TextBlock.fontSize 12.0; TextBlock.foreground Brushes.Gray; TextBlock.margin (0.0, 10.0, 0.0, 0.0) ]
                            ]
                        ]
                    )
                ]
            ]
        ]

    let cleanView state dispatch =
        DockPanel.create [
            DockPanel.children [
                // Right Action Bar
                StackPanel.create [
                    StackPanel.dock Dock.Right
                    StackPanel.width 250.0
                    StackPanel.margin (20.0, 0.0, 0.0, 0.0)
                    StackPanel.spacing 15.0
                    StackPanel.children [
                        Border.create [
                            Border.background (SolidColorBrush.Parse "#252525")
                            Border.cornerRadius 12.0
                            Border.padding 20.0
                            Border.child (
                                StackPanel.create [
                                    StackPanel.spacing 10.0
                                    StackPanel.children [
                                        TextBlock.create [ TextBlock.text "Actions"; TextBlock.fontWeight FontWeight.Bold ]
                                        Button.create [
                                            Button.content "Run Analysis"
                                            Button.horizontalAlignment HorizontalAlignment.Stretch
                                            Button.padding 15.0
                                            Button.background Brushes.DodgerBlue
                                            Button.isEnabled (not state.IsScanning && not state.IsCleaning)
                                            Button.onClick (fun _ -> dispatch StartScan)
                                        ]
                                        Button.create [
                                            Button.content "Clean Selected"
                                            Button.horizontalAlignment HorizontalAlignment.Stretch
                                            Button.padding 15.0
                                            Button.background Brushes.SeaGreen
                                            Button.isEnabled (not state.IsScanning && not state.IsCleaning)
                                            Button.onClick (fun _ -> dispatch StartClean)
                                        ]
                                    ]
                                ]
                            )
                        ]
                        TextBlock.create [
                            TextBlock.text state.CurrentProgress
                            TextBlock.fontSize 11.0
                            TextBlock.foreground Brushes.DodgerBlue
                            TextBlock.textWrapping TextWrapping.Wrap
                        ]
                        if state.IsScanning || state.IsCleaning then
                            ProgressBar.create [ ProgressBar.isIndeterminate true; ProgressBar.height 2.0 ]
                    ]
                ]

                // Main Selection Area
                ScrollViewer.create [
                    ScrollViewer.content (
                        StackPanel.create [
                            StackPanel.spacing 15.0
                            StackPanel.children [
                                TextBlock.create [ TextBlock.text "Select targets to clean"; TextBlock.fontSize 20.0; TextBlock.fontWeight FontWeight.Bold ]
                                
                                // Category Groups
                                for cat in [SystemTemporary; BrowserCache; DevelopmentTools; GamingCache; WindowsLogs; ApplicationCache] do
                                    let catName = CleanTargets.getCategoryName cat
                                    let targetsInCat = state.Targets |> List.filter (fun t -> t.Target.Category = cat)
                                    let allSelected = targetsInCat |> List.forall (fun t -> t.IsSelected)
                                    
                                    Expander.create [
                                        Expander.header (
                                            StackPanel.create [
                                                StackPanel.orientation Orientation.Horizontal
                                                StackPanel.spacing 10.0
                                                StackPanel.children [
                                                    CheckBox.create [
                                                        CheckBox.isChecked allSelected
                                                        CheckBox.onChecked (fun _ -> dispatch (SelectCategory (cat, true)))
                                                        CheckBox.onUnchecked (fun _ -> dispatch (SelectCategory (cat, false)))
                                                    ]
                                                    TextBlock.create [ TextBlock.text catName; TextBlock.verticalAlignment VerticalAlignment.Center ]
                                                ]
                                            ]
                                        )
                                        Expander.content (
                                            StackPanel.create [
                                                StackPanel.margin (30.0, 0.0, 0.0, 0.0)
                                                StackPanel.children [
                                                    for item in targetsInCat do
                                                        Components.categoryCard item.Target.Name item.Target.EstimatedSize item.IsSelected (fun sel -> dispatch (ToggleTarget (item.Target.Name, sel)))
                                                ]
                                            ]
                                        )
                                    ]
                            ]
                        ]
                    )
                ]
            ]
        ]

    let view state dispatch =
        DockPanel.create [
            DockPanel.background (SolidColorBrush.Parse "#121212")
            DockPanel.children [
                // Sidebar Navigation
                StackPanel.create [
                    StackPanel.dock Dock.Left
                    StackPanel.width 80.0
                    StackPanel.background (SolidColorBrush.Parse "#181818")
                    StackPanel.spacing 10.0
                    StackPanel.padding (0.0, 40.0)
                    StackPanel.children [
                        for tab in ["Dashboard"; "Clean"; "History"] do
                            Button.create [
                                Button.content (match tab with | "Dashboard" -> "🏠" | "Clean" -> "🧹" | "History" -> "📜" | _ -> "")
                                Button.fontSize 24.0
                                Button.width 60.0
                                Button.height 60.0
                                Button.margin 10.0
                                Button.background (if state.ActiveTab = tab then SolidColorBrush.Parse("#2d2d2d") else Brushes.Transparent)
                                Button.borderThickness (if state.ActiveTab = tab then 1.0 else 0.0)
                                Button.borderBrush Brushes.DodgerBlue
                                Button.onClick (fun _ -> dispatch (SetTab tab))
                                Button.cornerRadius 12.0
                            ]
                    ]
                ]

                // Content Area
                Border.create [
                    Border.padding 30.0
                    Border.child (
                        match state.ActiveTab with
                        | "Dashboard" -> dashboardView state dispatch
                        | "Clean" -> cleanView state dispatch
                        | _ -> TextBlock.create [ TextBlock.text "History View - Coming Soon" ]
                    )
                ]
            ]
        ]
