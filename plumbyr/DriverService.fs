namespace WindowsCleaner

open System
open System.IO
open System.Diagnostics
open System.Threading

// Driver mutations remain here; read-only inventory/update scans use DriverScanner.
module DriverService =

    let private cachePath () = Path.Combine(AppFiles.dataDirectory(), "drivers-last-scan.json")
    let private runScan mode ct = DriverScanner.worker (AppFiles.get "Drivers/DriverScan.ps1") mode ct
    let scanDrivers ct = DriverScanner.scanInventoryWith runScan ct
    let checkDriverUpdates ct = DriverScanner.scanUpdatesWith runScan (cachePath()) ct
    let lastDriverScan () = DriverScanner.loadLastScan (cachePath())
    let scanner = { Inventory = scanDrivers; Updates = checkDriverUpdates; LastScan = lastDriverScan }

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

    // StellarHub's selected-update flow, with exact revisions and confirmed outcomes.
    let installSelectedDriverUpdates updates restorePoint ct onEvent =
        DriverInstaller.validateSelection updates
        // Installation may change the machine even if its worker later fails.
        // Never retain a pre-install scan as the next launch's available updates.
        let cache = cachePath()
        if File.Exists cache then File.Delete cache
        DriverInstaller.run (AppFiles.get "Drivers/DriverInstall.ps1") (AppFiles.dataDirectory()) updates restorePoint ct onEvent

    let installer: DriverInstallBackend = installSelectedDriverUpdates

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
