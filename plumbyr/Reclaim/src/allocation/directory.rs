//! Deleted files named by FAT12/16/32 and exFAT directory entries.
//!
//! A deleted entry keeps the file's name (FAT loses the first letter of the
//! short name), its exact size and its first cluster. Files are rebuilt only
//! when every cluster they need is still marked free, so their data cannot
//! belong to a newer file. Directories are walked with bounded depth, entries
//! and bytes, and each directory cluster is visited once.
// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]

use std::collections::HashSet;
use std::sync::Arc;

use crate::bytes;
use crate::{Found, Validation};

/// Deepest directory nesting followed.
pub(super) const MAX_DEPTH: usize = 32;
/// Most directories read on one volume.
pub(super) const MAX_DIRECTORIES: usize = 20_000;
/// Most bytes of directory data read on one volume.
pub(super) const MAX_DIRECTORY_BYTES: u64 = 128 * 1024 * 1024;
/// Most deleted files reported from one volume.
pub(super) const MAX_FILES: usize = 50_000;
/// Longest cluster chain followed for one file or directory.
pub(super) const MAX_CHAIN: usize = 1 << 20;

/// Where the data heap starts and how big a cluster is.
#[derive(Clone, Copy)]
pub(super) struct Heap {
    pub start: u64,
    pub cluster_size: u64,
    /// Highest valid cluster number (clusters are numbered from 2).
    pub last: u64,
}
impl Heap {
    pub(super) fn offset(self, cluster: u64) -> Option<u64> {
        if cluster < 2 || cluster > self.last {
            return None;
        }
        cluster
            .checked_sub(2)?
            .checked_mul(self.cluster_size)?
            .checked_add(self.start)
    }
    /// Contiguous byte extents for a cluster list, merged where adjacent.
    pub(super) fn extents(self, clusters: &[u64]) -> Option<Vec<(u64, u64)>> {
        let mut extents: Vec<(u64, u64)> = Vec::new();
        for cluster in clusters {
            let at = self.offset(*cluster)?;
            match extents.last_mut() {
                Some((start, length)) if start.checked_add(*length) == Some(at) => {
                    *length = length.checked_add(self.cluster_size)?;
                }
                _ => extents.push((at, self.cluster_size)),
            }
        }
        Some(extents)
    }
    pub(super) fn cluster_count(self, size: u64) -> u64 {
        size.div_ceil(self.cluster_size).max(1)
    }
}

/// A deleted file found in a directory: name, size and clusters.
pub(super) fn found(name: String, size: u64, extents: Vec<(u64, u64)>) -> Option<Found> {
    let offset = extents.first()?.0;
    Some(Found {
        offset,
        size,
        kind: crate::ntfs::extension_kind(&name),
        selected: true,
        // The entry describes the data; its bytes are checked before carving trusts it.
        validation: Validation::MetadataChecked,
        original_name: Some(name),
        resident_data: None,
        ntfs: None,
        extents: Some(Arc::from(extents)),
    })
}

/// A name from directory data is shown and used as a file name, so it must
/// be printable and must not climb or split paths.
pub(super) fn clean_name(name: &str) -> Option<String> {
    let name = name.trim_end_matches([' ', '.']);
    (!name.is_empty()
        && name.chars().count() <= 255
        && !name
            .chars()
            .any(|c| c.is_control() || matches!(c, '/' | '\\' | '\0'))
        && !matches!(name, "." | ".."))
    .then(|| name.to_owned())
}

/// Tracks the work budget of one directory walk.
pub(super) struct Budget {
    pub directories: usize,
    pub bytes: u64,
    pub visited: HashSet<u64>,
}
impl Budget {
    pub(super) fn new() -> Self {
        Self {
            directories: 0,
            bytes: 0,
            visited: HashSet::new(),
        }
    }
    /// Takes `bytes` of directory data from the budget; false when spent.
    pub(super) fn take(&mut self, bytes: u64) -> bool {
        let Some(total) = self.bytes.checked_add(bytes) else {
            return false;
        };
        if total > MAX_DIRECTORY_BYTES || self.directories >= MAX_DIRECTORIES {
            return false;
        }
        self.bytes = total;
        self.directories = self.directories.saturating_add(1);
        true
    }
}

// ---------------------------------------------------------------- FAT12/16/32

/// The checksum a long-name entry stores for its 8.3 short name.
fn short_name_checksum(short: &[u8; 11]) -> u8 {
    short
        .iter()
        .fold(0u8, |sum, byte| sum.rotate_right(1).wrapping_add(*byte))
}

/// Up to 13 UTF-16 characters of one long-name entry, without padding.
fn long_name_part(entry: &[u8]) -> Vec<u16> {
    let ranges = [(1usize, 11usize), (14, 26), (28, 32)];
    let mut units = Vec::with_capacity(13);
    for (from, to) in ranges {
        for pair in entry.get(from..to).unwrap_or_default().as_chunks::<2>().0 {
            let unit = u16::from_le_bytes(*pair);
            if unit == 0 || unit == 0xffff {
                return units;
            }
            units.push(unit);
        }
    }
    units
}

/// The short 8.3 name of a deleted entry, whose first byte was overwritten.
fn short_name(entry: &[u8], first: u8) -> Option<String> {
    let base = entry.get(1..8)?;
    let extension = entry.get(8..11)?;
    let mut name = String::new();
    name.push(char::from(first));
    name.extend(base.iter().map(|b| char::from(*b)));
    let mut name = name.trim_end().to_owned();
    let extension = extension.iter().map(|b| char::from(*b)).collect::<String>();
    let extension = extension.trim_end();
    if !extension.is_empty() {
        name.push('.');
        name.push_str(extension);
    }
    (name.is_ascii()).then_some(name)
}

/// One parsed FAT directory record.
pub(super) enum FatEntry {
    /// A live subdirectory starting at this cluster.
    Directory(u64),
    /// A deleted file: name, size and first cluster.
    Deleted(String, u64, u64),
}

/// Parses the 32-byte records of one FAT directory.
///
/// `fat32` selects whether the high cluster word is used.
pub(super) fn fat_entries(data: &[u8], fat32: bool) -> Vec<FatEntry> {
    let mut entries = Vec::new();
    // Long-name parts in on-disk order (last part first) and their checksum.
    let mut long_parts: Vec<(u8, Vec<u16>)> = Vec::new();
    for entry in data.as_chunks::<32>().0 {
        let Some(&marker) = entry.first() else {
            break;
        };
        if marker == 0 {
            break;
        }
        let attributes = entry.get(11).copied().unwrap_or(0);
        if attributes == 0x0f {
            long_parts.push((entry.get(13).copied().unwrap_or(0), long_name_part(entry)));
            continue;
        }
        let parts = std::mem::take(&mut long_parts);
        if attributes & 0x08 != 0 {
            continue;
        }
        let high = if fat32 {
            u64::from(bytes::u16_le(entry, 20))
        } else {
            0
        };
        let cluster = (high << 16) | u64::from(bytes::u16_le(entry, 26));
        let size = u64::from(bytes::u32_le(entry, 28));
        if marker != 0xe5 {
            if attributes & 0x10 != 0 && marker != b'.' && cluster >= 2 {
                entries.push(FatEntry::Directory(cluster));
            }
            continue;
        }
        if attributes & 0x10 != 0 || size == 0 || cluster < 2 {
            continue;
        }
        let Some(short) = entry.get(..11).and_then(|s| <[u8; 11]>::try_from(s).ok()) else {
            continue;
        };
        // The long name survives deletion; its checksum also recovers the
        // short name's lost first letter.
        let long = parts.first().and_then(|(checksum, _)| {
            if parts.iter().any(|(sum, _)| sum != checksum) {
                return None;
            }
            let units: Vec<u16> = parts
                .iter()
                .rev()
                .flat_map(|(_, part)| part.clone())
                .collect();
            let long = String::from_utf16(&units).ok()?;
            // The short name is derived from the long one, so only its first
            // letter (or `_` for one that has no 8.3 form) can have been lost.
            // Trying every letter would accept a stranger's name 1 time in 3.
            let derived = long
                .chars()
                .find(|c| !matches!(c, ' ' | '.'))
                .filter(char::is_ascii_alphanumeric)
                .map(|c| c.to_ascii_uppercase() as u8);
            let matches = |first: u8| {
                let mut original = short;
                if let Some(byte) = original.first_mut() {
                    *byte = first;
                }
                short_name_checksum(&original) == *checksum
            };
            (derived.is_some_and(matches) || matches(b'_')).then_some(long)
        });
        let name = long.or_else(|| short_name(entry, b'_'));
        if let Some(name) = name.as_deref().and_then(clean_name) {
            entries.push(FatEntry::Deleted(name, size, cluster));
        }
    }
    entries
}

// ---------------------------------------------------------------- exFAT

/// One parsed exFAT directory entry set.
pub(super) enum ExfatEntry {
    /// A live subdirectory: first cluster, length, and whether it is contiguous.
    Directory(u64, u64, bool),
    /// A deleted file: name, size, first cluster, and whether it is contiguous.
    Deleted(String, u64, u64, bool),
}

/// Parses the entry sets of one exFAT directory.
pub(super) fn exfat_entries(data: &[u8]) -> Vec<ExfatEntry> {
    let mut entries = Vec::new();
    let records: Vec<&[u8]> = data
        .as_chunks::<32>()
        .0
        .iter()
        .map(<[u8; 32]>::as_slice)
        .collect();
    let mut index = 0usize;
    while let Some(record) = records.get(index) {
        let kind = record.first().copied().unwrap_or(0);
        if kind == 0 {
            break;
        }
        index = index.saturating_add(1);
        // File entry: 0x85 in use, 0x05 deleted.
        if kind & 0x7f != 0x05 {
            continue;
        }
        let in_use = kind & 0x80 != 0;
        let secondary = usize::from(record.get(1).copied().unwrap_or(0));
        let attributes = bytes::u16_le(record, 4);
        if !(2..=18).contains(&secondary) {
            continue;
        }
        let Some(set) = records.get(index..index.saturating_add(secondary)) else {
            break;
        };
        let Some(stream) = set.first() else {
            continue;
        };
        // Stream extension: 0xc0 in use, 0x40 deleted, matching the file entry.
        let stream_kind = stream.first().copied().unwrap_or(0);
        if stream_kind & 0x7f != 0x40 || (stream_kind & 0x80 != 0) != in_use {
            continue;
        }
        let flags = stream.get(1).copied().unwrap_or(0);
        let name_length = usize::from(stream.get(3).copied().unwrap_or(0));
        let cluster = u64::from(bytes::u32_le(stream, 20));
        let length = bytes::u64_le(stream, 24);
        let contiguous = flags & 0x02 != 0;
        // name_length comes from one byte, so it is at most 255.
        let mut units = Vec::with_capacity(name_length.min(255));
        for name in set.iter().skip(1) {
            if name.first().copied().unwrap_or(0) & 0x7f != 0x41 {
                break;
            }
            for pair in name.get(2..32).unwrap_or_default().as_chunks::<2>().0 {
                if units.len() < name_length {
                    units.push(u16::from_le_bytes(*pair));
                }
            }
        }
        index = index.saturating_add(secondary);
        if cluster < 2 {
            continue;
        }
        if attributes & 0x10 != 0 {
            if in_use {
                entries.push(ExfatEntry::Directory(cluster, length, contiguous));
            }
            continue;
        }
        if in_use || length == 0 || units.len() != name_length {
            continue;
        }
        if let Some(name) = String::from_utf16(&units)
            .ok()
            .as_deref()
            .and_then(clean_name)
        {
            entries.push(ExfatEntry::Deleted(name, length, cluster, contiguous));
        }
    }
    entries
}

#[cfg(test)]
mod tests {
    use super::*;

    fn short_entry(name: &[u8; 11], first: u8, attributes: u8, cluster: u16, size: u32) -> Vec<u8> {
        let mut entry = vec![0; 32];
        entry[..11].copy_from_slice(name);
        entry[0] = first;
        entry[11] = attributes;
        entry[26..28].copy_from_slice(&cluster.to_le_bytes());
        entry[28..32].copy_from_slice(&size.to_le_bytes());
        entry
    }

    /// Deleted long-name entries for `text`, in on-disk order (last part first).
    fn long_entry(checksum: u8, text: &str) -> Vec<u8> {
        let mut units: Vec<u16> = text.encode_utf16().collect();
        units.push(0);
        let slots = [1, 3, 5, 7, 9, 14, 16, 18, 20, 22, 24, 28, 30];
        let mut entries = Vec::new();
        for part in units.chunks(13).rev() {
            let mut entry = vec![0xff; 32];
            entry[0] = 0xe5;
            entry[11] = 0x0f;
            entry[12] = 0;
            entry[13] = checksum;
            entry[26] = 0;
            entry[27] = 0;
            for (slot, unit) in slots.iter().zip(part) {
                entry[*slot..*slot + 2].copy_from_slice(&unit.to_le_bytes());
            }
            entries.extend(entry);
        }
        entries
    }

    #[test]
    fn deleted_fat_entries_keep_long_names_and_restore_short_names() {
        let short = *b"HOLIDA~1JPG";
        let checksum = short_name_checksum(&short);
        let mut data = Vec::new();
        data.extend(long_entry(checksum, "Holiday photo.jpg"));
        data.extend(short_entry(&short, 0xe5, 0x20, 7, 5000));
        data.extend(short_entry(b"NOTES   TXT", 0xe5, 0x20, 9, 10));
        data.extend(short_entry(b"PICTURES   ", b'P', 0x10, 12, 0));
        data.extend(short_entry(b"EMPTY   TXT", 0xe5, 0x20, 13, 0));
        let entries = fat_entries(&data, false);
        assert!(
            matches!(&entries[0], FatEntry::Deleted(name, 5000, 7) if name == "Holiday photo.jpg")
        );
        assert!(matches!(&entries[1], FatEntry::Deleted(name, 10, 9) if name == "_OTES.TXT"));
        assert!(matches!(entries[2], FatEntry::Directory(12)));
        assert_eq!(entries.len(), 3);
    }

    #[test]
    fn a_long_name_with_the_wrong_checksum_falls_back_to_the_short_name() {
        let mut data = long_entry(0x12, "Somebody else.txt");
        data.extend(short_entry(b"REPORT  PDF", 0xe5, 0x20, 5, 99));
        let entries = fat_entries(&data, true);
        assert!(matches!(&entries[0], FatEntry::Deleted(name, 99, 5) if name == "_EPORT.PDF"));
    }

    #[test]
    fn deleted_exfat_sets_keep_names_sizes_and_contiguity() {
        let mut data = vec![0u8; 32 * 6];
        let set = |data: &mut Vec<u8>,
                   at: usize,
                   kind: u8,
                   attributes: u16,
                   name: &str,
                   cluster: u32,
                   length: u64,
                   flags: u8| {
            let units: Vec<u16> = name.encode_utf16().collect();
            let names = units.len().div_ceil(15);
            data[at * 32] = kind;
            data[at * 32 + 1] = u8::try_from(1 + names).unwrap();
            data[at * 32 + 4..at * 32 + 6].copy_from_slice(&attributes.to_le_bytes());
            let stream = (at + 1) * 32;
            data[stream] = (kind & 0x80) | 0x40;
            data[stream + 1] = flags;
            data[stream + 3] = u8::try_from(units.len()).unwrap();
            data[stream + 20..stream + 24].copy_from_slice(&cluster.to_le_bytes());
            data[stream + 24..stream + 32].copy_from_slice(&length.to_le_bytes());
            for (i, chunk) in units.chunks(15).enumerate() {
                let entry = (at + 2 + i) * 32;
                data[entry] = (kind & 0x80) | 0x41;
                for (j, unit) in chunk.iter().enumerate() {
                    data[entry + 2 + j * 2..entry + 4 + j * 2].copy_from_slice(&unit.to_le_bytes());
                }
            }
        };
        set(
            &mut data,
            0,
            0x05,
            0x20,
            "Video 2024.mp4",
            40,
            123_456_789,
            0x03,
        );
        set(&mut data, 3, 0x85, 0x10, "DCIM", 8, 4096, 0x03);
        let entries = exfat_entries(&data);
        assert!(
            matches!(&entries[0], ExfatEntry::Deleted(name, 123_456_789, 40, true) if name == "Video 2024.mp4")
        );
        assert!(matches!(entries[1], ExfatEntry::Directory(8, 4096, true)));
    }

    #[test]
    fn hostile_directory_bytes_never_panic() {
        let mut data = vec![0u8; 4096];
        for seed in 0u32..2000 {
            for (i, byte) in data.iter_mut().enumerate() {
                let mixed = seed
                    .wrapping_mul(2_654_435_761)
                    .wrapping_add(u32::try_from(i).unwrap().wrapping_mul(40_503));
                *byte = mixed.to_le_bytes()[1];
            }
            drop(fat_entries(&data, seed % 2 == 0));
            drop(exfat_entries(&data));
        }
        assert!(clean_name("../x").is_none());
        assert!(clean_name("a\u{0}b").is_none());
        assert_eq!(clean_name("name. ").as_deref(), Some("name"));
    }
}
