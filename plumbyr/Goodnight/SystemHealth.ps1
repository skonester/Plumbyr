# Adapted from System Health.bat: DISM CheckHealth, ScanHealth, RestoreHealth, SFC.
[CmdletBinding()]
param(
    [ValidateSet('Check', 'Repair')][string]$Mode = 'Check',
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'Plumbyr\reports\sleep'),
    [string]$StatusPath,
    [switch]$ListSteps
)
$ErrorActionPreference = 'Stop'
$steps = @(
    @{ Tool = 'DISM.exe'; Arguments = @('/Online','/Cleanup-Image','/CheckHealth'); Label = 'Checking Windows component health' },
    @{ Tool = 'DISM.exe'; Arguments = @('/Online','/Cleanup-Image','/ScanHealth'); Label = 'Scanning Windows component store' }
)
if ($Mode -eq 'Repair') {
    $steps += @{ Tool = 'DISM.exe'; Arguments = @('/Online','/Cleanup-Image','/RestoreHealth'); Label = 'Repairing Windows component store' }
    $steps += @{ Tool = 'sfc.exe'; Arguments = @('/scannow'); Label = 'Checking and repairing system files' }
} else {
    $steps += @{ Tool = 'sfc.exe'; Arguments = @('/verifyonly'); Label = 'Verifying system files without repairs' }
}
# Allows integration tests to inspect exact commands without invoking Windows tools.
if ($ListSteps) {
    $steps | ForEach-Object { $_.Tool + ' ' + ($_.Arguments -join ' ') }
    exit 0
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'System health tools require Administrator privileges.'
    exit 1
}
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path
$report = Join-Path $OutDir ('SystemHealth-' + $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '.txt')
$failed = $false
$restart = $false
function Write-ProgressStatus([string]$Text) {
    Write-Output $Text
    if ($StatusPath) { Set-Content -LiteralPath $StatusPath -Value $Text -Encoding Ascii }
}
Start-Transcript -LiteralPath $report | Out-Null
try {
    Write-Output ('System Health - ' + $Mode)
    Write-Output ('Started: ' + (Get-Date))
    for ($index = 0; $index -lt $steps.Count; $index++) {
        $step = $steps[$index]
        Write-ProgressStatus ('Step ' + ($index + 1) + '/' + $steps.Count + ': ' + $step.Label + '...')
        $tool = Join-Path $env:SystemRoot ('System32\' + $step.Tool)
        $arguments = $step.Arguments
        Write-Output ($step.Tool + ' ' + ($arguments -join ' '))
        & $tool @arguments 2>&1 | Out-Host
        $result = $LASTEXITCODE
        Write-Output ('Exit code: ' + $result)
        if ($result -eq 3010) { $restart = $true }
        elseif ($result -ne 0) { $failed = $true }
    }
    Write-ProgressStatus 'Finished. Review the report for findings and repair results.'
    Write-Output ('Report: ' + $report)
    Write-Output 'An exit code alone does not establish that Windows is healthy; review the tool findings above.'
} catch {
    $failed = $true
    Write-ProgressStatus ('Failed: ' + $_.Exception.Message)
} finally {
    Stop-Transcript | Out-Null
}
if ($failed) { exit 1 }
if ($restart) { exit 3010 }
exit 0

