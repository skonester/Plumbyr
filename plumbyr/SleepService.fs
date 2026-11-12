namespace WindowsCleaner

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

// Sleep behavior adapted from the local Goodnight program. The platform boundary
// lets countdowns, event handling and lifecycle tests run without changing power state.
[<CLIMutable>]
type SleepSettings = {
    KeepSleep: bool
    SuspendProcesses: bool
    DisableDevices: bool
    WakeLog: bool
    WakeSources: string array
    LidDelaySeconds: int
}

module SleepSettings =
    let defaults () = {
        KeepSleep = false; SuspendProcesses = false; DisableDevices = false
        WakeLog = true; WakeSources = [| "PowerButton" |]; LidDelaySeconds = 40
    }
    let sources = [ "DisplayOn", "Lid / display"; "Keyboard", "Keyboard"; "Mouse", "Mouse"
                    "TouchPad", "Touchpad"; "PowerAction", "Power cable"; "Other", "Other" ]
    let normalize settings =
        let known = sources |> List.map fst |> Set.ofList |> Set.add "PowerButton"
        let values = if isNull settings.WakeSources then [||] else settings.WakeSources
        { settings with
            WakeSources = values |> Array.filter known.Contains |> Array.append [| "PowerButton" |] |> Array.distinct
            LidDelaySeconds = Math.Clamp(settings.LidDelaySeconds, 0, 3600) }
    let load path =
        if File.Exists(path) then JsonSerializer.Deserialize<SleepSettings>(File.ReadAllText(path)) |> normalize
        else defaults()
    let save path settings =
        let settings = normalize settings
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))) |> ignore
        let temporary = path + ".tmp"
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings))
        File.Move(temporary, path, true)

type SleepEvent =
    | EnterSleep
    | Wake of source: string
    | Resume
    | Lid of closed: bool
    | PowerMessage of string
    | MonitorFailed of string

type SleepTool = Diagnostics | HealthCheck | HealthRepair | Capabilities

module SleepTool =
    let canCancel = function Diagnostics | Capabilities -> true | _ -> false

type ISleepPlatform =
    abstract Subscribe: (SleepEvent -> unit) -> IDisposable
    abstract DisplayOff: unit -> unit
    abstract SuspendApplications: unit -> unit
    abstract DisableWakeDevices: SleepSettings -> unit
    abstract Restore: unit -> unit
    abstract QuickOff: unit -> Task<int>
    abstract RunTool: SleepTool * (string -> unit) * CancellationToken -> Task<int>

type SleepSnapshot = {
    Monitoring: bool
    Countdown: int option
    QuickOffRunning: bool
    Tool: SleepTool option
    CancellingTool: bool
    Status: string
    Closing: bool
}

type SleepController(platform: ISleepPlatform, writeToLog: string -> unit,
                     ?delay: TimeSpan -> CancellationToken -> Task) =
    let gate = obj()
    let changed = Event<SleepSnapshot>()
    let wait = defaultArg delay (fun span token -> Task.Delay(span, token))
    let mutable settings = SleepSettings.defaults()
    let mutable subscription: IDisposable option = None
    let mutable countdown: CancellationTokenSource option = None
    let mutable returnToSleep: CancellationTokenSource option = None
    let mutable lidTimer: CancellationTokenSource option = None
    let mutable toolCancellation: CancellationTokenSource option = None
    let mutable state = { Monitoring = false; Countdown = None; QuickOffRunning = false
                          Tool = None; CancellingTool = false; Status = "Sleep monitoring is paused."; Closing = false }

    let publish text =
        state <- { state with Status = text }
        writeToLog ("Sleep: " + text)
        changed.Trigger(state)

    let cancelTimer (timer: CancellationTokenSource option) =
        timer |> Option.iter (fun source -> try source.Cancel() with :? ObjectDisposedException -> ())

    let restore () =
        try platform.Restore()
        with ex -> writeToLog ("Sleep recovery needs attention: " + ex.Message)

    let pause () =
        state <- { state with Monitoring = false }
        cancelTimer returnToSleep
        cancelTimer lidTimer
        returnToSleep <- None
        lidTimer <- None
        subscription |> Option.iter (fun listener -> listener.Dispose())
        subscription <- None
        restore()

    let cancelTool () =
        match state.Tool, toolCancellation with
        | Some tool, Some source when SleepTool.canCancel tool ->
            if not state.CancellingTool then
                state <- { state with CancellingTool = true }
                source.Cancel()
                publish (if state.Closing then "Stopping diagnostic collection before closing..."
                         else "Stopping diagnostic collection...")
            true
        | _ -> false

    let rec handle event =
        lock gate (fun () ->
            if state.Monitoring && not state.Closing then
                if settings.WakeLog then writeToLog (sprintf "Sleep event: %A" event)
                try
                    match event with
                    | MonitorFailed message ->
                        pause()
                        publish ("Monitoring stopped: " + message)
                    | EnterSleep ->
                        cancelTimer returnToSleep
                        returnToSleep <- None
                        if settings.SuspendProcesses then platform.SuspendApplications()
                        if settings.DisableDevices then platform.DisableWakeDevices(settings)
                    | Resume -> restore()
                    | Wake source ->
                        // Always restore inputs/apps on resume, including a blocked wake.
                        restore()
                        cancelTimer returnToSleep
                        returnToSleep <- None
                        if settings.KeepSleep && not (Array.contains source settings.WakeSources) then
                            let cts = new CancellationTokenSource()
                            returnToSleep <- Some cts
                            publish (sprintf "Blocked wake (%s). Display turns off in 5 seconds; Pause cancels." source)
                            task {
                                try
                                    do! wait (TimeSpan.FromSeconds(5.0)) cts.Token
                                    lock gate (fun () ->
                                        if state.Monitoring && not cts.IsCancellationRequested then platform.DisplayOff())
                                with
                                | :? OperationCanceledException -> ()
                                | ex -> lock gate (fun () -> publish ("Could not turn off display: " + ex.Message))
                                cts.Dispose()
                            } |> ignore
                    | Lid false ->
                        cancelTimer lidTimer
                        lidTimer <- None
                        restore()
                    | Lid true when settings.LidDelaySeconds > 0 ->
                        cancelTimer lidTimer
                        let cts = new CancellationTokenSource()
                        lidTimer <- Some cts
                        task {
                            try
                                do! wait (TimeSpan.FromSeconds(float settings.LidDelaySeconds)) cts.Token
                                lock gate (fun () ->
                                    if state.Monitoring && not cts.IsCancellationRequested then
                                        handle EnterSleep
                                        platform.DisplayOff())
                            with
                            | :? OperationCanceledException -> ()
                            | ex -> lock gate (fun () -> restore(); publish ("Lid action failed: " + ex.Message))
                            cts.Dispose()
                        } |> ignore
                    | _ -> ()
                with ex ->
                    restore()
                    publish ("Monitoring action failed: " + ex.Message))

    member _.Changed = changed.Publish
    member _.Snapshot = lock gate (fun () -> state)
    member _.Settings = lock gate (fun () -> { settings with WakeSources = Array.copy settings.WakeSources })

    member _.UpdateSettings(value) =
        lock gate (fun () ->
            settings <- SleepSettings.normalize value
            cancelTimer returnToSleep
            cancelTimer lidTimer
            returnToSleep <- None
            lidTimer <- None
            restore())

    member _.Start() =
        lock gate (fun () ->
            if state.Closing || state.Countdown.IsSome || state.QuickOffRunning || state.Tool.IsSome then false
            elif state.Monitoring then true
            else
                try
                    state <- { state with Monitoring = true }
                    subscription <- Some (platform.Subscribe handle)
                    publish "Sleep monitoring is running."
                    true
                with ex ->
                    pause()
                    publish ("Could not start monitoring: " + ex.Message)
                    false)

    member _.Pause() =
        lock gate (fun () ->
            pause()
            publish "Sleep monitoring is paused.")

    member _.CancelSleep() =
        lock gate (fun () ->
            match countdown with
            | Some cts ->
                cts.Cancel()
                countdown <- None
                state <- { state with Countdown = None }
                publish "QuickOff countdown cancelled."
                true
            | None -> false)

    member _.ScheduleQuickOff(seconds: int) =
        lock gate (fun () ->
            if state.Closing || state.Countdown.IsSome || state.QuickOffRunning || state.Tool.IsSome then false
            elif not (List.contains seconds [5; 15; 30]) then false
            else
                let cts = new CancellationTokenSource()
                countdown <- Some cts
                state <- { state with Countdown = Some seconds }
                publish (sprintf "QuickOff sleep in %d seconds." seconds)
                task {
                    try
                        for remaining in seconds .. -1 .. 1 do
                            do! wait (TimeSpan.FromSeconds(1.0)) cts.Token
                            lock gate (fun () ->
                                if not cts.IsCancellationRequested then
                                    state <- { state with Countdown = Some (remaining - 1) }
                                    changed.Trigger(state))
                        let launched =
                            lock gate (fun () ->
                                if cts.IsCancellationRequested || state.Closing then None
                                else
                                    countdown <- None
                                    state <- { state with Countdown = None; QuickOffRunning = true }
                                    pause()
                                    publish "Requesting QuickOff sleep. Monitoring is paused."
                                    Some (platform.QuickOff()))
                        match launched with
                        | Some work ->
                            let! code = work
                            lock gate (fun () ->
                                state <- { state with QuickOffRunning = false }
                                publish (if code = 0 then "QuickOff command finished. Monitoring remains paused."
                                         else sprintf "QuickOff command failed (exit %d). Monitoring remains paused." code))
                        | None -> ()
                    with
                    | :? OperationCanceledException -> ()
                    | ex ->
                        lock gate (fun () ->
                            countdown <- None
                            state <- { state with Countdown = None; QuickOffRunning = false }
                            publish ("QuickOff failed. Monitoring remains paused: " + ex.Message))
                    cts.Dispose()
                } |> ignore
                true)

    member _.RunTool(tool: SleepTool, output: string -> unit) =
        lock gate (fun () ->
            if state.Closing || state.Countdown.IsSome || state.QuickOffRunning || state.Tool.IsSome then false
            else
                // Tools must not be suspended or lose devices during a monitoring event.
                pause()
                let cts = new CancellationTokenSource()
                toolCancellation <- Some cts
                state <- { state with Tool = Some tool }
                publish (sprintf "Running %A. Monitoring is paused." tool)
                // Completion and cancellation must not depend on the UI message loop.
                Task.Run<unit>(Func<Task<unit>>(fun () -> task {
                    let! message = task {
                        try
                            let! code = platform.RunTool(tool, output, cts.Token)
                            return if code = 0 then "Finished. Review the output and reports for findings."
                                   elif code = 3010 then "Finished. Windows needs a restart; review the report."
                                   else sprintf "Tool finished with exit %d. Review the report for details." code
                        with
                        | :? OperationCanceledException when cts.IsCancellationRequested ->
                            return "Diagnostic collection cancelled. Existing reports may be incomplete."
                        | ex -> return "Tool failed: " + ex.Message
                    }
                    lock gate (fun () ->
                        toolCancellation <- None
                        state <- { state with Tool = None; CancellingTool = false }
                        publish message)
                    cts.Dispose()
                })) |> ignore
                true)

    member _.CancelDiagnostics() = lock gate cancelTool

    member _.TryClose() =
        lock gate (fun () ->
            // Read-only collectors can be stopped. The window retries Close when
            // their process cleanup finishes, without another click from the user.
            if state.QuickOffRunning then false
            elif state.Tool |> Option.exists (SleepTool.canCancel >> not) then false
            elif state.Tool.IsSome then
                state <- { state with Closing = true }
                cancelTool() |> ignore
                false
            else
                cancelTimer countdown
                countdown <- None
                state <- { state with Countdown = None; Closing = true }
                pause()
                true)

    interface IDisposable with
        member _.Dispose() =
            lock gate (fun () ->
                cancelTimer countdown
                countdown <- None
                state <- { state with Countdown = None; Closing = true }
                cancelTool() |> ignore
                pause())
