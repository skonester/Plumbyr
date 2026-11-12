<img src="plumbyr/Assets/icon.png" width="128" height="128" alt="logo"/> <img src="plumbyr/Assets/GPL3.png" width="128" height="128" alt="GPL3"/> <a href="https://github.com/skonester/Plumbyr/blob/main/LICENSE">GitHub License</a>

# P.L.U.M.B.Y.R

**Plumbing Linked Universal Maintenance & Binary Yield Reclaimer**

## System Overview
Plumbyr is a Windows cleaner/diagnostics app (F# / Avalonia / .NET 10) that loads JSON maintenance rules, analyzes files, shows size/count results, and lets you review selected cleanup targets.

## Key Features
- Rule-based cleanup via embedded `win32rules/*.json`
- Browser cache analysis via bundled `Kudu/worker.mjs` + pinned Node runtime
- System diagnostics (`SystemInfo` via WMI/DXGI)
- Space map with drive/folder drill-down, size bars, and the 40 biggest files
- Command Center terminal with PowerShell/CMD execution
- Windows 10/11 optimization tweaks (`OptimizeService`, ported from Swept)
- Goodnight sleep monitoring, QuickOff countdown, power diagnostics, Windows health checks/repairs, and supported sleep modes
- Cleaning history (`CleaningHistory` DB) and log export

## Tech Stack & Dependencies
- F# / .NET 10 (`net10.0-windows`), Avalonia 12.1.2, Serilog, ByteSize, FSharp.Json
- System.Management, Vortice.DXGI, Synthora
- Bundled Node (`node.exe`) from pinned archive (`scripts/node-runtime.json`)

## Installation & Setup
```powershell
# Build (downloads pinned Node, restores NuGet)
./scripts/Publish.ps1
# Dev run
dotnet run --project plumbyr/Plumbyr.fsproj
```

## Execution & Usage
```powershell
# Self-contained executable (no sibling files needed)
./dist/win-x64/Plumbyr.exe
# Browser analysis to JSON (no UI)
./dist/win-x64/Plumbyr.exe --analyze-browsers report.json
```

## Build

Build requirements: Windows x64, .NET 10 SDK, and Windows PowerShell. The first build downloads a pinned Node archive from nodejs.org and restores NuGet packages. Subsequent builds reuse the local caches; a separate Node/npm installation is unnecessary.

From the repository root:

~~~powershell
./scripts/Publish.ps1
~~~

Equivalent publish command:

~~~powershell
dotnet publish plumbyr/Plumbyr.fsproj -c Release -p:PublishProfile=SingleFile -r win-x64
~~~

The `SingleFile.pubxml` profile sets `SelfContained=true`, `PublishSingleFile=true`, `EnableCompressionInSingleFile=true`, `PublishTrimmed=false`, and `RuntimeIdentifier=win-x64`. Output is written to `dist/win-x64/`. The publish script verifies that `dist/win-x64/` contains exactly one file: `Plumbyr.exe`. Trimming is disabled (`PublishTrimmed=false`) to preserve F# JSON serialization, reflection, and Avalonia bindings.

For development:

~~~powershell
dotnet run --project plumbyr/Plumbyr.fsproj
~~~

## Releases

`.github/workflows/release.yml` builds on a GitHub Windows runner and publishes a release with:

- `Plumbyr-<version>-setup.exe` — Inno Setup installer (`installer/Plumbyr.iss`)
- `Plumbyr-<version>-portable.exe` — the single-file executable
- `SHA256SUMS.txt`

Trigger it by pushing a version tag (`git tag v1.0.2 && git push origin v1.0.2`) or by running **Release** from the Actions tab. The portable build is smoke-tested (`tests/SingleFileSmoke.ps1`) before anything is published.

## Features

- **Rule-based cleanup:** JSON definitions, grouped analysis results, selected cleanup actions, and activity logging.
- **Browser cache analysis:** Kudu's original TypeScript discovers Chromium browser caches; a bundled Node worker measures paths, sizes, and file counts. Includes cancellation, timeout handling, and incomplete-scan reporting.
- **System diagnostics:** Windows/hardware information using System.Management and DXGI.
- **Space map:** Roomy's folder-size explorer ported to Avalonia/F#. Choose a drive and select **Scan**, or use **Choose folder** to map one location. Browse size-sorted folders using the bars, breadcrumbs and **Up**; reveal folders or any of the 40 biggest files in File Explorer. Scans run in the background with progress and cancellation. Sizes are logical file bytes, not guaranteed reclaimable space; junctions/symbolic links are skipped and unreadable entries are reported. Files are never deleted by this feature.
- **Command Center:** activity terminal, diagnostics, and optional Windows command output.
- **Optimize:** conservative Windows 10/11 tweaks (telemetry, ads, background apps, Game DVR, power plan, built-in app debloat, and more). Registry values and stock tools only; a system restore point is created first by default.
- **Cleaning history:** session statistics and terminal-log export.
- **Sleep:** Goodnight's features ported to native F# / Avalonia. Includes allowed wake sources, return-to-sleep display behavior, optional application suspension and device control, lid-close delay, wake logging, sign-in startup, a cancelable QuickOff countdown, sleep/power/device/event reports, separate Windows check and repair actions, and supported sleep states. Helpers and the QuickOff MIT notice are embedded in the single executable.

Open **Sleep** (or enter `sleep` / `goodnight` in Command Center). Monitoring starts paused; press **Start monitoring** to apply your saved settings. Power button wake is always allowed. Goodnight's return-to-sleep behavior turns the display off; Windows and your hardware determine the resulting sleep state. QuickOff preserves Goodnight's command and may hibernate. Choose a 5, 15, or 30 second delay; **Cancel sleep** remains available in the sidebar while browsing other sections. Closing cancels a pending countdown. QuickOff pauses monitoring before dispatch and leaves it paused after return or failure.

Sleep reports are saved to `%LOCALAPPDATA%\Plumbyr\reports\sleep`; settings are saved to `sleep-settings.json` in Plumbyr's data folder. Diagnostic collection includes a 60-second energy analysis. Use **Cancel diagnostics** to stop report collection; partial reports are kept. Closing during collection stops the collector and its child processes, then closes automatically after cleanup. **Check Windows** verifies health without repairs; **Repair Windows** confirms before running DISM RestoreHealth and SFC /scannow. Tools pause monitoring and cannot overlap QuickOff. Windows health tools and a dispatched power command must finish before normal exit. Enabling sign-in startup creates the separate `PlumbyrSleep` scheduled task, which opens Plumbyr with `--sleep-monitor`.

Application and device controls are opt-in. They restore changes on resume, Pause, and normal exit. A separate recovery helper records changed processes/devices and attempts restoration if Plumbyr exits unexpectedly. Only current-session, noncritical applications outside Windows are considered; the app, its ancestors/children, Windows shells, and recovery helper are excluded. Only devices that were enabled are changed, and disabling is not persisted across reboot. Actual sleep/wake behavior still needs checking on your hardware.

Open **Browsers > Analyze browser caches** for read-only browser analysis. Sizes show current logical file bytes, not guaranteed reclaimable space. Firefox is not included in this integration. **Open cleanup rules** opens the existing browser cleanup workflow.

Browser analysis can also write a JSON report without opening the UI:

~~~powershell
./dist/win-x64/Plumbyr.exe --analyze-browsers report.json
~~~

## Verification

Verify Goodnight's port without suspending the host, disabling devices, or running Windows repairs:

~~~powershell
dotnet run --project tests/SleepSmoke/SleepSmoke.fsproj -c Release
./tests/GoodnightScriptsSmoke.ps1
~~~

The sleep smoke check covers countdown timing/cancellation, launch failure, tool exclusion, pause/close cleanup, wake-source policy, lid-open cancellation, saved settings, all five themed pages at two widths, and native read-only listener registration. It resolves embedded Goodnight helpers through `AppFiles.get`, compares their contents with source, and checks their syntax, Check/Repair command plans, and empty-journal recovery. Rendered previews are written to `.build/sleep-*.png`.

Verify the space map scanner with isolated fixtures:

~~~powershell
dotnet fsi tests/SpaceScannerSmoke.fsx
~~~

This checks nested and deep folder totals, hidden/system files, empty locations, the top 40 files, link handling, cancellation, and read-only behavior.

The headless Avalonia smoke check renders the page and verifies folder selection, drill-down, breadcrumbs, Up, and cancellation:

~~~powershell
dotnet run --project tests/SpaceMapUiSmoke/SpaceMapUiSmoke.fsproj -c Release
~~~

After publishing:

~~~powershell
dotnet fsi tests/KuduBridgeSmoke.fsx
~~~

This checks fixture measurements, missing dependencies, cancellation, timeouts, output limits, and worker errors using the embedded runtime.

From an administrator PowerShell (the app manifest requires elevation):

~~~powershell
./tests/SingleFileSmoke.ps1
~~~

This copies only the EXE to a fresh fixture directory, clears PATH, disables external .NET lookup, verifies browser analysis, and removes its own fixture files. Browser fixture paths follow the child's `LOCALAPPDATA` environment variable; the application's extracted payload uses Windows' LocalApplicationData known folder, which may differ.

## Repository Layout

~~~text
plumbyr/             F# application, assets, and publish profile
plumbyr/Kudu/        Worker adapter and the small retained Kudu source subset
win32rules/          Embedded JSON cleaning rules
scripts/            Publish script and checksum-pinned Node preparation
tests/              Bridge and standalone-executable smoke tests
dist/               Generated executable (ignored)
.build/             Download cache and build checks (ignored)
~~~

Edit win32rules and rebuild to update embedded cleanup definitions. The app does not load rules from the current working directory.

Only the Kudu modules used by browser discovery are retained. Their original MIT license is in plumbyr/Kudu/vendor/LICENSE. The Optimize feature is an F# port of `cleaner_optimize.py` from [Swept](https://github.com/vladcodex/swept) (MIT, Copyright (c) 2026 vladcodex); its license notice is kept at the top of plumbyr/OptimizeService.fs. Node version and official archive/executable checksums are pinned in scripts/node-runtime.json; its license and dependency notices are embedded from the verified archive. See [single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) for the .NET bundling mechanism.

The Space map feature is an F# / Avalonia adaptation of Roomy's `SpaceScanner` and `SpaceMapVM` (MIT, Copyright (c) 2026 krezio). The license notice is preserved in `plumbyr/SpaceScanner.fs` and embedded in the application assembly.

Plumbyr is under active development. Review selected cleanup targets before deleting files.
