# Plumbyr architecture and contributor guide

Plumbyr is a Windows cleaner and diagnostics application built with F# and Avalonia. This guide covers implementation, detailed feature behavior, building, verification, and releases. See the [README](README.md) for downloads and a user overview.

[Build and run](#build-and-run) · [Application structure](#application-structure) · [Feature behavior](#feature-behavior) · [Verification](#verification) · [Release pipeline](#release-pipeline) · [Credits](#third-party-code-and-licenses)

## Build and run

Building requires Windows x64, the .NET 10 SDK, Windows PowerShell, Rust 1.94 or later, and Visual C++ build tools. The first build restores NuGet/Cargo packages and downloads a checksum-pinned Node archive from nodejs.org. Later builds reuse the local caches. A separate Node/npm installation is unnecessary. Rust builds the bundled recovery engine; published apps require no Rust installation.

From the repository root:

```powershell
# Publish the self-contained executable to dist/win-x64/Plumbyr.exe
./scripts/Publish.ps1

# Run a development build
dotnet run --project plumbyr/Plumbyr.fsproj
```

The equivalent publish command is:

```powershell
dotnet publish plumbyr/Plumbyr.fsproj -c Release -p:PublishProfile=SingleFile -r win-x64
```

The [SingleFile publish profile](plumbyr/Properties/PublishProfiles/SingleFile.pubxml) sets `SelfContained=true`, `PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true`, `PublishTrimmed=false`, and `RuntimeIdentifier=win-x64`. Trimming is disabled to preserve F# JSON serialization, reflection, and Avalonia bindings. The publish script verifies that the output directory contains exactly one file: `Plumbyr.exe`.

The app manifest requires administrator access. Published builds include the .NET runtime, bundled Node, cleanup rules, and helper scripts. See Microsoft's [single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) for the underlying bundling mechanism.

## Application structure

The desktop shell uses Avalonia with Synthora controls. The project targets `net10.0-windows`; its package references include Avalonia 12.1.2, Synthora, AvaloniaEdit, Serilog, ByteSize, FSharp.Json, System.Management, System.Diagnostics.EventLog, and Vortice.DXGI. Exact dependency versions live in [Plumbyr.fsproj](plumbyr/Plumbyr.fsproj).

| Component | Responsibility |
| --- | --- |
| `Program.fs` | Application startup, navigation, dashboard, cleanup controls, Command Center, and window lifecycle. |
| `AppFiles` | Extract and verify embedded payloads; resolve application data and runtime paths. |
| `RuleLoader`, `CleanTargets`, `ScanEngine` | Turn JSON rules into cleanup targets, analyze paths, execute selected cleanup, and collect results. |
| `KuduBridge`, `BrowserAnalysisView` | Run the bundled Node worker and display Chromium cache measurements. |
| `SystemInfo` | Read Windows and hardware diagnostics through WMI and DXGI. |
| `SpaceScanner`, `SpaceMapView` | Build and display a read-only folder-size tree and bounded top-40 file list. |
| `RecoveryService`, `RecoveryView`, `Reclaim/` | Native F# source selection and recovered-file pages, with a bundled Reclaim recovery worker. |
| `DriverService`, `DriversView` | Inspect drivers, use Windows Update, export backups, install local INF packages, and request restore points. |
| `OptimizeService`, `OptimizeView` | Present and apply selected Windows settings and debloat actions. |
| `SleepController`, `SleepPlatform`, `SleepView` | Manage monitoring, countdowns, Windows power integration, diagnostic tools, and sleep settings. |
| `ActivityLog`, `CleaningHistory` | Record activity and cleanup session statistics. |

Startup initializes the embedded payload before loading rules or displaying the application. Cleanup rules become `CleanTarget` values; `ScanEngine.AnalyzeTargets()` produces size/count results for review. Selected cleanup runs through `ScanEngine.CleanTargets()`, with session statistics saved through `CleaningHistory`.

### Payloads and user data

`AppFiles` uses Windows' `LocalApplicationData` known folder, normally `%LOCALAPPDATA%\Plumbyr`. Changing the `LOCALAPPDATA` environment variable does not necessarily change the path returned by `Environment.GetFolderPath`.

Embedded files are materialized under `runtime/<assembly MVID>/` within that data directory. Extraction checks resource hashes and replaces mismatched files through temporary files. Multiple builds can have separate runtime directories. Call `AppFiles.get` to resolve a payload instead of guessing an extraction path.

Writable application data stays outside the extracted payload:

| Path within the data directory | Contents |
| --- | --- |
| `plumbyr_history.db` | Cleanup session statistics. |
| `plumbyr_log*.txt` | Rolling application logs. |
| `sleep-settings.json` | Saved sleep preferences. |
| `reports/sleep/` | Sleep diagnostics, event exports, transcripts, and Windows health reports. |
| `sleep-recovery-<pid>.json` | Recovery journal for processes and devices changed by that session. |

### Repository layout

```text
plumbyr/             F# application, assets, and publish profile
plumbyr/Kudu/        Node worker and retained Kudu source
plumbyr/Goodnight/   Bundled sleep/health/recovery helpers and QuickOff notice
plumbyr/Reclaim/     Retained MIT recovery engine, Windows worker, and fixtures
win32rules/          Embedded JSON cleaning rules
scripts/            Publish script and checksum-pinned Node preparation
installer/          Inno Setup installer definition
tests/              Scanner, bridge, UI, helper, and executable smoke checks
dist/               Generated release artifacts (ignored)
.build/             Download cache and verification output (ignored)
```

To extend cleanup coverage, edit or add JSON files in `win32rules/` and rebuild. The app loads embedded rules, rather than rules from its working directory. New behavior can also hook `ScanEngine` events or extend `KuduBridge` settings.

## Feature behavior

### Cleanup and browser analysis

Rule-based cleanup groups JSON targets into system, application, browser, gaming/GPU, and privacy categories. The UI presents analysis results before selected cleanup actions. Privacy data is opt-in because removing cookies, history, or application state can sign users out.

The **Browsers** page runs read-only cache analysis. Kudu's retained TypeScript discovers Chromium caches; the bundled Node worker measures paths, logical file bytes, and file counts. The bridge supports cancellation, timeouts, output limits, and incomplete-scan reporting. Logical bytes are not guaranteed reclaimable space. Firefox is not included in this integration. **Open cleanup rules** opens the existing browser cleanup workflow.

Browser analysis can write a JSON report without opening the UI:

```powershell
./dist/win-x64/Plumbyr.exe --analyze-browsers report.json
```

The Node version, architecture, and official archive/executable checksums are pinned in [scripts/node-runtime.json](scripts/node-runtime.json). [Prepare-Node.ps1](scripts/Prepare-Node.ps1) prepares the verified runtime for embedding.

### Space map

Choose a drive and select **Scan**, or use **Choose folder** to map a location. Browse size-sorted folders through size bars, breadcrumbs, and **Up**; reveal folders or any of the 40 biggest files in Explorer.

`SpaceScanner` uses an iterative, cancellable walk. Sizes are logical file bytes, junctions/symbolic links are skipped, and unreadable entries are reported. The feature never deletes files. `SpaceMapView` runs scanning in the background with progress and cancellation. Closing the window cancels its scan.

### Sources and recovered files

**Sources** lists local volumes and available physical disks, with read-only refresh and an option to open a disk image. Locked/inaccessible volumes are disabled. The default scans unused space in NTFS, FAT, exFAT, ext2/3/4, and HFS+ volumes. Unsupported or damaged allocation maps fail without falling back to occupied data. Choose **All bytes** explicitly for signature carving, including existing files; use a volume for unused-space scanning rather than an unpartitioned whole disk.

**Recovered files** updates during scanning. Search names, types, and decimal/hex offsets; filter by category; sort by discovery, name, or size; and inspect details and decoded picture previews. Incoming events are batched every 100 ms with a bounded UI processing budget. A cached filter/sort index processes only new candidates until the user changes the filters or sort. The 100-row page keeps existing controls, patches changed selection/status, and inserts/removes/moves only rows whose membership/order changes. New results beyond a full page do not rebuild its controls or reset focus/scroll. Selection counts and bytes update incrementally. Selection persists across filters, and recovery includes all selected results. Original names, timestamps, and fragmentation are retained where filesystem records provide them. Carved files get generated names. No deletion dates are inferred.

Recovery can run while scanning. The worker keeps the original read-only source handle and opens independent cursors for previews/exports. It checks source changes and validates exports, uses portable filenames, and never replaces an existing file. Raw-source size, sector geometry, and backing disks are queried on that handle, without requiring file metadata or resolving the source drive letter again. Raw-source exports require a destination on a verified different physical disk, including when two drive letters share a disk. Unknown destinations and incomplete disk-extent responses are rejected. Image exports can use another folder on the same drive without changing the image. Stop recovery finishes the current file; closing waits for that file before stopping. Scan cancellation keeps found results. Results remain in memory until another scan, worker loss, or exit. A worker restart clears old results and isolates old messages; rapid picture selection uses one decoder with only the latest preview queued.

The F# bridge communicates over local JSON lines with an embedded Windows Rust worker built by `Prepare-Recovery.ps1`. No Reclaim installation, network service, or Rust runtime is needed by users. Sources are read-only, but Plumbyr still extracts its payload and writes its normal logs under LocalApplicationData. Run from another drive and avoid further writes to a drive containing lost data. Most carved formats have a 64 MiB limit; large ZIPs and filesystem-reconstructed files have separate limits. Recovery cannot restore overwritten or TRIM-erased data. See [the integration notes](plumbyr/Reclaim/README.md).

### Diagnostics, drivers, and Command Center

`SystemInfo` provides Windows and hardware information through WMI and DXGI. Command Center displays activity, runs diagnostics, accepts PowerShell/CMD commands, and exports terminal logs.

Driver tools scan installed signed drivers, export a CSV inventory, check/install driver updates through Windows Update, back up third-party drivers with DISM, and install local INF packages with `pnputil`. Driver and optimization pages can request a system restore point.

### Windows optimization

The optimization service is an F# port of Swept's Windows 10/11 tweaks. Options cover telemetry, advertising, background applications, Game DVR, power plans, built-in application debloat, and related settings. It uses registry values and stock Windows tools.

The UI requests a restore point first by default. If creating one fails, the app logs the failure and continues; a restore point is not guaranteed. Review selected changes and their results in Command Center.

### Sleep controls and reports

Goodnight's features are available in five tabs: **Sleep settings**, **Diagnostics**, **System health**, **Sleep mode**, and **QuickOff sleep**. Enter `sleep` or `goodnight` in Command Center to open this section.

Monitoring starts paused on a normal launch. **Start monitoring** applies the saved settings. The power button is always an allowed wake source; optional sources include lid/display, keyboard, mouse, touchpad, power-cable changes, and other wake events. Settings also cover wake logging, a lid-close delay, application suspension, and device control.

Goodnight's return-to-sleep behavior turns the display off. Windows and the hardware determine the resulting sleep state. The sleep-mode page reports the states Windows exposes; it does not force Modern Standby or change firmware capabilities.

**QuickOff sleep** preserves Goodnight's compatibility command and may hibernate. Delays are 5, 15, or 30 seconds. **Cancel sleep** is available across sleep tabs and in the sidebar while browsing other sections. Closing cancels a pending countdown. Monitoring pauses before dispatch and stays paused after return or failure; saved settings are kept.

Diagnostic collection includes power settings, wake sources, devices, recent events, a sleep study where supported, a 60-second energy analysis, and a console transcript. Use **Cancel diagnostics** to stop collection; partial reports are kept. Closing during collection stops the collector and its child processes, then closes automatically after cleanup.

**Check Windows** runs DISM CheckHealth/ScanHealth and SFC verification without repairs. **Repair Windows** asks for confirmation before running DISM RestoreHealth and SFC `/scannow`, which can replace Windows files, download repair content, and require a restart. Tools pause monitoring and cannot overlap QuickOff. Windows health tools and a dispatched power command must finish before normal exit.

Enabling sign-in startup creates the `PlumbyrSleep` scheduled task. It opens Plumbyr with `--sleep-monitor` to start monitoring. Reports and settings use the paths listed under [Payloads and user data](#payloads-and-user-data).

#### Sleep implementation and recovery

`SleepController` owns monitoring state, cancellable countdown/lid/wake timers, tool exclusion, and pause/close recovery. `ISleepPlatform` isolates Windows effects for tests. `SleepPlatform` serializes Kernel-Power events and native lid/suspend callbacks, launches embedded helpers, and retains process handles and changed device IDs for restoration.

Application and device controls are opt-in. Changes are restored on resume, display-on, Pause, settings changes, and normal exit. Only current-session, noncritical applications outside Windows are considered; Plumbyr, its ancestors/children, Windows shells, and its recovery helper are excluded. Only devices that were enabled are changed, and disabling is not persisted across reboot. A separate journal-based helper attempts restoration after unexpected exit.

`SleepWindowLifetime` requests collector cancellation on Close and retries window closure after cleanup finishes. Process execution runs off the UI thread, output-pipe draining is bounded, and report text is batched to keep Close/Cancel responsive. Actual sleep and wake transitions require manual validation on the target hardware.

## Verification

Run these checks from the repository root. The fixtures exercise analysis, UI behavior, and lifecycle handling without running Windows repairs or putting the host to sleep.

### Sleep controls and bundled helpers

```powershell
dotnet run --project tests/SleepSmoke/SleepSmoke.fsproj -c Release
./tests/GoodnightScriptsSmoke.ps1
```

The sleep check covers countdown timing/cancellation, duplicate requests, launch failures, tool exclusion, pause/close recovery, wake-source policy, lid-open cancellation, saved settings, and all five themed pages at two widths. It verifies Close/Cancel during collection, Close after completion/failure, and cleanup of real fixture child processes. Native Windows listeners are registered read-only; host applications and devices are not suspended or disabled.

Embedded helpers are resolved through `AppFiles.get`, compared with source, and checked for syntax, Check/Repair command plans, and empty-journal recovery. The standalone helper script can also run these safe checks against the source scripts. Rendered previews are written to `.build/sleep-*.png`.

### Space map

```powershell
dotnet fsi tests/SpaceScannerSmoke.fsx
dotnet run --project tests/SpaceMapUiSmoke/SpaceMapUiSmoke.fsproj -c Release
```

Scanner fixtures cover nested/deep totals, hidden/system files, empty locations, the top 40 files, links, cancellation, and read-only behavior. The headless UI check renders the page and verifies folder selection, drill-down, breadcrumbs, **Up**, and cancellation. Its preview is written to `.build/space-map-ui.png`.

### Recovery

```powershell
cargo test --locked --manifest-path plumbyr/Reclaim/Cargo.toml --target-dir .build/recovery
dotnet run --project tests/RecoverySmoke/RecoverySmoke.fsproj -c Release
```

The retained engine tests exercise allocation maps, fragmented/deleted files, corruption, read-only aligned reads, independent cursors, previews, no-clobber exports, incomplete extent lists, pinned source disks, and same-physical-disk rejection. The F# smoke check uses the actual embedded worker with synthetic images, including a formatted ext4 deletion fixture. It verifies byte-exact exports, source hashes, unsupported-map errors, native source/image selection, filtering, persistent selection, previews, cancellation, and closing during recovery. It also exports/previews during a running scan, forcibly terminates a private fixture worker, verifies pending requests fail, restarts and rescans, and checks that the UI clears stale results. A separate controlled protocol fixture streams 5,000 new candidates while checking visible row identity, selection, keyboard focus, scroll position, sorted insertion, and status updates. No host drive is scanned or written. Source discovery and destination classification only query host drive metadata. Rendered pages are saved as `.build/recovery-*.png` at two widths. Live raw-device access, USB removal, and real NTFS/FAT/exFAT recovery still require the disposable-drive validation described in [the integration notes](plumbyr/Reclaim/README.md).

### Browser bridge and portable executable

After publishing:

```powershell
dotnet fsi tests/KuduBridgeSmoke.fsx
```

The bridge check covers fixture measurements, missing dependencies, cancellation, timeouts, output limits, and worker errors using the embedded runtime.

From an administrator PowerShell:

```powershell
./tests/SingleFileSmoke.ps1
```

The portable check copies only the EXE into a fresh fixture directory, clears PATH, disables external .NET lookup, verifies browser analysis, and removes its own fixture files. Browser fixture paths follow the child's `LOCALAPPDATA` environment variable. The extracted application payload uses Windows' LocalApplicationData known folder, which may differ; the check does not guess that path or assume only one extracted build exists.

## Release pipeline

[release.yml](.github/workflows/release.yml) builds on a GitHub Windows runner, checks the sleep controls and portable executable, builds an Inno Setup installer, and publishes:

- `Plumbyr-<version>-setup.exe` — installer for Program Files and the Start menu, with an uninstaller.
- `Plumbyr-<version>-portable.exe` — self-contained executable.
- `SHA256SUMS.txt` — checksums for the executables.

Trigger a release by pushing a version tag or running **Release** from the Actions tab:

```powershell
git tag v1.0.3
git push origin v1.0.3
```

The installer definition is [installer/Plumbyr.iss](installer/Plumbyr.iss). JavaScript actions in the workflow declare Node 24; this action runtime is separate from the application runtime pinned in `scripts/node-runtime.json`. Build steps use Windows PowerShell where needed to avoid inheriting PowerShell 7 module paths into Windows PowerShell helpers.

## Third-party code and licenses

Plumbyr is licensed under [GPL-3.0-or-later](LICENSE). Retained third-party notices remain with their code:

- **Kudu:** only the TypeScript modules and browser definitions used for discovery are retained. Its MIT notice is in [plumbyr/Kudu/vendor/LICENSE](plumbyr/Kudu/vendor/LICENSE).
- **Swept:** `OptimizeService.fs` adapts `cleaner_optimize.py` from [Swept](https://github.com/vladcodex/swept), MIT, copyright 2026 vladcodex. The notice is preserved at the top of [OptimizeService.fs](plumbyr/OptimizeService.fs).
- **Roomy:** `SpaceScanner` and `SpaceMapView` adapt Roomy's `SpaceScanner` and `SpaceMapVM`, MIT, copyright 2026 krezio. The notice is preserved in [SpaceScanner.fs](plumbyr/SpaceScanner.fs) and embedded in the assembly.
- **Reclaim:** the Windows recovery engine and project-owned fixtures are retained from Reclaim 0.2.4, MIT, copyright 2026 Reclaim contributors. [Provenance and changes](plumbyr/Reclaim/README.md); [license](plumbyr/Reclaim/LICENSE), also embedded in the payload. Sources and Recovered files are implemented in F#/Avalonia.
- **Goodnight and QuickOff:** helper provenance and integration notes are in [plumbyr/Goodnight/README.md](plumbyr/Goodnight/README.md). QuickOff's MIT notice is in [QuickOff-LICENSE.txt](plumbyr/Goodnight/QuickOff-LICENSE.txt) and embedded with the helpers.
- **Node:** the license and dependency notices are embedded from the checksum-verified official archive prepared by `Prepare-Node.ps1`.
