namespace WindowsCleaner

open System
open System.IO

module CleanTargets =
    let mutable private cachedTargets: CleanTarget list option = None

    let getAllTargets () = 
        match cachedTargets with
        | Some targets -> targets
        | None ->
            // Try different paths to find win32rules
            let paths = [
                "win32rules"
                "../win32rules"
                "../../win32rules"
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "win32rules")
            ]
            
            let rulesDir = paths |> List.tryFind Directory.Exists
            match rulesDir with
            | Some dir ->
                let targets = RuleLoader.loadAllRules dir
                cachedTargets <- Some targets
                targets
            | None -> 
                // Fallback to minimal hardcoded targets if rules not found
                [ { Name = "User TEMP Files"; Path = "%TEMP%"; IsRecursive = true; DeleteFolder = true; Category = SystemTemporary; EstimatedSize = "Unknown"; Description = "Temporary files"; NeedsAdmin = false; SourceFile = "Internal" } ]

    let getCategoryName category =
        match category with
        | SystemTemporary -> "System"
        | BrowserCache -> "Browsers"
        | DevelopmentTools -> "Development"
        | GamingCache -> "Gaming/GPU"
        | WindowsLogs -> "Logs"
        | ApplicationCache -> "Applications"
        | UserTemporary -> "User Data"
        | UpdateCache -> "Updates"
