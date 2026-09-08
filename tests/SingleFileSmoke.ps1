param([string]$Executable = (Join-Path $PSScriptRoot '../dist/win-x64/Plumbyr.exe'))
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$fixture = [IO.Path]::GetFullPath((Join-Path $repoRoot ('.build/smoke-' + [Guid]::NewGuid().ToString('N'))))
$variables = @('PATH', 'LOCALAPPDATA', 'APPDATA', 'DOTNET_ROOT', 'DOTNET_ROOT_X64', 'DOTNET_MULTILEVEL_LOOKUP', 'DOTNET_ROLL_FORWARD')
$previous = @{}
foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$testApp = $null
try {
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    Copy-Item -LiteralPath $Executable -Destination (Join-Path $fixture 'Plumbyr.exe')
    $cache = Join-Path $fixture 'Local/Google/Chrome/User Data/Default/Cache/Cache_Data'
    New-Item -ItemType Directory -Path $cache -Force | Out-Null
    $sample = Join-Path $cache 'sample.bin'
    [IO.File]::WriteAllText($sample, 'abc', [Text.UTF8Encoding]::new($false))
    $env:PATH = ''
    $env:LOCALAPPDATA = Join-Path $fixture 'Local'
    $env:APPDATA = Join-Path $fixture 'Roaming'
    $env:DOTNET_ROOT = Join-Path $fixture 'no-dotnet'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $env:DOTNET_ROLL_FORWARD = 'Disable'
    $reportPath = Join-Path $fixture 'report.json'
    $startInfo = New-Object Diagnostics.ProcessStartInfo
    $startInfo.FileName = Join-Path $fixture 'Plumbyr.exe'
    $startInfo.WorkingDirectory = $fixture
    $startInfo.Arguments = '--analyze-browsers "' + $reportPath + '"'
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($name in $variables) { $startInfo.EnvironmentVariables[$name] = [Environment]::GetEnvironmentVariable($name) }
    $testApp = [Diagnostics.Process]::Start($startInfo)
    if (-not $testApp.WaitForExit(60000)) { throw 'Single-file analysis timed out.' }
    if ($testApp.ExitCode -ne 0) { throw "Single-file app exited with code $($testApp.ExitCode)." }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
    if (@($report.Targets).Count -ne 1 -or $report.Targets[0].Bytes -ne 3 -or $report.Targets[0].Files -ne 1) {
        throw 'Unexpected browser analysis result from the standalone executable.'
    }
    if ([IO.File]::ReadAllText($sample) -ne 'abc') { throw 'Analysis changed the fixture.' }
    $looseRuntime = Get-ChildItem -LiteralPath $fixture -File | Where-Object { $_.Extension -in @('.dll', '.ts', '.mjs') -or $_.Name -eq 'node.exe' }
    if ($looseRuntime) { throw 'The application required runtime files beside the executable.' }
    Write-Host 'PASS: isolated Plumbyr.exe analyzed 1 cache / 1 file / 3 bytes with empty PATH and .NET lookup disabled.'
    Write-Host 'PASS: bundled resources and Node work without shipping sibling files; analysis left the fixture unchanged.'
} finally {
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    if ($null -ne $testApp -and -not $testApp.HasExited) { $testApp.Kill(); $testApp.WaitForExit() }
    # This is a fresh, test-owned directory; verify its absolute boundary before cleanup.
    if ($fixture.StartsWith($repoRoot + '\.build\smoke-', [StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $fixture)) {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
