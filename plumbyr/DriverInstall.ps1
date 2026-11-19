# Selected-update download/install flow adapted from StellarHub 1.0.0,
# stellarhub/engine/drivers.py (_INSTALL), Esteban Arosemena and contributors.
# https://github.com/estebanarosemena/StellarHub
# GPL-3.0-only; see COPYING and docs/stellarhub-driver-scanning.md.
# Modified for Plumbyr, 2026-11-19: exact revisions, strict per-update results,
# verified optional restore point, cooperative stopping, and no forced reboot.
param([Parameter(Mandatory=$true)][string]$Request, [Parameter(Mandatory=$true)][string]$StopFile)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$selected = @(); $finished = @{}; $reboot = $false; $restore = 'Not requested'
$fatal = ''; $deadline = [DateTimeOffset]::UtcNow.AddHours(1)
function Send-Event($payload) {
    $payload.schema = 1
    [Console]::Out.WriteLine(($payload | ConvertTo-Json -Depth 5 -Compress))
    [Console]::Out.Flush()
}
function Stop-Requested {
    (Test-Path -LiteralPath $StopFile) -or [DateTimeOffset]::UtcNow -ge $deadline
}
function Send-Result($selection, [string]$state, [int]$code, [string]$hr, [bool]$restart, [string]$message) {
    $key = ([string]$selection.id).ToLowerInvariant() + ':' + [int]$selection.revision
    $finished[$key] = $true
    Send-Event @{ stage='result'; id=[string]$selection.id; revision=[int]$selection.revision
        state=$state; code=$code; hresult=$hr; reboot=$restart; message=$message }
}
try {
    $requestData = Get-Content -LiteralPath $Request -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($requestData.schema -ne 1 -or $requestData.restorePoint -isnot [bool]) { throw 'Invalid driver install request.' }
    $selected = @($requestData.updates)
    if ($selected.Count -lt 1 -or $selected.Count -gt 256) { throw 'Select between 1 and 256 driver updates.' }
    $keys = @{}
    foreach ($selection in $selected) {
        $guid = [guid]::Empty
        if (-not [guid]::TryParse([string]$selection.id, [ref]$guid) -or
            $selection.revision -isnot [int] -or $selection.revision -lt 1) { throw 'Invalid selected update identity.' }
        $key = $guid.ToString()
        if ($keys.ContainsKey($key)) { throw 'Duplicate selected update identity.' }
        $keys[$key] = $true
    }
    if (Stop-Requested) { throw 'Stopped before starting.' }
    Send-Event @{ stage='search'; message='Rechecking selected drivers with Windows Update' }
    $session = New-Object -ComObject Microsoft.Update.Session
    $session.ClientApplicationID = 'Plumbyr'
    $search = $session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
    if ([int]$search.ResultCode -ne 2) { throw "Windows Update could not completely revalidate the selection (result $($search.ResultCode)). Scan again." }
    $available = @{}
    foreach ($update in $search.Updates) {
        $key = ([string]$update.Identity.UpdateID).ToLowerInvariant() + ':' + [int]$update.Identity.RevisionNumber
        if ($available.ContainsKey($key)) { throw 'Windows Update returned a duplicate update identity.' }
        $available[$key] = $update
    }
    $todo = @()
    foreach ($selection in $selected) {
        $key = ([string]$selection.id).ToLowerInvariant() + ':' + [int]$selection.revision
        if ($available.ContainsKey($key)) { $todo += $selection }
        else { Send-Result $selection 'unavailable' 0 '' $false 'This exact update revision is no longer offered. Scan again.' }
    }
    if ($todo.Count -gt 0 -and $requestData.restorePoint -and -not (Stop-Requested)) {
        $restore = 'Not created'
        Send-Event @{ stage='restore'; message='Creating and verifying a restore point' }
        $description = 'Plumbyr drivers ' + [guid]::NewGuid().ToString('N').Substring(0, 12)
        $before = @(Get-ComputerRestorePoint -ErrorAction Stop)
        $oldSequence = if ($before.Count) { [int](($before | Measure-Object SequenceNumber -Maximum).Maximum) } else { -1 }
        Checkpoint-Computer -Description $description -RestorePointType DEVICE_DRIVER_INSTALL -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null
        $created = @(Get-ComputerRestorePoint -ErrorAction Stop | Where-Object { $_.SequenceNumber -gt $oldSequence -and $_.Description -eq $description })
        if ($created.Count -eq 0) { throw 'Windows did not create a new restore point. No drivers were installed. Check System Protection, or uncheck the restore-point option before retrying.' }
        $restore = 'Created and verified'
        Send-Event @{ stage='restore'; message='Restore point created and verified' }
    }
    foreach ($selection in $todo) {
        if (Stop-Requested) { break }
        $key = ([string]$selection.id).ToLowerInvariant() + ':' + [int]$selection.revision
        $update = $available[$key]
        try {
            if (-not $update.EulaAccepted) { [void]$update.AcceptEula() }
            $collection = New-Object -ComObject Microsoft.Update.UpdateColl
            [void]$collection.Add($update)
            Send-Event @{ stage='download'; id=[string]$selection.id; revision=[int]$selection.revision; message='Downloading selected driver' }
            $downloader = $session.CreateUpdateDownloader(); $downloader.Updates = $collection
            $download = $downloader.Download()
            $downloadItem = $download.GetUpdateResult(0)
            if ([int]$download.ResultCode -ne 2 -or [int]$downloadItem.ResultCode -ne 2 -or
                [int]$download.HResult -lt 0 -or [int]$downloadItem.HResult -lt 0 -or -not $update.IsDownloaded) {
                $downloadHr = if ([int]$downloadItem.HResult -ne 0) { [int]$downloadItem.HResult } else { [int]$download.HResult }
                Send-Result $selection 'failed' ([int]$downloadItem.ResultCode) ('{0:X8}' -f $downloadHr) $false 'Download did not complete successfully. Driver was not installed.'
                continue
            }
            if (Stop-Requested) { break }
            $installer = $session.CreateUpdateInstaller(); $installer.Updates = $collection
            $installer.AllowSourcePrompts = $false; $installer.ForceQuiet = $true
            if ($installer.RebootRequiredBeforeInstallation) {
                $reboot = $true
                throw 'Windows requires a restart before more drivers can be installed.'
            }
            Send-Event @{ stage='install'; id=[string]$selection.id; revision=[int]$selection.revision; message='Installing selected driver; stopping waits for this Windows operation' }
            $installed = $installer.Install()
            $item = $installed.GetUpdateResult(0)
            $code = [int]$item.ResultCode
            if ($code -eq 2 -and [int]$installed.ResultCode -ne 2) { $code = [int]$installed.ResultCode }
            if ($code -eq 2 -and ([int]$item.HResult -lt 0 -or [int]$installed.HResult -lt 0)) { $code = 3 }
            $restart = [bool]($installed.RebootRequired -or $item.RebootRequired)
            $reboot = $reboot -or $restart
            $state = if ($code -eq 2) { 'installed' } elseif ($code -eq 3) { 'partial' } else { 'failed' }
            $message = if ($code -eq 2) { 'Windows Update confirmed installation.' } elseif ($code -eq 3) { 'Windows Update completed with errors. Check this driver before retrying.' } else { 'Windows Update did not confirm a successful installation.' }
            $hr = if ([int]$item.HResult -ne 0) { [int]$item.HResult } else { [int]$installed.HResult }
            Send-Result $selection $state $code ('{0:X8}' -f $hr) $restart $message
        } catch {
            Send-Result $selection 'failed' 4 ('{0:X8}' -f [int]$_.Exception.HResult) $false $_.Exception.Message
        }
    }
} catch {
    $fatal = $_.Exception.Message
    Send-Event @{ stage='error'; message=$fatal }
} finally {
    $cancelled = Test-Path -LiteralPath $StopFile
    $expired = [DateTimeOffset]::UtcNow -ge $deadline
    if ($expired -and -not $fatal) { $fatal = 'The one-hour limit was reached; the current Windows operation was allowed to finish.' }
    foreach ($selection in $selected) {
        $key = ([string]$selection.id).ToLowerInvariant() + ':' + [int]$selection.revision
        if (-not $finished.ContainsKey($key)) {
            $reason = if ($cancelled) { 'Stopped before installation; completed driver changes were kept.' } elseif ($fatal) { $fatal } else { 'Driver was not attempted.' }
            Send-Result $selection 'notAttempted' 0 '' $false $reason
        }
    }
    Send-Event @{ stage='end'; cancelled=[bool]$cancelled; reboot=[bool]$reboot; restorePoint=$restore; error=$fatal }
}
