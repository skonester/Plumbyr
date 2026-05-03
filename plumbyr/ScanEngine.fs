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

    let hasWildcard (path: string) =
        not (String.IsNullOrWhiteSpace path) && (path.Contains("*") || path.Contains("?"))

    let expandWildcardPath (path: string) =
        let normalized = path.Replace("/", "\\")
        if not (hasWildcard normalized) then [ normalized ]
        else
            let parts = normalized.Split([| '\\' |], StringSplitOptions.RemoveEmptyEntries)
            let root =
                let rootPart = Path.GetPathRoot(normalized)
                if String.IsNullOrWhiteSpace(rootPart) then "" else rootPart.TrimEnd('\\')
            let startIndex = if String.IsNullOrWhiteSpace(root) then 0 else 1
            let rec walk bases index =
                if index >= parts.Length then bases
                else
                    let part = parts.[index]
                    let next =
                        bases
                        |> List.collect (fun basePath ->
                            try
                                if part.Contains("*") || part.Contains("?") then
                                    if Directory.Exists(basePath) then
                                        Directory.GetDirectories(basePath, part, SearchOption.TopDirectoryOnly)
                                        |> Array.toList
                                    else []
                                else
                                    [ Path.Combine(basePath, part) ]
                            with _ -> [])
                    walk next (index + 1)
            let initial =
                if String.IsNullOrWhiteSpace(root) then [ "" ] else [ root ]
            walk initial startIndex

    let enumerateFilesSafe dir recursive =
        try
            let option = if recursive then SearchOption.AllDirectories else SearchOption.TopDirectoryOnly
            Directory.EnumerateFiles(dir, "*", option)
            |> Seq.filter (isExcluded >> not)
            |> Seq.toArray
        with _ -> [||]

    let enumerateDirsSafe dir recursive =
        try
            let option = if recursive then SearchOption.AllDirectories else SearchOption.TopDirectoryOnly
            Directory.EnumerateDirectories(dir, "*", option)
            |> Seq.filter (isExcluded >> not)
            |> Seq.toArray
        with _ -> [||]

    let analyzePath (target: CleanTarget) =
        let paths = expandWildcardPath (Environment.ExpandEnvironmentVariables(target.Path))
        let mutable exists = false
        let mutable bytes = 0uL
        let mutable files = 0
        let mutable folders = 0
        let mutable error: string option = None

        for path in paths do
            try
                if Directory.Exists(path) then
                    exists <- true
                    let fileList = enumerateFilesSafe path target.IsRecursive
                    let folderList = enumerateDirsSafe path target.IsRecursive
                    files <- files + fileList.Length
                    folders <- folders + folderList.Length
                    for file in fileList do
                        try bytes <- bytes + uint64 (FileInfo(file).Length) with ex -> error <- Some ex.Message
                elif File.Exists(path) then
                    exists <- true
                    files <- files + 1
                    try bytes <- bytes + uint64 (FileInfo(path).Length) with ex -> error <- Some ex.Message
            with ex ->
                error <- Some ex.Message

        { Target = target; Exists = exists; EstimatedBytes = bytes; FileCount = files; FolderCount = folders; Error = error }

    member this.OnFileProcessed = onFileProcessed.Publish

    member this.DetectApplications() =
        detectedApps.Clear()
        let targets = CleanTargets.getAllTargets()
        Log.Information("Scanning {Count} target definitions...", targets.Length)
        for result in this.AnalyzeTargets(targets) do
            if result.Exists then
                detectedApps.Add(result.Target.Name)
                Log.Information("[{Source}] DETECTED: {Name} ({Bytes} bytes)", result.Target.SourceFile, result.Target.Name, result.EstimatedBytes)
                onFileProcessed.Trigger(sprintf "[%s] DETECTED: %s (%d files, %d bytes)" result.Target.SourceFile result.Target.Name result.FileCount result.EstimatedBytes)
        Log.Information("Analysis finished. Found {Count} items to clean.", detectedApps.Count)

    member this.GetDetectedApps() = List.ofSeq detectedApps

    member this.AnalyzeTargets(targets: CleanTarget list) : ScanTargetResult list =
        targets
        |> List.map analyzePath

    member this.AnalyzeAll() : ScanTargetResult list =
        this.AnalyzeTargets(CleanTargets.getAllTargets())

    member this.CleanAll() =
        this.CleanTargets(CleanTargets.getAllTargets())

    member this.CleanTargets(targets: CleanTarget list) =
        stats <- { BytesFreed = 0uL; FilesDeleted = 0; FoldersDeleted = 0; Errors = 0; Warnings = 0 }
        
        for target in targets do
            let path = Environment.ExpandEnvironmentVariables(target.Path)
            if isExcluded path then
                Log.Warning("SKIP (PROTECTED): {Name}", target.Name)
                onFileProcessed.Trigger(sprintf "SKIP (PROTECTED): %s" target.Name)
            else
                for resolvedPath in expandWildcardPath path do
                    if Directory.Exists(resolvedPath) then
                        Log.Information("[{Source}] PURGING: {Name}", target.SourceFile, target.Name)
                        onFileProcessed.Trigger(sprintf "[%s] PURGING: %s" target.SourceFile target.Name)
                        this.CleanDirectory(resolvedPath, target.IsRecursive)
                        if target.DeleteFolder then
                            try
                                Directory.Delete(resolvedPath, false)
                                stats <- { stats with FoldersDeleted = stats.FoldersDeleted + 1 }
                            with _ -> ()
                    elif File.Exists(resolvedPath) then
                        Log.Information("[{Source}] PURGING: {Name}", target.SourceFile, target.Name)
                        onFileProcessed.Trigger(sprintf "[%s] PURGING: %s" target.SourceFile target.Name)
                        this.CleanFile(resolvedPath)

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
