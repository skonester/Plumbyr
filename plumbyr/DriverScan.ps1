# Inventory and Windows Update queries adapted from StellarHub 1.0.0,
# stellarhub/engine/drivers.py, by Esteban Arosemena and contributors.
# https://github.com/estebanarosemena/StellarHub
# GPL-3.0-only; see COPYING and docs/stellarhub-driver-scanning.md.
# Modified for Plumbyr, 2026-11-19: strict JSON envelope, operation results,
# complete device IDs, explicit failures, warnings, and separate read-only modes.
param([ValidateSet('inventory', 'updates')][string]$Mode = 'updates')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$warnings = [Collections.Generic.List[string]]::new()
function Read-Inventory {
    $states = @{}
    Get-CimInstance Win32_PnPEntity | ForEach-Object {
        $states[$_.DeviceID] = @{ code = $_.ConfigManagerErrorCode; ids = @($_.HardwareID) + @($_.CompatibleID) }
    }
    @(Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceName -and $_.DriverVersion } | ForEach-Object {
        $state = $states[$_.DeviceID]
        [pscustomobject]@{
            name = [string]$_.DeviceName; cls = [string]$_.DeviceClass
            maker = [string]$_.Manufacturer; provider = [string]$_.DriverProviderName
            version = [string]$_.DriverVersion; inf = [string]$_.InfName
            date = if ($_.DriverDate) { $_.DriverDate.ToString('yyyy-MM-dd') } else { '' }
            instance = [string]$_.DeviceID; code = if ($state) { $state.code } else { $null }
            hardwareIds = @(@($_.HardWareID) + @($state.ids) | Where-Object { $_ } | Sort-Object -Unique)
        }
    })
}
try {
    $drivers = @()
    try { $drivers = @(Read-Inventory) }
    catch {
        if ($Mode -eq 'inventory') { throw }
        $warnings.Add('Installed driver details could not be read: ' + $_.Exception.Message)
    }
    $updates = @(); $resultCode = 2
    if ($Mode -eq 'updates') {
        $session = New-Object -ComObject Microsoft.Update.Session
        $session.ClientApplicationID = 'Plumbyr'
        $result = $session.CreateUpdateSearcher().Search("IsInstalled=0 and Type='Driver' and IsHidden=0")
        $resultCode = [int]$result.ResultCode
        if ($resultCode -notin @(2, 3)) { throw "Windows Update search failed (result $resultCode)." }
        if ($resultCode -eq 3) { $warnings.Add('Windows Update completed with errors; results may be incomplete.') }
        foreach ($warning in $result.Warnings) {
            $warnings.Add(('{0} (0x{1:X8})' -f $warning.Message, $warning.HResult))
        }
        $updates = @($result.Updates | ForEach-Object {
            $u = $_
            $date = ''; try { $date = ([datetime]$u.DriverVerDate).ToString('yyyy-MM-dd') } catch {}
            [pscustomobject]@{
                id = [string]$u.Identity.UpdateID; revision = [int]$u.Identity.RevisionNumber
                title = [string]$u.Title; provider = [string]$u.DriverProvider
                maker = [string]$u.DriverManufacturer; model = [string]$u.DriverModel
                cls = [string]$u.DriverClass; hardwareId = [string]$u.DriverHardwareID
                date = $date; sizeMB = [double]($u.MaxDownloadSize / 1MB)
                mayReboot = [bool]($u.InstallationBehavior.RebootBehavior -ne 0)
            }
        })
    }
    [pscustomobject]@{
        schema = 1; kind = $Mode; resultCode = $resultCode
        checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('o')
        drivers = @($drivers); updates = @($updates); warnings = @($warnings.ToArray())
    } | ConvertTo-Json -Depth 7 -Compress
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
