# Reclaim recovery engine

Retained from the local Reclaim 0.2.4 source snapshot, upstream
https://github.com/sudo-py-dev/reclaim, copyright 2026 Reclaim contributors,
under the MIT license in `LICENSE`. The license is embedded in Plumbyr's payload.
Test fixtures and the ten core integration-test suites are retained unchanged
from Reclaim and are project-owned data/code under the same license. They cover
format corruption, containers, large ZIPs, NTFS resident records, ext journals,
fragmented files, competing exports, and partial-read/disconnect cleanup.

Plumbyr implements Sources and Recovered files in F#/Avalonia. This directory
retains Reclaim's allocation maps, carving, validation, NTFS reconstruction,
aligned source reader, independent preview reader, Windows drive discovery,
physical-disk destination checks, and no-clobber recovery. The Rust worker
is embedded inside the portable EXE; users need no Rust installation.

Integration changes:

- Removed Slint, translations, updater, installer, and non-Windows discovery.
- `lib.rs` omits the translation/updater modules and uses the Rust 1.94-compatible
  `map_or_else` spelling in `recover_unique`.
- `drives.rs` is a Windows-only facade. `drives/windows.rs` substitutes an English
  formatter for one translated error. Source size, sector geometry, and backing
  disks come from the opened device handle; exports compare the pinned backing
  disks against the destination. Raw devices never require ordinary file metadata.
  Truncated extent lists fail closed. Tests cover source-disk pinning and same-volume
  rejection. `preview.rs` accepts the known source size to bound sector read-ahead.
- `main.rs` is Plumbyr's JSON-lines host. It owns read-only source handles and
  candidates. Commands refer to result IDs, never caller-provided byte ranges.
- The worker checks destination physical disks before export and each file,
  rejects unknown destinations, checks source changes, and supports cancellation
  between exports. Each file is validated and published without replacement.
- One image decoder runs at a time. The F# view coalesces rapid preview selection.
  Connection generations isolate messages and pending requests across worker
  failure/restart; lost candidate lists are cleared instead of reusing result IDs.
- Like Reclaim's UI, result updates are batched at 100 ms and applied incrementally.
  The F# view caches its matching/sorted candidates, keeps surviving row controls,
  and patches only changed properties or page entries. The streaming regression
  verifies row reuse, focus, scroll, selection, sorting, and status changes.
- Full-byte image scanning is available explicitly. The default searches only
  unused clusters and fails on unsupported/corrupt maps without falling back.

Build requires Windows x64, Rust 1.94 or later, and Visual C++ build tools.
`scripts/Prepare-Recovery.ps1` builds with the checked-in Cargo lockfile.
The release worker targets `x86_64-pc-windows-msvc` explicitly and links the C
runtime statically for portable deployment.
`cargo test --locked --manifest-path plumbyr/Reclaim/Cargo.toml --target-dir .build/recovery`
runs retained engine tests and Windows reader/destination tests from the repo root.

Protocol: one JSON object per line on stdin/stdout. Requests contain numeric `id`
and `cmd`; replies contain the same `id` and `data` or `error`. Commands are
`discover`, `scan` (`path`, `unusedOnly`), `cancel`, `preview` (`index`), `recover`
(`folder`, `indices`), and `cancelRecovery`. Unsolicited `event` objects report
found files, progress, unreadable ranges, and per-file recovery results. PNG
previews are bounded, decoded in the worker, and returned as base64.

Windows API references: [volume disk extents](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_volume_get_volume_disk_extents),
[device numbers](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-ioctl_storage_get_device_number),
[volume paths and junctions](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumepathnamew).

Automated tests use file images, synthetic allocation maps, and read-only host
metadata queries. Before a release intended for important recovery work, validate
on a disposable USB drive as administrator: delete known NTFS/FAT/exFAT files,
recover to a different physical disk, verify hashes, confirm same-disk rejection
through another drive letter/junction, then exercise removal during scanning and
recovery. Never use the only copy of valuable data for hardware validation.
