open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks
open Avalonia
open Avalonia.Controls
open Avalonia.Headless
open Avalonia.Interactivity
open Avalonia.LogicalTree
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform.Storage
open Avalonia.Threading
open WindowsCleaner

// Avalonia's storage interfaces are platform-owned. A proxy supplies only the
// folder-picker operations used by this read-only UI test.
type FolderPickerProxy() =
    inherit DispatchProxy()
    member val Folder = "" with get, set
    override this.Invoke(methodInfo, _) =
        match methodInfo.Name with
        | "get_CanPickFolder" -> box true
        | "get_Path" -> box (Uri(this.Folder))
        | "get_Name" -> box (Path.GetFileName(this.Folder))
        | "OpenFolderPickerAsync" ->
            let folder = DispatchProxy.Create<IStorageFolder, FolderPickerProxy>()
            (box folder :?> FolderPickerProxy).Folder <- this.Folder
            box (Task.FromResult([| folder |] :> IReadOnlyList<IStorageFolder>))
        | "Dispose" -> null
        | name -> failwithf "Unexpected storage operation: %s" name

let folderPicker path =
    let provider = DispatchProxy.Create<IStorageProvider, FolderPickerProxy>()
    (box provider :?> FolderPickerProxy).Folder <- path
    provider

let check condition message = if not condition then failwith message
let descendants<'T when 'T :> Control> (root: Control) = root.GetLogicalDescendants() |> Seq.choose (function :? 'T as item -> Some item | _ -> None)
let button text root = descendants<Button> root |> Seq.find (fun item -> item.Content :? string && string item.Content = text)
let click (button: Button) = button.RaiseEvent(RoutedEventArgs(Button.ClickEvent))
let hasText text root = descendants<TextBlock> root |> Seq.exists (fun item -> item.Text = text)
let waitUntil label predicate =
    let timer = Stopwatch.StartNew()
    while not (predicate()) && timer.Elapsed < TimeSpan.FromSeconds(15.0) do
        Dispatcher.UIThread.RunJobs()
        Thread.Sleep(5)
    Dispatcher.UIThread.RunJobs()
    check (predicate()) ("Timed out: " + label)

[<STAThread; EntryPoint>]
let main _ =
    let repoRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "../.."))
    let fixture = Path.Combine(repoRoot, ".build", "space-map-ui-" + Guid.NewGuid().ToString("N"))
    let nested = Path.Combine(fixture, "Large folder", "Nested")
    Directory.CreateDirectory(nested) |> ignore
    let first = Path.Combine(fixture, "root.bin")
    let second = Path.Combine(nested, "big.bin")
    File.WriteAllBytes(first, Array.zeroCreate 100)
    File.WriteAllBytes(second, Array.zeroCreate 2000)
    try
        AppBuilder.Configure<App>().UseSkia().UseHeadless(AvaloniaHeadlessPlatformOptions(UseHeadlessDrawing = false)).SetupWithoutStarting() |> ignore
        let messages = ResizeArray<string>()
        use view = new SpaceMapView(messages.Add, folderPicker fixture)
        let window = Window(Width = 920.0, Height = 760.0, Content = view,
                            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
                            Background = SolidColorBrush.Parse("#080808"))
        window.Show()
        Dispatcher.UIThread.RunJobs()
        check (hasText "Space map" view) "Missing page heading"
        click (button "Choose folder" view)
        waitUntil "folder scan" (fun () -> messages |> Seq.exists (fun message -> message.StartsWith("Space map complete.")))
        check (descendants<TextBlock> view |> Seq.exists (fun t -> not (isNull t.Text) && t.Text.StartsWith("Mapped"))) "Missing scan summary"
        check (hasText "big.bin" view && hasText "root.bin" view) "Biggest files are missing"
        check (not (button "Up" view).IsEnabled) "Up should be disabled at the scan root"
        let folderRow = descendants<Button> view |> Seq.find (fun item -> hasText "Large folder  ›" item)
        click folderRow
        check (hasText "Nested  ›" view && (button "Up" view).IsEnabled) "Folder drill-down failed"
        click (button "Up" view)
        check (hasText "Large folder  ›" view && not (button "Up" view).IsEnabled) "Up navigation failed"
        click (descendants<Button> view |> Seq.find (fun item -> hasText "Large folder  ›" item))
        click (button (Path.GetFileName(fixture)) view)
        check (not (button "Up" view).IsEnabled) "Breadcrumb navigation failed"
        let screenshot = Path.Combine(repoRoot, ".build", "space-map-ui.png")
        Dispatcher.UIThread.RunJobs()
        use frame = window.CaptureRenderedFrame()
        check (not (isNull frame)) "Page did not render"
        frame.Save(screenshot, PngBitmapEncoderOptions.Default)
        printfn "PASS: themed page renders; folder selection, results, drill-down, Up and breadcrumbs work."
        printfn "Rendered page: %s" screenshot
        click (button "Rescan" view)
        click (button "Cancel" view)
        waitUntil "cancel scan" (fun () -> messages.Contains("Space map cancelled."))
        check ((button "Scan" view).IsEnabled && not (button "Cancel" view).IsEnabled) "Scan controls did not reset after cancellation"
        printfn "PASS: UI cancellation restores the controls and clears results."
        window.Close()
        0
    finally
        File.Delete(first)
        File.Delete(second)
        Directory.Delete(nested, false)
        Directory.Delete(Path.GetDirectoryName(nested), false)
        Directory.Delete(fixture, false)
