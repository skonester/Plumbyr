<img src="plumbyr/Assets/icon.png" width="128" height="128" alt="logo"/> <img src="plumbyr/Assets/GPL3.png" width="128" height="128" alt="GPL3"/> <a href="https://github.com/skonester/Plumbyr/blob/main/LICENSE">GitHub License</a>

# P.L.U.M.B.Y.R

**Plumbing Linked Universal Maintenance & Binary Yield Reclaimer**

## System Overview
Plumbyr is a Windows cleaner/diagnostics app (F# / Avalonia / .NET 10) that loads JSON maintenance rules, analyzes files, shows size/count results, and lets you review selected cleanup targets.

## Key Features
- Rule-based cleanup via embedded `win32rules/*.json`
- Browser cache analysis via bundled `Kudu/worker.mjs` + pinned Node runtime
- System diagnostics (`SystemInfo` via WMI/DXGI)
- Command Center terminal with PowerShell/CMD execution
- Windows 10/11 optimization tweaks (`OptimizeService`, ported from Swept)
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

Trigger it by pushing a version tag (`git tag v1.0.0 && git push origin v1.0.0`) or by running **Release** from the Actions tab. The portable build is smoke-tested (`tests/SingleFileSmoke.ps1`) before anything is published.

## Features

- **Rule-based cleanup:** JSON definitions, grouped analysis results, selected cleanup actions, and activity logging.
- **Browser cache analysis:** Kudu's original TypeScript discovers Chromium browser caches; a bundled Node worker measures paths, sizes, and file counts. Includes cancellation, timeout handling, and incomplete-scan reporting.
- **System diagnostics:** Windows/hardware information using System.Management and DXGI.
- **Command Center:** activity terminal, diagnostics, and optional Windows command output.
- **Optimize:** conservative Windows 10/11 tweaks (telemetry, ads, background apps, Game DVR, power plan, built-in app debloat, and more). Registry values and stock tools only; a system restore point is created first by default.
- **Cleaning history:** session statistics and terminal-log export.

Open **Browsers > Analyze browser caches** for read-only browser analysis. Sizes show current logical file bytes, not guaranteed reclaimable space. Firefox is not included in this integration. **Open cleanup rules** opens the existing browser cleanup workflow.

Browser analysis can also write a JSON report without opening the UI:

~~~powershell
./dist/win-x64/Plumbyr.exe --analyze-browsers report.json
~~~

## Verification

After publishing:

~~~powershell
dotnet fsi tests/KuduBridgeSmoke.fsx
~~~

This checks fixture measurements, missing dependencies, cancellation, timeouts, output limits, and worker errors using the embedded runtime.

From an administrator PowerShell (the app manifest requires elevation):

~~~powershell
./tests/SingleFileSmoke.ps1
~~~

This copies only the EXE to a fresh fixture directory, clears PATH, disables external .NET lookup, verifies browser analysis, and removes its own fixture files.

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

Plumbyr is under active development. Review selected cleanup targets before deleting files.
