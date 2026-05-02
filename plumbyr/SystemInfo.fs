namespace WindowsCleaner

open System
open System.Runtime.InteropServices
open System.Diagnostics
open System.IO
open Microsoft.Win32

type SystemInfoData = {
    OS: string
    Processor: string
    Memory: string
    GPU: string
    DiskSpace: string
    Architecture: string
}

module SystemInfo =
    let getBasicInfo () =
        let os = RuntimeInformation.OSDescription
        let arch = RuntimeInformation.OSArchitecture.ToString()
        let proc = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER")
        
        // Get RAM via WMI or Registry if possible, but let's use a simpler way for now
        let mem = "N/A" 
        
        { OS = os; Processor = proc; Memory = mem; GPU = "Scanning..."; DiskSpace = "Calculating..."; Architecture = arch }

    let getDetailedInfoAsync () =
        async {
            let info = getBasicInfo()
            // We can run a command to get more details if needed
            // For now, let's just return what we have and maybe add more later
            return info
        }

    let getGpuInfo () =
        try
            let searcher = new Process()
            searcher.StartInfo.FileName <- "wmic"
            searcher.StartInfo.Arguments <- "path win32_VideoController get name"
            searcher.StartInfo.UseShellExecute <- false
            searcher.StartInfo.RedirectStandardOutput <- true
            searcher.StartInfo.CreateNoWindow <- true
            searcher.Start() |> ignore
            let output = searcher.StandardOutput.ReadToEnd()
            searcher.WaitForExit()
            let lines = output.Split([|'\r'; '\n'|], StringSplitOptions.RemoveEmptyEntries)
            if lines.Length > 1 then lines.[1].Trim() else "Unknown GPU"
        with _ -> "Unknown GPU"

    let getRamInfo () =
        try
            let searcher = new Process()
            searcher.StartInfo.FileName <- "wmic"
            searcher.StartInfo.Arguments <- "ComputerSystem get TotalPhysicalMemory"
            searcher.StartInfo.UseShellExecute <- false
            searcher.StartInfo.RedirectStandardOutput <- true
            searcher.StartInfo.CreateNoWindow <- true
            searcher.Start() |> ignore
            let output = searcher.StandardOutput.ReadToEnd()
            searcher.WaitForExit()
            let lines = output.Split([|'\r'; '\n'|], StringSplitOptions.RemoveEmptyEntries)
            if lines.Length > 1 then 
                let bytes = int64 (lines.[1].Trim())
                sprintf "%.2f GB" (float bytes / 1073741824.0)
            else "Unknown RAM"
        with _ -> "Unknown RAM"

    let getFullReport () =
        let gpu = getGpuInfo()
        let ram = getRamInfo()
        let baseInfo = getBasicInfo()
        { baseInfo with GPU = gpu; Memory = ram }
