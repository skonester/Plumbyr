param([string]$ScriptsDirectory = (Join-Path $PSScriptRoot '../plumbyr/Goodnight'))
$ErrorActionPreference = 'Stop'
$scripts = [IO.Path]::GetFullPath($ScriptsDirectory)
foreach ($name in @('SleepDebug.ps1', 'SystemHealth.ps1', 'SleepRecovery.ps1')) {
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $scripts $name), [ref]$null, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
}
$powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
$check = @(& $powershell -NoProfile -NonInteractive -File (Join-Path $scripts 'SystemHealth.ps1') -Mode Check -ListSteps)
if ($LASTEXITCODE -ne 0 -or ($check -join '|') -ne 'DISM.exe /Online /Cleanup-Image /CheckHealth|DISM.exe /Online /Cleanup-Image /ScanHealth|sfc.exe /verifyonly') {
    throw 'Check mode must only inspect Windows health.'
}
$repair = @(& $powershell -NoProfile -NonInteractive -File (Join-Path $scripts 'SystemHealth.ps1') -Mode Repair -ListSteps)
if ($LASTEXITCODE -ne 0 -or ($repair -join '|') -ne 'DISM.exe /Online /Cleanup-Image /CheckHealth|DISM.exe /Online /Cleanup-Image /ScanHealth|DISM.exe /Online /Cleanup-Image /RestoreHealth|sfc.exe /scannow') {
    throw 'Repair mode command plan differs from Goodnight.'
}
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('Plumbyr-empty-recovery-' + [Guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($fixture, '{"Processes":[],"Devices":[]}')
    & $powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $scripts 'SleepRecovery.ps1') -Journal $fixture -RecoverNow
    if ($LASTEXITCODE -ne 0 -or (Test-Path -LiteralPath $fixture)) { throw 'Empty recovery journal was not completed and removed.' }
} finally {
    if (Test-Path -LiteralPath $fixture) { Remove-Item -LiteralPath $fixture -Force }
}
Write-Host 'PASS: all bundled PowerShell scripts parse; Check/Repair plans match Goodnight; recovery helper compiles and handles an empty journal.'
