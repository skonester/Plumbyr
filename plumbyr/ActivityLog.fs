namespace WindowsCleaner

open System
open System.IO

type ActivityLog() =
    let mutable logStream: StreamWriter = null
    static let instance = ActivityLog()
    static member GetInstance() = instance

    member this.Initialize(path: string) =
        try
            logStream <- new StreamWriter(path, true)
            logStream.AutoFlush <- true
        with e ->
            printfn "Failed to initialize log: %s" e.Message

    member this.Log(level: LogLevel, message: string) =
        let timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        let levelStr = sprintf "%A" level
        let logLine = sprintf "[%s] [%s] %s" timestamp levelStr message
        if logStream <> null then
            try logStream.WriteLine(logLine) with _ -> ()

    member this.LogToConsole(level: LogLevel, message: string) =
        this.Log(level, message)
        let color = 
            match level with
            | INFO -> ConsoleColor.Cyan
            | WARNING -> ConsoleColor.Yellow
            | ERR -> ConsoleColor.Red
            | SUCCESS -> ConsoleColor.Green
        
        let oldColor = Console.ForegroundColor
        Console.ForegroundColor <- color
        printfn "[%A] %s" level message
        Console.ForegroundColor <- oldColor

    member this.Close() =
        if logStream <> null then
            logStream.Close()
            logStream <- null
