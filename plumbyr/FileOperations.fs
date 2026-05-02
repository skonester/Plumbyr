namespace WindowsCleaner

open System
open System.IO

module FileOperations =
    let directoryExists path = 
        try Directory.Exists(path) with _ -> false

    let fileExists path = 
        try File.Exists(path) with _ -> false

    let getEnvVar name = 
        Environment.GetEnvironmentVariable(name) |> Option.ofObj |> Option.defaultValue ""

    let expandPath (path: string) =
        Environment.ExpandEnvironmentVariables(path)

    let getDirectorySize (path: string) =
        let mutable size = 0uL
        try
            if directoryExists path then
                let di = DirectoryInfo(path)
                for fi in di.EnumerateFiles("*", SearchOption.AllDirectories) do
                    try size <- size + uint64 fi.Length with _ -> ()
            size
        with _ -> size

    let countFiles (path: string) recursive =
        try
            if directoryExists path then
                let option = if recursive then SearchOption.AllDirectories else SearchOption.TopDirectoryOnly
                Directory.GetFiles(path, "*", option).Length
            else 0
        with _ -> 0

    let countDirectories (path: string) recursive =
        try
            if directoryExists path then
                let option = if recursive then SearchOption.AllDirectories else SearchOption.TopDirectoryOnly
                Directory.GetDirectories(path, "*", option).Length
            else 0
        with _ -> 0

    let deleteDirectory path recursive =
        try
            if directoryExists path then
                Directory.Delete(path, recursive)
            true
        with _ -> false

    let removeFile path =
        try
            if fileExists path then
                File.Delete(path)
            true
        with _ -> false

    let createDirectoryRecursive path =
        try
            Directory.CreateDirectory(path) |> ignore
            true
        with _ -> false
