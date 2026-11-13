// Plumbyr's local JSON-lines host for the MIT-licensed Reclaim engine.
// Source handles are read-only. Recovery uses the original authorized handle,
// checked filenames, physical-disk destination checks and no-clobber exports.
mod drives;
mod preview;
use anyhow::{Context, bail};
use base64::Engine;
use serde_json::{Value, json};
use std::{
    io::{self, BufRead, Write},
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
    },
    time::Instant,
};

fn explain(template: &str, value: String) -> String {
    template.replace("{0}", &value)
}
fn emit(value: Value) {
    let stdout = io::stdout();
    let mut output = stdout.lock();
    if serde_json::to_writer(&mut output, &value).is_ok() {
        let _ = output.write_all(b"\n");
        let _ = output.flush();
    }
}
fn reply(id: u64, result: anyhow::Result<Value>) {
    match result {
        Ok(data) => emit(json!({"id": id, "data": data})),
        Err(e) => emit(json!({"id": id, "error": format!("{e:#}")})),
    }
}
struct Session {
    file: Arc<std::fs::File>,
    size: u64,
    alignment: usize,
    stamp: Option<(u64, Option<std::time::SystemTime>)>,
    device: Option<drives::DeviceInfo>,
    found: Mutex<Vec<reclaim::Found>>,
}
fn stamp(file: &std::fs::File) -> io::Result<(u64, Option<std::time::SystemTime>)> {
    let m = file.metadata()?;
    Ok((m.len(), m.modified().ok()))
}
fn check_source(session: &Session) -> anyhow::Result<()> {
    if let Some(expected) = &session.device {
        if drives::device_info(&session.file, expected.physical).as_ref() != Some(expected) {
            bail!("The source device changed or disconnected. Scan it again before recovering.");
        }
    } else if Some(stamp(&session.file)?) != session.stamp {
        bail!("Source changed since scanning. Scan it again before recovering.");
    }
    Ok(())
}
fn destination(session: &Session, folder: &std::path::Path) -> anyhow::Result<()> {
    if !folder.is_dir() {
        bail!("Choose an existing recovery folder.");
    }
    let target = session
        .device
        .as_ref()
        .map_or(drives::Destination::Separate, |d| {
            drives::destination_for_device(d, folder)
        });
    match target {
        drives::Destination::Separate => Ok(()),
        drives::Destination::SameDrive => bail!(
            "Choose a folder on a different physical drive. Saving here could overwrite files you are trying to recover."
        ),
        drives::Destination::Unknown => bail!(
            "The destination's physical drive could not be verified. Choose a folder on another local drive, such as a USB drive."
        ),
    }
}
fn found(session: &Session, index: usize) -> anyhow::Result<reclaim::Found> {
    session
        .found
        .lock()
        .map_err(|_| anyhow::anyhow!("Results unavailable"))?
        .get(index)
        .cloned()
        .context("This result is no longer available.")
}
fn run() -> anyhow::Result<()> {
    let cancel = Arc::new(AtomicBool::new(false));
    let export_cancel = Arc::new(AtomicBool::new(false));
    let scanning = Arc::new(AtomicBool::new(false));
    let exporting = Arc::new(AtomicBool::new(false));
    let previewing = Arc::new(AtomicBool::new(false));
    let mut session: Option<Arc<Session>> = None;
    for line in io::stdin().lock().lines() {
        let line = line?;
        if line.len() > 2 * 1024 * 1024 {
            bail!("Command too large");
        }
        let command: Value = match serde_json::from_str(&line) {
            Ok(c) => c,
            Err(_) => continue,
        };
        let id = command["id"].as_u64().unwrap_or(0);
        let result = (|| -> anyhow::Result<Option<Value>> {
            match command["cmd"].as_str().unwrap_or("") {
                "discover" => {
                    let rows = drives::discover().map_err(anyhow::Error::msg)?;
                    Ok(Some(json!(rows.into_iter().map(|d| json!({"path": d.path, "name": d.name, "nameIsMessage": d.name_is_message, "phone": d.phone, "size": d.size, "kind": d.kind, "location": d.location, "mounted": d.mounted, "encrypted": d.encrypted, "sectorSize": d.sector_size})).collect::<Vec<_>>())))
                }
                "cancel" => {
                    cancel.store(true, Ordering::Relaxed);
                    Ok(Some(json!({})))
                }
                "cancelRecovery" => {
                    export_cancel.store(true, Ordering::Relaxed);
                    Ok(Some(json!({})))
                }
                "scan" => {
                    if scanning.load(Ordering::Acquire) || exporting.load(Ordering::Acquire) {
                        bail!("An operation is already running.");
                    }
                    let path =
                        PathBuf::from(command["path"].as_str().context("Source path required")?);
                    let device = path.to_string_lossy().starts_with(r"\\.\");
                    let mut physical = false;
                    // Device size/alignment comes from fresh discovery, never from the UI.
                    if device {
                        let drive = drives::discover()
                            .map_err(anyhow::Error::msg)?
                            .into_iter()
                            .find(|d| d.path == path)
                            .context("This drive is no longer available. Refresh Sources.")?;
                        if drive.encrypted {
                            bail!("Unlock this volume before scanning.");
                        }
                        physical = drive.kind == "Entire drive";
                    }
                    let file = Arc::new(drives::open_source(&path).context("Could not open source read-only. Unlock BitLocker volumes and run Plumbyr as administrator.")?);
                    let device = if device {
                        Some(drives::device_info(&file, physical).context("Could not verify the source device's size, sector geometry and backing disk. Refresh Sources and try again.")?)
                    } else {
                        None
                    };
                    let image_stamp = if device.is_none() {
                        Some(stamp(&file)?)
                    } else {
                        None
                    };
                    let (size, alignment) = device.as_ref().map_or_else(
                        || (image_stamp.as_ref().unwrap().0, 1),
                        |d| (d.size, d.alignment),
                    );
                    let s = Arc::new(Session {
                        stamp: image_stamp,
                        device,
                        file,
                        size,
                        alignment,
                        found: Mutex::new(Vec::new()),
                    });
                    let mut input =
                        reclaim::SourceReader::new(s.file.try_clone()?, alignment, Some(size))?;
                    session = Some(s.clone());
                    cancel.store(false, Ordering::Relaxed);
                    scanning.store(true, Ordering::Release);
                    let (cancel, scanning) = (cancel.clone(), scanning.clone());
                    let unused = command["unusedOnly"].as_bool().unwrap_or(true);
                    std::thread::spawn(move || {
                        let mut last = Instant::now();
                        let mut progress_total = size;
                        let event = |e| match e {
                            reclaim::Event::Found(f) => {
                                let mut files = s.found.lock().unwrap();
                                let index = files.len();
                                let metadata = f.ntfs.as_ref().map(|n| json!({"created": n.created, "modified": n.modified, "accessed": n.accessed, "recordChanged": n.record_changed, "record": n.record_number}));
                                emit(
                                    json!({"event": "found", "operation": id, "index": index, "name": f.recovery_name(index + 1), "kind": f.kind, "size": f.size, "offset": f.offset, "category": reclaim::category_index(f.kind), "validation": f.validation.label(), "metadata": metadata}),
                                );
                                files.push(f);
                            }
                            reclaim::Event::Progress(bytes) => {
                                if last.elapsed().as_millis() >= 100 {
                                    emit(
                                        json!({"event": "progress", "operation": id, "bytes": bytes, "total": progress_total}),
                                    );
                                    last = Instant::now();
                                }
                            }
                            reclaim::Event::UnusedSpace { bytes, filesystem } => {
                                progress_total = bytes;
                                emit(
                                    json!({"event": "unusedSpace", "operation": id, "bytes": bytes, "filesystem": filesystem}),
                                );
                            }
                            reclaim::Event::Unreadable { offset, bytes } => emit(
                                json!({"event": "unreadable", "operation": id, "offset": offset, "bytes": bytes}),
                            ),
                            reclaim::Event::Checking { .. } => {}
                        };
                        let result = if unused {
                            reclaim::scan_unused_reader(&mut input, size, &cancel, event)
                        } else {
                            reclaim::scan_reader(&mut input, &cancel, event)
                        };
                        scanning.store(false, Ordering::Release);
                        reply(
                            id,
                            result
                                .map(|_| json!({"cancelled": cancel.load(Ordering::Relaxed)}))
                                .map_err(Into::into),
                        );
                    });
                    Ok(None)
                }
                "preview" => {
                    let s = session.as_ref().context("Scan a source first")?.clone();
                    let f = found(
                        &s,
                        command["index"].as_u64().context("Result required")? as usize,
                    )?;
                    if !preview::supported(f.kind) {
                        bail!(
                            "Preview is available for pictures. Recover this file to inspect its contents."
                        );
                    }
                    if previewing
                        .compare_exchange(false, true, Ordering::AcqRel, Ordering::Acquire)
                        .is_err()
                    {
                        bail!("A picture preview is already loading. Try again when it finishes.");
                    }
                    let previewing = previewing.clone();
                    std::thread::spawn(move || {
                        let result = (|| {
                            check_source(&s)?;
                            let image =
                                preview::load(&f, Some(s.file.clone()), s.alignment, Some(s.size))?;
                            let mut png = io::Cursor::new(Vec::new());
                            image.write_to(&mut png, image::ImageFormat::Png)?;
                            Ok(
                                json!({"png": base64::engine::general_purpose::STANDARD.encode(png.into_inner())}),
                            )
                        })();
                        previewing.store(false, Ordering::Release);
                        reply(id, result)
                    });
                    Ok(None)
                }
                "recover" => {
                    if exporting.load(Ordering::Acquire) {
                        bail!("Recovery is already running.");
                    }
                    let s = session.as_ref().context("Scan a source first")?.clone();
                    let folder = PathBuf::from(
                        command["folder"]
                            .as_str()
                            .context("Recovery folder required")?,
                    );
                    destination(&s, &folder)?;
                    let files = command["indices"]
                        .as_array()
                        .context("Select files to recover")?
                        .iter()
                        .map(|i| {
                            let index = i.as_u64().context("Invalid result")? as usize;
                            Ok((index, found(&s, index)?))
                        })
                        .collect::<anyhow::Result<Vec<_>>>()?;
                    if files.is_empty() {
                        bail!("Select files to recover");
                    }
                    export_cancel.store(false, Ordering::Relaxed);
                    exporting.store(true, Ordering::Release);
                    let (export_cancel, exporting) = (export_cancel.clone(), exporting.clone());
                    std::thread::spawn(move || {
                        let result = (|| -> anyhow::Result<Value> {
                            check_source(&s)?;
                            let mut input = reclaim::SourceReader::new(
                                preview::Input::new(s.file.clone())?,
                                s.alignment,
                                Some(s.size),
                            )?;
                            let (mut saved, mut failed) = (0, 0);
                            for (index, f) in files {
                                if export_cancel.load(Ordering::Relaxed) {
                                    break;
                                }
                                destination(&s, &folder)?;
                                check_source(&s)?;
                                match reclaim::recover_unique(&mut input, &folder, &f, index + 1) {
                                    Ok(path) => {
                                        saved += 1;
                                        emit(
                                            json!({"event": "recovered", "index": index, "path": path}),
                                        );
                                    }
                                    Err(e) => {
                                        failed += 1;
                                        emit(
                                            json!({"event": "recoveryFailed", "index": index, "error": e.to_string()}),
                                        );
                                    }
                                }
                            }
                            Ok(
                                json!({"saved": saved, "failed": failed, "cancelled": export_cancel.load(Ordering::Relaxed)}),
                            )
                        })();
                        exporting.store(false, Ordering::Release);
                        reply(id, result);
                    });
                    Ok(None)
                }
                _ => bail!("Unknown recovery command"),
            }
        })();
        match result {
            Ok(Some(data)) => reply(id, Ok(data)),
            Ok(None) => {}
            Err(e) => reply(id, Err(e)),
        }
    }
    cancel.store(true, Ordering::Relaxed);
    export_cancel.store(true, Ordering::Relaxed);
    Ok(())
}
fn main() {
    if let Err(e) = run() {
        eprintln!("{e:#}");
    }
}
