namespace WindowsCleaner

open System
open System.IO
open System.Reflection
open System.Security.Cryptography

/// Embedded payloads are materialized per build; writable user data stays separate.
module AppFiles =
    let private assembly = typeof<CleanTarget>.Assembly
    let private prefix = "Plumbyr.Payload/"
    let private dataRoot = lazy (
        let root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plumbyr")
        Directory.CreateDirectory(root) |> ignore
        root)
    let dataDirectory () = dataRoot.Value

    let private payloadRoot = lazy (
        let root = Path.Combine(dataDirectory(), "runtime", assembly.ManifestModule.ModuleVersionId.ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        root)

    let private extract (name: string) =
        let relative = name.Substring(prefix.Length)
        let target = Path.GetFullPath(Path.Combine(payloadRoot.Value, relative.Replace('/', Path.DirectorySeparatorChar)))
        if not (target.StartsWith(payloadRoot.Value + string Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) then
            failwith "Invalid bundled resource path."
        use source = assembly.GetManifestResourceStream(name)
        if isNull source then failwithf "Bundled file is missing: %s" relative
        let expectedHash = SHA256.HashData(source)
        let matches () =
            if not (File.Exists(target)) then false
            else
                use existing = File.OpenRead(target)
                SHA256.HashData(existing) = expectedHash
        if not (matches()) then
            Directory.CreateDirectory(Path.GetDirectoryName(target)) |> ignore
            source.Position <- 0L
            let temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp"
            try
                use output = File.Create(temporary)
                source.CopyTo(output)
                output.Dispose()
                File.Move(temporary, target, true)
            finally
                if File.Exists(temporary) then File.Delete(temporary)
        target

    let private resources = lazy (
        let names = assembly.GetManifestResourceNames() |> Array.filter (fun name -> name.StartsWith(prefix, StringComparison.Ordinal))
        if names.Length = 0 then failwith "This build has no bundled application files."
        names |> Array.map (fun name -> name.Substring(prefix.Length), extract name) |> Map.ofArray)

    let get (relative: string) =
        match resources.Value |> Map.tryFind (relative.Replace('\\', '/')) with
        | Some path -> path
        | None -> failwithf "Bundled file is missing: %s" relative

    let rulesDirectory () =
        get "win32rules/system.json" |> Path.GetDirectoryName

    let initialize () = resources.Value |> ignore
