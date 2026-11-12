namespace WindowsCleaner

open System
open System.Collections.Generic
open System.ComponentModel
open System.Diagnostics
open System.Diagnostics.Eventing.Reader
open System.IO
open System.Management
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq

module private SleepNative =
    [<UnmanagedFunctionPointer(CallingConvention.Winapi)>]
    type PowerCallback = delegate of nativeint * uint32 * nativeint -> uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type Subscription =
        val mutable Callback: nativeint
        val mutable Context: nativeint

    [<DllImport("powrprof.dll")>]
    extern uint32 PowerSettingRegisterNotification(Guid& setting, uint32 flags, Subscription& recipient, nativeint& registration)
    [<DllImport("powrprof.dll")>]
    extern uint32 PowerSettingUnregisterNotification(nativeint registration)
    [<DllImport("user32.dll", SetLastError = true)>]
    extern nativeint RegisterSuspendResumeNotification(Subscription& recipient, uint32 flags)
    [<DllImport("user32.dll")>]
    extern bool UnregisterSuspendResumeNotification(nativeint registration)
    [<DllImport("user32.dll", SetLastError = true)>]
    extern nativeint SendMessageTimeoutW(nativeint window, uint32 message, nativeint wParam, nativeint lParam,
                                       uint32 flags, uint32 timeout, nativeint& result)
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint OpenProcess(uint32 access, bool inheritHandle, int pid)
    [<DllImport("kernel32.dll")>]
    extern bool CloseHandle(nativeint handle)
    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool IsProcessCritical(nativeint handle, bool& critical)
    [<DllImport("ntdll.dll")>]
    extern int NtSuspendProcess(nativeint handle)
    [<DllImport("ntdll.dll")>]
    extern int NtResumeProcess(nativeint handle)
    [<DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)>]
    extern uint32 CM_Locate_DevNodeW(uint32& node, string deviceId, uint32 flags)
    [<DllImport("cfgmgr32.dll")>]
    extern uint32 CM_Disable_DevNode(uint32 node, uint32 flags)
    [<DllImport("cfgmgr32.dll")>]
    extern uint32 CM_Enable_DevNode(uint32 node, uint32 flags)

[<CLIMutable>]
type SleepRecoveryProcess = { Id: int; Started: int64 }
[<CLIMutable>]
type SleepRecoveryJournal = { Processes: SleepRecoveryProcess array; Devices: string array }

module SleepPlatform =
    let reportsDirectory () =
        let path = Path.Combine(AppFiles.dataDirectory(), "reports", "sleep")
        Directory.CreateDirectory(path) |> ignore
        path

    let private systemTool name = Path.Combine(Environment.SystemDirectory, name)

    let runProcessWithCancellation executable (arguments: string list) (output: string -> unit) (ct: CancellationToken) = Task.Run<int>(Func<Task<int>>(fun () -> task {
        ct.ThrowIfCancellationRequested()
        let info = ProcessStartInfo(executable, UseShellExecute = false, CreateNoWindow = true,
                                    RedirectStandardOutput = true, RedirectStandardError = true,
                                    WorkingDirectory = Environment.SystemDirectory)
        for argument in arguments do info.ArgumentList.Add(argument)
        use child = new Process(StartInfo = info)
        let exited = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let stdoutDone = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let stderrDone = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        child.EnableRaisingEvents <- true
        child.Exited.Add(fun _ -> exited.TrySetResult(()) |> ignore)
        child.OutputDataReceived.Add(fun line ->
            if isNull line.Data then stdoutDone.TrySetResult(()) |> ignore
            else output line.Data)
        child.ErrorDataReceived.Add(fun line ->
            if isNull line.Data then stderrDone.TrySetResult(()) |> ignore
            else output line.Data)
        if not (child.Start()) then failwithf "Could not start %s" executable
        child.BeginOutputReadLine()
        child.BeginErrorReadLine()
        try
            try
                // WaitForExitAsync also waits for redirected-pipe EOF. A descendant
                // retaining a pipe must not keep a finished report permanently busy.
                do! exited.Task.WaitAsync(ct)
                try
                    let! _ = Task.WhenAll(stdoutDone.Task, stderrDone.Task).WaitAsync(TimeSpan.FromSeconds(2.0), ct)
                    ()
                with :? TimeoutException -> output "The tool exited; a remaining output stream was closed."
                ct.ThrowIfCancellationRequested()
                return child.ExitCode
            with :? OperationCanceledException ->
                if not child.HasExited then
                    try child.Kill(entireProcessTree = true)
                    with :? InvalidOperationException -> () // Exited between the check and Kill.
                if not (child.WaitForExit(5000)) then failwith "The diagnostic process did not stop within 5 seconds."
                return raise (OperationCanceledException(ct))
        finally
            try child.CancelOutputRead() with :? InvalidOperationException -> ()
            try child.CancelErrorRead() with :? InvalidOperationException -> ()
    }))

    let runProcess executable arguments output =
        runProcessWithCancellation executable arguments output CancellationToken.None

    let private powershell () = systemTool "WindowsPowerShell/v1.0/powershell.exe"
    let private scriptArguments script = [ "-NoProfile"; "-NonInteractive"; "-ExecutionPolicy"; "Bypass"; "-File"; AppFiles.get ("Goodnight/" + script) ]

    let startupEnabled () = task {
        let! code = runProcess (systemTool "schtasks.exe") [ "/Query"; "/TN"; "PlumbyrSleep" ] ignore
        return code = 0
    }

    let setStartup enabled output = task {
        if enabled then
            let executable = Environment.ProcessPath
            if String.IsNullOrWhiteSpace(executable) || String.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase) then
                failwith "Launch the published Plumbyr executable before enabling sign-in startup."
            return! runProcess (systemTool "schtasks.exe")
                        [ "/Create"; "/TN"; "PlumbyrSleep"; "/TR"; sprintf "\"%s\" --sleep-monitor" executable
                          "/SC"; "ONLOGON"; "/RL"; "HIGHEST"; "/F" ] output
        else
            return! runProcess (systemTool "schtasks.exe") [ "/Delete"; "/TN"; "PlumbyrSleep"; "/F" ] output
    }

    let wakeSource reason =
        match reason with
        | 1 -> "PowerButton" | 31 -> "Keyboard" | 32 -> "Mouse" | 33 -> "TouchPad"
        | 5 -> "PowerAction" | 15 | 20 -> "DisplayOn" | _ -> "Other"

    let private subscribe (emit: SleepEvent -> unit) =
        let mutable stopped = 0
        let cancellation = new CancellationTokenSource()
        let events = MailboxProcessor.Start((fun inbox ->
            let rec loop () = async {
                let! event = inbox.Receive()
                if Volatile.Read(&stopped) = 0 then emit event
                return! loop()
            }
            loop()), cancellationToken = cancellation.Token)
        let queue event = if Volatile.Read(&stopped) = 0 then events.Post(event)
        let query = EventLogQuery("System", PathType.LogName,
                        "*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=506 or EventID=507 or EventID=105)]]")
        let watcher = new EventLogWatcher(query)
        let mutable lidGuid = Guid("ba3e0f4d-b817-4094-a2d1-d56379e6a0f3")
        let mutable displayGuid = Guid("6fe69556-704a-47a0-8f24-c28d936fda47")
        let callback = SleepNative.PowerCallback(fun _ kind data ->
            try
                match kind with
                | 4u -> queue EnterSleep
                | 7u | 18u -> queue Resume
                | 0x8013u when data <> 0n ->
                    // POWERBROADCAST_SETTING: GUID (16), DWORD length (4), DWORD value.
                    if Marshal.ReadInt32(data, 16) >= 4 then
                        let setting = Marshal.PtrToStructure<Guid>(data)
                        let value = Marshal.ReadInt32(data, 20)
                        if setting = lidGuid then queue (Lid (value = 0))
                        elif setting = displayGuid && value = 1 then queue Resume
                | _ -> ()
            with _ -> () // Exceptions must never cross the native callback boundary.
            0u)
        let mutable recipient = SleepNative.Subscription(Callback = Marshal.GetFunctionPointerForDelegate(callback), Context = 0n)
        let mutable lidRegistration = 0n
        let mutable displayRegistration = 0n
        let mutable suspendRegistration = 0n
        let cleanup () =
            if Interlocked.Exchange(&stopped, 1) = 0 then
                watcher.Dispose()
                if lidRegistration <> 0n then SleepNative.PowerSettingUnregisterNotification(lidRegistration) |> ignore
                if displayRegistration <> 0n then SleepNative.PowerSettingUnregisterNotification(displayRegistration) |> ignore
                if suspendRegistration <> 0n then SleepNative.UnregisterSuspendResumeNotification(suspendRegistration) |> ignore
                cancellation.Cancel()
                cancellation.Dispose()
            GC.KeepAlive(callback)
        try
            watcher.EventRecordWritten.Add(fun args ->
                try
                    if not (isNull args.EventException) then queue (MonitorFailed args.EventException.Message)
                    elif not (isNull args.EventRecord) then
                        use record = args.EventRecord
                        match record.Id with
                        | 506 -> queue EnterSleep
                        | 507 ->
                            let xml = XDocument.Parse(record.ToXml())
                            let reason = xml.Descendants() |> Seq.tryFind (fun element ->
                                element.Name.LocalName = "Data" && (let attribute = element.Attribute(XName.Get "Name") in not (isNull attribute) && attribute.Value = "Reason"))
                            let value = reason |> Option.map (fun element -> Int32.Parse(element.Value)) |> Option.defaultValue -1
                            queue (Wake (wakeSource value))
                        | 105 -> queue (PowerMessage "AC/DC power source changed.")
                        | _ -> ()
                with ex -> queue (PowerMessage ("Could not read power event: " + ex.Message)))
            watcher.Enabled <- true
            suspendRegistration <- SleepNative.RegisterSuspendResumeNotification(&recipient, 2u)
            if suspendRegistration = 0n then raise (Win32Exception(Marshal.GetLastWin32Error()))
            let error = SleepNative.PowerSettingRegisterNotification(&lidGuid, 2u, &recipient, &lidRegistration)
            if error <> 0u then raise (Win32Exception(int error))
            let error = SleepNative.PowerSettingRegisterNotification(&displayGuid, 2u, &recipient, &displayRegistration)
            if error <> 0u then raise (Win32Exception(int error))
            { new IDisposable with member _.Dispose() = cleanup() }
        with _ -> cleanup(); reraise()

    type WindowsSleepPlatform(log: string -> unit) =
        let suspended = Dictionary<int, nativeint * SleepRecoveryProcess>()
        let devices = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let journalPath = Path.Combine(AppFiles.dataDirectory(), sprintf "sleep-recovery-%d.json" Environment.ProcessId)
        let mutable guardianStarted = false

        let journal () =
            let value = { Processes = suspended.Values |> Seq.map snd |> Seq.toArray; Devices = devices |> Seq.toArray }
            if value.Processes.Length = 0 && value.Devices.Length = 0 then
                if File.Exists(journalPath) then File.Delete(journalPath)
            else
                let temporary = journalPath + ".tmp"
                File.WriteAllText(temporary, JsonSerializer.Serialize(value))
                File.Move(temporary, journalPath, true)

        let guardian () =
            if not guardianStarted then
                use current = Process.GetCurrentProcess()
                let ready = journalPath + ".ready"
                if File.Exists(ready) then File.Delete(ready)
                let info = ProcessStartInfo(powershell(), UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Environment.SystemDirectory)
                for value in scriptArguments "SleepRecovery.ps1" @
                             [ "-OwnerId"; string current.Id; "-OwnerStarted"; string (current.StartTime.ToUniversalTime().Ticks); "-Journal"; journalPath; "-ReadyPath"; ready ] do
                    info.ArgumentList.Add(value)
                use child = Process.Start(info)
                if isNull child then failwith "Could not start sleep recovery helper."
                let timer = Stopwatch.StartNew()
                while not (File.Exists(ready)) && not child.HasExited && timer.Elapsed < TimeSpan.FromSeconds(15.0) do Thread.Sleep(50)
                if not (File.Exists(ready)) then failwith "Sleep recovery helper was not ready; applications and devices were left unchanged."
                File.Delete(ready)
                guardianStarted <- true

        let restore () =
            for KeyValue(pid, (handle, _)) in suspended |> Seq.toArray do
                if SleepNative.NtResumeProcess(handle) = 0 then
                    SleepNative.CloseHandle(handle) |> ignore
                    suspended.Remove(pid) |> ignore
                else
                    // An exited process needs no restoration; the retained handle prevents PID reuse.
                    try
                        use child = Process.GetProcessById(pid)
                        if child.HasExited then
                            SleepNative.CloseHandle(handle) |> ignore
                            suspended.Remove(pid) |> ignore
                        else log (sprintf "Could not resume process %d; recovery will retry." pid)
                    with :? ArgumentException ->
                        SleepNative.CloseHandle(handle) |> ignore
                        suspended.Remove(pid) |> ignore
            for device in devices |> Seq.toArray do
                let mutable node = 0u
                if SleepNative.CM_Locate_DevNodeW(&node, device, 0u) = 0u && SleepNative.CM_Enable_DevNode(node, 0u) = 0u then
                    devices.Remove(device) |> ignore
                else log ("Could not restore input device; recovery will retry: " + device)
            journal()

        interface ISleepPlatform with
            member _.Subscribe emit = subscribe emit
            member _.DisplayOff() =
                let mutable result = 0n
                // Match Goodnight's SC_MONITORPOWER behavior; this is a display action.
                if SleepNative.SendMessageTimeoutW(0xffffn, 0x112u, 0xF170n, 2n, 2u, 1000u, &result) = 0n then
                    raise (Win32Exception(Marshal.GetLastWin32Error()))
            member _.Restore() = restore()
            member _.SuspendApplications() =
                guardian()
                use self = Process.GetCurrentProcess()
                let excluded = HashSet<int>()
                excluded.Add(self.Id) |> ignore
                let ownTree = HashSet<int>()
                ownTree.Add(self.Id) |> ignore
                // Exclude both ancestors and descendants so the app, shell, tools and
                // recovery helper remain able to resume everything they changed.
                use search = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process")
                use rows = search.Get()
                let parents = rows |> Seq.cast<ManagementObject> |> Seq.map (fun row -> Convert.ToInt32(row["ProcessId"]), Convert.ToInt32(row["ParentProcessId"])) |> Map.ofSeq
                let mutable parent = self.Id
                while parents.ContainsKey(parent) && not (excluded.Contains(parents[parent])) do
                    parent <- parents[parent]
                    excluded.Add(parent) |> ignore
                let mutable added = true
                while added do
                    added <- false
                    for KeyValue(pid, parentId) in parents do
                        if ownTree.Contains(parentId) && ownTree.Add(pid) then
                            excluded.Add(pid) |> ignore
                            added <- true
                let windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + string Path.DirectorySeparatorChar
                let shellNames = set [ "explorer"; "cmd"; "powershell"; "pwsh"; "conhost"; "OpenConsole"; "WindowsTerminal"; "dwm" ]
                for child in Process.GetProcesses() do
                    use child = child
                    try
                        if child.SessionId = self.SessionId && not (excluded.Contains(child.Id)) && not (shellNames.Contains child.ProcessName) && not (suspended.ContainsKey(child.Id)) then
                            let path = child.MainModule.FileName
                            if not (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) then
                                let handle = SleepNative.OpenProcess(0x1800u, false, child.Id)
                                if handle <> 0n then
                                    let mutable critical = true
                                    if SleepNative.IsProcessCritical(handle, &critical) && not critical then
                                        let entry = { Id = child.Id; Started = child.StartTime.ToUniversalTime().Ticks }
                                        suspended.Add(child.Id, (handle, entry))
                                        journal() // Record recovery before changing the process.
                                        if SleepNative.NtSuspendProcess(handle) <> 0 then
                                            suspended.Remove(child.Id) |> ignore
                                            SleepNative.CloseHandle(handle) |> ignore
                                    else SleepNative.CloseHandle(handle) |> ignore
                    with ex -> log ("Skipped application: " + ex.Message)
                journal()
            member _.DisableWakeDevices settings =
                guardian()
                let allowed source = Array.contains source settings.WakeSources
                use search = new ManagementObjectSearcher("SELECT PNPDeviceID, PNPClass, Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode = 0")
                use rows = search.Get()
                for row in rows |> Seq.cast<ManagementObject> do
                    let className = string row["PNPClass"]
                    let name = string row["Name"]
                    let source =
                        if name.Contains("touchpad", StringComparison.OrdinalIgnoreCase) || name.Contains("触摸板") then "TouchPad"
                        elif className = "Mouse" then "Mouse"
                        elif className = "Keyboard" then "Keyboard"
                        elif className = "Bluetooth" || className = "Net" then "Other"
                        else ""
                    if source <> "" && not (allowed source) then
                        let device = string row["PNPDeviceID"]
                        let mutable node = 0u
                        if not (devices.Contains(device)) && SleepNative.CM_Locate_DevNodeW(&node, device, 0u) = 0u then
                            devices.Add(device) |> ignore
                            journal()
                            // CM_DISABLE_UI_NOT_OK; allow normal restart and never persist across reboot.
                            if SleepNative.CM_Disable_DevNode(node, 4u) <> 0u then
                                devices.Remove(device) |> ignore
                                log ("Could not disable device: " + name)
                journal()
            member _.QuickOff() =
                // QuickOff compatibility deliberately preserves the source command,
                // including its possible hibernation behavior. See the bundled MIT notice.
                runProcess (systemTool "rundll32.exe") [ "powrprof.dll,SetSuspendState"; "0,1,0" ] log
            member _.RunTool(tool, output, ct) =
                match tool with
                | Capabilities -> runProcessWithCancellation (systemTool "powercfg.exe") [ "/a" ] output ct
                | Diagnostics -> runProcessWithCancellation (powershell()) (scriptArguments "SleepDebug.ps1" @ [ "-OutDir"; reportsDirectory() ]) output ct
                | HealthCheck | HealthRepair ->
                    runProcess (powershell()) (scriptArguments "SystemHealth.ps1" @
                        [ "-Mode"; (if tool = HealthRepair then "Repair" else "Check"); "-OutDir"; reportsDirectory() ]) output
