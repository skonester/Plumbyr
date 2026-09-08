# P.L.U.M.B.Y.R

**Plumbing Linked Universal Maintenance & Binary Yield Reclaimer**

Plumbyr is a Windows cleaner and diagnostics app written in F# with Avalonia. It loads maintenance rules, analyzes files, shows size/count results, and lets you review selected cleanup targets.

## Download / Run

The distributable is **dist/win-x64/Plumbyr.exe**: one self-contained Windows x64 executable. Copy that file to the destination computer and run it. No separate .NET or Node installation is needed. The existing application manifest requests administrator rights.

The EXE bundles .NET 10, Avalonia 12.0.2 UI libraries, System.Management, DXGI, Serilog, ByteSize, FSharp.Json, a pinned Node runtime (`node.exe`), the Kudu browser worker (`Kudu/worker.mjs`), embedded cleaning rules (`win32rules/*.json`), assets (`Assets/icon.png`), and license notices (`Kudu/vendor/LICENSE`, `NODE-LICENSE`). The `.fsproj` embeds these as `Plumbyr.Payload/*` resources; `Prepare-Node.ps1` downloads the pinned archive to `.build/node/win-x64` before build. On first launch, bundled files are materialized in a per-build cache under **%LOCALAPPDATA%/Plumbyr/runtime**. .NET also extracts native libraries to its standard temporary bundle cache. No sibling files need to be distributed with the EXE.

Logs and cleaning history live under **%LOCALAPPDATA%/Plumbyr**, so the executable can run from a directory that is not writable.

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

## Features

- **Rule-based cleanup:** JSON definitions, grouped analysis results, selected cleanup actions, and activity logging.
- **Browser cache analysis:** Kudu's original TypeScript discovers Chromium browser caches; a bundled Node worker measures paths, sizes, and file counts. Includes cancellation, timeout handling, and incomplete-scan reporting.
- **System diagnostics:** Windows/hardware information using System.Management and DXGI.
- **Command Center:** activity terminal, diagnostics, and optional Windows command output.
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

Only the Kudu modules used by browser discovery are retained. Their original MIT license is in plumbyr/Kudu/vendor/LICENSE. Node version and official archive/executable checksums are pinned in scripts/node-runtime.json; its license and dependency notices are embedded from the verified archive. See [single-file deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) for the .NET bundling mechanism.

Plumbyr is under active development. Review selected cleanup targets before deleting files.
