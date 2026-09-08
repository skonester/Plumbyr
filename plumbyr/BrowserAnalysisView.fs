namespace WindowsCleaner

open System
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Data
open ByteSizeLib

type BrowserUsageRow(target: BrowserCacheUsage) =
    member _.Label = target.Label
    member _.Path = target.Path
    member _.Size = ByteSize.FromBytes(float target.Bytes).ToString()
    member _.Files = target.Files
    member _.Status = if target.Skipped = 0L then "Measured" else sprintf "%d entries skipped" target.Skipped

type BrowserAnalysisView(writeToLog: string -> unit, openCleanup: unit -> unit) as this =
    inherit UserControl()

    let mutable activeScan: CancellationTokenSource option = None
    let mutable disposed = false
    let analyzeButton = Button(Content = "Analyze browser caches", Padding = Thickness(18.0, 12.0))
    let cancelButton = Button(Content = "Cancel", IsEnabled = false, Padding = Thickness(18.0, 12.0))
    let status = TextBlock(Text = "Ready to analyze Chromium browser caches for this Windows account.", TextWrapping = TextWrapping.Wrap)
    let progress = ProgressBar(IsIndeterminate = true, IsVisible = false, Height = 3.0)
    let results = DataGrid(AutoGenerateColumns = false, IsReadOnly = true, CanUserResizeColumns = true,
                           GridLinesVisibility = DataGridGridLinesVisibility.Horizontal)

    do
        let layout = Grid(RowDefinitions = RowDefinitions("Auto,Auto,Auto,Auto,*"), Margin = Thickness(30.0))
        let heading = StackPanel(Spacing = 10.0, Margin = Thickness(0.0, 0.0, 0.0, 22.0))
        heading.Children.Add(TextBlock(Text = "Browser cache analysis", FontSize = 30.0, FontWeight = FontWeight.Bold))
        heading.Children.Add(TextBlock(
            Text = "Find cache folders for Chrome, Edge, Brave, Opera and other Chromium browsers. Sizes show current disk usage; this analysis does not delete files.",
            Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap))
        layout.Children.Add(heading)

        let actions = StackPanel(Orientation = Orientation.Horizontal, Spacing = 12.0, Margin = Thickness(0.0, 0.0, 0.0, 18.0))
        let cleanupButton = Button(Content = "Open cleanup rules", Padding = Thickness(18.0, 12.0))
        cleanupButton.Click.Add(fun _ -> openCleanup())
        actions.Children.AddRange [analyzeButton; cancelButton; cleanupButton]
        Grid.SetRow(actions, 1)
        layout.Children.Add(actions)
        status.Margin <- Thickness(0.0, 0.0, 0.0, 16.0)
        Grid.SetRow(status, 2)
        layout.Children.Add(status)
        Grid.SetRow(progress, 3)
        layout.Children.Add(progress)

        let column title property width =
            DataGridTextColumn(Header = title, Binding = Binding(property), Width = width)
        results.Columns.Add(column "Cache" "Label" (DataGridLength(260.0)))
        results.Columns.Add(column "Size" "Size" (DataGridLength(100.0)))
        results.Columns.Add(column "Files" "Files" (DataGridLength(80.0)))
        results.Columns.Add(column "Status" "Status" (DataGridLength(150.0)))
        results.Columns.Add(column "Path" "Path" (DataGridLength(420.0)))
        Grid.SetRow(results, 4)
        layout.Children.Add(results)
        this.Content <- layout

        cancelButton.Click.Add(fun _ ->
            activeScan |> Option.iter (fun scan -> scan.Cancel())
            cancelButton.IsEnabled <- false
            status.Text <- "Cancelling browser analysis...")

        analyzeButton.Click.Add(fun _ ->
            if activeScan.IsNone && not disposed then
                let scan = new CancellationTokenSource()
                activeScan <- Some scan
                analyzeButton.IsEnabled <- false
                cancelButton.IsEnabled <- true
                progress.IsVisible <- true
                results.ItemsSource <- [||]
                status.Text <- "Finding and measuring browser caches..."
                writeToLog "Browser cache analysis started."
                task {
                    try
                        try
                            let! report = KuduBridge.analyze scan.Token
                            if not disposed then
                                let targets = report.Targets |> List.sortByDescending (fun target -> target.Bytes)
                                results.ItemsSource <- targets |> List.map BrowserUsageRow |> List.toArray
                                let bytes = targets |> List.sumBy (fun target -> target.Bytes)
                                let files = targets |> List.sumBy (fun target -> target.Files)
                                let skipped = targets |> List.sumBy (fun target -> target.Skipped)
                                let summary =
                                    if targets.IsEmpty then "No Chromium cache folders found for this Windows account."
                                    else sprintf "%d cache folders | %s on disk | %d files" targets.Length (ByteSize.FromBytes(float bytes).ToString()) files
                                let incomplete =
                                    if skipped > 0L || not report.Warnings.IsEmpty then
                                        sprintf " Some locations could not be measured (%d entries skipped). Totals may be incomplete." skipped
                                    else ""
                                status.Text <- summary + incomplete
                                writeToLog ("Browser analysis complete. " + status.Text)
                                for warning in report.Warnings do writeToLog warning
                        with
                        | :? OperationCanceledException ->
                            if not disposed then status.Text <- "Browser analysis cancelled."
                            writeToLog "Browser analysis cancelled."
                        | ex ->
                            if not disposed then status.Text <- ex.Message
                            writeToLog ("Browser analysis failed: " + ex.Message)
                    finally
                        activeScan <- None
                        scan.Dispose()
                        if not disposed then
                            analyzeButton.IsEnabled <- true
                            cancelButton.IsEnabled <- false
                            progress.IsVisible <- false
                } |> ignore)

    interface IDisposable with
        member _.Dispose() =
            disposed <- true
            activeScan |> Option.iter (fun scan -> scan.Cancel())
