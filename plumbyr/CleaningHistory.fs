namespace WindowsCleaner

open System
open System.IO
open System.Collections.Generic

type CleaningHistory() =
    let mutable databasePath = ""
    let mutable sessions = List<CleaningSession>()
    
    static let instance = CleaningHistory()
    static member GetInstance() = instance

    member this.Initialize(path: string) =
        databasePath <- path
        this.LoadDatabase()

    member private this.LoadDatabase() =
        sessions.Clear()
        if File.Exists(databasePath) then
            try
                let lines = File.ReadAllLines(databasePath)
                for line in lines do
                    if not (String.IsNullOrWhiteSpace(line)) then
                        let parts = line.Split('|')
                        if parts.Length = 6 then
                            let session = {
                                Timestamp = DateTime.FromBinary(int64 parts.[0])
                                BytesFreed = uint64 parts.[1]
                                FilesDeleted = int parts.[2]
                                FoldersDeleted = int parts.[3]
                                Errors = int parts.[4]
                                Warnings = int parts.[5]
                            }
                            sessions.Add(session)
            with _ -> ()

    member private this.SaveDatabase() =
        try
            let lines = 
                sessions 
                |> Seq.map (fun s -> 
                    sprintf "%d|%u|%d|%d|%d|%d" 
                        (s.Timestamp.ToBinary()) 
                        s.BytesFreed 
                        s.FilesDeleted 
                        s.FoldersDeleted 
                        s.Errors 
                        s.Warnings)
            File.WriteAllLines(databasePath, lines)
        with _ -> ()

    member this.SaveSession(session: CleaningSession) =
        sessions.Add(session)
        this.SaveDatabase()

    member private this.AggregateSessions(startTime: DateTime, endTime: DateTime) =
        let filtered = sessions |> Seq.filter (fun s -> s.Timestamp >= startTime && s.Timestamp <= endTime)
        {
            TotalBytesFreed = filtered |> Seq.sumBy (fun s -> s.BytesFreed)
            TotalFilesDeleted = filtered |> Seq.sumBy (fun s -> s.FilesDeleted)
            TotalFoldersDeleted = filtered |> Seq.sumBy (fun s -> s.FoldersDeleted)
            TotalErrors = filtered |> Seq.sumBy (fun s -> s.Errors)
            TotalWarnings = filtered |> Seq.sumBy (fun s -> s.Warnings)
            SessionCount = filtered |> Seq.length
        }

    member this.GetStats24Hours() = this.AggregateSessions(DateTime.Now.AddDays(-1.0), DateTime.Now)
    member this.GetStats7Days() = this.AggregateSessions(DateTime.Now.AddDays(-7.0), DateTime.Now)
    member this.GetStats31Days() = this.AggregateSessions(DateTime.Now.AddDays(-31.0), DateTime.Now)
    member this.GetStatsOverall() = this.AggregateSessions(DateTime.MinValue, DateTime.MaxValue)

    member this.GetRecentSessions(count: int) =
        sessions 
        |> Seq.sortByDescending (fun s -> s.Timestamp)
        |> Seq.truncate count
        |> Seq.toList
