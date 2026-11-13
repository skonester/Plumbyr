//! ext2, ext3 and ext4 free-block maps, read from each block group's bitmap.
//!
//! The map comes from the superblock, the group descriptor table and the block
//! bitmaps. Deleted files lose their block pointers, so they are rebuilt from
//! journal copies (`journal`, ext3/ext4) or leftover block maps (`indirect`,
//! ext2/ext3) before free blocks are carved.
// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]

use std::io::{self, Read, Seek};
use std::sync::atomic::AtomicBool;

use crate::bytes;

use super::{MAX_MAP, Plan, check, invalid, product, read_at};

/// Byte offset of the superblock from the start of the volume.
pub(super) const SUPERBLOCK: usize = 1024;
/// `s_magic`, relative to the superblock.
pub(super) const MAGIC_OFFSET: usize = 0x38;
pub(super) const MAGIC: u16 = 0xef53;

const COMPAT_HAS_JOURNAL: u32 = 0x4;

const INCOMPAT_FILETYPE: u32 = 0x2;
// Set while a journaled volume is mounted or after an unclean shutdown.
const INCOMPAT_RECOVER: u32 = 0x4;
const INCOMPAT_EXTENTS: u32 = 0x40;
const INCOMPAT_64BIT: u32 = 0x80;
const INCOMPAT_MMP: u32 = 0x100;
const INCOMPAT_FLEX_BG: u32 = 0x200;
const INCOMPAT_EA_INODE: u32 = 0x400;
const INCOMPAT_DIRDATA: u32 = 0x1000;
const INCOMPAT_CSUM_SEED: u32 = 0x2000;
const INCOMPAT_LARGEDIR: u32 = 0x4000;
const INCOMPAT_INLINE_DATA: u32 = 0x8000;
const INCOMPAT_ENCRYPT: u32 = 0x10000;
const INCOMPAT_CASEFOLD: u32 = 0x20000;
/// Features that do not change where block bitmaps live or what a bit means.
/// Compression, external journal devices and `META_BG` descriptor placement are
/// not in this list, so those volumes are rejected instead of misread.
const INCOMPAT_KNOWN: u32 = INCOMPAT_FILETYPE
    | INCOMPAT_RECOVER
    | INCOMPAT_EXTENTS
    | INCOMPAT_64BIT
    | INCOMPAT_MMP
    | INCOMPAT_FLEX_BG
    | INCOMPAT_EA_INODE
    | INCOMPAT_DIRDATA
    | INCOMPAT_CSUM_SEED
    | INCOMPAT_LARGEDIR
    | INCOMPAT_INLINE_DATA
    | INCOMPAT_ENCRYPT
    | INCOMPAT_CASEFOLD;
/// Features that only ext4 sets; without them a journaled volume is ext3.
const INCOMPAT_EXT4: u32 = INCOMPAT_EXTENTS
    | INCOMPAT_64BIT
    | INCOMPAT_FLEX_BG
    | INCOMPAT_EA_INODE
    | INCOMPAT_INLINE_DATA
    | INCOMPAT_ENCRYPT
    | INCOMPAT_CASEFOLD
    | INCOMPAT_CSUM_SEED
    | INCOMPAT_LARGEDIR;

const RO_COMPAT_GDT_CSUM: u32 = 0x10;
// One bitmap bit per cluster of blocks instead of per block.
const RO_COMPAT_BIGALLOC: u32 = 0x200;
const RO_COMPAT_METADATA_CSUM: u32 = 0x400;

/// `bg_flags`: the group's block bitmap was never written because no block in
/// the group was ever allocated. Only trusted when group checksums are on.
const BLOCK_UNINIT: u16 = 0x2;

/// Superblock geometry checked before any group is read.
struct Geometry {
    block_size: u64,
    blocks: u64,
    first_data_block: u64,
    blocks_per_group: u64,
    descriptor_size: u64,
    is_64bit: bool,
    uninit_trusted: bool,
    /// Files are mapped by extent trees, not block maps (ext4).
    extents: bool,
    name: &'static str,
}

fn unsupported_feature() -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        "This ext volume uses a feature Reclaim cannot map yet (compression, an external journal, META_BG, or bigalloc), so it was not scanned.",
    )
}

fn u32_field(superblock: &[u8], at: usize) -> u32 {
    bytes::u32_le(superblock, at)
}

fn geometry(superblock: &[u8], total: u64) -> io::Result<Geometry> {
    let log_block_size = u32_field(superblock, 0x18);
    let first_data_block = u64::from(u32_field(superblock, 0x14));
    let blocks_per_group = u64::from(u32_field(superblock, 0x20));
    let revision = u32_field(superblock, 0x4c);
    let (compat, incompat, ro_compat) = if revision == 0 {
        (0, 0, 0)
    } else {
        (
            u32_field(superblock, 0x5c),
            u32_field(superblock, 0x60),
            u32_field(superblock, 0x64),
        )
    };
    if incompat & !INCOMPAT_KNOWN != 0 || ro_compat & RO_COMPAT_BIGALLOC != 0 {
        return Err(unsupported_feature());
    }
    if log_block_size > 6 {
        return Err(invalid("Invalid ext block size"));
    }
    let block_size = 1024u64 << log_block_size;
    let is_64bit = incompat & INCOMPAT_64BIT != 0;
    let high = if is_64bit {
        u64::from(u32_field(superblock, 0x150))
    } else {
        0
    };
    let blocks = high
        .checked_shl(32)
        .map(|high| high | u64::from(u32_field(superblock, 0x04)))
        .ok_or_else(|| invalid("Invalid ext block count"))?;
    let descriptor_size = if is_64bit {
        u64::from(bytes::u16_le(superblock, 0xfe))
    } else {
        32
    };
    let expected_first = u64::from(block_size == 1024);
    if first_data_block != expected_first
        || blocks_per_group == 0
        || !blocks_per_group.is_multiple_of(8)
        || blocks_per_group > block_size.saturating_mul(8)
        || blocks <= first_data_block
        || product(blocks, block_size)? > total
        || !descriptor_size.is_power_of_two()
        || !(32..=1024).contains(&descriptor_size)
        || (is_64bit && descriptor_size < 64)
    {
        return Err(invalid("Invalid ext volume geometry"));
    }
    let name = if compat & COMPAT_HAS_JOURNAL == 0 {
        "ext2"
    } else if incompat & INCOMPAT_EXT4 == 0 {
        "ext3"
    } else {
        "ext4"
    };
    Ok(Geometry {
        block_size,
        blocks,
        first_data_block,
        blocks_per_group,
        descriptor_size,
        is_64bit,
        uninit_trusted: ro_compat & (RO_COMPAT_GDT_CSUM | RO_COMPAT_METADATA_CSUM) != 0,
        extents: incompat & INCOMPAT_EXTENTS != 0,
        name,
    })
}

/// Adds every clear bit of one group's bitmap to `plan` as a free block.
fn add_free_blocks(
    plan: &mut Plan,
    bitmap: &[u8],
    first_block: u64,
    count: u64,
    block_size: u64,
) -> io::Result<()> {
    let mut index = 0u64;
    for byte in bitmap {
        if index >= count {
            break;
        }
        if *byte == 0xff {
            index = index.saturating_add(8);
            continue;
        }
        for bit in 0..8u32 {
            if index >= count {
                break;
            }
            if byte & (1 << bit) == 0 {
                let block = first_block
                    .checked_add(index)
                    .ok_or_else(|| invalid("Invalid ext block number"))?;
                plan.add(product(block, block_size)?, block_size)?;
            }
            index = index.saturating_add(1);
        }
    }
    Ok(())
}

/// True when `block` lies inside one of the sorted free ranges of `plan`.
fn is_free(plan: &Plan, block: u64, block_size: u64) -> bool {
    let Some(at) = block.checked_mul(block_size) else {
        return false;
    };
    let next = plan.ranges.partition_point(|(start, _)| *start <= at);
    next.checked_sub(1)
        .and_then(|index| plan.ranges.get(index))
        .is_some_and(|(start, length)| at < start.saturating_add(*length))
}

/// Builds the free-block map of an ext2, ext3 or ext4 volume.
///
/// `head` holds at least the first 2048 bytes of the volume.
pub(super) fn plan(
    input: &mut (impl Read + Seek),
    head: &[u8],
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Plan> {
    let superblock = head
        .get(SUPERBLOCK..SUPERBLOCK.saturating_add(1024))
        .ok_or_else(|| invalid("ext superblock is truncated"))?;
    let geometry = geometry(superblock, total)?;
    let data_blocks = geometry.blocks.saturating_sub(geometry.first_data_block);
    let groups = data_blocks.div_ceil(geometry.blocks_per_group);
    let table_length = product(groups, geometry.descriptor_size)?;
    if table_length > MAX_MAP as u64 {
        return Err(invalid(
            "ext group descriptor table is too large to map safely",
        ));
    }
    let table_block = geometry
        .first_data_block
        .checked_add(1)
        .ok_or_else(|| invalid("Invalid ext volume geometry"))?;
    let table = read_at(
        input,
        product(table_block, geometry.block_size)?,
        bytes::to_usize(table_length),
        total,
        cancel,
    )?;
    let mut plan = Plan::new(geometry.name);
    if !geometry.extents {
        plan.indirect = Some(super::indirect::Geometry {
            block_size: geometry.block_size,
            blocks: geometry.blocks,
        });
    }
    // Blocks the bitmaps themselves must mark as used.
    let mut metadata = vec![geometry.first_data_block];
    let table_blocks = table_length.div_ceil(geometry.block_size);
    metadata.extend((0..table_blocks).filter_map(|n| table_block.checked_add(n)));
    let descriptor_size = bytes::to_usize(geometry.descriptor_size);
    for (group, descriptor) in (0u64..).zip(table.chunks_exact(descriptor_size)) {
        check(cancel)?;
        let flags = bytes::u16_le(descriptor, 0x12);
        if geometry.uninit_trusted && flags & BLOCK_UNINIT != 0 {
            // Nothing was ever stored in this group, so there is nothing to recover.
            continue;
        }
        let high = if geometry.is_64bit {
            u64::from(bytes::u32_le(descriptor, 0x20))
        } else {
            0
        };
        let bitmap_block = (high << 32) | u64::from(bytes::u32_le(descriptor, 0));
        if bitmap_block < geometry.first_data_block || bitmap_block >= geometry.blocks {
            return Err(invalid("Invalid ext block bitmap location"));
        }
        metadata.push(bitmap_block);
        let first_block = product(group, geometry.blocks_per_group)?
            .checked_add(geometry.first_data_block)
            .ok_or_else(|| invalid("Invalid ext volume geometry"))?;
        let count = geometry
            .blocks
            .saturating_sub(first_block)
            .min(geometry.blocks_per_group);
        let bitmap = read_at(
            input,
            product(bitmap_block, geometry.block_size)?,
            bytes::to_usize(count.div_ceil(8)),
            total,
            cancel,
        )?;
        add_free_blocks(&mut plan, &bitmap, first_block, count, geometry.block_size)?;
    }
    for block in metadata {
        if is_free(&plan, block, geometry.block_size) {
            return Err(invalid("ext metadata is incorrectly marked unused"));
        }
    }
    if u32_field(superblock, 0x5c) & COMPAT_HAS_JOURNAL != 0 {
        let revision = u32_field(superblock, 0x4c);
        let inode_size = if revision == 0 {
            128
        } else {
            u64::from(bytes::u16_le(superblock, 0x58))
        };
        let inodes_per_group = u64::from(u32_field(superblock, 0x28));
        if inode_size.is_power_of_two()
            && (128..=geometry.block_size).contains(&inode_size)
            && inodes_per_group > 0
        {
            let volume = super::journal::Volume {
                block_size: geometry.block_size,
                blocks: geometry.blocks,
                inodes_per_group,
                inode_size,
                journal_inode: u64::from(u32_field(superblock, 0xe0)),
                descriptors: &table,
                descriptor_size,
                is_64bit: geometry.is_64bit,
            };
            plan.files = super::files_or_none(
                super::journal::deleted_files(input, &volume, &plan, total, cancel),
                "journal",
                cancel,
            )?;
        }
    }
    Ok(plan)
}
