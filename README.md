# P.L.U.M.B.Y.R

**Plumbing Linked Universal Maintenance & Binary Yield Reclaimer**

Plumbyr is a Windows cleaner and diagnostics app written in F# with Avalonia. It is aimed at a CCleaner-style workflow: load maintenance rules, scan for reclaimable files, show clear size/count results, and let the user review what will be cleaned before running an action.

## Current Capabilities

- **Rule-based cleaner**: Loads JSON cleaning rules from the linked `win32rules` folder so targets can be updated without rewriting scanner logic.
- **FSharp.Json rule loading**: Uses typed F# models for rule parsing instead of ad hoc string handling.
- **Cleaner-style scan results**: Groups duplicate rule entries, detects file and folder sizes, and reports reclaimable bytes before cleanup.
- **Safe cleanup flow**: Allows selected targets to be analyzed or cleaned while logging skipped, missing, and protected paths.
- **System diagnostics**: Reports operating system, CPU, RAM, storage, display adapter, driver, and dedicated VRAM details using Windows APIs and DXGI instead of relying on `dxdiag` output.
- **Activity terminal**: Provides a command-center view for cleaner events, diagnostics reports, and optional Windows command output. The default prompt is `help`.
- **App branding**: Uses the Plumbyr app icon in the executable and displays the app image inside the window.
- **Structured logging**: Uses Serilog for application logs and debugging output.

## Tech Stack

- **Language**: F#
- **Runtime**: .NET 10
- **UI**: Avalonia 12 with Fluent styling
- **Rules**: JSON files linked from `win32rules`
- **JSON parsing**: FSharp.Json
- **Diagnostics**: System.Management and Vortice.DXGI
- **Logging**: Serilog

## Project Layout

```text
Cleaner/
+- plumbyr/
|  +- Program.fs          # Avalonia UI and app flow
|  +- RuleLoader.fs       # JSON rule loading
|  +- ScanEngine.fs       # Size detection, analysis, and cleanup
|  +- SystemInfo.fs       # Windows and hardware diagnostics
|  +- Types.fs            # Shared domain models
|  +- Assets/             # App icon and image assets
|  +- Plumbyr.fsproj
+- win32rules/            # JSON cleaner rule definitions
+- README.md
```

## Requirements

- Windows 10 or Windows 11
- .NET 10 SDK

## Build And Run

From the repository root:

```powershell
dotnet build plumbyr\Plumbyr.fsproj
dotnet run --project plumbyr\Plumbyr.fsproj
```

Or from the app folder:

```powershell
cd plumbyr
dotnet build
dotnet run
```

## Updating Cleaner Rules

The app links the JSON files from `win32rules` into the build output. To update cleaner coverage, edit or replace the rule files in that folder, then rebuild the app.

Rule loading is handled by `RuleLoader.fs`, which maps JSON entries into typed F# records before the scanner expands paths, checks existence, measures size, and groups duplicate entries for display.

## Notes

Plumbyr is still under active development. Treat cleanup actions with the same care as any system maintenance tool: scan first, review the selected targets, then clean. Not responsible for data loss.
