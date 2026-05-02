namespace WindowsCleaner

open System

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

// JSON Structure Types
type JsonTarget = {
    path: string
    subcategory: string
    description: string
    needsAdmin: Nullable<bool>
}

type JsonApp = {
    id: string
    name: string
    paths: string[]
    description: string
    childSubdir: string
}

type ChromiumCacheDirs = {
    cache: string
    codeCache: string
    gpuCache: string
    serviceWorker: string
}

type ChromiumBrowser = {
    key: string
    BaseDir: string
}

type FirefoxConfig = {
    BaseDir: string
    cache: string
}

type FirefoxFork = {
    key: string
    BaseDir: string
    cache: string
}

type RulesFile = {
    [<System.Text.Json.Serialization.JsonPropertyName("type")>]
    RuleType: string
    cleanTargets: JsonTarget[]
    singleFileTargets: JsonTarget[]
    apps: JsonApp[]
    chromiumCacheDirs: ChromiumCacheDirs
    chromium: ChromiumBrowser[]
    firefox: FirefoxConfig
    firefoxForks: FirefoxFork[]
    libraries: string[]
    redistPatterns: string[]
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
