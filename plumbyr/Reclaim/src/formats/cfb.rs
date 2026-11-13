//! CFB allocation extent and root CLSID; application streams remain unverified.
use super::{CandidateEnd, Probe, le16, le32, need, span};
use std::{
    collections::HashSet,
    sync::atomic::{AtomicBool, Ordering},
};
const FREE: u32 = 0xffff_ffff;
const END: u32 = 0xffff_fffe;
const FAT: u32 = 0xffff_fffd;
const DIF: u32 = 0xffff_fffc;
fn sector(id: u32, size: usize) -> Result<usize, CandidateEnd> {
    span((u64::from(id) + 1) * size as u64, size as u64).map(|end| end - size)
}
pub(super) fn end(b: &[u8], cancel: &AtomicBool) -> Probe {
    need(b, 512)?;
    if !b.starts_with(b"\xd0\xcf\x11\xe0\xa1\xb1\x1a\xe1")
        || b[8..24] != [0; 16]
        || le16(b, 28) != 0xfffe
        || le16(b, 32) != 6
        || b[34..40] != [0; 6]
        || le32(b, 56) != 4096
    {
        return Err(CandidateEnd::Invalid);
    }
    let major = le16(b, 26);
    let shift = le16(b, 30);
    if !matches!((major, shift), (3, 9) | (4, 12)) || (major == 3 && le32(b, 40) != 0) {
        return Err(CandidateEnd::Invalid);
    }
    let size = 1usize << shift;
    need(b, size)?;
    if major == 4 && b[512..size].iter().any(|c| *c != 0) {
        return Err(CandidateEnd::Invalid);
    }
    let capacity = super::LIMIT / size - 1;
    let entries = size / 4;
    let count = le32(b, 44);
    let dif_count = le32(b, 72);
    if count == 0 || count > capacity.div_ceil(entries) || dif_count > count.div_ceil(entries - 1) {
        return Err(CandidateEnd::Invalid);
    }
    let mut fats = Vec::new();
    for p in (76..512).step_by(4) {
        let id = crate::bytes::u32_le(b, p);
        if id != FREE {
            fats.push(id);
        }
    }
    let mut difs = HashSet::new();
    let mut next = crate::bytes::u32_le(b, 68);
    for _ in 0..dif_count {
        if cancel.load(Ordering::Relaxed) || !difs.insert(next) {
            return Err(CandidateEnd::Invalid);
        }
        let start = sector(next, size)?;
        need(b, start + size)?;
        for p in (start..start + size - 4).step_by(4) {
            let id = crate::bytes::u32_le(b, p);
            if id != FREE {
                fats.push(id);
            }
        }
        next = crate::bytes::u32_le(b, start + size - 4);
    }
    if next != END || fats.len() != count {
        return Err(CandidateEnd::Invalid);
    }
    let unique: HashSet<_> = fats.iter().copied().collect();
    if unique.len() != count || unique.iter().any(|id| difs.contains(id)) {
        return Err(CandidateEnd::Invalid);
    }
    // Sector IDs are not validated yet, so reserve no more than the scanned
    // bytes could hold (one entry per four bytes), never count * entries.
    let mut fat = Vec::with_capacity(count.saturating_mul(entries).min(b.len() / 4));
    for id in &fats {
        if cancel.load(Ordering::Relaxed) {
            return Err(CandidateEnd::Invalid);
        }
        let start = sector(*id, size)?;
        need(b, start + size)?;
        for p in (start..start + size).step_by(4) {
            fat.push(crate::bytes::u32_le(b, p));
        }
    }
    let allocated = fat
        .iter()
        .rposition(|id| *id != FREE)
        .ok_or(CandidateEnd::Invalid)?
        + 1;
    if allocated > capacity {
        return Err(CandidateEnd::Invalid);
    }
    for (i, id) in fat[..allocated].iter().enumerate() {
        if *id < DIF && (*id as usize >= allocated || *id as usize == i) {
            return Err(CandidateEnd::Invalid);
        }
        if *id == FAT && !u32::try_from(i).is_ok_and(|i| unique.contains(&i))
            || *id == DIF && !u32::try_from(i).is_ok_and(|i| difs.contains(&i))
        {
            return Err(CandidateEnd::Invalid);
        }
    }
    if fats.iter().any(|id| fat.get(*id as usize) != Some(&FAT))
        || difs.iter().any(|id| fat.get(*id as usize) != Some(&DIF))
    {
        return Err(CandidateEnd::Invalid);
    }
    let end = span(size as u64, allocated as u64 * size as u64)?;
    need(b, end)?;
    let mut id = crate::bytes::u32_le(b, 48);
    let mut seen = HashSet::new();
    while id != END {
        if cancel.load(Ordering::Relaxed) || id as usize >= allocated || !seen.insert(id) {
            return Err(CandidateEnd::Invalid);
        }
        let at = sector(id, size)?;
        if seen.len() == 1 {
            if b[at + 66] != 5 || !(2..=64).contains(&le16(b, at + 64)) {
                return Err(CandidateEnd::Invalid);
            }
            let name: Vec<u16> = b[at..at + 22]
                .as_chunks::<2>()
                .0
                .iter()
                .map(|pair| u16::from_le_bytes(*pair))
                .collect();
            if name != "Root Entry\0".encode_utf16().collect::<Vec<_>>() {
                return Err(CandidateEnd::Invalid);
            }
        }
        id = fat[id as usize];
        if id >= DIF && id != END {
            return Err(CandidateEnd::Invalid);
        }
    }
    if seen.is_empty() || major == 4 && seen.len() != le32(b, 40) {
        return Err(CandidateEnd::Invalid);
    }
    Ok(end)
}
pub(super) fn kind(b: &[u8]) -> &'static str {
    let size = 1usize << le16(b, 30);
    let root = (le32(b, 48) + 1) * size;
    let guid = &b[root + 80..root + 96];
    if guid[4..] != [0, 0, 0, 0, 0xc0, 0, 0, 0, 0, 0, 0, 0x46] {
        return "ole";
    }
    match le32(guid, 0) {
        0x000c_1084 => "msi",
        0x000c_1086 => "msp",
        0x000c_1082 => "mst",
        _ => "ole",
    }
}
