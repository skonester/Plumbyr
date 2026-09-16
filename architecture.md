# Plumbyr Architecture

- Pattern: Avalonia desktop (F#) + embedded payload pipeline + JSON rule loader + Node worker bridge.
- Key modules: `RuleLoader` (JSON→CleanTarget), `ScanEngine` (analyze/clean), `KuduBridge` (Node worker), `SystemInfo` (WMI/DXGI), `DriverService` (WMI/COM), `AppFiles` (embedded resource extraction to `%LOCALAPPDATA%/Plumbyr/runtime`).
- Lifecycle: `AppFiles.initialize()` → `RuleLoader.loadAllRules()` → `ScanEngine.AnalyzeTargets()` → `CleanTargets` → `CleaningHistory` DB.
- Extension: add JSON to `win32rules/`, rebuild; hook `ScanEngine` events; extend `KuduBridge` settings.
