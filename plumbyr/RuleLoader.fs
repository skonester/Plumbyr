namespace WindowsCleaner

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

module RuleLoader =
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
        match ruleType.ToLower() with
        | "system" -> SystemTemporary
        | "browsers" -> BrowserCache
        | "apps" -> ApplicationCache
        | "gaming" -> GamingCache
        | "gpu-cache" -> GamingCache
        | "steam" -> GamingCache
        | "databases" -> ApplicationCache
        | "misc" -> UserTemporary
        | _ -> ApplicationCache

    let getProp (el: JsonElement) (name: string) =
        let found, prop = el.TryGetProperty(name)
        if found then Some prop else None

    let getStr (el: JsonElement) (name: string) (defaultVal: string) =
        match getProp el name with
        | Some p when p.ValueKind = JsonValueKind.String -> p.GetString()
        | _ -> defaultVal

    let loadRulesFromFile (filePath: string) : CleanTarget list =
        try
            let json = File.ReadAllText(filePath)
            use doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            
            let fileName = Path.GetFileName(filePath)
            let ruleType = getStr root "type" "apps"
            let cat = mapCategory ruleType
            let targets = ResizeArray<CleanTarget>()

            // 1. Handle cleanTargets (System/Misc style)
            match getProp root "cleanTargets" with
            | Some ct when ct.ValueKind = JsonValueKind.Array ->
                for t in ct.EnumerateArray() do
                    targets.Add({
                        Name = getStr t "subcategory" "Unknown"
                        Path = expandPath (getStr t "path" "")
                        IsRecursive = true
                        DeleteFolder = true
                        Category = cat
                        EstimatedSize = "Unknown"
                        Description = getStr t "description" ""
                        NeedsAdmin = match getProp t "needsAdmin" with Some p -> p.GetBoolean() | _ -> false
                        SourceFile = fileName
                    })
            | _ -> ()

            // 2. Handle singleFileTargets
            match getProp root "singleFileTargets" with
            | Some sft when sft.ValueKind = JsonValueKind.Array ->
                for t in sft.EnumerateArray() do
                    targets.Add({
                        Name = getStr t "subcategory" "Unknown"
                        Path = expandPath (getStr t "path" "")
                        IsRecursive = false
                        DeleteFolder = false
                        Category = cat
                        EstimatedSize = "File"
                        Description = getStr t "description" ""
                        NeedsAdmin = match getProp t "needsAdmin" with Some p -> p.GetBoolean() | _ -> false
                        SourceFile = fileName
                    })
            | _ -> ()

            // 3. Handle apps (Apps/GPU style)
            match getProp root "apps" with
            | Some apps when apps.ValueKind = JsonValueKind.Array ->
                for a in apps.EnumerateArray() do
                    let name = getStr a "name" "Unknown"
                    let desc = getStr a "description" ""
                    let childSubdir = getStr a "childSubdir" ""
                    
                    match getProp a "paths" with
                    | Some paths when paths.ValueKind = JsonValueKind.Array ->
                        for p in paths.EnumerateArray() do
                            let rawPath = p.GetString()
                            let fullPath = if String.IsNullOrEmpty(childSubdir) then rawPath else Path.Combine(rawPath, childSubdir)
                            targets.Add({
                                Name = name
                                Path = expandPath fullPath
                                IsRecursive = true; DeleteFolder = true; Category = cat; EstimatedSize = "Calculating..."; Description = desc; NeedsAdmin = false
                                SourceFile = fileName
                            })
                    | _ -> ()
            | _ -> ()

            // 4. Handle Browsers (Browsers style)
            if ruleType = "browsers" then
                match getProp root "chromium" with
                | Some chromium when chromium.ValueKind = JsonValueKind.Array ->
                    match getProp root "chromiumCacheDirs" with
                    | Some cacheDirs ->
                        let cachePath = getStr cacheDirs "cache" "Cache/Cache_Data"
                        for b in chromium.EnumerateArray() do
                            let key = getStr b "key" "Unknown"
                            let basePath = getStr b "base" ""
                            targets.Add({
                                Name = sprintf "%s Cache" key
                                Path = expandPath (Path.Combine(basePath, "Default", cachePath))
                                IsRecursive = true; DeleteFolder = true; Category = cat; EstimatedSize = "Browser"; Description = ""; NeedsAdmin = false
                                SourceFile = fileName
                            })
                    | _ -> ()
                | _ -> ()

            List.ofSeq targets
        with ex ->
            printfn "Error loading rules from %s: %s" filePath ex.Message
            []

    let loadAllRules (rulesDir: string) : CleanTarget list =
        if not (Directory.Exists(rulesDir)) then []
        else
            Directory.GetFiles(rulesDir, "*.json")
            |> Array.toList
            |> List.collect loadRulesFromFile
