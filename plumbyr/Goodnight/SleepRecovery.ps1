# Recovery watchdog for Plumbyr's opt-in application/device sleep controls.
param([int]$OwnerId, [long]$OwnerStarted, [Parameter(Mandatory=$true)][string]$Journal, [string]$ReadyPath, [switch]$RecoverNow)
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PlumbyrSleepRecovery {
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
    [DllImport("cfgmgr32.dll", CharSet=CharSet.Unicode)] public static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] public static extern uint CM_Enable_DevNode(uint node, uint flags);
}
'@
if ($ReadyPath) { Set-Content -LiteralPath $ReadyPath -Value 'Ready' -Encoding Ascii }
if (-not $RecoverNow) {
    while ($true) {
        try {
            $owner = Get-Process -Id $OwnerId -ErrorAction Stop
            if ($owner.StartTime.ToUniversalTime().Ticks -ne $OwnerStarted) { break }
        } catch { break }
        Start-Sleep -Seconds 2
    }
}
if (-not (Test-Path -LiteralPath $Journal)) { exit 0 }
$state = Get-Content -LiteralPath $Journal -Raw | ConvertFrom-Json
$remainingProcesses = @()
$remainingDevices = @()
foreach ($entry in $state.Processes) {
    try {
        $process = Get-Process -Id $entry.Id -ErrorAction Stop
    } catch { continue }
    if ($process.StartTime.ToUniversalTime().Ticks -ne $entry.Started) { continue }
    $handle = [PlumbyrSleepRecovery]::OpenProcess(0x800, $false, $entry.Id)
    try {
        if ($handle -eq [IntPtr]::Zero -or [PlumbyrSleepRecovery]::NtResumeProcess($handle) -ne 0) { $remainingProcesses += $entry }
    } finally { if ($handle -ne [IntPtr]::Zero) { [PlumbyrSleepRecovery]::CloseHandle($handle) | Out-Null } }
}
foreach ($device in $state.Devices) {
    [uint32]$node = 0
    if ([PlumbyrSleepRecovery]::CM_Locate_DevNodeW([ref]$node, $device, 0) -ne 0 -or
        [PlumbyrSleepRecovery]::CM_Enable_DevNode($node, 0) -ne 0) { $remainingDevices += $device }
}
if ($remainingProcesses.Count -eq 0 -and $remainingDevices.Count -eq 0) { Remove-Item -LiteralPath $Journal -Force }
else { @{ Processes = $remainingProcesses; Devices = $remainingDevices } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $Journal -Encoding UTF8 }
if ($remainingProcesses.Count -gt 0 -or $remainingDevices.Count -gt 0) { exit 1 }
exit 0
