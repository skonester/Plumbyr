namespace WindowsCleaner

open System
open System.IO
open FSharp.Json

module RuleLoader =
    let private jsonConfig =
        JsonConfig.create(deserializeOption = DeserializeOption.AllowOmit, allowUntyped = true)

    let private valueOr defaultValue value =
        defaultArg value defaultValue

    let private nonEmpty value =
        match value with
        | Some text when not (String.IsNullOrWhiteSpace text) -> Some text
        | _ -> None

    let expandPath (path: string) =
        if String.IsNullOrEmpty(path) then ""
        else
            let mutable result = path.Replace("/", "\\")
            let vars = [
                "${LOCALAPPDATA}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                "${APPDATA}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
                "${WINDIR}", Environment.GetFolderPath(Environment.SpecialFolder.Windows)
                "${PROGRAMDATA}", Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
                "${PROGRAMFILES}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                "${PROGRAMFILES_X86}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                "${HOME}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                "${USERPROFILE}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            ]
            for (var, value) in vars do
                result <- result.Replace(var, value)
            result

    let mapCategory (ruleType: string) =
        match ruleType.ToLowerInvariant() with
        | "system" -> SystemTemporary
        | "browsers" -> BrowserCache
        | "apps" -> ApplicationCache
        | "gaming" -> GamingCache
        | "gpu-cache" -> GamingCache
        | "steam" -> GamingCache
        | "databases" -> ApplicationCache
        | "misc" -> UserTemporary
        | _ -> ApplicationCache

    let private targetFromJsonTarget fileName cat recursive deleteFolder estimatedSize (target: JsonTarget) =
        match nonEmpty target.path with
        | Some path ->
            Some {
                Name = valueOr "Unknown" target.subcategory
                Path = expandPath path
                IsRecursive = recursive
                DeleteFolder = deleteFolder
                Category = cat
                EstimatedSize = estimatedSize
                Description = valueOr "" target.description
                NeedsAdmin = valueOr false target.needsAdmin
                SourceFile = fileName
            }
        | None -> None

    let private appTargets fileName cat (app: JsonApp) =
        let name = valueOr "Unknown" app.name
        let desc = valueOr "" app.description
        let childSubdir = valueOr "" app.childSubdir

        app.paths
        |> Option.defaultValue [||]
        |> Array.choose (fun rawPath ->
            if String.IsNullOrWhiteSpace rawPath then None
            else
                let fullPath =
                    if String.IsNullOrWhiteSpace childSubdir then rawPath
                    else Path.Combine(rawPath, childSubdir)

                Some {
                    Name = name
                    Path = expandPath fullPath
                    IsRecursive = true
                    DeleteFolder = true
                    Category = cat
                    EstimatedSize = "Calculating..."
                    Description = desc
                    NeedsAdmin = false
                    SourceFile = fileName
                })
        |> Array.toList

    let private browserTargets fileName cat (rules: RulesFile) =
        let targets = ResizeArray<CleanTarget>()

        let cacheDirs = valueOr { cache = None; codeCache = None; gpuCache = None; serviceWorker = None } rules.chromiumCacheDirs
        let cachePaths =
            [|
                valueOr "Cache/Cache_Data" cacheDirs.cache
                valueOr "Code Cache" cacheDirs.codeCache
                valueOr "GPUCache" cacheDirs.gpuCache
                valueOr "Service Worker/CacheStorage" cacheDirs.serviceWorker
            |]
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.distinct

        for browser in Option.defaultValue [||] rules.chromium do
            let key = valueOr "Unknown" browser.key
            match nonEmpty browser.basePath with
            | Some basePath ->
                for cachePath in cachePaths do
                    targets.Add({
                        Name = sprintf "%s Cache" key
                        Path = expandPath (Path.Combine(basePath, "Default", cachePath))
                        IsRecursive = true
                        DeleteFolder = true
                        Category = cat
                        EstimatedSize = "Browser"
                        Description = "Browser cache, code cache, GPU cache, and service worker storage"
                        NeedsAdmin = false
                        SourceFile = fileName
                    })
            | None -> ()

        let addFirefoxTargets browserName cacheBase =
            let expandedBase = expandPath cacheBase
            if Directory.Exists(expandedBase) then
                for profile in Directory.GetDirectories(expandedBase) do
                    targets.Add({
                        Name = sprintf "%s Cache" browserName
                        Path = Path.Combine(profile, "cache2")
                        IsRecursive = true
                        DeleteFolder = true
                        Category = cat
                        EstimatedSize = "Browser"
                        Description = "Firefox-family profile cache"
                        NeedsAdmin = false
                        SourceFile = fileName
                    })
            else
                targets.Add({
                    Name = sprintf "%s Cache" browserName
                    Path = Path.Combine(expandedBase, "*", "cache2")
                    IsRecursive = true
                    DeleteFolder = true
                    Category = cat
                    EstimatedSize = "Browser"
                    Description = "Firefox-family profile cache"
                    NeedsAdmin = false
                    SourceFile = fileName
                })

        match rules.firefox |> Option.bind (fun firefox -> nonEmpty firefox.cache) with
        | Some cacheBase -> addFirefoxTargets "Firefox" cacheBase
        | None -> ()

        for fork in Option.defaultValue [||] rules.firefoxForks do
            match nonEmpty fork.cache with
            | Some cacheBase -> addFirefoxTargets (valueOr "Firefox fork" fork.key) cacheBase
            | None -> ()

        List.ofSeq targets

    let private steamTargets fileName cat (rules: RulesFile) =
        let redistPatterns =
            rules.redistPatterns
            |> Option.defaultValue [||]
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.distinct

        rules.libraries
        |> Option.defaultValue [||]
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> Array.collect (fun library ->
            let libraryPath = expandPath library
            redistPatterns
            |> Array.map (fun pattern ->
                {
                    Name = "Steam Redistributables"
                    Path = Path.Combine(libraryPath, "steamapps", "common", "*", pattern)
                    IsRecursive = true
                    DeleteFolder = true
                    Category = cat
                    EstimatedSize = "Steam"
                    Description = "Per-game redistributable installers and setup folders; games keep their main files"
                    NeedsAdmin = false
                    SourceFile = fileName
                }))
        |> Array.toList

    let private databaseTargets fileName cat (rules: RulesFile) =
        let sharedSets = valueOr { chromium = None; firefox = None } rules.sharedDbFileSets

        let resolveDbFiles (dbFiles: obj option) =
            match dbFiles with
            | Some (:? string as aliasOrFile) when aliasOrFile.Equals("$chromium", StringComparison.OrdinalIgnoreCase) ->
                valueOr [||] sharedSets.chromium
            | Some (:? string as aliasOrFile) when aliasOrFile.Equals("$firefox", StringComparison.OrdinalIgnoreCase) ->
                valueOr [||] sharedSets.firefox
            | Some (:? string as fileName) ->
                [| fileName |]
            | Some (:? seq<obj> as files) ->
                files
                |> Seq.choose (fun file ->
                    match file with
                    | :? string as text when not (String.IsNullOrWhiteSpace text) -> Some text
                    | _ -> None)
                |> Seq.toArray
            | _ -> [||]

        rules.targets
        |> Option.defaultValue [||]
        |> Array.collect (fun target ->
            match nonEmpty target.basePath with
            | None -> [||]
            | Some basePath ->
                let expandedBase = expandPath basePath
                let profileSegment =
                    if valueOr false target.multiProfile then "*" else ""

                resolveDbFiles target.dbFiles
                |> Array.map (fun dbFile ->
                    let path =
                        if String.IsNullOrWhiteSpace profileSegment then
                            Path.Combine(expandedBase, dbFile)
                        else
                            Path.Combine(expandedBase, profileSegment, dbFile)

                    {
                        Name = sprintf "%s Databases" (valueOr "Unknown" target.label)
                        Path = path
                        IsRecursive = false
                        DeleteFolder = false
                        Category = cat
                        EstimatedSize = "Database"
                        Description = valueOr "" target.description
                        NeedsAdmin = false
                        SourceFile = fileName
                    }))
        |> Array.toList

    let loadRulesFromFile (filePath: string) : CleanTarget list =
        try
            let json = File.ReadAllText(filePath)
            let rules = Json.deserializeEx<RulesFile> jsonConfig json

            let fileName = Path.GetFileName(filePath)
            let ruleType = valueOr "apps" rules.ruleType
            let cat = mapCategory ruleType

            let cleanTargets =
                rules.cleanTargets
                |> Option.defaultValue [||]
                |> Array.choose (targetFromJsonTarget fileName cat true true "Unknown")
                |> Array.toList

            let singleFileTargets =
                rules.singleFileTargets
                |> Option.defaultValue [||]
                |> Array.choose (targetFromJsonTarget fileName cat false false "File")
                |> Array.toList

            let appRuleTargets =
                rules.apps
                |> Option.defaultValue [||]
                |> Array.toList
                |> List.collect (appTargets fileName cat)

            let browserRuleTargets =
                if ruleType.Equals("browsers", StringComparison.OrdinalIgnoreCase) then
                    browserTargets fileName cat rules
                else []

            let steamRuleTargets =
                if ruleType.Equals("steam", StringComparison.OrdinalIgnoreCase) then
                    steamTargets fileName cat rules
                else []

            let databaseRuleTargets =
                if ruleType.Equals("databases", StringComparison.OrdinalIgnoreCase) then
                    databaseTargets fileName cat rules
                else []

            [
                cleanTargets
                singleFileTargets
                appRuleTargets
                browserRuleTargets
                steamRuleTargets
                databaseRuleTargets
            ]
            |> List.concat
        with ex ->
            printfn "Error loading rules from %s: %s" filePath ex.Message
            []

    let loadAllRules (rulesDir: string) : CleanTarget list =
        if not (Directory.Exists(rulesDir)) then []
        else
            Directory.GetFiles(rulesDir, "*.json")
            |> Array.toList
            |> List.collect loadRulesFromFile
            |> List.distinctBy (fun t ->
                let normalizedPath =
                    if String.IsNullOrWhiteSpace(t.Path) then ""
                    else t.Path.Trim().TrimEnd('\\').ToLowerInvariant()
                (t.Name.Trim().ToLowerInvariant(), normalizedPath, t.Category))
