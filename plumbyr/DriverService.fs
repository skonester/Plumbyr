namespace WindowsCleaner

open System
open System.IO
open System.Management
open System.Diagnostics
open System.Threading

// -----------------------------------------------------------------------
// Driver record types
// -----------------------------------------------------------------------

type DriverItem = {
    DeviceName   : string
    DeviceClass  : string
    Manufacturer : string
    DriverVersion: string
    DriverDate   : string
    InfName      : string
}

type DriverUpdateInfo = {
    Title    : string
    Provider : string
    SizeMB   : float
}

// -----------------------------------------------------------------------
// DriverService module — F# port of DriverUpdate PowerShell scripts
// -----------------------------------------------------------------------
module DriverService =

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    let private wmiString (obj: ManagementBaseObject) key =
        try
            let v = obj.[key]
            if isNull v then "" else v.ToString().Trim()
        with _ -> ""

    let private wmiDate (obj: ManagementBaseObject) key =
        try
            let v = obj.[key]
            if isNull v then ""
            else
                // WMI DMTF datetime: "20250101000000.000000+000"
                let s = v.ToString()
                if s.Length >= 8 then
                    sprintf "%s-%s-%s" s.[0..3] s.[4..5] s.[6..7]
                else s
        with _ -> ""

    let private runProcess (exe: string) (args: string) (onLog: string -> unit) =
        try
            let psi = ProcessStartInfo(exe, args)
            psi.UseShellExecute        <- false
            psi.CreateNoWindow         <- true
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError  <- true
            let proc = Process.Start(psi)
            proc.OutputDataReceived.Add(fun e -> if not (isNull e.Data) then onLog e.Data)
            proc.ErrorDataReceived.Add(fun e ->  if not (isNull e.Data) then onLog (sprintf "ERR: %s" e.Data))
            proc.BeginOutputReadLine()
            proc.BeginErrorReadLine()
            proc.WaitForExit()
            proc.ExitCode
        with ex ->
            onLog (sprintf "[ERROR] Failed to start '%s': %s" exe ex.Message)
            -1

    // ------------------------------------------------------------------
    // 1) Scan installed signed drivers via Win32_PnPSignedDriver
    // ------------------------------------------------------------------

    let scanDrivers () : DriverItem list =
        try
            use searcher = new ManagementObjectSearcher(
                "SELECT DeviceName, Manufacturer, DriverVersion, InfName, DeviceClass, DriverDate FROM Win32_PnPSignedDriver")
            use results = searcher.Get()
            [ for mbo in results do
                let item = {
                    DeviceName    = wmiString mbo "DeviceName"
                    DeviceClass   = wmiString mbo "DeviceClass"
                    Manufacturer  = wmiString mbo "Manufacturer"
                    DriverVersion = wmiString mbo "DriverVersion"
                    DriverDate    = wmiDate   mbo "DriverDate"
                    InfName       = wmiString mbo "InfName"
                }
                mbo.Dispose()
                yield item ]
            |> List.sortBy (fun d -> d.DeviceName)
        with _ ->
            []

    // ------------------------------------------------------------------
    // 2) Export driver list to CSV
    // ------------------------------------------------------------------

    let exportDriversCsv (drivers: DriverItem list) (filePath: string) =
        use sw = new StreamWriter(filePath, false, Text.Encoding.UTF8)
        sw.WriteLine("DeviceName,DeviceClass,Manufacturer,DriverVersion,DriverDate,InfName")
        for d in drivers do
            let esc (s: string) = "\"" + s.Replace("\"", "\"\"") + "\""
            sw.WriteLine(sprintf "%s,%s,%s,%s,%s,%s"
                (esc d.DeviceName) (esc d.DeviceClass) (esc d.Manufacturer)
                (esc d.DriverVersion) (esc d.DriverDate) (esc d.InfName))

    // ------------------------------------------------------------------
    // 3) Check driver updates via Windows Update COM object
    //    (mirrors DriveUpdateV3.ps1 CheckDriverUpdates task)
    // ------------------------------------------------------------------

    let checkDriverUpdates (ct: CancellationToken) (onLog: string -> unit) : DriverUpdateInfo list =
        onLog "Checking for available driver updates via Windows Update..."
        try
            let sessionType  = Type.GetTypeFromProgID("Microsoft.Update.Session")
            if isNull sessionType then
                onLog "[ERROR] Windows Update COM object not available on this system."
                []
            else
            let session   = Activator.CreateInstance(sessionType)
            let searcher  = sessionType.InvokeMember("CreateUpdateSearcher", Reflection.BindingFlags.InvokeMethod, null, session, [||])
            let st        = searcher.GetType()
            onLog "Searching Windows Update catalog (this may take a moment)..."
            let result    = st.InvokeMember("Search", Reflection.BindingFlags.InvokeMethod, null, searcher, [| "IsInstalled=0 and Type='Driver'" |])
            let rt        = result.GetType()
            let coll      = rt.InvokeMember("Updates", Reflection.BindingFlags.GetProperty, null, result, [||])
            let ct2       = coll.GetType()
            let count     = ct2.InvokeMember("Count", Reflection.BindingFlags.GetProperty, null, coll, [||]) :?> int
            if count = 0 then
                onLog "No driver updates available. Your drivers are up to date!"
                []
            else
                onLog (sprintf "Found %d driver update(s) available:" count)
                [ for i in 0 .. count - 1 do
                    let u   = ct2.InvokeMember("Item", Reflection.BindingFlags.InvokeMethod, null, coll, [| i |])
                    let ut  = u.GetType()
                    let getString n = try ut.InvokeMember(n, Reflection.BindingFlags.GetProperty, null, u, [||]) :?> string with _ -> ""
                    let getFloat  n = try Convert.ToDouble(ut.InvokeMember(n, Reflection.BindingFlags.GetProperty, null, u, [||])) with _ -> 0.0
                    let title    = getString "Title"
                    let provider = getString "DriverProvider"
                    let size     = getFloat  "MaxDownloadSize" / 1_048_576.0
                    onLog (sprintf "  %d. %s" (i+1) title)
                    if provider <> "" then onLog (sprintf "     Provider : %s" provider)
                    if size > 0.0    then onLog (sprintf "     Size     : %.2f MB" size)
                    yield { Title = title; Provider = provider; SizeMB = size } ]
        with ex ->
            onLog (sprintf "[ERROR] Failed to check driver updates: %s" ex.Message)
            onLog "Make sure the Windows Update service is running and you are an administrator."
            []

    // ------------------------------------------------------------------
    // 4) Run the full Windows Update pipeline (scan -> download -> install)
    //    (mirrors DriveUpdateV3.ps1 WindowsUpdate task)
    // ------------------------------------------------------------------

    let runWindowsUpdate (ct: CancellationToken) (onLog: string -> unit) (onProgress: int -> unit) =
        onLog "Starting Windows Update pipeline..."
        try
            onLog "[1/4] Triggering update detection (wuauclt /detectnow)..."
            runProcess "wuauclt.exe" "/detectnow" onLog |> ignore
            onProgress 10
            if not ct.IsCancellationRequested then
                Thread.Sleep(3000)
                onLog "[2/4] Scanning for updates (usoclient StartScan)..."
                let r2 = runProcess "usoclient.exe" "StartScan" onLog
                if r2 <> 0 then onLog "  (Scan returned non-zero; system may already be scanning)"
                onProgress 30
            if not ct.IsCancellationRequested then
                Thread.Sleep(3000)
                onLog "[3/4] Downloading updates (usoclient StartDownload)..."
                let r3 = runProcess "usoclient.exe" "StartDownload" onLog
                if r3 <> 0 then onLog "  (No updates to download, or system is up to date)"
                onProgress 65
            if not ct.IsCancellationRequested then
                Thread.Sleep(3000)
                onLog "[4/4] Installing updates (usoclient StartInstall)..."
                let r4 = runProcess "usoclient.exe" "StartInstall" onLog
                if r4 <> 0 then onLog "  (No updates to install, or system is up to date)"
                onProgress 100
                onLog ""
                onLog "Windows Update pipeline complete. Check Settings -> Windows Update for status."
            else
                onLog "[CANCELLED] Task was cancelled."
        with ex ->
            onLog (sprintf "[ERROR] Windows Update pipeline failed: %s" ex.Message)

    // ------------------------------------------------------------------
    // 5) Backup 3rd-party drivers via DISM
    //    (mirrors DriveUpdateV3.ps1 BackupDrivers task)
    // ------------------------------------------------------------------

    let backupDrivers (destPath: string) (ct: CancellationToken) (onLog: string -> unit) (onProgress: int -> unit) =
        let cleanDest = destPath.Trim().TrimEnd('\\', '/')
        onLog (sprintf "Backing up drivers to: %s" cleanDest)
        try
            if not (Directory.Exists(cleanDest)) then
                Directory.CreateDirectory(cleanDest) |> ignore
            onLog "Exporting drivers with DISM (this may take several minutes)..."
            onProgress 10
            let rc = runProcess "dism.exe" (sprintf "/online /export-driver /destination:\"%s\"" cleanDest) onLog
            onProgress 90
            if rc = 0 then
                let count = Directory.GetDirectories(cleanDest, "*", SearchOption.TopDirectoryOnly).Length
                onLog (sprintf "Backup complete: approximately %d driver package(s) exported." count)
            else
                onLog "[WARNING] DISM finished with a non-zero exit code. Check log above."
            onProgress 100
        with ex ->
            onLog (sprintf "[ERROR] Backup failed: %s" ex.Message)

    // ------------------------------------------------------------------
    // 6) Install drivers from a folder (recursive .inf scan + pnputil)
    //    (mirrors DriveUpdateV3.ps1 InstallDrivers task)
    // ------------------------------------------------------------------

    let installDriversFromFolder (folderPath: string) (ct: CancellationToken) (onLog: string -> unit) (onProgress: int -> unit) =
        let cleanFolder = folderPath.Trim().TrimEnd('\\', '/')
        onLog (sprintf "Installing drivers from: %s" cleanFolder)
        if not (Directory.Exists(cleanFolder)) then
            onLog (sprintf "[ERROR] Folder not found: %s" folderPath)
        else
            try
                let infFiles = Directory.GetFiles(folderPath, "*.inf", SearchOption.AllDirectories)
                if infFiles.Length = 0 then
                    onLog "[ERROR] No .inf driver files found in the selected folder."
                else
                    onLog (sprintf "Found %d .inf file(s). Installing..." infFiles.Length)
                    let mutable ok   = 0
                    let mutable fail = 0
                    for i in 0 .. infFiles.Length - 1 do
                        if not ct.IsCancellationRequested then
                            let inf = infFiles.[i]
                            onLog (sprintf "[%d/%d] %s" (i+1) infFiles.Length (Path.GetFileName(inf)))
                            let rc = runProcess "pnputil.exe" (sprintf "/add-driver \"%s\" /install" inf) onLog
                            if rc = 0 then ok <- ok + 1 else fail <- fail + 1
                            onProgress (10 + int (float (i+1) / float infFiles.Length * 85.0))
                            Thread.Sleep(250)
                    onProgress 100
                    onLog ""
                    onLog (sprintf "Installation complete: %d succeeded, %d failed." ok fail)
                    if ok > 0 then onLog "Note: A reboot may be required for some drivers to take effect."
            with ex ->
                onLog (sprintf "[ERROR] Install failed: %s" ex.Message)

    // ------------------------------------------------------------------
    // 7) Create a System Restore Point via PowerShell
    //    (mirrors DriveUpdateV3.ps1 New-RestorePoint function)
    // ------------------------------------------------------------------

    let createRestorePoint (description: string) (onLog: string -> unit) =
        onLog (sprintf "Creating system restore point: '%s'..." description)
        try
            let sysDrive = Environment.GetEnvironmentVariable("SystemDrive")
            let escaped  = description.Replace("'", "''")
            let script   = sprintf "Enable-ComputerRestore -Drive '%s\\' -ErrorAction SilentlyContinue; Checkpoint-Computer -Description '%s' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop" sysDrive escaped
            let rc = runProcess "powershell.exe" (sprintf "-NoProfile -NonInteractive -Command \"%s\"" script) onLog
            if rc = 0 then
                onLog "System restore point created successfully!"
                true
            else
                onLog "[ERROR] Failed to create restore point. Try running as administrator."
                false
        with ex ->
            onLog (sprintf "[ERROR] %s" ex.Message)
            false
