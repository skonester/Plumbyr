$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
dotnet publish (Join-Path $repoRoot 'plumbyr/Plumbyr.fsproj') -c Release -p:PublishProfile=SingleFile
if ($LASTEXITCODE -ne 0) { throw 'Plumbyr publish failed.' }
$publishDir = Join-Path $repoRoot 'dist/win-x64'
$files = @(Get-ChildItem -LiteralPath $publishDir -Force)
if ($files.Count -ne 1 -or $files[0].Name -ne 'Plumbyr.exe') {
    throw "Expected only Plumbyr.exe in $publishDir. Remove any stale output and retry."
}
Write-Host ("Ready: {0} ({1:N1} MB)" -f $files[0].FullName, ($files[0].Length / 1MB))
