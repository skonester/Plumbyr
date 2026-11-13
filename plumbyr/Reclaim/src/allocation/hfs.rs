//! HFS+ and HFSX free-block maps, read from the volume's allocation file.
//!
//! The allocation file is a bitmap with one bit per allocation block, most
//! significant bit first. Only the eight extents in the volume header are
//! followed; an allocation file that spills into the extents overflow file is
//! rejected rather than read partially.
// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]

use std::io::{self, Read, Seek};
use std::sync::atomic::AtomicBool;

use crate::bytes;

use super::{BitOrder, MAX_MAP, Plan, check, from_bitmap, invalid, product, read_at};

/// Byte offset of the volume header from the start of the volume.
pub(super) const HEADER: usize = 1024;
/// Last byte of the volume header.
const HEADER_END: u64 = HEADER as u64 + 511;
pub(super) const SIGNATURE_PLUS: &[u8; 2] = b"H+";
pub(super) const SIGNATURE_X: &[u8; 2] = b"HX";
/// `HFSPlusVolumeHeader.allocationFile`, relative to the header.
const ALLOCATION_FORK: usize = 112;
const EXTENT_COUNT: usize = 8;
/// Largest allocation file read, with room for clump rounding past the bitmap.
const MAX_FORK: u64 = 2 * MAX_MAP as u64;

fn overflow_extents() -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        "This HFS+ volume's free-space map is too fragmented for Reclaim to read yet, so it was not scanned.",
    )
}

fn u32_field(header: &[u8], at: usize) -> u64 {
    u64::from(bytes::u32_be(header, at))
}

/// Reads bit `block` of an HFS+ bitmap; a block past the end counts as used.
fn is_used(bitmap: &[u8], block: u64) -> bool {
    let Ok(index) = usize::try_from(block / 8) else {
        return true;
    };
    bitmap
        .get(index)
        .is_none_or(|byte| byte & (0x80 >> (block % 8)) != 0)
}

/// Builds the free-block map of an HFS+ or HFSX volume.
///
/// `head` holds at least the first 2048 bytes of the volume.
pub(super) fn plan(
    input: &mut (impl Read + Seek),
    head: &[u8],
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Plan> {
    let header = head
        .get(HEADER..HEADER.saturating_add(512))
        .ok_or_else(|| invalid("HFS+ volume header is truncated"))?;
    let (name, version) = match header.get(..2) {
        Some(signature) if signature == SIGNATURE_PLUS => ("HFS+", 4),
        Some(signature) if signature == SIGNATURE_X => ("HFSX", 5),
        _ => return Err(invalid("Invalid HFS+ volume header")),
    };
    let block_size = u32_field(header, 40);
    let blocks = u32_field(header, 44);
    if bytes::u16_be(header, 2) != version
        || !block_size.is_power_of_two()
        || block_size < 512
        || blocks == 0
        || product(blocks, block_size)? > total
        || blocks.div_ceil(8) > MAX_MAP as u64
    {
        return Err(invalid("Invalid HFS+ volume geometry"));
    }
    let fork = header
        .get(ALLOCATION_FORK..ALLOCATION_FORK.saturating_add(80))
        .ok_or_else(|| invalid("HFS+ volume header is truncated"))?;
    let logical_size = bytes::u64_be(fork, 0);
    let fork_blocks = u32_field(fork, 12);
    let needed = blocks.div_ceil(8);
    if logical_size < needed || product(fork_blocks, block_size)? < needed {
        return Err(invalid("Invalid HFS+ allocation file size"));
    }
    let mut extents = Vec::with_capacity(EXTENT_COUNT);
    let mut listed = 0u64;
    for extent in fork.get(16..80).unwrap_or_default().as_chunks::<8>().0 {
        let start = u32_field(extent, 0);
        let count = u32_field(extent, 4);
        if count == 0 {
            break;
        }
        if start.checked_add(count).is_none_or(|end| end > blocks) {
            return Err(invalid("Invalid HFS+ allocation file extent"));
        }
        listed = listed.saturating_add(count);
        extents.push((start, count));
    }
    if listed < fork_blocks {
        return Err(overflow_extents());
    }
    // Bounds the per-block metadata check below (CWE-400).
    if listed != fork_blocks || product(fork_blocks, block_size)? > MAX_FORK {
        return Err(invalid("Invalid HFS+ allocation file size"));
    }
    // audit-allow(DOS-ALLOC): needed is checked against MAX_MAP (64 MiB) above.
    let mut bitmap = Vec::with_capacity(bytes::to_usize(needed));
    for (start, count) in &extents {
        check(cancel)?;
        let remaining = needed.saturating_sub(bitmap.len() as u64);
        if remaining == 0 {
            break;
        }
        let length = product(*count, block_size)?.min(remaining);
        bitmap.extend(read_at(
            input,
            product(*start, block_size)?,
            bytes::to_usize(length),
            total,
            cancel,
        )?);
    }
    if (bitmap.len() as u64) < needed {
        return Err(invalid("HFS+ allocation file is truncated"));
    }
    // The volume header, its backup near the end, and the allocation file
    // itself are always allocated on a healthy volume.
    let volume_end = product(blocks, block_size)?;
    let header_blocks = [HEADER as u64, HEADER_END, volume_end.saturating_sub(1024)]
        .map(|at| at.checked_div(block_size).unwrap_or(u64::MAX));
    let fork_blocks = extents
        .iter()
        .flat_map(|(start, count)| *start..start.saturating_add(*count));
    if header_blocks
        .into_iter()
        .chain(fork_blocks)
        .any(|block| !is_used(&bitmap, block))
    {
        return Err(invalid("HFS+ metadata is incorrectly marked unused"));
    }
    from_bitmap(
        &bitmap,
        blocks,
        0,
        block_size,
        BitOrder::MostSignificantFirst,
        name,
        cancel,
    )
}
