namespace WindowsCleaner

open System
open System.IO
open System.Management
open Microsoft.Win32
open System.Runtime.InteropServices
open Vortice.DXGI

type SystemInfoData = {
    OS: string
    Processor: string
    Memory: string
    GPU: string
    DiskSpace: string
    Architecture: string
}

type DiagnosticItem = {
    Section: string
    Name: string
    Value: string
}

type GpuAdapterInfo = {
    Name: string
    DedicatedVideoMemoryBytes: uint64
    DedicatedSystemMemoryBytes: uint64
    SharedSystemMemoryBytes: uint64
    DriverVersion: string option
    IsSoftware: bool
}

module SystemInfo =
    let private unknown = "Unknown"

    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)>]
    type private MemoryStatusEx =
        val mutable dwLength: uint32
        val mutable dwMemoryLoad: uint32
        val mutable ullTotalPhys: uint64
        val mutable ullAvailPhys: uint64
        val mutable ullTotalPageFile: uint64
        val mutable ullAvailPageFile: uint64
        val mutable ullTotalVirtual: uint64
        val mutable ullAvailVirtual: uint64
        val mutable ullAvailExtendedVirtual: uint64

    [<DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)>]
    extern bool private GlobalMemoryStatusEx(MemoryStatusEx& lpBuffer)

    let private stringValue (mo: ManagementBaseObject) propertyName =
        try
            match mo.Properties.[propertyName].Value with
            | null -> None
            | value ->
                let text = string value
                if String.IsNullOrWhiteSpace text then None else Some text
        with _ -> None

    let private uint64Value (mo: ManagementBaseObject) propertyName =
        try
            match mo.Properties.[propertyName].Value with
            | null -> None
            | :? uint64 as value -> Some value
            | :? uint32 as value -> Some(uint64 value)
            | :? int64 as value when value >= 0L -> Some(uint64 value)
            | :? int as value when value >= 0 -> Some(uint64 value)
            | value ->
                match UInt64.TryParse(string value) with
                | true, parsed -> Some parsed
                | _ -> None
        with _ -> None

    let private queryFirst className (properties: string list) =
        try
            let query = sprintf "SELECT %s FROM %s" (String.concat ", " properties) className
            use searcher = new ManagementObjectSearcher(query)
            searcher.Get()
            |> Seq.cast<ManagementObject>
            |> Seq.tryHead
        with _ -> None

    let private queryAll className (properties: string list) =
        try
            let query = sprintf "SELECT %s FROM %s" (String.concat ", " properties) className
            use searcher = new ManagementObjectSearcher(query)
            searcher.Get()
            |> Seq.cast<ManagementObject>
            |> Seq.toList
        with _ -> []

    let private formatBytes bytes =
        let gb = float bytes / 1073741824.0
        if gb >= 1024.0 then sprintf "%.2f TB" (gb / 1024.0)
        else sprintf "%.2f GB" gb

    let private formatGpuMemory bytes =
        if bytes = 0UL then unknown
        else
            let gib = float bytes / 1073741824.0
            let cardClass = int (Math.Round(gib, MidpointRounding.AwayFromZero))
            if cardClass > 0 && abs (gib - float cardClass) < 0.25 then
                sprintf "%.2f GiB (~%d GB)" gib cardClass
            else
                sprintf "%.2f GiB" gib

    let private add section name value =
        { Section = section; Name = name; Value = if String.IsNullOrWhiteSpace value then unknown else value }

    let private getRegistryValue (hive: RegistryKey) (subKey: string) (name: string) =
        try
            use key = hive.OpenSubKey(subKey)
            if isNull key then None
            else
                match key.GetValue(name) with
                | null -> None
                | value ->
                    let text = string value
                    if String.IsNullOrWhiteSpace text then None else Some text
        with _ -> None

    let private getMemoryStatus () =
        try
            let mutable status = MemoryStatusEx()
            status.dwLength <- uint32 (Marshal.SizeOf<MemoryStatusEx>())
            if GlobalMemoryStatusEx(&status) then Some status else None
        with _ -> None

    let private fixedDisksFromDriveInfo () =
        try
            DriveInfo.GetDrives()
            |> Array.filter (fun drive -> drive.DriveType = DriveType.Fixed && drive.IsReady)
            |> Array.toList
        with _ -> []

    let private getWmiGpuDriverVersions () =
        let videoDrivers =
            queryAll "Win32_VideoController" [ "Name"; "DriverVersion" ]
            |> List.choose (fun gpu ->
                match stringValue gpu "Name" with
                | Some name ->
                    Some(name, stringValue gpu "DriverVersion")
                | None -> None)

        let signedDrivers =
            queryAll "Win32_PnPSignedDriver" [ "DeviceName"; "DriverVersion"; "DeviceClass" ]
            |> List.choose (fun driver ->
                let deviceClass = stringValue driver "DeviceClass" |> Option.defaultValue ""
                if not (deviceClass.Equals("DISPLAY", StringComparison.OrdinalIgnoreCase)) then None
                else
                    match stringValue driver "DeviceName" with
                    | Some name -> Some(name, stringValue driver "DriverVersion")
                    | None -> None)

        videoDrivers @ signedDrivers
        |> List.distinctBy fst

    let private getWmiGpuNames () =
        queryAll "Win32_VideoController" [ "Name" ]
        |> List.choose (fun gpu ->
            match stringValue gpu "Name" with
            | Some name -> Some name
            | None -> None)

    let private lookupDriverVersion gpuName =
        getWmiGpuDriverVersions()
        |> List.tryPick (fun (name, version) ->
            if name.Equals(gpuName, StringComparison.OrdinalIgnoreCase) then version
            elif gpuName.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(gpuName, StringComparison.OrdinalIgnoreCase) then version
            else None)

    let private getDxgiAdapters () =
        try
            use factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>()
            let adapters = ResizeArray<GpuAdapterInfo>()
            let mutable index = 0u
            let mutable keepGoing = true

            while keepGoing do
                let mutable adapter: IDXGIAdapter1 = null
                let result = factory.EnumAdapters1(index, &adapter)
                if result.Success && not (isNull adapter) then
                    try
                        let desc = adapter.Description1
                        let dedicatedVideoMemory = uint64 desc.DedicatedVideoMemory
                        let dedicatedSystemMemory = uint64 desc.DedicatedSystemMemory
                        let sharedSystemMemory = uint64 desc.SharedSystemMemory
                        let isSoftware = desc.Flags.HasFlag(AdapterFlags.Software)

                        adapters.Add({
                            Name = if String.IsNullOrWhiteSpace desc.Description then unknown else desc.Description
                            DedicatedVideoMemoryBytes = dedicatedVideoMemory
                            DedicatedSystemMemoryBytes = dedicatedSystemMemory
                            SharedSystemMemoryBytes = sharedSystemMemory
                            DriverVersion = lookupDriverVersion desc.Description
                            IsSoftware = isSoftware
                        })
                    finally
                        adapter.Dispose()
                    index <- index + 1u
                else
                    keepGoing <- false

            adapters |> Seq.toList
        with _ -> []

    let getBasicInfo () =
        {
            OS = RuntimeInformation.OSDescription
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") |> Option.ofObj |> Option.defaultValue unknown
            Memory = unknown
            GPU = unknown
            DiskSpace = unknown
            Architecture = RuntimeInformation.OSArchitecture.ToString()
        }

    let getOsInfo () =
        match queryFirst "Win32_OperatingSystem" [ "Caption"; "Version"; "BuildNumber" ] with
        | Some os ->
            let caption = stringValue os "Caption" |> Option.defaultValue RuntimeInformation.OSDescription
            let version = stringValue os "Version" |> Option.defaultValue ""
            let build = stringValue os "BuildNumber" |> Option.defaultValue ""
            [ caption; version; if String.IsNullOrWhiteSpace build then "" else sprintf "Build %s" build ]
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> String.concat " "
        | None -> RuntimeInformation.OSDescription

    let getCpuInfo () =
        match queryFirst "Win32_Processor" [ "Name" ] with
        | Some cpu -> stringValue cpu "Name" |> Option.defaultValue unknown
        | None ->
            getRegistryValue Registry.LocalMachine @"HARDWARE\DESCRIPTION\System\CentralProcessor\0" "ProcessorNameString"
            |> Option.orElse (Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") |> Option.ofObj)
            |> Option.defaultValue unknown

    let getRamInfo () =
        match queryFirst "Win32_ComputerSystem" [ "TotalPhysicalMemory" ] with
        | Some system ->
            uint64Value system "TotalPhysicalMemory"
            |> Option.map formatBytes
            |> Option.orElse (getMemoryStatus() |> Option.map (fun status -> formatBytes status.ullTotalPhys))
            |> Option.defaultValue unknown
        | None ->
            getMemoryStatus()
            |> Option.map (fun status -> formatBytes status.ullTotalPhys)
            |> Option.defaultValue unknown

    let getGpuInfo () =
        let dxgiGpus =
            getDxgiAdapters()
            |> List.filter (fun gpu -> not gpu.IsSoftware)
            |> List.map (fun gpu -> gpu.Name)
            |> List.distinct

        let gpus =
            if not (List.isEmpty dxgiGpus) then dxgiGpus
            else
                getWmiGpuNames() |> List.distinct

        match gpus with
        | [] -> unknown
        | [ gpu ] -> gpu
        | many -> String.Join(", ", many)

    let getDiskInfo () =
        let disks =
            queryAll "Win32_LogicalDisk" [ "DeviceID"; "DriveType"; "FreeSpace"; "Size" ]
            |> List.choose (fun disk ->
                let driveType = uint64Value disk "DriveType" |> Option.defaultValue 0UL
                if driveType <> 3UL then None
                else
                    let id = stringValue disk "DeviceID" |> Option.defaultValue "Disk"
                    let free = uint64Value disk "FreeSpace"
                    let size = uint64Value disk "Size"
                    match free, size with
                    | Some freeBytes, Some sizeBytes -> Some(sprintf "%s %s free / %s total" id (formatBytes freeBytes) (formatBytes sizeBytes))
                    | _ -> None)

        if not (List.isEmpty disks) then String.Join("; ", disks)
        else
            let fallback =
                fixedDisksFromDriveInfo()
                |> List.map (fun drive -> sprintf "%s %s free / %s total" drive.Name (formatBytes (uint64 drive.AvailableFreeSpace)) (formatBytes (uint64 drive.TotalSize)))
            if List.isEmpty fallback then unknown else String.Join("; ", fallback)

    let getFullReport () =
        {
            OS = getOsInfo()
            Processor = getCpuInfo()
            Memory = getRamInfo()
            GPU = getGpuInfo()
            DiskSpace = getDiskInfo()
            Architecture = RuntimeInformation.OSArchitecture.ToString()
        }

    let getDiagnostics () =
        let info = getFullReport()

        let osItems = [
            add "Operating System" "OS" info.OS
            add "Operating System" "Architecture" info.Architecture
            add "Operating System" ".NET Runtime" RuntimeInformation.FrameworkDescription
        ]

        let hardwareItems = [
            add "Hardware" "CPU" info.Processor
            add "Hardware" "Memory" info.Memory
            add "Hardware" "GPU" info.GPU
        ]

        let wmiDiskItems =
            queryAll "Win32_LogicalDisk" [ "DeviceID"; "VolumeName"; "DriveType"; "FreeSpace"; "Size"; "FileSystem" ]
            |> List.choose (fun disk ->
                let driveType = uint64Value disk "DriveType" |> Option.defaultValue 0UL
                if driveType <> 3UL then None
                else
                    let id = stringValue disk "DeviceID" |> Option.defaultValue "Disk"
                    let name = stringValue disk "VolumeName" |> Option.defaultValue ""
                    let fileSystem = stringValue disk "FileSystem" |> Option.defaultValue unknown
                    let label = if String.IsNullOrWhiteSpace name then id else sprintf "%s (%s)" id name
                    match uint64Value disk "FreeSpace", uint64Value disk "Size" with
                    | Some freeBytes, Some sizeBytes ->
                        Some(add "Storage" label (sprintf "%s free / %s total, %s" (formatBytes freeBytes) (formatBytes sizeBytes) fileSystem))
                    | _ -> Some(add "Storage" label fileSystem))

        let diskItems =
            if not (List.isEmpty wmiDiskItems) then wmiDiskItems
            else
                fixedDisksFromDriveInfo()
                |> List.map (fun drive ->
                    add "Storage" drive.Name (sprintf "%s free / %s total, %s" (formatBytes (uint64 drive.AvailableFreeSpace)) (formatBytes (uint64 drive.TotalSize)) drive.DriveFormat))

        let videoItems =
            let dxgiItems =
                getDxgiAdapters()
                |> List.filter (fun gpu -> not gpu.IsSoftware)
                |> List.map (fun gpu ->
                    let dedicated = formatGpuMemory gpu.DedicatedVideoMemoryBytes
                    let shared = formatGpuMemory gpu.SharedSystemMemoryBytes
                    let driver = gpu.DriverVersion |> Option.defaultValue unknown
                    add "Display" gpu.Name (sprintf "Dedicated VRAM: %s, Shared memory: %s, Driver: %s" dedicated shared driver))

            if not (List.isEmpty dxgiItems) then dxgiItems
            else
                queryAll "Win32_VideoController" [ "Name"; "AdapterRAM"; "DriverVersion" ]
                |> List.mapi (fun index gpu ->
                    let name = stringValue gpu "Name" |> Option.defaultValue (sprintf "GPU %d" (index + 1))
                    let ram =
                        uint64Value gpu "AdapterRAM"
                        |> Option.map formatBytes
                        |> Option.defaultValue unknown
                    let driver = stringValue gpu "DriverVersion" |> Option.defaultValue unknown
                    add "Display" name (sprintf "VRAM: %s, Driver: %s" ram driver))

        [ osItems; hardwareItems; diskItems; videoItems ]
        |> List.concat

    let formatDiagnosticsReport () =
        let lines = ResizeArray<string>()
        lines.Add("PLUMBYR SYSTEM DIAGNOSTICS")
        lines.Add(sprintf "Generated: %s" (DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")))
        lines.Add("")

        getDiagnostics()
        |> List.groupBy (fun item -> item.Section)
        |> List.iter (fun (section, items) ->
            lines.Add(section.ToUpperInvariant())
            for item in items do
                let normalizedName = item.Name.TrimEnd('\\')
                let label =
                    if normalizedName.EndsWith(":", StringComparison.Ordinal) then normalizedName
                    else normalizedName + ":"
                lines.Add(sprintf "  %-18s %s" label item.Value)
            lines.Add(""))

        lines |> Seq.toList

    let getDetailedInfoAsync () =
        async {
            return getFullReport()
        }
