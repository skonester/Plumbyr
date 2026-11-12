# Goodnight integration

Ported from the sibling `Goodnight-sleep` repository, revision `05afe45`.

- `SleepDebug.ps1` retains Goodnight's comprehensive sleep/power/device/event
  collector and transcript support.
- `SystemHealth.ps1` retains the separate Check and Repair plans, with Plumbyr's
  report directory as its default. The application always supplies `-OutDir`.
- `SleepRecovery.ps1` is Plumbyr's recovery watchdog for opt-in process/device
  controls. It only resumes process IDs with matching start times and reenables
  device IDs recorded by Plumbyr.
- `QuickOff-LICENSE.txt` preserves the MIT notice for QuickOff compatibility.
  The command remains `rundll32.exe powrprof.dll,SetSuspendState 0,1,0`, which can
  hibernate. A successful command exit does not confirm sleep or resume.

The F# implementation lives in `SleepService.fs`, `SleepPlatform.fs`, and
`SleepView.fs`. The scripts and QuickOff notice are embedded and materialized by
`AppFiles`, so the portable executable needs no companion files. Runtime reports
and settings belong under `%LOCALAPPDATA%\Plumbyr`, outside the extracted payload.

Goodnight's display-off return-to-sleep behavior is preserved. Plumbyr restores
applications and devices on resume, display-on, Pause, settings changes, and
normal exit. The independent recovery helper also attempts restoration after
unexpected exit. The power button is always an allowed wake source. Monitoring
does not run after Plumbyr exits; optional sign-in startup explicitly starts it
with `--sleep-monitor`.

Automated checks use an injected platform and never invoke sleep, disable real
devices, suspend host applications, or run DISM/SFC. Actual power transitions
require manual validation on the target hardware.
