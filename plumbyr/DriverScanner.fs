namespace WindowsCleaner

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type DriverItem = {
    DeviceName: string; DeviceClass: string; Manufacturer: string
    DriverVersion: string; DriverDate: string; InfName: string
    Provider: string; InstanceId: string; HardwareIds: string array; ProblemCode: int option
}

type DriverUpdateInfo = {
    UpdateId: string; Revision: int; Title: string; Provider: string; Manufacturer: string
    DeviceClass: string; Model: string; HardwareId: string; DriverDate: string
    SizeMB: float; MayRequireReboot: bool; CurrentDriver: DriverItem option
}

type DriverUpdateScan = {
    CheckedAt: DateTimeOffset; Updates: DriverUpdateInfo list; Warnings: string list
}

[<RequireQualifiedAccess>]
type DriverScanResult<'T> = Success of 'T | Failed of string | Cancelled

type DriverScanBackend = {
    Inventory: CancellationToken -> DriverScanResult<DriverItem list>
    Updates: CancellationToken -> DriverScanResult<DriverUpdateScan>
    LastScan: unit -> DriverUpdateScan option
}

/// Read-only scans run in a separate process so a blocked WMI/COM call can be stopped.
module DriverScanner =
    let private maxOutput = 4 * 1024 * 1024

    let runProcess (startInfo: ProcessStartInfo) (timeout: TimeSpan) (ct: CancellationToken) =
        ct.ThrowIfCancellationRequested()
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.StandardOutputEncoding <- Text.Encoding.UTF8
        startInfo.StandardErrorEncoding <- Text.Encoding.UTF8
        use proc = new Process(StartInfo = startInfo)
        let kill () =
            try if not proc.HasExited then proc.Kill(true) with _ -> ()
        let read (reader: StreamReader) = task {
            let buffer = Array.zeroCreate<char> 4096
            let result = Text.StringBuilder()
            let mutable doneReading = false
            while not doneReading do
                let! count = reader.ReadAsync(buffer.AsMemory())
                if count = 0 then doneReading <- true
                elif result.Length + count > maxOutput then
                    kill()
                    raise (InvalidDataException("Driver scan output exceeded its limit."))
                else result.Append(buffer, 0, count) |> ignore
            return result.ToString()
        }
        try
            if not (proc.Start()) then failwith "Could not start the driver scan worker."
            let output = read proc.StandardOutput
            let errors = read proc.StandardError
            Task.WhenAll([| proc.WaitForExitAsync(); output :> Task; errors :> Task |])
                .WaitAsync(timeout, ct).GetAwaiter().GetResult()
            ct.ThrowIfCancellationRequested()
            if proc.ExitCode <> 0 then
                let detail = errors.Result.Trim()
                failwith (if detail = "" then sprintf "Driver scan worker exited with code %d." proc.ExitCode else detail)
            if not (String.IsNullOrWhiteSpace errors.Result) then
                failwith ("Driver scan reported an error: " + errors.Result.Trim())
            output.Result
        finally
            kill()
            try proc.WaitForExit(5000) |> ignore with _ -> ()

    let worker script mode ct =
        let info = ProcessStartInfo("powershell.exe")
        for arg in [ "-NoProfile"; "-NonInteractive"; "-ExecutionPolicy"; "Bypass"; "-File"; script; "-Mode"; mode ] do
            info.ArgumentList.Add(arg)
        runProcess info (TimeSpan.FromMinutes(if mode = "updates" then 10.0 else 2.0)) ct

    let private text (name: string) (value: JsonElement) =
        let p = value.GetProperty(name)
        if p.ValueKind <> JsonValueKind.String then failwithf "Invalid driver scan field: %s." name
        p.GetString()

    let private items (name: string) (value: JsonElement) =
        let p = value.GetProperty(name)
        if p.ValueKind <> JsonValueKind.Array then failwithf "Invalid driver scan array: %s." name
        p.EnumerateArray() |> Seq.toList

    let private inventory root =
        items "drivers" root |> List.map (fun d -> {
            DeviceName = text "name" d; DeviceClass = text "cls" d; Manufacturer = text "maker" d
            DriverVersion = text "version" d; DriverDate = text "date" d; InfName = text "inf" d
            Provider = text "provider" d; InstanceId = text "instance" d
            HardwareIds = items "hardwareIds" d |> List.map (fun p ->
                if p.ValueKind <> JsonValueKind.String then failwith "Invalid hardware ID."
                p.GetString()) |> List.toArray
            ProblemCode = let p = d.GetProperty("code") in if p.ValueKind = JsonValueKind.Null then None else Some(p.GetInt32())
        }) |> List.sortBy (fun d -> d.DeviceName)

    /// Matching is only for display. Shared/compatible IDs may describe multiple devices.
    let matchCurrent (hardwareId: string) (drivers: DriverItem list) =
        if String.IsNullOrWhiteSpace hardwareId then None
        else
            match drivers |> List.filter (fun d -> d.HardwareIds |> Array.exists (fun id ->
                String.Equals(id.Trim(), hardwareId.Trim(), StringComparison.OrdinalIgnoreCase))) with
            | [driver] -> Some driver
            | _ -> None

    let private envelope kind (json: string) =
        if json.Length > maxOutput then failwith "Driver scan response exceeded its limit."
        let doc = JsonDocument.Parse(json)
        try
            let root = doc.RootElement
            if root.GetProperty("schema").GetInt32() <> 1 || text "kind" root <> kind then
                failwith "Invalid driver scan response."
            let code = root.GetProperty("resultCode").GetInt32()
            if code <> 2 && code <> 3 then failwithf "Driver scan failed (result %d)." code
            let warnings = items "warnings" root |> List.map (fun p ->
                if p.ValueKind <> JsonValueKind.String then failwith "Invalid scan warning."
                p.GetString())
            let warnings = if code = 3 && warnings.IsEmpty then [ "Windows Update returned incomplete results." ] else warnings
            doc, warnings
        with _ -> doc.Dispose(); reraise()

    let parseInventory json =
        let doc, warnings = envelope "inventory" json
        use doc = doc
        if not warnings.IsEmpty then failwith (String.concat " " warnings)
        inventory doc.RootElement

    let parseUpdates json =
        let doc, warnings = envelope "updates" json
        use doc = doc
        let root = doc.RootElement
        let drivers = inventory root
        let checkedAt = DateTimeOffset.Parse(text "checkedAtUtc" root, Globalization.CultureInfo.InvariantCulture)
        let updates = items "updates" root |> List.map (fun u ->
            let id = text "id" u
            let mutable parsed = Guid.Empty
            if not (Guid.TryParse(id, &parsed)) then failwith "Invalid Windows Update identity."
            let size = u.GetProperty("sizeMB").GetDouble()
            if not (Double.IsFinite size) || size < 0.0 then failwith "Invalid update download size."
            let hw = text "hardwareId" u
            { UpdateId = id; Revision = u.GetProperty("revision").GetInt32(); Title = text "title" u
              Provider = text "provider" u; Manufacturer = text "maker" u; DeviceClass = text "cls" u
              Model = text "model" u; HardwareId = hw; DriverDate = text "date" u; SizeMB = size
              MayRequireReboot = u.GetProperty("mayReboot").GetBoolean(); CurrentDriver = matchCurrent hw drivers })
        { CheckedAt = checkedAt; Updates = updates; Warnings = warnings }

    let private execute (ct: CancellationToken) action =
        try
            ct.ThrowIfCancellationRequested()
            let result = action()
            ct.ThrowIfCancellationRequested()
            DriverScanResult.Success result
        with
        | :? OperationCanceledException -> DriverScanResult.Cancelled
        | :? TimeoutException -> DriverScanResult.Failed "Driver scan timed out. Windows Update or device inventory did not respond. Try again later."
        | ex -> DriverScanResult.Failed ("Driver scan failed: " + ex.Message)

    let scanInventoryWith run ct = execute ct (fun () -> run "inventory" ct |> parseInventory)

    let scanUpdatesWith run cachePath ct = execute ct (fun () ->
        let json = run "updates" ct
        let scan = parseUpdates json
        ct.ThrowIfCancellationRequested()
        // Only complete, validated scans replace the last successful result.
        if scan.Warnings.IsEmpty then
            let temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp"
            try
                try
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cachePath))) |> ignore
                    File.WriteAllText(temporary, json)
                    ct.ThrowIfCancellationRequested()
                    File.Move(temporary, cachePath, true)
                with :? IOException | :? UnauthorizedAccessException -> ()
            finally
                try if File.Exists temporary then File.Delete temporary with _ -> ()
        scan)

    let loadLastScan path =
        try
            if not (File.Exists path) || FileInfo(path).Length > int64 maxOutput then None
            else
                let scan = File.ReadAllText(path) |> parseUpdates
                if scan.Warnings.IsEmpty then Some scan else None
        with _ -> None

    let summary (scan: DriverUpdateScan) =
        if not scan.Warnings.IsEmpty then
            sprintf "Driver scan incomplete — %d update(s) returned. %s" scan.Updates.Length (String.concat " " scan.Warnings)
        elif scan.Updates.IsEmpty then "Windows Update offered no driver updates for this PC."
        else sprintf "Windows Update offered %d driver update(s). See the scan results and log for details." scan.Updates.Length
