//! NTFS 3-style deleted-record recovery.
//!
//! A deleted base record keeps its name, size and, for larger files, the list
//! of cluster runs that held the data. Resident data is taken from the record
//! itself. Cluster runs are only used when the caller knows the volume
//! geometry (an unused-space scan); they are read from the record, never guessed.
use crate::{Found, NtfsMetadata, Validation};
use std::{
    io::{self, Read},
    sync::{
        Arc,
        atomic::{AtomicBool, Ordering},
    },
};

const MAX_RECORD: usize = 16 * 1024;
fn short(bytes: &[u8], at: usize) -> u16 {
    crate::bytes::u16_le(bytes, at)
}
fn long(bytes: &[u8], at: usize) -> u32 {
    crate::bytes::u32_le(bytes, at)
}

// FILETIME is 100-nanosecond intervals since 1601-01-01 UTC. Zero means
// unavailable here; reject values beyond year 9999 rather than invent a date.
fn filetime(bytes: &[u8], at: usize) -> Option<i64> {
    let ticks = u64::from_le_bytes(bytes.get(at..at + 8)?.try_into().ok()?);
    if ticks == 0 {
        return None;
    }
    let seconds = i64::try_from(ticks / 10_000_000)
        .ok()?
        .checked_sub(11_644_473_600)?;
    (seconds <= 253_402_300_799).then_some(seconds)
}

pub(crate) fn read_record(
    input: &mut impl Read,
    offset: u64,
    cancel: &AtomicBool,
) -> io::Result<Option<(Found, usize)>> {
    let mut header = [0u8; 48];
    if let Err(e) = input.read_exact(&mut header) {
        return if e.kind() == io::ErrorKind::UnexpectedEof {
            Ok(None)
        } else {
            Err(e)
        };
    }
    let size = long(&header, 28) as usize;
    if &header[..4] != b"FILE" || !size.is_power_of_two() || !(1024..=MAX_RECORD).contains(&size) {
        return Ok(None);
    }
    // audit-allow(DOS-ALLOC): size is a power of two within 1 KiB..=MAX_RECORD, checked above.
    let mut record = vec![0; size];
    record[..48].copy_from_slice(&header);
    if let Err(e) = input.read_exact(&mut record[48..]) {
        return if e.kind() == io::ErrorKind::UnexpectedEof {
            Ok(None)
        } else {
            Err(e)
        };
    }
    Ok(parse_record(record, offset, None, cancel).map(|found| (found, size)))
}

/// Volume geometry needed to turn a deleted record's cluster runs into bytes.
#[derive(Clone, Copy)]
pub(crate) struct Geometry {
    pub cluster_size: u64,
    pub clusters: u64,
}

/// Most cluster runs accepted for one deleted file.
const MAX_RUNS: usize = 4096;

/// A deleted file from a raw MFT record of a mapped volume: resident data, or
/// nonresident data as byte extents of that volume.
pub(crate) fn deleted_file(
    record: Vec<u8>,
    geometry: Geometry,
    cancel: &AtomicBool,
) -> Option<Found> {
    parse_record(record, 0, Some(geometry), cancel)
}

fn little(bytes: &[u8]) -> u64 {
    bytes
        .iter()
        .rev()
        .fold(0, |value, byte| (value << 8) | u64::from(*byte))
}

/// Byte extents of an unnamed, uncompressed, fully initialized nonresident
/// $DATA attribute, or `None` when it cannot be read exactly.
fn extents(attr: &[u8], geometry: Geometry) -> Option<(u64, Vec<(u64, u64)>)> {
    // Flags: compressed, encrypted or sparse data cannot be copied as stored.
    if attr.len() < 64 || short(attr, 12) != 0 || crate::bytes::u64_le(attr, 16) != 0 {
        return None;
    }
    let last = crate::bytes::u64_le(attr, 24);
    let size = crate::bytes::u64_le(attr, 48);
    let initialized = crate::bytes::u64_le(attr, 56);
    if size == 0 || initialized != size {
        return None;
    }
    let mut p = usize::from(short(attr, 32));
    let mut runs: Vec<(u64, u64)> = Vec::new();
    let mut lcn = 0i128;
    let mut covered = 0u64;
    loop {
        let tag = *attr.get(p)?;
        p = p.checked_add(1)?;
        if tag == 0 {
            break;
        }
        let n = usize::from(tag & 15);
        let o = usize::from(tag >> 4);
        // o == 0 is a sparse run: there is no stored data to recover.
        if n == 0 || n > 8 || o == 0 || o > 8 || runs.len() >= MAX_RUNS {
            return None;
        }
        let length = little(attr.get(p..p.checked_add(n)?)?);
        p = p.checked_add(n)?;
        let raw = attr.get(p..p.checked_add(o)?)?;
        let negative = raw.last().is_some_and(|b| b & 128 != 0);
        let value = i128::from(little(raw));
        let delta = if negative {
            value.checked_sub(1i128.checked_shl(u32::try_from(o.checked_mul(8)?).ok()?)?)?
        } else {
            value
        };
        p = p.checked_add(o)?;
        lcn = lcn.checked_add(delta)?;
        let start = u64::try_from(lcn).ok()?;
        if length == 0 || start.checked_add(length)? > geometry.clusters {
            return None;
        }
        covered = covered.checked_add(length)?;
        let bytes = length.checked_mul(geometry.cluster_size)?;
        let at = start.checked_mul(geometry.cluster_size)?;
        match runs.last_mut() {
            Some((previous, previous_len)) if previous.checked_add(*previous_len) == Some(at) => {
                *previous_len = previous_len.checked_add(bytes)?;
            }
            _ => runs.push((at, bytes)),
        }
    }
    if runs.is_empty()
        || last.checked_add(1) != Some(covered)
        || covered.checked_mul(geometry.cluster_size)? < size
    {
        return None;
    }
    Some((size, runs))
}

fn parse_record(
    mut record: Vec<u8>,
    offset: u64,
    geometry: Option<Geometry>,
    cancel: &AtomicBool,
) -> Option<Found> {
    if cancel.load(Ordering::Relaxed)
        || short(&record, 16) == 0
        || short(&record, 22) != 0
        || record[32..40] != [0; 8]
    {
        return None;
    }
    let used = long(&record, 24) as usize;
    let first = short(&record, 20) as usize;
    let usa = short(&record, 4) as usize;
    let count = short(&record, 6) as usize;
    if count < 2 || !record.len().is_multiple_of(count - 1) {
        return None;
    }
    let sector = record.len() / (count - 1);
    // NTFS 3.1 records keep their update array at 48; the older 3.0 layout,
    // still written by Linux's ntfs3 driver, keeps it at 42 and has no
    // record-number field.
    if !matches!(sector, 512 | 4096)
        || usa < 42
        || usa + count * 2 > first
        || first < 48
        || !first.is_multiple_of(8)
        || used > record.len()
        || used < first + 4
    {
        return None;
    }
    let sequence = short(&record, usa);
    if sequence == 0 {
        return None;
    }
    // Reject torn records before restoring the original sector-tail bytes.
    for index in 1..count {
        let tail = index * sector - 2;
        if short(&record, tail) != sequence {
            return None;
        }
        let original = [record[usa + index * 2], record[usa + index * 2 + 1]];
        record[tail..tail + 2].copy_from_slice(&original);
    }
    let mut at = first;
    let mut name: Option<(u8, String)> = None;
    let mut data = None;
    let mut nonresident = None;
    let mut terminated = false;
    let mut standard_info = false;
    let mut metadata = NtfsMetadata {
        record_number: if usa >= 48 { long(&record, 44) } else { 0 },
        sequence_number: short(&record, 16),
        ..Default::default()
    };
    while at + 4 <= used {
        let kind = long(&record, at);
        if kind == u32::MAX {
            terminated = true;
            break;
        }
        if at + 16 > used {
            return None;
        }
        let length = long(&record, at + 4) as usize;
        if length < 24
            || !length.is_multiple_of(8)
            || at.checked_add(length)? > used
            || !matches!(record[at + 8], 0 | 1)
        {
            return None;
        }
        let attr = &record[at..at + length];
        // Attribute lists and external data require full run-list reconstruction.
        if kind == 0x20 {
            return None;
        }
        if !matches!(
            kind,
            0x10 | 0x30
                | 0x40
                | 0x50
                | 0x60
                | 0x70
                | 0x80
                | 0x90
                | 0xa0
                | 0xb0
                | 0xc0
                | 0xd0
                | 0xe0
                | 0xf0
                | 0x100
        ) {
            return None;
        }
        if record[at + 8] == 0 && matches!(kind, 0x10 | 0x30 | 0x80) {
            if short(attr, 12) != 0 {
                return None;
            }
            let value_at = short(attr, 20) as usize;
            let value_len = long(attr, 16) as usize;
            if value_at < 24 || value_at.checked_add(value_len)? > length {
                return None;
            }
            let value = &attr[value_at..value_at + value_len];
            match kind {
                0x10 => {
                    if value.len() < 48 {
                        return None;
                    }
                    if standard_info {
                        return None;
                    }
                    standard_info = true;
                    metadata.created = filetime(value, 0);
                    metadata.modified = filetime(value, 8);
                    metadata.record_changed = filetime(value, 16);
                    metadata.accessed = filetime(value, 24);
                }
                0x30 => {
                    if attr[9] != 0 || value.len() < 66 {
                        return None;
                    }
                    let characters = value[64] as usize;
                    let namespace = value[65];
                    if characters == 0 || namespace > 3 || value.len() != 66 + characters * 2 {
                        return None;
                    }
                    let utf16: Vec<_> = value[66..]
                        .as_chunks::<2>()
                        .0
                        .iter()
                        .map(|b| u16::from_le_bytes(*b))
                        .collect();
                    let candidate = String::from_utf16(&utf16).ok()?;
                    if candidate
                        .chars()
                        .any(|c| c.is_control() || matches!(c, '/' | '\\'))
                        || matches!(candidate.as_str(), "." | "..")
                    {
                        return None;
                    }
                    let rank = match namespace {
                        1 | 3 => 3,
                        0 => 2,
                        _ => 1,
                    };
                    if name.as_ref().is_none_or(|(old, _)| rank > *old) {
                        name = Some((rank, candidate));
                    }
                }
                0x80 if attr[9] == 0 => {
                    if data.is_some() || nonresident.is_some() {
                        return None;
                    }
                    data = Some(Arc::<[u8]>::from(value));
                }
                _ => {}
            }
        } else if kind == 0x80 && attr[9] == 0 {
            let geometry = geometry?;
            if data.is_some() || nonresident.is_some() {
                return None;
            }
            nonresident = Some(extents(attr, geometry)?);
        }
        at += length;
    }
    if !terminated || !standard_info {
        return None;
    }
    let name = name.map(|(_, name)| name);
    if let Some((size, runs)) = nonresident {
        let (offset, _) = *runs.first()?;
        return Some(Found {
            offset,
            size,
            // Linux's ntfs3 driver removes the name from a deleted record but
            // keeps its data runs; the type then comes from the file's bytes.
            kind: name.as_deref().map_or("bin", extension_kind),
            selected: true,
            // The record describes the data exactly; its bytes are not checked here.
            validation: Validation::MetadataChecked,
            original_name: name,
            resident_data: None,
            ntfs: Some(metadata),
            extents: Some(Arc::from(runs)),
        });
    }
    let name = name?;
    let data = data?;
    let kind = if data.starts_with(b"SQLite format 3\0") {
        "sqlite"
    } else {
        extension_kind(&name)
    };
    let validation = match crate::validation::validate(kind, &data, cancel)? {
        Validation::ImageDecoded => Validation::ImageDecoded,
        Validation::StructureChecked => Validation::StructureChecked,
        Validation::MetadataChecked | Validation::Unverified => Validation::MetadataChecked,
    };
    Some(Found {
        offset,
        size: data.len() as u64,
        kind,
        selected: true,
        validation,
        original_name: Some(name),
        resident_data: Some(data),
        ntfs: Some(metadata),
        extents: None,
    })
}

pub(crate) fn extension_kind(name: &str) -> &'static str {
    match name
        .rsplit_once('.')
        .map(|(_, ext)| ext.to_ascii_lowercase())
        .as_deref()
    {
        Some("jpg" | "jpeg") => "jpg",
        Some("png") => "png",
        Some("gif") => "gif",
        Some("bmp") => "bmp",
        Some("webp") => "webp",
        Some("ico") => "ico",
        Some("pdf") => "pdf",
        Some("sqlite" | "sqlite3") => "sqlite",
        Some("ogg" | "oga") => "ogg",
        Some("opus") => "opus",
        Some("ogv") => "ogv",
        Some("zip") => "zip",
        Some("docx") => "docx",
        Some("xlsx") => "xlsx",
        Some("pptx") => "pptx",
        Some("docm") => "docm",
        Some("xlsm") => "xlsm",
        Some("pptm") => "pptm",
        Some("odt") => "odt",
        Some("ods") => "ods",
        Some("odp") => "odp",
        Some("epub") => "epub",
        Some("txt") => "txt",
        Some("csv") => "csv",
        Some("json") => "json",
        Some("xml") => "xml",
        Some("md") => "md",
        Some("html" | "htm") => "html",
        Some("log") => "log",
        Some("wav") => "wav",
        Some("aiff" | "aif") => "aiff",
        Some("au") => "au",
        Some("avi") => "avi",
        Some("mp3") => "mp3",
        Some("mp4" | "m4v") => "mp4",
        Some("mov") => "mov",
        Some("3gp" | "3g2") => "3gp",
        Some("mkv") => "mkv",
        Some("webm") => "webm",
        Some("heic" | "heif") => "heic",
        Some("m4a") => "m4a",
        Some("jar") => "jar",
        Some("apk") => "apk",
        Some("exe") => "exe",
        Some("dll") => "dll",
        Some("elf") => "elf",
        Some("msi") => "msi",
        Some("msp") => "msp",
        Some("mst") => "mst",
        Some("deb") => "deb",
        Some("rpm") => "rpm",
        Some("7z") => "7z",
        Some("cab") => "cab",
        Some("rar") => "rar",
        Some("tar") => "tar",
        Some("qoi") => "qoi",
        Some("dex") => "dex",
        Some("mid" | "midi") => "mid",
        Some("swf") => "swf",
        Some("gz") => "gz",
        Some("xar") => "xar",
        Some("pkg") => "pkg",
        Some("ole") => "ole",

        _ => "bin",
    }
}
