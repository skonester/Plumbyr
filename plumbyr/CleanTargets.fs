namespace WindowsCleaner

open System
open System.IO

module CleanTargets =
    let mutable private cachedTargets: CleanTarget list option = None

    let getAllTargets () = 
        match cachedTargets with
        | Some targets -> targets
        | None ->
            let targets = RuleLoader.loadAllRules (AppFiles.rulesDirectory())
            cachedTargets <- Some targets
            targets

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
