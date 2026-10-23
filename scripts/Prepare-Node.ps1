$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repoRoot = Split-Path $PSScriptRoot -Parent
# Hash via .NET rather than Get-FileHash: when this script is launched by powershell.exe
# from a pwsh (PowerShell 7) session, the inherited PSModulePath can hide Get-FileHash.
function Get-Sha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $sha.Dispose() }
}
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'node-runtime.json') -Raw | ConvertFrom-Json
$archiveName = "node-v$($pin.version)-$($pin.architecture)"
$cacheRoot = Join-Path $repoRoot '.build/node'
$runtimeRoot = Join-Path $cacheRoot $pin.architecture
$archive = Join-Path $cacheRoot "$archiveName.zip"
New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    $download = "$archive.download"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "https://nodejs.org/dist/v$($pin.version)/$archiveName.zip" -OutFile $download
        if ((Get-Sha256 $download) -ne $pin.archiveSha256) {
            throw 'Downloaded Node archive does not match the pinned SHA-256 checksum.'
        }
        Move-Item -LiteralPath $download -Destination $archive -Force
    } finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    }
}
if ((Get-Sha256 $archive) -ne $pin.archiveSha256) {
    throw "Cached Node archive has an invalid checksum: $archive"
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($name in @('node.exe', 'LICENSE')) {
        $entry = $zip.GetEntry("$archiveName/$name")
        if ($null -eq $entry) { throw "Node archive is missing $name" }
        $destination = Join-Path $runtimeRoot $name
        # Avoid touching unchanged resources on incremental builds.
        $inputStream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $expected = [BitConverter]::ToString($sha.ComputeHash($inputStream)).Replace('-', '') }
        finally { $inputStream.Dispose(); $sha.Dispose() }
        if ((Test-Path -LiteralPath $destination) -and (Get-Sha256 $destination) -eq $expected) { continue }
        $inputStream = $entry.Open()
        $outputStream = [IO.File]::Create($destination)
        try { $inputStream.CopyTo($outputStream) }
        finally { $outputStream.Dispose(); $inputStream.Dispose() }
    }
} finally { $zip.Dispose() }
if ((Get-Sha256 (Join-Path $runtimeRoot 'node.exe')) -ne $pin.executableSha256) {
    throw 'Extracted Node executable does not match the pinned SHA-256 checksum.'
}
