//! Deleted ext3/ext4 files rebuilt from inode copies in the journal.
//!
//! On delete, Linux clears a file's block list from its inode and erases its
//! directory entry. The journal (JBD2) often still holds an older copy of the
//! inode-table block, from when the file existed, with the full block list and
//! size, and older copies of directory blocks with its name. A copy is used
//! only for an inode that is now free and whose blocks are all still unused,
//! so the data cannot belong to a newer file.
//!
//! Every read is bounded: the journal length, the number of logged blocks,
//! block-map depth and the number of files reported.
// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]

use std::collections::HashMap;
use std::io::{self, Read, Seek};
use std::sync::atomic::AtomicBool;

use crate::bytes;

use super::{Plan, check, directory, read_at};

const JOURNAL_MAGIC: u32 = 0xc03b_3998;
const DESCRIPTOR: u32 = 1;
const SUPERBLOCK_V1: u32 = 3;
const SUPERBLOCK_V2: u32 = 4;
const FEATURE_64BIT: u32 = 0x2;
const FEATURE_CSUM_V2: u32 = 0x8;
const FEATURE_CSUM_V3: u32 = 0x10;
const TAG_ESCAPED: u32 = 0x1;
const TAG_SAME_UUID: u32 = 0x2;
const TAG_LAST: u32 = 0x8;
const EXTENT_MAGIC: u16 = 0xf30a;
const INODE_FLAG_EXTENTS: u32 = 0x8_0000;
const MODE_TYPE: u16 = 0xf000;
const MODE_FILE: u16 = 0x8000;
/// Most journal blocks walked (a 1 GiB journal at 4 KiB blocks).
const MAX_JOURNAL_BLOCKS: u64 = 1 << 18;
/// Deepest extent tree or indirect map followed.
const MAX_DEPTH: u32 = 5;
/// Most block ranges accepted for one file.
const MAX_EXTENTS: usize = 4096;
/// Most inode bytes kept from journal copies while collecting (64 MiB).
const MAX_COPY_BYTES: u64 = 64 << 20;
/// Most file names kept from journal directory copies.
const MAX_NAMES: usize = 100_000;

/// What the journal reader needs from the ext superblock and group table.
pub(super) struct Volume<'a> {
    pub block_size: u64,
    pub blocks: u64,
    pub inodes_per_group: u64,
    pub inode_size: u64,
    pub journal_inode: u64,
    pub descriptors: &'a [u8],
    pub descriptor_size: usize,
    pub is_64bit: bool,
}
impl Volume<'_> {
    fn offset(&self, block: u64) -> Option<u64> {
        block.checked_mul(self.block_size)
    }
    /// Block number holding inode `number`, and the inode's byte offset in it.
    fn inode_place(&self, number: u64) -> Option<(u64, u64)> {
        let index = number.checked_sub(1)?;
        let group = index.checked_div(self.inodes_per_group)?;
        let within = index.checked_rem(self.inodes_per_group)?;
        let descriptor = self.descriptors.get(
            usize::try_from(group)
                .ok()?
                .checked_mul(self.descriptor_size)?..,
        )?;
        let high = if self.is_64bit {
            u64::from(bytes::u32_le(descriptor, 0x28))
        } else {
            0
        };
        let table = (high << 32) | u64::from(bytes::u32_le(descriptor, 8));
        let byte = within.checked_mul(self.inode_size)?;
        let block = table.checked_add(byte.checked_div(self.block_size)?)?;
        Some((block, byte.checked_rem(self.block_size)?))
    }
    /// Inode numbers stored in block `block`, if it is part of an inode table.
    fn inodes_in(&self, block: u64) -> Option<u64> {
        let groups = self.descriptors.len().checked_div(self.descriptor_size)?;
        let per_block = self.block_size.checked_div(self.inode_size)?;
        let table_blocks = self
            .inodes_per_group
            .checked_mul(self.inode_size)?
            .div_ceil(self.block_size);
        for group in 0..groups {
            let descriptor = self
                .descriptors
                .get(group.checked_mul(self.descriptor_size)?..)?;
            let high = if self.is_64bit {
                u64::from(bytes::u32_le(descriptor, 0x28))
            } else {
                0
            };
            let table = (high << 32) | u64::from(bytes::u32_le(descriptor, 8));
            if block >= table && block < table.saturating_add(table_blocks) {
                let first = u64::try_from(group)
                    .ok()?
                    .checked_mul(self.inodes_per_group)?
                    .checked_add(block.checked_sub(table)?.checked_mul(per_block)?)?
                    .checked_add(1)?;
                return Some(first);
            }
        }
        None
    }
}

fn read_block(
    input: &mut (impl Read + Seek),
    volume: &Volume<'_>,
    block: u64,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Option<Vec<u8>>> {
    if block == 0 || block >= volume.blocks {
        return Ok(None);
    }
    let Some(at) = volume.offset(block) else {
        return Ok(None);
    };
    read_at(input, at, bytes::to_usize(volume.block_size), total, cancel).map(Some)
}

/// The data blocks of an inode as `(first block, count)` runs in file order,
/// from its extent tree (ext4) or block map (ext2/ext3). Child blocks are read
/// from the disk; `None` when any part is missing or implausible.
fn block_runs(
    input: &mut (impl Read + Seek),
    volume: &Volume<'_>,
    inode: &[u8],
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Option<Vec<(u64, u64)>>> {
    let map = inode.get(0x28..0x28 + 60).unwrap_or_default();
    let mut runs = Vec::new();
    let complete = if bytes::u32_le(inode, 0x20) & INODE_FLAG_EXTENTS != 0 {
        extent_runs(input, volume, map, MAX_DEPTH, &mut runs, total, cancel)?
    } else {
        let mut ok = true;
        for entry in map.get(..48).unwrap_or_default().as_chunks::<4>().0 {
            push_block(&mut runs, u64::from(u32::from_le_bytes(*entry)));
        }
        for (slot, depth) in [(48usize, 1u32), (52, 2), (56, 3)] {
            let block = u64::from(bytes::u32_le(map, slot));
            if block != 0 {
                ok &= indirect_runs(input, volume, block, depth, &mut runs, total, cancel)?;
            }
        }
        ok
    };
    // Block 0 never holds file data; a run at 0 means a hole or a cleared map.
    let usable = complete
        && !runs.is_empty()
        && runs.len() <= MAX_EXTENTS
        && runs.iter().all(|(start, count)| {
            *start != 0
                && start
                    .checked_add(*count)
                    .is_some_and(|end| end <= volume.blocks)
        });
    Ok(usable.then_some(runs))
}

pub(super) fn push_block(runs: &mut Vec<(u64, u64)>, block: u64) {
    if block == 0 {
        return;
    }
    match runs.last_mut() {
        Some((start, count)) if start.checked_add(*count) == Some(block) => {
            *count = count.saturating_add(1);
        }
        _ => runs.push((block, 1)),
    }
}

fn extent_runs(
    input: &mut (impl Read + Seek),
    volume: &Volume<'_>,
    node: &[u8],
    depth_left: u32,
    runs: &mut Vec<(u64, u64)>,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<bool> {
    if bytes::u16_le(node, 0) != EXTENT_MAGIC || depth_left == 0 || runs.len() > MAX_EXTENTS {
        return Ok(false);
    }
    let entries = usize::from(bytes::u16_le(node, 2));
    let depth = bytes::u16_le(node, 6);
    if entries == 0 {
        return Ok(false);
    }
    for index in 0..entries {
        let Some(entry) = index
            .checked_mul(12)
            .and_then(|at| at.checked_add(12))
            .and_then(|at| node.get(at..at.checked_add(12)?))
        else {
            return Ok(false);
        };
        if depth == 0 {
            let length = u64::from(bytes::u16_le(entry, 4));
            // Above 32768 the extent is allocated but unwritten: no data to recover.
            if length == 0 || length > 32768 {
                return Ok(false);
            }
            let start =
                (u64::from(bytes::u16_le(entry, 6)) << 32) | u64::from(bytes::u32_le(entry, 8));
            match runs.last_mut() {
                Some((previous, count)) if previous.checked_add(*count) == Some(start) => {
                    *count = count.saturating_add(length);
                }
                _ => runs.push((start, length)),
            }
        } else {
            let child =
                (u64::from(bytes::u16_le(entry, 8)) << 32) | u64::from(bytes::u32_le(entry, 4));
            let Some(block) = read_block(input, volume, child, total, cancel)? else {
                return Ok(false);
            };
            if !extent_runs(
                input,
                volume,
                &block,
                depth_left.saturating_sub(1),
                runs,
                total,
                cancel,
            )? {
                return Ok(false);
            }
        }
    }
    Ok(true)
}

fn indirect_runs(
    input: &mut (impl Read + Seek),
    volume: &Volume<'_>,
    block: u64,
    depth: u32,
    runs: &mut Vec<(u64, u64)>,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<bool> {
    if depth == 0 || runs.len() > MAX_EXTENTS {
        return Ok(false);
    }
    let Some(data) = read_block(input, volume, block, total, cancel)? else {
        return Ok(false);
    };
    for entry in data.as_chunks::<4>().0 {
        let child = u64::from(u32::from_le_bytes(*entry));
        if child == 0 {
            break;
        }
        if child >= volume.blocks {
            return Ok(false);
        }
        if depth == 1 {
            push_block(runs, child);
        } else if !indirect_runs(
            input,
            volume,
            child,
            depth.saturating_sub(1),
            runs,
            total,
            cancel,
        )? {
            return Ok(false);
        }
    }
    Ok(true)
}

/// File names from a directory block copy: inode number to name.
fn directory_names(block: &[u8], names: &mut HashMap<u64, String>) {
    let mut at = 0usize;
    while let Some(entry) = block.get(at..) {
        let inode = u64::from(bytes::u32_le(entry, 0));
        let record = usize::from(bytes::u16_le(entry, 4));
        let length = usize::from(entry.get(6).copied().unwrap_or(0));
        let kind = entry.get(7).copied().unwrap_or(0);
        if record < 12 || !record.is_multiple_of(4) || length.saturating_add(8) > record {
            return;
        }
        // file_type 1 is a regular file.
        if inode != 0
            && kind == 1
            && (names.len() < MAX_NAMES || names.contains_key(&inode))
            && let Some(name) = entry
                .get(8..length.saturating_add(8))
                .and_then(|raw| std::str::from_utf8(raw).ok())
                .and_then(directory::clean_name)
        {
            names.insert(inode, name);
        }
        at = at.saturating_add(record);
    }
}

/// Size in bytes of one descriptor tag.
fn tag_size(incompat: u32) -> usize {
    if incompat & FEATURE_CSUM_V3 != 0 {
        16
    } else if incompat & FEATURE_64BIT != 0 {
        12
    } else {
        8
    }
}

/// Where the journal's `index`-th logged block really is. The log is a ring
/// from `first_log` to `end`: past `end` it continues at `first_log`. `None`
/// when even the wrapped position falls outside the log.
fn ring_index(index: usize, first_log: usize, end: usize) -> Option<usize> {
    if index < end {
        return Some(index);
    }
    first_log
        .checked_add(index.checked_sub(end)?)
        .filter(|at| *at < end)
}

/// Deleted regular files whose inode survives in the journal.
pub(super) fn deleted_files(
    input: &mut (impl Read + Seek),
    volume: &Volume<'_>,
    plan: &Plan,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Vec<crate::Found>> {
    let Some((block, offset)) = volume.inode_place(volume.journal_inode) else {
        return Ok(Vec::new());
    };
    let Some(table) = read_block(input, volume, block, total, cancel)? else {
        return Ok(Vec::new());
    };
    let journal_inode = table
        .get(bytes::to_usize(offset)..)
        .unwrap_or_default()
        .get(..bytes::to_usize(volume.inode_size))
        .unwrap_or_default()
        .to_vec();
    let Some(journal) = block_runs(input, volume, &journal_inode, total, cancel)? else {
        return Ok(Vec::new());
    };
    let journal_blocks: Vec<u64> = journal
        .iter()
        .flat_map(|(start, count)| *start..start.saturating_add(*count))
        .take(bytes::to_usize(MAX_JOURNAL_BLOCKS))
        .collect();
    let Some(first) = journal_blocks.first().copied() else {
        return Ok(Vec::new());
    };
    let Some(header) = read_block(input, volume, first, total, cancel)? else {
        return Ok(Vec::new());
    };
    let kind = bytes::u32_be(&header, 4);
    if bytes::u32_be(&header, 0) != JOURNAL_MAGIC
        || !matches!(kind, SUPERBLOCK_V1 | SUPERBLOCK_V2)
        || u64::from(bytes::u32_be(&header, 12)) != volume.block_size
    {
        return Ok(Vec::new());
    }
    let incompat = if kind == SUPERBLOCK_V2 {
        bytes::u32_be(&header, 0x28)
    } else {
        0
    };
    let tag_size = tag_size(incompat);
    let checksummed = incompat & (FEATURE_CSUM_V2 | FEATURE_CSUM_V3) != 0;
    // The log is a ring from s_first to s_maxlen: a descriptor near its end
    // logs its data blocks from s_first again. Block 0 is this superblock.
    let end = journal_blocks
        .len()
        .min(bytes::to_usize(u64::from(bytes::u32_be(&header, 0x10))));
    let first_log = bytes::to_usize(u64::from(bytes::u32_be(&header, 0x14)));
    if first_log == 0 || first_log >= end {
        return Ok(Vec::new());
    }
    let log_block = |index: usize| -> Option<u64> {
        journal_blocks
            .get(ring_index(index, first_log, end)?)
            .copied()
    };
    let max_copies = bytes::to_usize(MAX_COPY_BYTES.checked_div(volume.inode_size).unwrap_or(0));
    // Latest journal copy of each inode that still had a block list, and names.
    let mut copies: HashMap<u64, (u32, Vec<u8>)> = HashMap::new();
    let mut names: HashMap<u64, String> = HashMap::new();
    let mut index = 1usize;
    while index < end
        && let Some(&physical) = journal_blocks.get(index)
    {
        if index.is_multiple_of(1024) {
            check(cancel)?;
        }
        index = index.saturating_add(1);
        let Some(descriptor) = read_block(input, volume, physical, total, cancel)? else {
            continue;
        };
        if bytes::u32_be(&descriptor, 0) != JOURNAL_MAGIC
            || bytes::u32_be(&descriptor, 4) != DESCRIPTOR
        {
            continue;
        }
        let sequence = bytes::u32_be(&descriptor, 8);
        let tail = if checksummed { 4 } else { 0 };
        let mut at = 12usize;
        while let Some(tag) = descriptor.get(at..at.saturating_add(tag_size)) {
            let (target, flags) = if incompat & FEATURE_CSUM_V3 != 0 {
                let high = u64::from(bytes::u32_be(tag, 8));
                (
                    (high << 32) | u64::from(bytes::u32_be(tag, 0)),
                    bytes::u32_be(tag, 4),
                )
            } else {
                let high = if incompat & FEATURE_64BIT != 0 {
                    u64::from(bytes::u32_be(tag, 8))
                } else {
                    0
                };
                (
                    (high << 32) | u64::from(bytes::u32_be(tag, 0)),
                    u32::from(bytes::u16_be(tag, 6)),
                )
            };
            at = at.saturating_add(tag_size);
            if flags & TAG_SAME_UUID == 0 {
                at = at.saturating_add(16);
            }
            // The logged copy is the next journal block.
            let Some(copy_at) = log_block(index) else {
                break;
            };
            index = index.saturating_add(1);
            if let Some(mut copy) = read_block(input, volume, copy_at, total, cancel)? {
                if flags & TAG_ESCAPED != 0
                    && let Some(magic) = copy.get_mut(..4)
                {
                    magic.copy_from_slice(&JOURNAL_MAGIC.to_be_bytes());
                }
                if let Some(first_inode) = volume.inodes_in(target) {
                    let size = bytes::to_usize(volume.inode_size);
                    for (number, inode) in (first_inode..).zip(copy.chunks_exact(size.max(1))) {
                        let mode = bytes::u16_le(inode, 0);
                        let has_map = if bytes::u32_le(inode, 0x20) & INODE_FLAG_EXTENTS != 0 {
                            bytes::u16_le(inode, 0x28) == EXTENT_MAGIC
                                && bytes::u16_le(inode, 0x2a) != 0
                        } else {
                            bytes::u32_le(inode, 0x28) != 0
                        };
                        if mode & MODE_TYPE == MODE_FILE && has_map {
                            let newer = copies
                                .get(&number)
                                .is_none_or(|(seen, _)| sequence.wrapping_sub(*seen) < 1 << 31);
                            // Bounded: new inodes stop being kept at the budget.
                            if newer && (copies.len() < max_copies || copies.contains_key(&number))
                            {
                                copies.insert(number, (sequence, inode.to_vec()));
                            }
                        }
                    }
                } else {
                    directory_names(&copy, &mut names);
                }
            }
            if flags & TAG_LAST != 0
                || at.saturating_add(tag_size) > descriptor.len().saturating_sub(tail)
            {
                break;
            }
        }
    }
    let mut numbers: Vec<u64> = copies.keys().copied().collect();
    numbers.sort_unstable();
    let mut files = Vec::new();
    for number in numbers {
        check(cancel)?;
        let Some((_, inode)) = copies.get(&number) else {
            continue;
        };
        // The live inode must be free now: the file was deleted.
        let Some((block, offset)) = volume.inode_place(number) else {
            continue;
        };
        let Some(table) = read_block(input, volume, block, total, cancel)? else {
            continue;
        };
        let live = table.get(bytes::to_usize(offset)..).unwrap_or_default();
        if bytes::u16_le(live, 26) != 0 {
            continue;
        }
        let size =
            u64::from(bytes::u32_le(inode, 4)) | (u64::from(bytes::u32_le(inode, 0x6c)) << 32);
        if size == 0 {
            continue;
        }
        let Some(runs) = block_runs(input, volume, inode, total, cancel)? else {
            continue;
        };
        let mut extents = Vec::with_capacity(runs.len());
        let mut covered = 0u64;
        for (start, count) in runs {
            let (Some(at), Some(length)) =
                (volume.offset(start), count.checked_mul(volume.block_size))
            else {
                continue;
            };
            extents.push((at, length));
            covered = covered.saturating_add(length);
        }
        if covered < size
            || !extents
                .iter()
                .all(|(at, length)| plan.is_unused(*at, *length))
        {
            continue;
        }
        let name = names.get(&number).cloned();
        let Some(mut found) = directory::found(
            name.clone().unwrap_or_else(|| format!("inode {number}")),
            size,
            extents,
        ) else {
            continue;
        };
        if name.is_none() {
            found.original_name = None;
            found.kind = "bin";
        }
        files.push(found);
        if files.len() >= directory::MAX_FILES {
            break;
        }
    }
    Ok(files)
}

#[cfg(test)]
mod tests {
    use super::ring_index;

    #[test]
    fn logged_blocks_past_the_end_continue_at_the_start_of_the_log() {
        // Block 0 is the journal superblock; the log runs from 1 to 99.
        assert_eq!(ring_index(98, 1, 100), Some(98));
        assert_eq!(ring_index(99, 1, 100), Some(99));
        assert_eq!(ring_index(100, 1, 100), Some(1));
        assert_eq!(ring_index(102, 1, 100), Some(3));
        // A wrap that would run past the log again is refused, not looped.
        assert_eq!(ring_index(199, 1, 100), None);
        assert_eq!(ring_index(usize::MAX, 1, 100), None);
    }
}
