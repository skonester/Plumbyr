namespace WindowsCleaner

open System
open FSharp.Json

type LogLevel =
    | INFO
    | WARNING
    | ERR
    | SUCCESS

type CleanCategory = 
    | SystemTemporary
    | BrowserCache
    | DevelopmentTools
    | GamingCache
    | WindowsLogs
    | ApplicationCache
    | UserTemporary
    | UpdateCache

type CleanStats = {
    mutable BytesFreed: uint64
    mutable FilesDeleted: int
    mutable FoldersDeleted: int
    mutable Errors: int
    mutable Warnings: int
}

type CleanTarget = {
    Name: string
    Path: string
    IsRecursive: bool
    DeleteFolder: bool
    Category: CleanCategory
    EstimatedSize: string
    Description: string
    NeedsAdmin: bool
    SourceFile: string
}

type ScanTargetResult = {
    Target: CleanTarget
    Exists: bool
    EstimatedBytes: uint64
    FileCount: int
    FolderCount: int
    Error: string option
}

// JSON Structure Types
type JsonTarget = {
    path: string option
    subcategory: string option
    description: string option
    needsAdmin: bool option
}

type JsonApp = {
    id: string option
    name: string option
    paths: string[] option
    description: string option
    childSubdir: string option
}

type ChromiumCacheDirs = {
    cache: string option
    codeCache: string option
    gpuCache: string option
    serviceWorker: string option
}

type ChromiumBrowser = {
    key: string option
    [<JsonField("base")>]
    basePath: string option
}

type FirefoxConfig = {
    [<JsonField("base")>]
    basePath: string option
    cache: string option
}

type FirefoxFork = {
    key: string option
    [<JsonField("base")>]
    basePath: string option
    cache: string option
}

type SharedDbFileSets = {
    chromium: string[] option
    firefox: string[] option
}

type DatabaseTarget = {
    label: string option
    basePath: string option
    dbFiles: obj option
    multiProfile: bool option
    profilePattern: string[] option
    description: string option
}

type RulesFile = {
    [<JsonField("type")>]
    ruleType: string option
    cleanTargets: JsonTarget[] option
    singleFileTargets: JsonTarget[] option
    apps: JsonApp[] option
    chromiumCacheDirs: ChromiumCacheDirs option
    chromium: ChromiumBrowser[] option
    firefox: FirefoxConfig option
    firefoxForks: FirefoxFork[] option
    libraries: string[] option
    redistPatterns: string[] option
    sharedDbFileSets: SharedDbFileSets option
    targets: DatabaseTarget[] option
}

type AggregatedStats = {
    TotalBytesFreed: uint64
    TotalFilesDeleted: int
    TotalFoldersDeleted: int
    TotalErrors: int
    TotalWarnings: int
    SessionCount: int
}

type CleaningSession = {
    Timestamp: DateTime
    BytesFreed: uint64
    FilesDeleted: int
    FoldersDeleted: int
    Errors: int
    Warnings: int
}

type SelectionState = {
    SelectedTargets: CleanTarget list
    SelectedCategories: CleanCategory list
    QuickSelectAll: bool
    QuickSelectSystem: bool
    QuickSelectBrowser: bool
    QuickSelectGaming: bool
}
