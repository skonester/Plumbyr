namespace WindowsCleaner

open System
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type BrowserCacheUsage = {
    Browser: string
    Label: string
    Path: string
    Bytes: int64
    Files: int64
    Skipped: int64
}

type BrowserAnalysis = {
    Targets: BrowserCacheUsage list
    Warnings: string list
}

module KuduBridge =
    type Settings = {
        WorkerPath: string
        NodeExecutable: string
        Timeout: TimeSpan
    }

    let defaultSettings () = {
        WorkerPath = AppFiles.get "Kudu/worker.mjs"
        NodeExecutable = AppFiles.get "Kudu/node.exe"
        Timeout = TimeSpan.FromMinutes(2.0)
    }

    let private readBounded (reader: StreamReader) limit (token: CancellationToken) = task {
        let buffer = Array.zeroCreate<char> 4096
        let text = StringBuilder()
        let mutable finished = false
        while not finished do
            let! count = reader.ReadAsync(buffer.AsMemory(), token)
            if count = 0 then finished <- true
            elif text.Length + count > limit then
                // Cancel the worker immediately instead of leaving a full pipe blocked.
                failwith "Browser analysis output exceeded its size limit."
            else text.Append(buffer, 0, count) |> ignore
        return text.ToString()
    }

    let private parseResponse exitCode (output: string) (errors: string) =
        if String.IsNullOrWhiteSpace(output) then
            failwithf "Browser analysis did not return a result. The bundled worker may be damaged. %s" errors
        use document = JsonDocument.Parse(output)
        let root = document.RootElement
        if root.GetProperty("version").GetInt32() <> 1 then failwith "Unsupported browser analysis response version."
        if not (root.GetProperty("ok").GetBoolean()) then failwith (root.GetProperty("error").GetString())
        if exitCode <> 0 then failwithf "Browser analysis exited with code %d. %s" exitCode errors
        let getText (item: JsonElement) (name: string) =
            let value = item.GetProperty(name).GetString()
            if String.IsNullOrWhiteSpace(value) then failwith "Browser analysis returned an empty field."
            value
        let getCount (item: JsonElement) (name: string) =
            let value = item.GetProperty(name).GetInt64()
            if value < 0L then failwith "Browser analysis returned a negative count."
            value
        {
            Targets = [
                for item in root.GetProperty("targets").EnumerateArray() do
                    let path = getText item "path"
                    if not (Path.IsPathFullyQualified(path)) then failwith "Browser analysis returned a relative path."
                    yield {
                        Browser = getText item "browser"
                        Label = getText item "label"
                        Path = path
                        Bytes = getCount item "bytes"
                        Files = getCount item "files"
                        Skipped = getCount item "skipped"
                    }
            ]
            Warnings = [ for warning in root.GetProperty("warnings").EnumerateArray() -> warning.GetString() ]
        }

    let analyzeWith settings (cancellationToken: CancellationToken) = task {
        cancellationToken.ThrowIfCancellationRequested()
        if not (File.Exists(settings.WorkerPath)) then
            failwith "Browser analysis files are missing. Rebuild or reinstall Plumbyr."
        let startInfo = ProcessStartInfo(settings.NodeExecutable)
        startInfo.ArgumentList.Add(settings.WorkerPath)
        startInfo.UseShellExecute <- false
        startInfo.CreateNoWindow <- true
        startInfo.RedirectStandardInput <- true
        startInfo.RedirectStandardOutput <- true
        startInfo.RedirectStandardError <- true
        startInfo.StandardInputEncoding <- UTF8Encoding(false)
        startInfo.StandardOutputEncoding <- Encoding.UTF8
        startInfo.StandardErrorEncoding <- Encoding.UTF8
        // A local Node worker should not inherit arbitrary runtime injection options.
        startInfo.Environment.Remove("NODE_OPTIONS") |> ignore
        use worker = new Process(StartInfo = startInfo)
        try
            if not (worker.Start()) then failwith "Could not start browser analysis."
        with :? Win32Exception ->
            failwith "The bundled Node runtime could not be started. Rebuild or reinstall Plumbyr."
        use deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
        deadline.CancelAfter(settings.Timeout)
        let stopWorker () =
            try
                if not worker.HasExited then worker.Kill(true)
            with :? InvalidOperationException -> ()
        use registration = deadline.Token.Register(fun () -> stopWorker())
        // Any read failure cancels the worker, preventing pipe deadlocks on oversized output.
        let mutable readFailure: exn = null
        let read reader limit = task {
            try return! readBounded reader limit deadline.Token
            with ex ->
                if not (ex :? OperationCanceledException) then
                    Interlocked.CompareExchange(&readFailure, ex, null) |> ignore
                deadline.Cancel()
                return raise ex
        }
        let output = read worker.StandardOutput (4 * 1024 * 1024)
        let errors = read worker.StandardError (64 * 1024)
        try
            try
                do! worker.StandardInput.WriteAsync("{\"version\":1,\"method\":\"analyze-browsers\"}".AsMemory(), deadline.Token)
                worker.StandardInput.Close()
                do! worker.WaitForExitAsync(deadline.Token)
                let! streams = Task.WhenAll(output, errors)
                cancellationToken.ThrowIfCancellationRequested()
                return parseResponse worker.ExitCode streams[0] streams[1]
            with ex ->
                // Prefer the actual output-limit failure over the resulting cancellation.
                if not (isNull readFailure) then return raise readFailure
                elif errors.IsFaulted then return raise (errors.Exception.GetBaseException())
                elif cancellationToken.IsCancellationRequested then
                    return raise (OperationCanceledException(cancellationToken))
                elif deadline.IsCancellationRequested then
                    return raise (TimeoutException("Browser analysis timed out. Try again after closing busy browsers."))
                else return raise ex
        finally
            stopWorker()
    }

    let analyze cancellationToken = analyzeWith (defaultSettings()) cancellationToken
