namespace WindowsCleaner

// Space map ported from Roomy's Core/SpaceScanner.cs and ViewModels/SpaceMapVM.cs.
// MIT License
// Copyright (c) 2026 krezio
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Security
open System.Threading

[<Sealed>]
type SpaceNode(name: string, fullPath: string, parent: SpaceNode option) =
    member _.Name = name
    member _.FullPath = fullPath
    member _.Parent = parent
    member val FileBytes = 0L with get, set
    member val FileCount = 0L with get, set
    member val Size = 0L with get, set
    member val Children = ResizeArray<SpaceNode>() with get

type SpaceFile = { FullPath: string; Size: int64 }

type SpaceScanProgress = {
    Files: int64
    Bytes: int64
    Folders: int64
    SkippedEntries: int64
    SkippedLinks: int64
}

type SpaceScanResult = {
    Root: SpaceNode
    BigFiles: SpaceFile array
    Progress: SpaceScanProgress
}

module SpaceScanner =
    let private isReadError (ex: exn) =
        match ex with
        | :? UnauthorizedAccessException | :? IOException | :? SecurityException -> true
        | _ -> false

    /// Logical file bytes only. Never opens file contents or follows junctions/symlinks.
    /// An iterative walk keeps deeply nested trees off the call stack.
    let scan (path: string) (ct: CancellationToken) (progress: IProgress<SpaceScanProgress>) =
        ct.ThrowIfCancellationRequested()
        let fullPath = Path.GetFullPath(path)
        let rootInfo = DirectoryInfo(fullPath)
        if not rootInfo.Exists then raise (DirectoryNotFoundException("The selected folder or drive is unavailable."))
        if rootInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) then
            raise (IOException("Select the original folder instead of a symbolic link or junction."))

        let root = SpaceNode(rootInfo.Name, fullPath, None)
        let pending = Stack<SpaceNode>()
        let scanned = ResizeArray<SpaceNode>()
        let biggest = PriorityQueue<SpaceFile, int64>()
        let mutable files, bytes, folders, skipped, links = 0L, 0L, 0L, 0L, 0L
        let snapshot () = { Files = files; Bytes = bytes; Folders = folders; SkippedEntries = skipped; SkippedLinks = links }
        let timer = Stopwatch.StartNew()
        let report force =
            if force || timer.ElapsedMilliseconds >= 150L then
                progress.Report(snapshot())
                timer.Restart()

        // Enumerate hidden/system entries too, and count access failures explicitly.
        let options = EnumerationOptions(RecurseSubdirectories = false, IgnoreInaccessible = false,
                                         AttributesToSkip = enum<FileAttributes> 0, ReturnSpecialDirectories = false)
        pending.Push(root)
        report true
        while pending.Count > 0 do
            ct.ThrowIfCancellationRequested()
            let node = pending.Pop()
            scanned.Add(node)
            folders <- folders + 1L
            try
                // A queued directory can have changed since its parent was enumerated.
                if (File.GetAttributes(node.FullPath)).HasFlag(FileAttributes.ReparsePoint) then
                    links <- links + 1L
                else
                    for entry in DirectoryInfo(node.FullPath).EnumerateFileSystemInfos("*", options) do
                        ct.ThrowIfCancellationRequested()
                        try
                            if entry.Attributes.HasFlag(FileAttributes.ReparsePoint) then
                                links <- links + 1L
                            elif entry.Attributes.HasFlag(FileAttributes.Directory) then
                                let child = SpaceNode(entry.Name, entry.FullName, Some node)
                                node.Children.Add(child)
                                pending.Push(child)
                            else
                                let length = (entry :?> FileInfo).Length
                                node.FileBytes <- node.FileBytes + length
                                node.FileCount <- node.FileCount + 1L
                                bytes <- bytes + length
                                files <- files + 1L
                                if biggest.Count < 40 || length > (biggest.Peek()).Size then
                                    biggest.Enqueue({ FullPath = entry.FullName; Size = length }, length)
                                    if biggest.Count > 40 then biggest.Dequeue() |> ignore
                        with ex when isReadError ex -> skipped <- skipped + 1L
                        report false
            with ex when isReadError ex -> skipped <- skipped + 1L
            report false

        // Children appear after their parents in scan order: aggregate bottom up.
        for index = scanned.Count - 1 downto 0 do
            ct.ThrowIfCancellationRequested()
            let node = scanned[index]
            node.Children.Sort(Comparison<SpaceNode>(fun a b ->
                let bySize = compare b.Size a.Size
                if bySize <> 0 then bySize else StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name)))
            node.Size <- node.FileBytes + (node.Children |> Seq.sumBy _.Size)
        report true
        ct.ThrowIfCancellationRequested()
        { Root = root
          BigFiles = biggest.UnorderedItems |> Seq.map (fun struct (file, _) -> file) |> Seq.sortByDescending _.Size |> Seq.toArray
          Progress = snapshot() }
