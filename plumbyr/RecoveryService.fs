namespace WindowsCleaner

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.Text.Json
open System.Threading
open System.Threading.Tasks

type RecoverySource =
    { Path: string; Name: string; Size: int64; Kind: string; Location: string; Mounted: bool; Encrypted: bool }
    override this.ToString() = sprintf "%s  ·  %s  ·  %s" this.Name this.Kind this.Location

type RecoveryFile =
    { Index: int; Name: string; Kind: string; Size: int64; Offset: int64; Category: int; Validation: string; Metadata: string
      mutable Selected: bool; mutable SavedPath: string; mutable Error: string }

type RecoveryEvent = { Generation: int64; Payload: JsonElement }

module RecoveryJson =
    let text (key: string) (value: JsonElement) = value.GetProperty(key).GetString() |> Option.ofObj |> Option.defaultValue ""
    let number (key: string) (value: JsonElement) = value.GetProperty(key).GetInt64()
    let flag (key: string) (value: JsonElement) = value.GetProperty(key).GetBoolean()
    let file (value: JsonElement) =
        let metadata = value.GetProperty("metadata")
        let times =
            if metadata.ValueKind = JsonValueKind.Null then "Original dates and folders are unavailable for carved files."
            else
                [ "created", "Created"; "modified", "Modified"; "accessed", "Accessed"; "recordChanged", "Record changed" ]
                |> List.choose (fun (key, label) ->
                    let item = metadata.GetProperty(key)
                    if item.ValueKind = JsonValueKind.Null then None
                    else
                        let value = try DateTimeOffset.FromUnixTimeSeconds(item.GetInt64()).ToString("u") with :? ArgumentOutOfRangeException -> "Unavailable"
                        Some(label + ": " + value))
                |> String.concat "\n"
        { Index = int (number "index" value); Name = text "name" value; Kind = text "kind" value
          Size = number "size" value; Offset = number "offset" value; Category = int (number "category" value)
          Validation = text "validation" value; Metadata = times; Selected = false; SavedPath = ""; Error = "" }

/// A local, long-lived worker owns the read-only source and recovery candidates.
/// Only result IDs cross back for export; the UI cannot supply raw disk ranges.
type RecoveryService(?workerPath: string) =
    let pending = ConcurrentDictionary<int64, Process * TaskCompletionSource<JsonElement>>()
    let events = Event<RecoveryEvent>()
    let writeLock = obj()
    let mutable sequence = 0L
    let mutable generation = 0L
    let mutable disposed = false
    let mutable worker: Process option = None

    let failPending owner message =
        for KeyValue(id, (processOwner, completion)) in pending do
            if owner |> Option.forall (fun p -> Object.ReferenceEquals(p, processOwner)) then
                match pending.TryRemove(id) with
                | true, _ -> completion.TrySetException(InvalidOperationException(message)) |> ignore
                | _ -> ()

    let start () =
        if disposed then raise (ObjectDisposedException(nameof RecoveryService))
        match worker with
        | Some p when not p.HasExited -> p
        | _ ->
            worker |> Option.iter (fun p ->
                failPending (Some p) "The recovery worker stopped. Return to Sources and scan again."
                p.Dispose())
            worker <- None
            let path = match workerPath with Some path -> path | None -> AppFiles.get "Reclaim/plumbyr-recovery.exe"
            let info = ProcessStartInfo(path,
                                        UseShellExecute = false, CreateNoWindow = true,
                                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true)
            let p = new Process(StartInfo = info, EnableRaisingEvents = true)
            generation <- generation + 1L
            let connectionGeneration = generation
            let sendEvent payload = events.Trigger({ Generation = connectionGeneration; Payload = payload })
            let isCurrent () = worker |> Option.exists (fun current -> Object.ReferenceEquals(current, p))
            p.OutputDataReceived.Add(fun args ->
                if not (isNull args.Data) && isCurrent() then
                    try
                        if args.Data.Length > 4 * 1024 * 1024 then failwith "Recovery response exceeded its limit."
                        use document = JsonDocument.Parse(args.Data)
                        let value = document.RootElement.Clone()
                        match value.TryGetProperty("event") with
                        | true, _ -> sendEvent value
                        | _ ->
                            let id = RecoveryJson.number "id" value
                            match pending.TryRemove(id) with
                            | true, (_, completion) ->
                                match value.TryGetProperty("error") with
                                | true, error -> completion.TrySetException(InvalidOperationException(error.GetString())) |> ignore
                                | _ -> completion.TrySetResult(value.GetProperty("data").Clone()) |> ignore
                            | _ -> ()
                    with ex ->
                        failPending (Some p) ("Recovery response failed: " + ex.Message)
                        try if not p.HasExited then p.Kill(true) with _ -> ())
            // Drain stderr so a worker error cannot block its output pipe.
            p.ErrorDataReceived.Add(fun _ -> ())
            p.Exited.Add(fun _ ->
                failPending (Some p) "The recovery worker stopped. Return to Sources and scan again."
                // Notify only the current connection, without blocking Dispose.
                ThreadPool.QueueUserWorkItem(WaitCallback(fun _ ->
                    lock writeLock (fun () ->
                        if not disposed && isCurrent() then sendEvent (JsonSerializer.SerializeToElement(dict [ "event", "workerStopped" ]))))) |> ignore)
            try
                if not (p.Start()) then failwith "Could not start recovery worker."
                worker <- Some p
                p.BeginOutputReadLine()
                p.BeginErrorReadLine()
                sendEvent (JsonSerializer.SerializeToElement(dict [ "event", "workerStarted" ]))
                p
            with _ -> worker <- None; p.Dispose(); reraise()

    member _.Events = events.Publish
    member _.Generation = Interlocked.Read(&generation)
    member _.IsRunning = lock writeLock (fun () -> not disposed && (worker |> Option.exists (fun p -> not p.HasExited)))

    member _.Request(command: string, fields: (string * obj) list) =
        lock writeLock (fun () ->
            let completion = TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously)
            try
                let p = start()
                let id = Interlocked.Increment(&sequence)
                let data = System.Collections.Generic.Dictionary<string, obj>()
                data["id"] <- id
                data["cmd"] <- command
                for key, value in fields do data[key] <- value
                pending[id] <- (p, completion)
                try
                    p.StandardInput.WriteLine(JsonSerializer.Serialize(data))
                    p.StandardInput.Flush()
                    if p.HasExited then failPending (Some p) "The recovery worker stopped. Return to Sources and scan again."
                with ex ->
                    pending.TryRemove(id) |> ignore
                    completion.TrySetException(ex) |> ignore
            with ex -> completion.TrySetException(ex) |> ignore
            completion.Task)

    member this.DiscoverAsync() = task {
        let! data = this.Request("discover", [])
        return data.EnumerateArray() |> Seq.map (fun d ->
            { Path = RecoveryJson.text "path" d; Name = RecoveryJson.text "name" d; Size = RecoveryJson.number "size" d
              Kind = RecoveryJson.text "kind" d; Location = RecoveryJson.text "location" d
              Mounted = RecoveryJson.flag "mounted" d; Encrypted = RecoveryJson.flag "encrypted" d }) |> Seq.toArray
    }

    interface IDisposable with
        member _.Dispose() =
            lock writeLock (fun () ->
                if not disposed then
                    disposed <- true
                    match worker with
                    | Some p ->
                        try if not p.HasExited then p.Kill(true) with _ -> ()
                        p.Dispose()
                    | None -> ()
                    failPending None "Recovery closed.")
