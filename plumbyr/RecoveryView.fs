namespace WindowsCleaner

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open Avalonia
open Avalonia.Controls
open Avalonia.Layout
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform.Storage
open Avalonia.Threading
open ByteSizeLib

type private RecoveryResultRow =
    { File: RecoveryFile; Root: Grid; Check: CheckBox; Description: TextBlock
      Prefix: string; mutable Suffix: string }

/// Native F# Sources and Recovered files pages sharing one read-only session.
type RecoveryView(writeToLog: string -> unit, storage: IStorageProvider, showResults: unit -> unit, ?service: RecoveryService) =
    let client = match service with Some service -> service | None -> new RecoveryService()
    let files = ResizeArray<RecoveryFile>()
    let matching = ResizeArray<RecoveryFile>()
    let selected = HashSet<int>()
    let viewportRows = Dictionary<int, RecoveryResultRow>()
    let mutable selectedBytes = 0L
    let mutable indexedCount = 0
    let mutable projectionKey: (string * int * int) option = None
    let mutable sortedMatches: SortedSet<RecoveryFile> option = None
    let incoming = ConcurrentQueue<RecoveryEvent>()
    let mutable disposed = false
    let mutable scanning = false
    let mutable recovering = false
    let mutable refreshing = false
    let mutable source: RecoverySource option = None
    let mutable scannedSource = ""
    let mutable page = 0
    let mutable dirty = false
    let mutable previewToken = 0
    let mutable previewLoading = false
    let mutable requestedPreview: (RecoveryFile * int) option = None
    let mutable bitmap: Bitmap option = None
    let mutable focused: RecoveryFile option = None
    let mutable unreadable = 0L
    let mutable closeAfterRecovery: (unit -> unit) option = None
    let bytes size = ByteSize.FromBytes(float size).ToString()
    let muted = SolidColorBrush.Parse("#a8a8b2")
    let primary = SolidColorBrush.Parse("#a78bfa")
    let text (value: string) = TextBlock(Text = value, TextWrapping = TextWrapping.Wrap)
    let button value = Button(Content = value, Padding = Thickness(12.0, 8.0))
    let sourcesPage = UserControl()
    let resultsPage = UserControl()
    let sourceRows = StackPanel(Spacing = 8.0)
    let sourceInfo = text "Choose a connected volume or open a disk image."
    let sourceStatus = text "Refresh Sources to list connected drives."
    let resultStatus = text "Scan a source to find recoverable files."
    let refresh = button "Refresh"
    let imagePicker = button "Open disk image"
    let start = button "Start scanning"
    let stop = button "Stop scanning"
    let stopResults = button "Stop scanning"
    let viewResults = button "View recovered files"
    let mode = ComboBox(Name = "RecoveryScanMode", ItemsSource = [| "Unused space (deleted files)"; "All bytes (includes existing files)" |], SelectedIndex = 0, MinWidth = 260.0)
    let progress = ProgressBar(Minimum = 0.0, Maximum = 100.0, Height = 4.0)
    let search = TextBox(PlaceholderText = "Search name, type or offset", MinWidth = 230.0)
    let category = ComboBox(Name = "RecoveryCategory", ItemsSource = [| "All files"; "Pictures"; "Documents"; "Archives"; "Audio"; "Video"; "Applications"; "Icons" |], SelectedIndex = 0, MinWidth = 145.0)
    let sort = ComboBox(Name = "RecoverySort", ItemsSource = [| "Discovery order"; "Name"; "Largest first" |], SelectedIndex = 0, MinWidth = 150.0)
    let selectVisible = button "Select matching"
    let clearSelection = button "Clear selection"
    let recover = button "Recover selected"
    let cancelRecovery = button "Stop recovery"
    let selectionInfo = text "0 files found"
    let rows = StackPanel(Name = "RecoveryFileRows", Spacing = 5.0)
    let fileScroll = ScrollViewer(Name = "RecoveryFileScroll", Content = rows)
    let emptyFiles = text "Files will appear here during a scan. You can stop scanning and keep the results."
    let previous = button "Previous"
    let next = button "Next"
    let pageInfo = text ""
    let details = text "Choose a file to see its details and picture preview."
    let previewStatus = text ""
    let preview = Image(MaxHeight = 240.0, Stretch = Stretch.Uniform)
    let mutable drainScheduled = 0
    let mutable drainTimer: System.Threading.Timer option = None

    let setStatus value =
        sourceStatus.Text <- value
        resultStatus.Text <- value

    let showError (ex: exn) =
        if not disposed then setStatus ex.Message
        writeToLog ("Recovery: " + ex.Message)

    let updateControls () =
        let busy = scanning || recovering || refreshing
        refresh.IsEnabled <- not busy
        imagePicker.IsEnabled <- not busy && storage.CanOpen
        sourceRows.IsEnabled <- not busy
        mode.IsEnabled <- not busy
        start.IsEnabled <- source.IsSome && not busy
        stop.IsVisible <- scanning
        stopResults.IsVisible <- scanning
        cancelRecovery.IsVisible <- recovering
        recover.IsEnabled <- not recovering && storage.CanPickFolder && selected.Count > 0
        selectVisible.IsEnabled <- not recovering && files.Count > 0
        clearSelection.IsEnabled <- not recovering && files.Count > 0

    let syncProjection () =
        let query = if isNull search.Text then "" else search.Text.Trim()
        let key = query, category.SelectedIndex, sort.SelectedIndex
        if projectionKey <> Some key then
            matching.Clear()
            indexedCount <- 0
            projectionKey <- Some key
            let order = sort.SelectedIndex
            sortedMatches <-
                if order <= 0 then None
                else
                    let comparer = Comparer<RecoveryFile>.Create(fun a b ->
                        let comparison = if order = 1 then StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name) else compare b.Size a.Size
                        if comparison = 0 then compare a.Index b.Index else comparison)
                    Some(SortedSet<RecoveryFile>(comparer))
        // Only new candidates enter the projection during streaming. A filter
        // or sort change is the only reason to inspect the existing list again.
        while indexedCount < files.Count do
            let file = files[indexedCount]
            if (category.SelectedIndex <= 0 || file.Category = category.SelectedIndex) &&
               (query = "" || (sprintf "%s %s %d %x" file.Name file.Kind file.Offset file.Offset).Contains(query, StringComparison.OrdinalIgnoreCase)) then
                matching.Add(file)
                sortedMatches |> Option.iter (fun items -> items.Add(file) |> ignore)
            indexedCount <- indexedCount + 1

    let visible () =
        syncProjection()
        match sortedMatches with Some items -> items :> seq<RecoveryFile> | None -> matching :> seq<RecoveryFile>

    let setSelected (file: RecoveryFile) value =
        if file.Selected <> value then
            file.Selected <- value
            if value then selected.Add(file.Index) |> ignore; selectedBytes <- selectedBytes + file.Size
            else selected.Remove(file.Index) |> ignore; selectedBytes <- selectedBytes - file.Size

    let resetResults () =
        files.Clear()
        matching.Clear()
        selected.Clear()
        selectedBytes <- 0L
        projectionKey <- None
        indexedCount <- 0
        viewportRows.Clear()
        rows.Children.Clear()

    let clearPreview () =
        previewToken <- previewToken + 1
        requestedPreview <- None
        preview.Source <- null
        bitmap |> Option.iter (fun b -> b.Dispose())
        bitmap <- None
        previewStatus.Text <- ""

    let updateDetails (file: RecoveryFile) =
        details.Text <- sprintf "%s\n%s · %s\nOffset: %d (0x%X)\n%s\n\n%s\nNo deletion date is available.%s%s"
            file.Name (file.Kind.ToUpperInvariant()) (bytes file.Size) file.Offset file.Offset file.Validation file.Metadata
            (if file.SavedPath = "" then "" else "\n\nSaved: " + file.SavedPath)
            (if file.Error = "" then "" else "\n\nRecovery failed: " + file.Error)

    let loadPreviews () = task {
        if not previewLoading then
            previewLoading <- true
            try
                // Keep one decoder in flight; rapid selection replaces the queued preview.
                while requestedPreview.IsSome && not disposed do
                    let file, token = requestedPreview.Value
                    requestedPreview <- None
                    try
                        let! result = client.Request("preview", [ "index", box file.Index ])
                        if not disposed && token = previewToken then
                            use stream = new MemoryStream(Convert.FromBase64String(RecoveryJson.text "png" result))
                            let image = new Bitmap(stream)
                            bitmap <- Some image
                            preview.Source <- image
                            previewStatus.Text <- ""
                    with ex -> if not disposed && token = previewToken then previewStatus.Text <- ex.Message
            finally previewLoading <- false
    }

    let showDetails (file: RecoveryFile) =
        focused <- Some file
        clearPreview()
        updateDetails file
        previewStatus.Text <- "Loading preview…"
        requestedPreview <- Some(file, previewToken)
        loadPreviews() |> ignore

    let updateSelection (count: int) =
        selectionInfo.Text <- sprintf "%s found · %s matching · %s selected (%s)" (files.Count.ToString("N0"))
            (count.ToString("N0")) (selected.Count.ToString("N0")) (bytes selectedBytes)
        updateControls()

    let makeRow (file: RecoveryFile) =
            let row = Grid(ColumnDefinitions = ColumnDefinitions("Auto,*"))
            row.Tag <- file.Index
            let check = CheckBox(IsChecked = Nullable file.Selected, IsEnabled = not recovering, Margin = Thickness(4.0, 0.0, 8.0, 0.0))
            check.IsCheckedChanged.Add(fun _ ->
                let value = check.IsChecked.GetValueOrDefault()
                if file.Selected <> value then setSelected file value; updateSelection matching.Count)
            row.Children.Add(check)
            let label = StackPanel(Spacing = 3.0)
            label.Children.Add(TextBlock(Text = file.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold))
            let prefix = sprintf "%s · %s · %s" (file.Kind.ToUpperInvariant()) (bytes file.Size) file.Validation
            let description = TextBlock(Text = prefix, Foreground = muted, FontSize = 12.0, TextWrapping = TextWrapping.Wrap)
            label.Children.Add(description)
            let item = Button(Content = label, Padding = Thickness(10.0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch)
            ToolTip.SetTip(item, sprintf "%s\nOffset: 0x%X" file.Name file.Offset)
            item.Click.Add(fun _ -> showDetails file)
            Grid.SetColumn(item, 1)
            row.Children.Add(item)
            { File = file; Root = row; Check = check; Description = description; Prefix = prefix; Suffix = "" }

    let patchRow (row: RecoveryResultRow) =
        if row.Check.IsEnabled = recovering then row.Check.IsEnabled <- not recovering
        if row.Check.IsChecked.GetValueOrDefault() <> row.File.Selected then row.Check.IsChecked <- Nullable row.File.Selected
        let suffix = if row.File.Error <> "" then " · Recovery failed" elif row.File.SavedPath <> "" then " · Saved" else ""
        if row.Suffix <> suffix then
            row.Suffix <- suffix
            row.Description.Text <- row.Prefix + suffix

    let render () =
        syncProjection()
        page <- min page (max 0 ((matching.Count - 1) / 100))
        updateSelection matching.Count
        let first = page * 100
        let desired =
            match sortedMatches with
            | Some items -> items |> Seq.skip first |> Seq.truncate 100 |> Seq.toArray
            | None -> Array.init (min 100 (matching.Count - first)) (fun i -> matching[first + i])
        let desiredIds = HashSet<int>(desired |> Seq.map (fun file -> file.Index))
        for index in viewportRows.Keys |> Seq.toArray do
            if not (desiredIds.Contains(index)) then
                rows.Children.Remove(viewportRows[index].Root) |> ignore
                viewportRows.Remove(index) |> ignore
        if desired.Length = 0 then
            emptyFiles.Text <- if files.Count = 0 then "Files will appear here during a scan. You can stop scanning and keep the results." else "No files match these filters."
            if not (rows.Children.Contains(emptyFiles)) then rows.Children.Add(emptyFiles)
        else
            rows.Children.Remove(emptyFiles) |> ignore
            for index = 0 to desired.Length - 1 do
                let file = desired[index]
                let row =
                    match viewportRows.TryGetValue(file.Index) with
                    | true, row -> row
                    | _ ->
                        let row = makeRow file
                        viewportRows.Add(file.Index, row)
                        row
                let position = rows.Children.IndexOf(row.Root)
                if position < 0 then rows.Children.Insert(index, row.Root)
                elif position <> index then rows.Children.Move(position, index)
                patchRow row
        previous.IsEnabled <- page > 0
        next.IsEnabled <- (page + 1) * 100 < matching.Count
        pageInfo.Text <- sprintf "Page %d of %d" (page + 1) (max 1 ((matching.Count + 99) / 100))
        updateControls()

    let applyEvent (value: JsonElement) =
        match RecoveryJson.text "event" value with
            | "workerStarted" | "workerStopped" as state ->
                resetResults()
                focused <- None
                clearPreview()
                details.Text <- "Choose a file to see its details and picture preview."
                dirty <- true
                if state = "workerStopped" then setStatus "The recovery worker stopped. Return to Sources and scan again."
            | "found" -> files.Add(RecoveryJson.file value); dirty <- true
            | "progress" ->
                let total = RecoveryJson.number "total" value
                if total > 0L then progress.Value <- min 100.0 (float (RecoveryJson.number "bytes" value) * 100.0 / float total)
            | "unusedSpace" -> setStatus (sprintf "Scanning %s of unused space (%s)…" (bytes (RecoveryJson.number "bytes" value)) (RecoveryJson.text "filesystem" value))
            | "unreadable" -> unreadable <- unreadable + RecoveryJson.number "bytes" value
            | "recovered" | "recoveryFailed" as event ->
                let index = int (RecoveryJson.number "index" value)
                if index >= 0 && index < files.Count then
                    if event = "recovered" then files[index].SavedPath <- RecoveryJson.text "path" value; files[index].Error <- ""
                    else files[index].Error <- RecoveryJson.text "error" value
                    focused |> Option.filter (fun f -> f.Index = index) |> Option.iter updateDetails
                    dirty <- true
            | _ -> ()

    let drain () =
        let mutable message = Unchecked.defaultof<RecoveryEvent>
        let mutable processed = 0
        let budget = Stopwatch.StartNew()
        while processed < 4096 && (processed = 0 || budget.ElapsedMilliseconds < 8L) && incoming.TryDequeue(&message) do
            processed <- processed + 1
            if message.Generation = client.Generation then applyEvent message.Payload
        if dirty then dirty <- false; render()

    let scheduleDrain () =
        if Interlocked.CompareExchange(&drainScheduled, 1, 0) = 0 then
            Dispatcher.UIThread.Post((fun () ->
                try if not disposed then drain()
                finally Interlocked.Exchange(&drainScheduled, 0) |> ignore), DispatcherPriority.Background)

    let subscription = client.Events.Subscribe(fun value ->
        if not disposed then incoming.Enqueue(value))

    let chooseSource (value: RecoverySource) =
        source <- Some value
        sourceInfo.Text <- sprintf "%s\n%s · %s\n%s%s" value.Name value.Path (bytes value.Size) value.Location
            (if value.Mounted then "\nThis source is mounted and can change while scanning. A disk image gives more reliable results." else "")
        updateControls()

    let refreshSources () = task {
        refreshing <- true
        setStatus "Looking for connected drives…"
        updateControls()
        try
            let! drives = client.DiscoverAsync()
            if not disposed then
                sourceRows.Children.Clear()
                for drive in drives do
                    let label = StackPanel(Spacing = 5.0)
                    label.Children.Add(TextBlock(Text = drive.Name, FontSize = 17.0, FontWeight = FontWeight.SemiBold))
                    let locked = if drive.Encrypted then "\nLocked or inaccessible. Unlock this volume, then refresh." else ""
                    label.Children.Add(text (sprintf "%s · %s · %s\n%s%s" drive.Kind (bytes drive.Size) drive.Location drive.Path locked))
                    let item = Button(Content = label, IsEnabled = not drive.Encrypted, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = Thickness(15.0))
                    item.Click.Add(fun _ -> chooseSource drive)
                    sourceRows.Children.Add(item)
                // Keep a selected image; invalidate a disconnected device.
                match source with
                | Some s when s.Kind <> "Disk image" ->
                    source <- drives |> Array.tryFind (fun d -> d.Path = s.Path && not d.Encrypted)
                    match source with Some s -> chooseSource s | None -> sourceInfo.Text <- "Choose a source."
                | _ -> ()
                setStatus (if drives.Length = 0 then "No readable local drives found. Open a disk image to begin." else sprintf "%d sources found. Choose a source to scan." drives.Length)
        with ex -> showError ex
        refreshing <- false
        if not disposed then updateControls()
    }

    let cancelScan () = task {
        try let! _ = client.Request("cancel", []) in () with ex -> showError ex
    }

    let cancelExport () = task {
        try
            let! _ = client.Request("cancelRecovery", [])
            if not disposed then setStatus "Stopping recovery after the current file…"
        with ex -> showError ex
    }

    let scan () = task {
        match source with
        | Some selected when not scanning && not recovering ->
            scanning <- true
            resetResults()
            while not incoming.IsEmpty do incoming.TryDequeue() |> ignore
            focused <- None
            clearPreview()
            details.Text <- "Choose a file to see its details and picture preview."
            scannedSource <- selected.Name
            unreadable <- 0L
            page <- 0
            progress.Value <- 0.0
            setStatus ("Scanning " + selected.Name + "…")
            render()
            showResults()
            writeToLog ("Recovery scan started: " + selected.Path)
            try
                let! result = client.Request("scan", [ "path", box selected.Path; "unusedOnly", box (mode.SelectedIndex = 0) ])
                if not disposed then
                    // Worker events precede completion on stdout. Drain all remaining candidates.
                    while not incoming.IsEmpty do drain()
                    let cancelled = RecoveryJson.flag "cancelled" result
                    if not cancelled then progress.Value <- 100.0
                    setStatus (sprintf "%s: %s · %s files found.%s" (if cancelled then "Scan stopped" else "Scan complete") scannedSource (files.Count.ToString("N0"))
                                        (if unreadable > 0L then sprintf " %s could not be read; results are incomplete." (bytes unreadable) else ""))
                    writeToLog resultStatus.Text
            with ex ->
                if not disposed then
                    while not incoming.IsEmpty do drain()
                    let message = if client.IsRunning then ex.Message + " Files already found remain available." else ex.Message
                    showError (InvalidOperationException(message))
            scanning <- false
            if not disposed then render()
        | _ -> ()
    }

    do
        start.Classes.Add("Primary")
        recover.Classes.Add("Primary")
        let heading title description =
            let panel = StackPanel(Spacing = 8.0, Margin = Thickness(0.0, 0.0, 0.0, 18.0))
            panel.Children.Add(TextBlock(Text = title, FontSize = 30.0, FontWeight = FontWeight.Bold))
            panel.Children.Add(TextBlock(Text = description, Foreground = muted, TextWrapping = TextWrapping.Wrap))
            panel
        let toolbar (controls: Control list) =
            let panel = WrapPanel(Orientation = Orientation.Horizontal)
            for control in controls do control.Margin <- Thickness(0.0, 0.0, 8.0, 8.0); panel.Children.Add(control)
            panel
        let sourceLayout = Grid(RowDefinitions = RowDefinitions("Auto,Auto,*,Auto,Auto,Auto"), Margin = Thickness(26.0))
        sourceLayout.Children.Add(heading "Sources" "Recover deleted files from a drive or disk image. Sources are opened read-only.")
        let sourceToolbar = toolbar [ refresh; imagePicker; viewResults ]
        Grid.SetRow(sourceToolbar, 1)
        sourceLayout.Children.Add(sourceToolbar)
        let scroll = ScrollViewer(Content = sourceRows, Margin = Thickness(0.0, 6.0, 0.0, 12.0))
        Grid.SetRow(scroll, 2)
        sourceLayout.Children.Add(scroll)
        sourceInfo.Margin <- Thickness(0.0, 8.0)
        Grid.SetRow(sourceInfo, 3)
        sourceLayout.Children.Add(sourceInfo)
        let scanToolbar = toolbar [ mode; start; stop ]
        Grid.SetRow(scanToolbar, 4)
        sourceLayout.Children.Add(scanToolbar)
        let sourceFooter = StackPanel(Spacing = 8.0)
        sourceFooter.Children.Add(sourceStatus)
        sourceFooter.Children.Add(TextBlock(Text = "Save recovered files to a different physical drive. Overwritten or TRIM-erased data cannot be recovered.", Foreground = muted, TextWrapping = TextWrapping.Wrap, FontSize = 12.0))
        Grid.SetRow(sourceFooter, 5)
        sourceLayout.Children.Add(sourceFooter)
        sourcesPage.Content <- sourceLayout

        let resultLayout = Grid(RowDefinitions = RowDefinitions("Auto,Auto,Auto,Auto,*,Auto,Auto"), Margin = Thickness(26.0))
        resultLayout.Children.Add(heading "Recovered files" "Review found files and recover your selection. Files may be incomplete; open recovered copies to check them.")
        let filters = toolbar [ search; category; sort ]
        Grid.SetRow(filters, 1)
        resultLayout.Children.Add(filters)
        Grid.SetRow(selectionInfo, 2)
        resultLayout.Children.Add(selectionInfo)
        progress.Margin <- Thickness(0.0, 10.0)
        Grid.SetRow(progress, 3)
        resultLayout.Children.Add(progress)
        let body = Grid(ColumnDefinitions = ColumnDefinitions("1.5*,16,*"))
        body.Children.Add(fileScroll)
        let detailPanel = StackPanel(Spacing = 12.0)
        detailPanel.Children.Add(TextBlock(Text = "File details", FontSize = 19.0, FontWeight = FontWeight.SemiBold, Foreground = primary))
        detailPanel.Children.Add(preview)
        detailPanel.Children.Add(previewStatus)
        detailPanel.Children.Add(details)
        let detailScroll = ScrollViewer(Content = detailPanel)
        Grid.SetColumn(detailScroll, 2)
        body.Children.Add(detailScroll)
        Grid.SetRow(body, 4)
        resultLayout.Children.Add(body)
        let footer = toolbar [ previous; pageInfo; next; selectVisible; clearSelection; recover; cancelRecovery; stopResults ]
        footer.Margin <- Thickness(0.0, 12.0, 0.0, 0.0)
        Grid.SetRow(footer, 5)
        resultLayout.Children.Add(footer)
        let statusPanel = StackPanel(Spacing = 6.0)
        statusPanel.Children.Add(resultStatus)
        statusPanel.Children.Add(TextBlock(Text = "Results stay available until the next scan or app close. Recover your selection before leaving the app.", FontSize = 12.0, Foreground = muted, TextWrapping = TextWrapping.Wrap))
        Grid.SetRow(statusPanel, 6)
        resultLayout.Children.Add(statusPanel)
        resultsPage.Content <- resultLayout

        refresh.Click.Add(fun _ -> refreshSources() |> ignore)
        let chooseImage () = task {
            try
                let! chosen = storage.OpenFilePickerAsync(FilePickerOpenOptions(Title = "Open disk image", AllowMultiple = false,
                    FileTypeFilter = [| FilePickerFileType("Disk images", Patterns = [| "*.img"; "*.dd"; "*.raw"; "*.bin"; "*.iso" |]); FilePickerFileType("All files", Patterns = [| "*" |]) |]))
                if chosen.Count > 0 && not disposed then
                    let path = chosen[0].TryGetLocalPath()
                    if String.IsNullOrWhiteSpace(path) then failwith "Choose a local disk image."
                    chooseSource { Path = path; Name = Path.GetFileName(path); Size = FileInfo(path).Length; Kind = "Disk image"; Location = "Local image file"; Mounted = false; Encrypted = false }
            with ex -> showError ex
        }
        imagePicker.Click.Add(fun _ -> chooseImage() |> ignore)
        start.Click.Add(fun _ -> scan() |> ignore)
        stop.Click.Add(fun _ -> cancelScan() |> ignore)
        stopResults.Click.Add(fun _ -> cancelScan() |> ignore)
        cancelRecovery.Click.Add(fun _ -> cancelExport() |> ignore)
        viewResults.Click.Add(fun _ -> showResults())
        search.TextChanged.Add(fun _ -> page <- 0; render())
        category.SelectionChanged.Add(fun _ -> page <- 0; render())
        sort.SelectionChanged.Add(fun _ -> page <- 0; render())
        previous.Click.Add(fun _ -> page <- max 0 (page - 1); render())
        next.Click.Add(fun _ -> page <- page + 1; render())
        selectVisible.Click.Add(fun _ ->
            for file in visible() do setSelected file true
            render())
        clearSelection.Click.Add(fun _ ->
            for index in selected |> Seq.toArray do setSelected files[index] false
            render())
        let exportSelection () = task {
            if not recovering then
                let indices = selected |> Seq.sort |> Seq.toArray
                if indices.Length > 0 then
                    // Reserve controls during the folder dialog too.
                    recovering <- true
                    render()
                    try
                        let! folders = storage.OpenFolderPickerAsync(FolderPickerOpenOptions(Title = "Recover to a different physical drive", AllowMultiple = false))
                        if folders.Count > 0 && not disposed && closeAfterRecovery.IsNone then
                            let path = folders[0].TryGetLocalPath()
                            if String.IsNullOrWhiteSpace(path) then failwith "Choose a local recovery folder."
                            setStatus "Recovering selected files…"
                            let! result = client.Request("recover", [ "folder", box path; "indices", box indices ])
                            if not disposed then
                                while not incoming.IsEmpty do drain()
                                setStatus (sprintf "%s: %d saved, %d failed. Check recovered files in %s."
                                    (if RecoveryJson.flag "cancelled" result then "Recovery stopped" else "Recovery complete")
                                    (RecoveryJson.number "saved" result) (RecoveryJson.number "failed" result) path)
                                writeToLog resultStatus.Text
                    with ex -> showError ex
                    recovering <- false
                    if not disposed then
                        render()
                        let close = closeAfterRecovery
                        closeAfterRecovery <- None
                        close |> Option.iter (fun action -> action())
        }
        recover.Click.Add(fun _ -> exportSelection() |> ignore)
        drainTimer <- Some(new System.Threading.Timer((fun _ ->
            if not disposed && not incoming.IsEmpty then scheduleDrain()), null, 100, 100))
        render()

    member _.SourcesPage = sourcesPage
    member _.ResultsPage = resultsPage
    member _.RefreshIfEmpty() = if sourceRows.Children.Count = 0 && not refreshing && not scanning && not recovering then refreshSources() |> ignore
    member _.TryClose(close: unit -> unit) =
        if recovering then
            closeAfterRecovery <- Some close
            cancelExport() |> ignore
            false
        else true

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true
                drainTimer |> Option.iter (fun timer -> timer.Dispose())
                subscription.Dispose()
                clearPreview()
                (client :> IDisposable).Dispose()
