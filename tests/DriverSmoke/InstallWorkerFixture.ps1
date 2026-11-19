param([string]$Request, [string]$StopFile)
$ErrorActionPreference = 'Stop'
$fixtureRequest = Get-Content -LiteralPath $Request -Raw | ConvertFrom-Json
$fixtureScenario = [string]$fixtureRequest.updates[0].title
$fixtureCalls = Join-Path (Split-Path $Request -Parent) 'calls.txt'
$fixtureSequence = 10; $fixtureDescription = 'Previous restore point'
function Record([string]$text) { [IO.File]::AppendAllText($fixtureCalls, $text + [Environment]::NewLine) }
function Get-ComputerRestorePoint {
    [pscustomobject]@{ SequenceNumber=$script:fixtureSequence; Description=$script:fixtureDescription }
}
function Checkpoint-Computer {
    param($Description, $RestorePointType, $ErrorAction, $WarningAction)
    Record 'restore'
    if ($fixtureScenario -eq 'restore-fail') { throw 'Fixture System Protection unavailable' }
    if ($fixtureScenario -ne 'restore-skipped') {
        $script:fixtureSequence = 11; $script:fixtureDescription = $Description
    }
}
function New-Result([int]$code, [bool]$restart=$false) {
    $result = [pscustomobject]@{ ResultCode=$code; HResult=if($code -eq 4){ -2145124329 }else{ 0 }; RebootRequired=$restart }
    $result | Add-Member ScriptMethod GetUpdateResult { param($index) $this }
    $result
}
function New-Object {
    param([string]$ComObject)
    if ($ComObject -eq 'Microsoft.Update.UpdateColl') {
        $collection = [pscustomobject]@{ Items=[Collections.Generic.List[object]]::new() }
        $collection | Add-Member ScriptMethod Add { param($update) [void]$this.Items.Add($update) }
        return $collection
    }
    if ($ComObject -ne 'Microsoft.Update.Session') { throw "Unexpected COM request: $ComObject" }
    $session = [pscustomobject]@{ ClientApplicationID='' }
    $session | Add-Member ScriptMethod CreateUpdateSearcher {
        $searcher = [pscustomobject]@{}
        $searcher | Add-Member ScriptMethod Search {
            param($criteria)
            if ($criteria -ne "IsInstalled=0 and Type='Driver' and IsHidden=0") { throw 'Unsafe search criteria' }
            Record 'search'
            if ($fixtureScenario -eq 'search-fail') { throw 'Fixture Windows Update offline' }
            $offers = @()
            foreach ($identity in @(
                @('11111111-2222-3333-4444-555555555555', 7),
                @('22222222-2222-3333-4444-555555555555', 4),
                @('99999999-2222-3333-4444-555555555555', 1))) {
                $update = [pscustomobject]@{ Identity=[pscustomobject]@{ UpdateID=$identity[0]; RevisionNumber=$identity[1] }
                    EulaAccepted=$false; IsDownloaded=$false }
                if ($fixtureScenario -eq 'revision-changed') { $update.Identity.RevisionNumber++ }
                $update | Add-Member ScriptMethod AcceptEula { Record ('eula:' + $this.Identity.UpdateID); $this.EulaAccepted=$true }
                $offers += $update
            }
            [pscustomobject]@{ ResultCode=if($fixtureScenario -eq 'search-partial'){3}else{2}; Updates=$offers }
        }
        $searcher
    }
    $session | Add-Member ScriptMethod CreateUpdateDownloader {
        $downloader = [pscustomobject]@{ Updates=$null }
        $downloader | Add-Member ScriptMethod Download {
            $update = $this.Updates.Items[0]
            Record ('download:' + $update.Identity.UpdateID)
            if ($fixtureScenario -eq 'crash-after-first' -and $update.Identity.UpdateID.StartsWith('22222222')) { [Environment]::Exit(19) }
            if ($fixtureScenario -eq 'download-fail') { return (New-Result 4) }
            $update.IsDownloaded = $true
            if ($fixtureScenario -eq 'cancel-download') { [IO.File]::WriteAllText($StopFile, 'stop') }
            New-Result 2
        }
        $downloader
    }
    $session | Add-Member ScriptMethod CreateUpdateInstaller {
        $installer = [pscustomobject]@{ Updates=$null; AllowSourcePrompts=$true; ForceQuiet=$false
            RebootRequiredBeforeInstallation=($fixtureScenario -eq 'reboot-before') }
        $installer | Add-Member ScriptMethod Install {
            $update = $this.Updates.Items[0]
            Record ('install:' + $update.Identity.UpdateID)
            if ($fixtureScenario -eq 'slow') { Start-Sleep -Milliseconds 400 }
            if ($fixtureScenario -eq 'stop-after-first') { [IO.File]::WriteAllText($StopFile, 'stop') }
            if ($fixtureScenario -eq 'install-fail') { return (New-Result 4) }
            if ($fixtureScenario -eq 'partial') { return (New-Result 3 $true) }
            if ($fixtureScenario -eq 'overall-warning') {
                $result = New-Result 3
                $result | Add-Member -Force ScriptMethod GetUpdateResult { param($index) New-Result 2 }
                return $result
            }
            if ($fixtureScenario -eq 'hresult-warning') {
                $result = New-Result 2; $result.HResult = -2145124329; return $result
            }
            New-Result 2 $true
        }
        $installer
    }
    $session
}
# All Windows-facing APIs used by the production helper are mocked above.
& (Join-Path $PSScriptRoot '../../plumbyr/DriverInstall.ps1') -Request $Request -StopFile $StopFile
exit $LASTEXITCODE
