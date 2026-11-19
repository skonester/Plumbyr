namespace WindowsCleaner

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading

[<RequireQualifiedAccess>]
type DriverInstallState = Installed | Partial | Failed | Unavailable | NotAttempted | Unknown

type DriverInstallResult = {
    UpdateId: string; Revision: int; State: DriverInstallState; Code: int
    HResult: string; RebootRequired: bool; Message: string
}

type DriverInstallReport = {
    Results: DriverInstallResult list; RebootRequired: bool; Cancelled: bool
    RestorePoint: string; Error: string option
}

type DriverInstallEvent = {
    Stage: string; UpdateId: string; Revision: int; Message: string; Result: DriverInstallResult option
}

type DriverInstallBackend = DriverUpdateInfo list -> bool -> CancellationToken -> (DriverInstallEvent -> unit) -> DriverInstallReport

module DriverInstaller =
    let private key (id: string) revision = (Guid.Parse(id)).ToString("D") + ":" + string revision

    let validateSelection (updates: DriverUpdateInfo list) =
        if updates.IsEmpty || updates.Length > 256 then invalidArg "updates" "Select between 1 and 256 updates."
        let ids = HashSet<Guid>()
        for update in updates do
            let mutable id = Guid.Empty
            if not (Guid.TryParse(update.UpdateId, &id)) || update.Revision < 1 || not (ids.Add(id)) then
                invalidArg "updates" "Invalid or duplicate selected update identity. Scan again."

    /// Completed results survive worker failures. Missing outcomes are never counted as success.
    type Results(updates: DriverUpdateInfo list, onEvent: DriverInstallEvent -> unit) =
        let selected = updates |> List.map (fun u -> key u.UpdateId u.Revision) |> Set.ofList
        let completed = Dictionary<string, DriverInstallResult>()
        let mutable ended = false
        let mutable reboot = false
        let mutable cancelled = false
        let mutable restore = "Not confirmed"
        let mutable error: string option = None
        let fail message = error <- Some message
        let str (root: JsonElement) (name: string) = root.GetProperty(name).GetString()
        member _.Accept(line: string) =
            try
                if ended then failwith "Installer sent data after its final result."
                if line.Length > 65536 then failwith "Installer message exceeded its limit."
                use doc = JsonDocument.Parse(line)
                let root = doc.RootElement
                if root.GetProperty("schema").GetInt32() <> 1 then failwith "Invalid installer response."
                let stage = str root "stage"
                if stage = "end" then
                    ended <- true
                    cancelled <- root.GetProperty("cancelled").GetBoolean()
                    reboot <- reboot || root.GetProperty("reboot").GetBoolean()
                    restore <- str root "restorePoint"
                    let detail = str root "error"
                    if not (String.IsNullOrWhiteSpace detail) then fail detail
                else
                    let hasId, _ = root.TryGetProperty("id")
                    let id = if hasId then str root "id" else ""
                    let revision = if hasId then root.GetProperty("revision").GetInt32() else 0
                    if hasId && not (selected.Contains(key id revision)) then failwith "Installer reported an update outside the selection."
                    let message = str root "message"
                    let result =
                        if stage = "result" then
                            if not hasId || completed.ContainsKey(key id revision) then failwith "Duplicate or missing installer identity."
                            let state =
                                match str root "state" with
                                | "installed" -> DriverInstallState.Installed | "partial" -> DriverInstallState.Partial
                                | "failed" -> DriverInstallState.Failed | "unavailable" -> DriverInstallState.Unavailable
                                | "notAttempted" -> DriverInstallState.NotAttempted | _ -> failwith "Invalid driver installation outcome."
                            let code = root.GetProperty("code").GetInt32()
                            if (state = DriverInstallState.Installed && code <> 2) || (state = DriverInstallState.Partial && code <> 3) then
                                failwith "Inconsistent driver installation result."
                            let result = { UpdateId = id; Revision = revision; State = state; Code = code
                                           HResult = str root "hresult"; RebootRequired = root.GetProperty("reboot").GetBoolean(); Message = message }
                            completed.Add(key id revision, result)
                            reboot <- reboot || result.RebootRequired
                            Some result
                        elif List.contains stage [ "search"; "restore"; "download"; "install"; "error" ] then None
                        else failwith "Unknown driver installer event."
                    try onEvent { Stage = stage; UpdateId = id; Revision = revision; Message = message; Result = result } with _ -> ()
            with ex -> fail ("Invalid installer response: " + ex.Message)

        member _.Finish(exitCode: int, stderr: string, stopRequested: bool, started: bool) =
            if not ended || exitCode <> 0 || not (String.IsNullOrWhiteSpace stderr) then
                fail (if String.IsNullOrWhiteSpace stderr then "Installer worker ended without a complete result. Scan again and check Windows Update history before retrying." else stderr)
            let results = updates |> List.map (fun update ->
                match completed.TryGetValue(key update.UpdateId update.Revision) with
                | true, result -> result
                | _ ->
                    if error.IsNone then fail "Installer omitted a selected driver result."
                    { UpdateId = update.UpdateId; Revision = update.Revision
                      State = if started then DriverInstallState.Unknown else DriverInstallState.NotAttempted
                      Code = 0; HResult = ""; RebootRequired = false
                      Message = if started then "No confirmed outcome. Check Windows Update history and scan again." else "Installer did not start." })
            { Results = results; RebootRequired = reboot; Cancelled = cancelled || stopRequested; RestorePoint = restore; Error = error }

    let run (script: string) (dataDirectory: string) (updates: DriverUpdateInfo list) restorePoint (ct: CancellationToken) onEvent =
        validateSelection updates
        let results = Results(updates, onEvent)
        if ct.IsCancellationRequested then results.Finish(0, "Stopped before starting.", true, false)
        else
            let job = Path.Combine(dataDirectory, "driver-installs", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(job) |> ignore
            let request = Path.Combine(job, "request.json")
            let stopFile = Path.Combine(job, "stop")
            let selections = updates |> List.map (fun u -> {| id = u.UpdateId; revision = u.Revision; title = u.Title |}) |> List.toArray
            File.WriteAllText(request, JsonSerializer.Serialize({| schema = 1; updates = selections; restorePoint = restorePoint |}))
            // Stop is cooperative: never kill a Windows installation in progress.
            use registration = ct.Register(fun () -> try File.WriteAllText(stopFile, "stop") with _ -> ())
            let info = ProcessStartInfo("powershell.exe", UseShellExecute = false, CreateNoWindow = true,
                                        RedirectStandardOutput = true, RedirectStandardError = true)
            info.StandardOutputEncoding <- Text.Encoding.UTF8
            info.StandardErrorEncoding <- Text.Encoding.UTF8
            for arg in [ "-NoProfile"; "-NonInteractive"; "-ExecutionPolicy"; "Bypass"; "-File"; script; "-Request"; request; "-StopFile"; stopFile ] do
                info.ArgumentList.Add(arg)
            use proc = new Process(StartInfo = info)
            let stderr = Text.StringBuilder()
            let mutable started = false
            let report =
                try
                    proc.OutputDataReceived.Add(fun args -> if not (isNull args.Data) then results.Accept(args.Data))
                    proc.ErrorDataReceived.Add(fun args ->
                        if not (isNull args.Data) && stderr.Length < 65536 then stderr.AppendLine(args.Data) |> ignore)
                    started <- proc.Start()
                    if not started then failwith "Could not start the driver installer."
                    proc.BeginOutputReadLine(); proc.BeginErrorReadLine()
                    proc.WaitForExit()
                    results.Finish(proc.ExitCode, stderr.ToString().Trim(), ct.IsCancellationRequested, true)
                with ex ->
                    if started then
                        try proc.WaitForExit() with _ -> ()
                    results.Finish(-1, ex.Message, ct.IsCancellationRequested, started)
            try
                File.WriteAllText(Path.Combine(job, "result.json"), JsonSerializer.Serialize({|
                    rebootRequired = report.RebootRequired; cancelled = report.Cancelled; restorePoint = report.RestorePoint
                    error = defaultArg report.Error ""; results = report.Results |> List.map (fun r -> {|
                        id = r.UpdateId; revision = r.Revision; state = string r.State; code = r.Code
                        hresult = r.HResult; rebootRequired = r.RebootRequired; message = r.Message |}) |> List.toArray |}))
            with :? IOException | :? UnauthorizedAccessException -> ()
            report

    let summary (report: DriverInstallReport) =
        let count state = report.Results |> List.filter (fun r -> r.State = state) |> List.length
        sprintf "%sInstalled: %d; completed with errors: %d; failed: %d; unavailable: %d; not attempted: %d; unconfirmed: %d.%s%s"
            (if report.Cancelled then "Stopped. " else "")
            (count DriverInstallState.Installed) (count DriverInstallState.Partial) (count DriverInstallState.Failed)
            (count DriverInstallState.Unavailable) (count DriverInstallState.NotAttempted) (count DriverInstallState.Unknown)
            (if report.RebootRequired then " Restart required." else "")
            (report.Error |> Option.map (fun e -> " " + e) |> Option.defaultValue " Scan again to refresh available updates.")
