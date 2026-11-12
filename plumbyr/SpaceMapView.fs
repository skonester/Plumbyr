namespace WindowsCleaner

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Platform.Storage
open Avalonia.Threading
open ByteSizeLib

type private SpaceDrive = { Root: string; Title: string }
    with override this.ToString() = this.Title

/// Avalonia adaptation of Roomy's folder-size map and biggest-files panel.
type SpaceMapView(writeToLog: string -> unit, storage: IStorageProvider) as this =
    inherit UserControl()

    // This view is constructed before attachment to Plumbyr's dark window.
    let brush (key: string) = Application.Current.FindResource(Avalonia.Styling.ThemeVariant.Dark, key) :?> IBrush
    let muted = brush "ThemeForegroundLowBrush"
    let primary = brush "PrimaryBrush"
    let cardBrush = SolidColorBrush.Parse("#121212")
    let bytes size = ByteSize.FromBytes(float size).ToString()
    let mutable activeScan: CancellationTokenSource option = None
    let mutable disposed = false
    let mutable chosenFolder: string option = None
    let mutable current: SpaceNode option = None
    let mutable unseen = 0L
    let mutable shownFolders = 200

    let drivePicker = ComboBox(MinWidth = 250.0, MaxWidth = 330.0)
    let scanButton = Button(Content = "Scan", Padding = Thickness(18.0, 10.0),
                            Theme = (Application.Current.FindResource("SolidButtonTheme") :?> Avalonia.Styling.ControlTheme))
    let cancelButton = Button(Content = "Cancel", IsEnabled = false, Padding = Thickness(16.0, 10.0))
    let folderButton = Button(Content = "Choose folder", Padding = Thickness(16.0, 10.0))
    let status = TextBlock(Text = "Pick a drive and select Scan to see what's using your space.",
                           Foreground = muted, TextWrapping = TextWrapping.Wrap)
    let sourceLabel = TextBlock(Foreground = muted, TextWrapping = TextWrapping.Wrap, IsVisible = false)
    let progressBar = ProgressBar(IsIndeterminate = true, IsVisible = false, Height = 3.0)
    let upButton = Button(Content = "Up", IsEnabled = false, Padding = Thickness(12.0, 7.0))
    let revealButton = Button(Content = "Show folder", IsEnabled = false, Padding = Thickness(12.0, 7.0))
    let crumbs = WrapPanel(Orientation = Orientation.Horizontal)
    let currentSize = TextBlock(FontSize = 21.0, FontWeight = FontWeight.SemiBold)
    let rows = StackPanel(Spacing = 6.0)
    let bigRows = StackPanel(Spacing = 6.0)
    let empty = TextBlock(Text = "See exactly what's using your space.\nScan a drive or choose a folder to begin.",
                          Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = Thickness(8.0, 28.0))

    let reveal (path: string) =
        try
            if not (Directory.Exists(path) || File.Exists(path)) then
                raise (FileNotFoundException("That location is no longer available. Rescan to refresh the map."))
            let start = ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                                         UseShellExecute = true, Arguments = "/select,\"" + path + "\"")
            Process.Start(start) |> ignore
        with ex ->
            status.Text <- "Couldn't open File Explorer: " + ex.Message
            writeToLog status.Text

    let clearResults () =
        current <- None
        unseen <- 0L
        rows.Children.Clear()
        rows.Children.Add(empty)
        bigRows.Children.Clear()
        bigRows.Children.Add(TextBlock(Text = "Your 40 biggest files will appear here after a scan.",
                                       Foreground = muted, TextWrapping = TextWrapping.Wrap))
        crumbs.Children.Clear()
        currentSize.Text <- ""
        upButton.IsEnabled <- false
        revealButton.IsEnabled <- false
        scanButton.Content <- "Scan"

    let rec navigate (node: SpaceNode) =
        current <- Some node
        shownFolders <- 200
        crumbs.Children.Clear()
        let chain = ResizeArray<SpaceNode>()
        let mutable ancestor = Some node
        while ancestor.IsSome do
            let item = ancestor.Value
            chain.Insert(0, item)
            ancestor <- item.Parent
        for index = 0 to chain.Count - 1 do
            let target = chain[index]
            if index > 0 then
                crumbs.Children.Add(TextBlock(Text = "/", Foreground = muted, Margin = Thickness(5.0), VerticalAlignment = VerticalAlignment.Center))
            let crumb = Button(Content = target.Name, Padding = Thickness(7.0), MaxWidth = 220.0)
            ToolTip.SetTip(crumb, target.FullPath)
            crumb.Click.Add(fun _ -> navigate target)
            crumbs.Children.Add(crumb)
        upButton.IsEnabled <- node.Parent.IsSome
        revealButton.IsEnabled <- true
        currentSize.Text <- bytes node.Size
        renderRows node

    and renderRows (node: SpaceNode) =
        rows.Children.Clear()
        let atRoot = node.Parent.IsNone
        let denominator = node.Size + (if atRoot then unseen else 0L)
        let maxSize =
            seq {
                yield node.FileBytes
                if atRoot then yield unseen
                for child in node.Children do yield child.Size
            } |> Seq.max |> max 1L

        let addRow name size (openFolder: (unit -> unit) option) (path: string option) =
            let row = Grid(ColumnDefinitions = ColumnDefinitions("*,Auto"), Margin = Thickness(0.0, 2.0))
            let content = Grid(RowDefinitions = RowDefinitions("Auto,Auto"), ColumnDefinitions = ColumnDefinitions("*,Auto"))
            let title = TextBlock(Text = name, FontSize = 13.0, TextTrimming = TextTrimming.CharacterEllipsis)
            content.Children.Add(title)
            let amount = TextBlock(Text = sprintf "%s  ·  %.1f%%" (bytes size) (if denominator > 0L then float size * 100.0 / float denominator else 0.0),
                                   Foreground = muted, FontSize = 12.0, Margin = Thickness(12.0, 0.0, 0.0, 0.0))
            Grid.SetColumn(amount, 1)
            content.Children.Add(amount)
            let bar = ProgressBar(Minimum = 0.0, Maximum = float maxSize, Value = float size,
                                  Foreground = (if openFolder.IsSome then primary else muted),
                                  Height = 5.0, Margin = Thickness(0.0, 9.0, 0.0, 0.0), IsHitTestVisible = false)
            Grid.SetRow(bar, 1)
            Grid.SetColumnSpan(bar, 2)
            content.Children.Add(bar)
            let body = Button(Content = content, Padding = Thickness(10.0),
                              HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch)
            body.Click.Add(fun _ -> openFolder |> Option.iter (fun action -> action()))
            ToolTip.SetTip(body, path |> Option.defaultValue "Used space not accounted for by readable files, including filesystem metadata.")
            row.Children.Add(body)
            match path with
            | Some value ->
                let show = Button(Content = "…", Padding = Thickness(9.0), Margin = Thickness(4.0, 0.0, 0.0, 0.0), VerticalAlignment = VerticalAlignment.Center)
                ToolTip.SetTip(show, "Show in File Explorer")
                show.Click.Add(fun _ -> reveal value)
                Grid.SetColumn(show, 1)
                row.Children.Add(show)
            | None -> ()
            rows.Children.Add(row)

        // Keep large directories responsive without silently dropping smaller folders.
        let entries =
            seq {
                for child in node.Children |> Seq.truncate shownFolders do
                    yield (child.Name + "  ›"), child.Size, Some(fun () -> navigate child), Some child.FullPath
                if node.FileCount > 0L then
                    yield (sprintf "Files in this folder (%s)" (node.FileCount.ToString("N0"))), node.FileBytes, None, Some node.FullPath
                if atRoot && unseen > 0L then
                    yield "Other used space / inaccessible files", unseen, None, None
            }
        for name, size, openFolder, path in entries |> Seq.sortByDescending (fun (_, size, _, _) -> size) do
            addRow name size openFolder path
        if node.Children.Count > shownFolders then
            let more = Button(Content = sprintf "Show more folders (%d remaining)" (node.Children.Count - shownFolders), Margin = Thickness(10.0))
            more.Click.Add(fun _ -> shownFolders <- shownFolders + 200; renderRows node)
            rows.Children.Add(more)
        if rows.Children.Count = 0 then
            rows.Children.Add(TextBlock(Text = "No readable files or subfolders in this folder.", Foreground = muted,
                                        TextWrapping = TextWrapping.Wrap, Margin = Thickness(10.0)))

    let scan (path: string) used =
        if activeScan.IsNone && not disposed then
            let cancellation = new CancellationTokenSource()
            activeScan <- Some cancellation
            clearResults()
            rows.Children.Clear()
            rows.Children.Add(TextBlock(Text = "Mapping folders… Large drives can take a few minutes.",
                                        Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = Thickness(10.0, 28.0)))
            sourceLabel.Text <- path
            sourceLabel.IsVisible <- true
            scanButton.IsEnabled <- false
            folderButton.IsEnabled <- false
            drivePicker.IsEnabled <- false
            cancelButton.IsEnabled <- true
            progressBar.IsVisible <- true
            status.Text <- "Starting space map…"
            writeToLog ("Space map started: " + path)
            let updates =
                { new IProgress<SpaceScanProgress> with
                    member _.Report(value) =
                        Dispatcher.UIThread.Post(fun () ->
                            if not disposed && activeScan = Some cancellation && not cancellation.IsCancellationRequested then
                                status.Text <- sprintf "%s files · %s · %s folders" (value.Files.ToString("N0")) (bytes value.Bytes) (value.Folders.ToString("N0"))) }
            task {
                try
                    try
                        let! report = Task.Run((fun () -> SpaceScanner.scan path cancellation.Token updates), cancellation.Token)
                        cancellation.Token.ThrowIfCancellationRequested()
                        if not disposed then
                            unseen <- max 0L (used - report.Root.Size)
                            navigate report.Root
                            bigRows.Children.Clear()
                            for file in report.BigFiles do
                                let details = StackPanel(Spacing = 4.0)
                                details.Children.Add(TextBlock(Text = Path.GetFileName(file.FullPath), TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 13.0))
                                details.Children.Add(TextBlock(Text = bytes file.Size, FontWeight = FontWeight.SemiBold, Foreground = primary))
                                details.Children.Add(TextBlock(Text = Path.GetDirectoryName(file.FullPath), TextTrimming = TextTrimming.CharacterEllipsis, Foreground = muted, FontSize = 11.0))
                                let button = Button(Content = details, Padding = Thickness(10.0), HorizontalAlignment = HorizontalAlignment.Stretch,
                                                    HorizontalContentAlignment = HorizontalAlignment.Stretch)
                                ToolTip.SetTip(button, file.FullPath + "\nShow in File Explorer")
                                button.Click.Add(fun _ -> reveal file.FullPath)
                                bigRows.Children.Add(button)
                            if report.BigFiles.Length = 0 then
                                bigRows.Children.Add(TextBlock(Text = "No readable files found.", Foreground = muted))
                            let p = report.Progress
                            status.Text <- sprintf "Mapped %s in %s files · %s unreadable entries · %s links skipped."
                                               (bytes p.Bytes) (p.Files.ToString("N0")) (p.SkippedEntries.ToString("N0")) (p.SkippedLinks.ToString("N0"))
                            if p.SkippedEntries > 0L then status.Text <- status.Text + " Totals are incomplete."
                            scanButton.Content <- "Rescan"
                            writeToLog ("Space map complete. " + status.Text)
                    with
                    | :? OperationCanceledException ->
                        if not disposed then
                            clearResults()
                            status.Text <- "Space map cancelled. Select Scan to start again."
                        writeToLog "Space map cancelled."
                    | ex ->
                        if not disposed then
                            clearResults()
                            status.Text <- "Couldn't map this location: " + ex.Message
                        writeToLog ("Space map failed: " + ex.Message)
                finally
                    activeScan <- None
                    cancellation.Dispose()
                    if not disposed then
                        drivePicker.IsEnabled <- true
                        folderButton.IsEnabled <- storage.CanPickFolder
                        scanButton.IsEnabled <- chosenFolder.IsSome || drivePicker.SelectedIndex >= 0
                        cancelButton.IsEnabled <- false
                        progressBar.IsVisible <- false
            } |> ignore

    do
        scanButton.Classes.Add("Primary")
        let layout = Grid(RowDefinitions = RowDefinitions("Auto,Auto,Auto,Auto,Auto,*"), Margin = Thickness(30.0))
        let heading = StackPanel(Spacing = 8.0, Margin = Thickness(0.0, 0.0, 0.0, 20.0))
        heading.Children.Add(TextBlock(Text = "Space map", FontSize = 30.0, FontWeight = FontWeight.Bold))
        heading.Children.Add(TextBlock(Text = "Find what's filling your drives. Open folders to explore their size, or reveal your biggest files in File Explorer.",
                                       Foreground = muted, TextWrapping = TextWrapping.Wrap))
        layout.Children.Add(heading)
        let toolbar = WrapPanel(Orientation = Orientation.Horizontal, Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        for control in [ drivePicker :> Control; scanButton; cancelButton; folderButton ] do
            control.Margin <- Thickness(0.0, 0.0, 10.0, 8.0)
            toolbar.Children.Add(control)
        Grid.SetRow(toolbar, 1)
        layout.Children.Add(toolbar)
        sourceLabel.Margin <- Thickness(0.0, 0.0, 0.0, 10.0)
        Grid.SetRow(sourceLabel, 2)
        layout.Children.Add(sourceLabel)
        status.Margin <- Thickness(0.0, 0.0, 0.0, 12.0)
        Grid.SetRow(status, 3)
        layout.Children.Add(status)
        Grid.SetRow(progressBar, 4)
        layout.Children.Add(progressBar)

        let panels = Grid(ColumnDefinitions = ColumnDefinitions("1.8*,16,*"), Margin = Thickness(0.0, 6.0, 0.0, 0.0))
        let folderPanel = Grid(RowDefinitions = RowDefinitions("Auto,Auto,Auto,*"))
        let navigation = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0)
        navigation.Children.AddRange [upButton; revealButton]
        folderPanel.Children.Add(navigation)
        crumbs.Margin <- Thickness(0.0, 10.0, 0.0, 10.0)
        Grid.SetRow(crumbs, 1)
        folderPanel.Children.Add(crumbs)
        currentSize.Margin <- Thickness(8.0, 0.0, 0.0, 12.0)
        Grid.SetRow(currentSize, 2)
        folderPanel.Children.Add(currentSize)
        let folderScroll = ScrollViewer(Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        Grid.SetRow(folderScroll, 3)
        folderPanel.Children.Add(folderScroll)
        panels.Children.Add(Border(Background = cardBrush, CornerRadius = CornerRadius(10.0), Padding = Thickness(14.0), Child = folderPanel))

        let filesPanel = Grid(RowDefinitions = RowDefinitions("Auto,Auto,*"))
        filesPanel.Children.Add(TextBlock(Text = "Biggest files", FontSize = 18.0, FontWeight = FontWeight.SemiBold))
        let note = TextBlock(Text = "Top 40 across the scanned location.\nSizes are logical bytes; links are skipped.", Foreground = muted,
                             TextWrapping = TextWrapping.Wrap, FontSize = 12.0, Margin = Thickness(0.0, 8.0, 0.0, 14.0))
        Grid.SetRow(note, 1)
        filesPanel.Children.Add(note)
        let fileScroll = ScrollViewer(Content = bigRows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                                      VerticalScrollBarVisibility = ScrollBarVisibility.Auto)
        Grid.SetRow(fileScroll, 2)
        filesPanel.Children.Add(fileScroll)
        let filesCard = Border(Background = cardBrush, CornerRadius = CornerRadius(10.0), Padding = Thickness(16.0), Child = filesPanel)
        Grid.SetColumn(filesCard, 2)
        panels.Children.Add(filesCard)
        Grid.SetRow(panels, 5)
        layout.Children.Add(panels)
        this.Content <- layout
        clearResults()

        let drives =
            DriveInfo.GetDrives()
            |> Array.choose (fun drive ->
                try
                    if drive.IsReady then
                        let label = if String.IsNullOrWhiteSpace drive.VolumeLabel then drive.DriveType.ToString() else drive.VolumeLabel
                        Some { Root = drive.RootDirectory.FullName
                               Title = sprintf "%s  %s (%s free)" drive.Name label (bytes drive.AvailableFreeSpace) }
                    else None
                with _ -> None)
        drivePicker.ItemsSource <- drives
        drivePicker.SelectionChanged.Add(fun _ ->
            if activeScan.IsNone then
                chosenFolder <- None
                clearResults()
                sourceLabel.IsVisible <- false
                scanButton.IsEnabled <- drivePicker.SelectedIndex >= 0
                status.Text <- "Select Scan to map this drive.")
        let systemRoot = Path.GetPathRoot(Environment.SystemDirectory)
        drivePicker.SelectedIndex <- drives |> Array.tryFindIndex (fun drive -> drive.Root = systemRoot) |> Option.defaultValue (if drives.Length > 0 then 0 else -1)
        scanButton.IsEnabled <- drivePicker.SelectedIndex >= 0
        folderButton.IsEnabled <- storage.CanPickFolder
        scanButton.Click.Add(fun _ ->
            match chosenFolder, drivePicker.SelectedItem with
            | Some path, _ -> scan path 0L
            | None, (:? SpaceDrive as drive) ->
                // Refresh used bytes so rescans reflect changes since the page was created.
                try
                    let info = DriveInfo(drive.Root)
                    scan drive.Root (info.TotalSize - info.TotalFreeSpace)
                with ex -> status.Text <- "Drive unavailable: " + ex.Message
            | _ -> ())
        cancelButton.Click.Add(fun _ ->
            activeScan |> Option.iter (fun cancellation -> cancellation.Cancel())
            cancelButton.IsEnabled <- false
            status.Text <- "Cancelling space map…")
        folderButton.Click.Add(fun _ ->
            task {
                try
                    let! selected = storage.OpenFolderPickerAsync(FolderPickerOpenOptions(Title = "Choose a folder to map", AllowMultiple = false))
                    if not disposed && selected.Count > 0 then
                        let path = selected[0].TryGetLocalPath()
                        if String.IsNullOrWhiteSpace path then status.Text <- "Choose a local folder to map."
                        else
                            chosenFolder <- Some path
                            scan path 0L
                with ex ->
                    if not disposed then status.Text <- "Couldn't choose a folder: " + ex.Message
            } |> ignore)
        upButton.Click.Add(fun _ -> current |> Option.bind _.Parent |> Option.iter navigate)
        revealButton.Click.Add(fun _ -> current |> Option.iter (fun node -> reveal node.FullPath))

    interface IDisposable with
        member _.Dispose() =
            disposed <- true
            activeScan |> Option.iter (fun cancellation -> cancellation.Cancel())
