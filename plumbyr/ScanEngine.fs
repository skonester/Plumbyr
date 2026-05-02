namespace WindowsCleaner

open System
open System.IO
open System.Collections.Generic
open Serilog

type ScanEngine() =
    let mutable detectedApps = List<string>()
    let mutable stats = { BytesFreed = 0uL; FilesDeleted = 0; FoldersDeleted = 0; Errors = 0; Warnings = 0 }
    
    let onFileProcessed = Event<string>()
    let isExcluded (path: string) =
        let exclusions = [ "waasmedic"; "Windows Defender" ]
        exclusions |> List.exists (fun ex -> path.Contains(ex, StringComparison.OrdinalIgnoreCase))

    member this.OnFileProcessed = onFileProcessed.Publish

    member this.DetectApplications() =
        detectedApps.Clear()
        let targets = CleanTargets.getAllTargets()
        Log.Information("Scanning {Count} target definitions...", targets.Length)
        for target in targets do
            let path = Environment.ExpandEnvironmentVariables(target.Path)
            if Directory.Exists(path) || File.Exists(path) then
                detectedApps.Add(target.Name)
                // Report finding to CLI
                Log.Information("[{Source}] DETECTED: {Name}", target.SourceFile, target.Name)
                onFileProcessed.Trigger(sprintf "[%s] DETECTED: %s" target.SourceFile target.Name)
        Log.Information("Analysis finished. Found {Count} items to clean.", detectedApps.Count)

    member this.GetDetectedApps() = List.ofSeq detectedApps

    member this.CleanAll() =
        stats <- { BytesFreed = 0uL; FilesDeleted = 0; FoldersDeleted = 0; Errors = 0; Warnings = 0 }
        let targets = CleanTargets.getAllTargets()
        
        for target in targets do
            let path = Environment.ExpandEnvironmentVariables(target.Path)
            if isExcluded path then
                Log.Warning("SKIP (PROTECTED): {Name}", target.Name)
                onFileProcessed.Trigger(sprintf "SKIP (PROTECTED): %s" target.Name)
            elif Directory.Exists(path) then
                Log.Information("[{Source}] PURGING: {Name}", target.SourceFile, target.Name)
                onFileProcessed.Trigger(sprintf "[%s] PURGING: %s" target.SourceFile target.Name)
                this.CleanDirectory(path, target.IsRecursive)
            elif File.Exists(path) then
                Log.Information("[{Source}] PURGING: {Name}", target.SourceFile, target.Name)
                onFileProcessed.Trigger(sprintf "[%s] PURGING: %s" target.SourceFile target.Name)
                this.CleanFile(path)

    member private this.CleanDirectory(dirPath: string, recursive: bool) =
        try
            let allFiles = 
                if recursive then Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories) 
                else Directory.GetFiles(dirPath)
            
            let files = allFiles |> Array.filter (fun f -> not (isExcluded f))
            
            for file in files do
                this.CleanFile(file)
                
            if recursive then
                let dirs = Directory.GetDirectories(dirPath, "*", SearchOption.AllDirectories)
                for dir in Array.rev dirs do
                    if not (isExcluded dir) then
                        try
                            Directory.Delete(dir)
                            stats <- { stats with FoldersDeleted = stats.FoldersDeleted + 1 }
                        with _ -> ()
        with ex ->
            stats <- { stats with Errors = stats.Errors + 1 }

    member private this.CleanFile(filePath: string) =
        try
            let fi = FileInfo(filePath)
            let size = uint64 fi.Length
            
            Log.Debug("DELETE: {Path}", filePath)
            onFileProcessed.Trigger(sprintf "PURGING: %s" filePath)
            
            File.Delete(filePath)
            stats <- { stats with 
                        BytesFreed = stats.BytesFreed + size
                        FilesDeleted = stats.FilesDeleted + 1 }
        with ex ->
            Log.Warning("SKIP (LOCKED): {File}", Path.GetFileName(filePath))
            onFileProcessed.Trigger(sprintf "SKIP (LOCKED): %s" (Path.GetFileName(filePath)))
            stats <- { stats with Warnings = stats.Warnings + 1 }

    member this.GetStats() = stats

    member this.SaveStatsToDatabase() =
        let history = CleaningHistory.GetInstance()
        let session = {
            Timestamp = DateTime.Now
            BytesFreed = stats.BytesFreed
            FilesDeleted = stats.FilesDeleted
            FoldersDeleted = stats.FoldersDeleted
            Errors = stats.Errors
            Warnings = stats.Warnings
        }
        history.SaveSession(session)
