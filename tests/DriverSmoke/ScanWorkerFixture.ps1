param([string]$Scanner, [string]$Scenario = 'offer')
$ErrorActionPreference = 'Stop'
# These functions replace every Windows-facing query used by the real script.
function Get-CimInstance {
    param([string]$ClassName)
    if ($Scenario -eq 'inventory-error') { throw 'Fixture inventory unavailable' }
    if ($ClassName -eq 'Win32_PnPEntity') {
        [pscustomobject]@{ DeviceID = 'fixture-device'; ConfigManagerErrorCode = 22
            HardwareID = @('PCI\VEN_1234&DEV_5678&SUBSYS_00000001'); CompatibleID = @('PCI\VEN_1234&DEV_5678') }
    } elseif ($ClassName -eq 'Win32_PnPSignedDriver') {
        [pscustomobject]@{ DeviceName = 'Fixture display'; DeviceClass = 'DISPLAY'; DeviceID = 'fixture-device'
            DriverVersion = '1.0.0.0'; InfName = 'oem1.inf'; DriverDate = [datetime]'2025-01-01'
            Manufacturer = 'Fixture maker'; DriverProviderName = 'Fixture provider'; HardWareID = 'PCI\VEN_1234&DEV_5678&SUBSYS_00000001' }
    } else { throw "Unexpected CIM query: $ClassName" }
}
function New-Object {
    param([string]$ComObject)
    if ($ComObject -ne 'Microsoft.Update.Session') { throw "Unexpected COM request: $ComObject" }
    $session = [pscustomobject]@{ ClientApplicationID = '' }
    $session | Add-Member ScriptMethod CreateUpdateSearcher {
        $searcher = [pscustomobject]@{}
        $searcher | Add-Member ScriptMethod Search {
            param($criteria)
            if ($criteria -ne "IsInstalled=0 and Type='Driver' and IsHidden=0") { throw 'Wrong driver search criteria' }
            if ($Scenario -eq 'search-error') { throw 'Fixture Windows Update offline' }
            $updates = @()
            if ($Scenario -eq 'offer') {
                $updates = @([pscustomobject]@{
                    Identity = [pscustomobject]@{ UpdateID = '11111111-2222-3333-4444-555555555555'; RevisionNumber = 7 }
                    Title = 'Fixture - Display - 2.0.0.0'; DriverProvider = 'Fixture provider'; DriverManufacturer = 'Fixture maker'
                    DriverModel = 'Fixture display'; DriverClass = 'DISPLAY'; DriverHardwareID = 'PCI\VEN_1234&DEV_5678'
                    DriverVerDate = [datetime]'2026-01-01'; MaxDownloadSize = 2097152
                    InstallationBehavior = [pscustomobject]@{ RebootBehavior = 2 }
                })
            }
            [pscustomobject]@{ ResultCode = if ($Scenario -eq 'partial') { 3 } else { 2 }; Updates = $updates; Warnings = @() }
        }
        $searcher
    }
    $session
}
& $Scanner -Mode $(if ($Scenario -eq 'inventory') { 'inventory' } else { 'updates' })
exit $LASTEXITCODE
