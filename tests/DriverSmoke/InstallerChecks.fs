module InstallerChecks

open System
open System.IO
open System.Text.Json
open System.Threading
open WindowsCleaner

let private check condition message = if not condition then failwith message

let run directory (offered: DriverUpdateInfo) =
    let second = { offered with UpdateId = "22222222-2222-3333-4444-555555555555"; Revision = 4 }
    let fixture = Path.Combine(__SOURCE_DIRECTORY__, "InstallWorkerFixture.ps1")
    let runCase name restore =
        let data = Path.Combine(directory, "install-fixtures", name + "-" + Guid.NewGuid().ToString("N"))
        let chosen = [ { offered with Title = name }; second ]
        let report = DriverInstaller.run fixture data chosen restore CancellationToken.None ignore
        let job = Directory.GetDirectories(Path.Combine(data, "driver-installs")) |> Array.exactlyOne
        let callsFile = Path.Combine(job, "calls.txt")
        let calls = if File.Exists callsFile then File.ReadAllLines callsFile else [||]
        check (not (calls |> Array.exists (fun call -> call.Contains("99999999")))) "Unselected driver was touched"
        check (report.Results.Length = 2 && File.Exists(Path.Combine(job, "result.json"))) "Per-driver outcomes were lost"
        report, calls
    let success, calls = runCase "success" true
    check (success.Error.IsNone && success.RebootRequired && success.Results |> List.forall (fun r -> r.State = DriverInstallState.Installed)) "Selected install was not confirmed"
    check (calls[0] = "search" && calls[1] = "restore" && calls |> Array.filter (fun s -> s.StartsWith("install:")) |> Array.length = 2) "Selected-only install/restore order is wrong"
    check (success.RestorePoint = "Created and verified") "Restore point was not verified"
    for scenario in [ "restore-fail"; "restore-skipped"; "search-fail"; "search-partial" ] do
        let result, calls = runCase scenario true
        check (result.Error.IsSome && result.Results |> List.forall (fun r -> r.State = DriverInstallState.NotAttempted)) (scenario + " was treated as success")
        check (not (calls |> Array.exists (fun s -> s.StartsWith("download:") || s.StartsWith("install:")))) "Install ran after failed preflight"
    let unavailable, calls = runCase "revision-changed" false
    check (unavailable.Results |> List.forall (fun r -> r.State = DriverInstallState.Unavailable)) "Changed revisions were installed"
    check (calls = [| "search" |]) "Unavailable selection was downloaded"
    let downloadFailure, calls = runCase "download-fail" false
    check (downloadFailure.Results |> List.forall (fun r -> r.State = DriverInstallState.Failed && r.HResult <> "")) "Download failure lost HRESULT"
    check (not (calls |> Array.exists (fun c -> c.StartsWith("install:")))) "Failed download was installed"
    let installFailure, _ = runCase "install-fail" false
    check (installFailure.Results |> List.forall (fun r -> r.State = DriverInstallState.Failed)) "Failed installation claimed success"
    let partial, _ = runCase "partial" false
    check (partial.Results |> List.forall (fun r -> r.State = DriverInstallState.Partial && r.Code = 3)) "Partial installation claimed full success"
    for scenario in [ "overall-warning"; "hresult-warning" ] do
        let warning, _ = runCase scenario false
        check (warning.Results |> List.forall (fun r -> r.State = DriverInstallState.Partial)) (scenario + " claimed full success")
    let stopped, calls = runCase "stop-after-first" false
    check (stopped.Cancelled && stopped.Results[0].State = DriverInstallState.Installed && stopped.Results[1].State = DriverInstallState.NotAttempted) "Stopping lost a completed installation"
    check (calls |> Array.filter (fun c -> c.StartsWith("install:")) |> Array.length = 1) "Stop started another driver"
    let stoppedDownload, calls = runCase "cancel-download" false
    check (stoppedDownload.Cancelled && stoppedDownload.Results |> List.forall (fun r -> r.State = DriverInstallState.NotAttempted)) "Stop after download was ignored"
    check (not (calls |> Array.exists (fun c -> c.StartsWith("install:")))) "Install started after stop request"
    let crash, _ = runCase "crash-after-first" false
    check (crash.Error.IsSome && crash.Results[0].State = DriverInstallState.Installed && crash.Results[1].State = DriverInstallState.Unknown) "Worker crash lost success or invented a result"
    let reboot, calls = runCase "reboot-before" false
    check (reboot.RebootRequired && not (calls |> Array.exists (fun c -> c.StartsWith("install:")))) "Pending reboot was not respected"
    // Cancellation reaches the actual host through its stop file, without killing a running install.
    use cancellation = new CancellationTokenSource()
    let slow = DriverInstaller.run fixture (Path.Combine(directory, "slow-" + Guid.NewGuid().ToString("N")))
                    [ { offered with Title = "slow" }; second ] false cancellation.Token
                    (fun event -> if event.Stage = "install" then cancellation.Cancel())
    check (slow.Cancelled && slow.Results[0].State = DriverInstallState.Installed && slow.Results[1].State = DriverInstallState.NotAttempted) "Host killed or lost the active Windows operation"
    let rejects choices =
        try DriverInstaller.validateSelection choices; false with :? ArgumentException -> true
    check (rejects [] && rejects [offered; offered] && rejects [{ offered with UpdateId = "invalid" }]) "Invalid selection was accepted"
    let serialize value = JsonSerializer.Serialize(value)
    let collector = DriverInstaller.Results([offered; second], ignore)
    collector.Accept(serialize {| schema=1; stage="result"; id=offered.UpdateId; revision=offered.Revision;
                                 state="installed"; code=2; hresult="00000000"; reboot=true; message="Confirmed" |})
    collector.Accept("malformed response")
    let unknown = collector.Finish(0, "", false, true)
    check (unknown.Error.IsSome && unknown.Results[0].State = DriverInstallState.Installed && unknown.Results[1].State = DriverInstallState.Unknown) "Malformed response lost confirmed outcomes"
    let inconsistent = DriverInstaller.Results([offered], ignore)
    inconsistent.Accept(serialize {| schema=1; stage="result"; id=offered.UpdateId; revision=offered.Revision;
                                    state="installed"; code=3; hresult="00000000"; reboot=false; message="Wrong" |})
    check ((inconsistent.Finish(0, "", false, true)).Results.Head.State = DriverInstallState.Unknown) "Code 3 was counted as installed"
    use embedded = typeof<DriverInstallReport>.Assembly.GetManifestResourceStream("Plumbyr.Payload/Drivers/DriverInstall.ps1")
    use reader = new StreamReader(embedded)
    let source = Path.Combine(__SOURCE_DIRECTORY__, "../../plumbyr/DriverInstall.ps1")
    check (reader.ReadToEnd() = File.ReadAllText(source)) "Embedded installer differs from source"
    printfn "Selected driver installation, restore, stopping, partial/failure, and worker-loss fixtures passed."
