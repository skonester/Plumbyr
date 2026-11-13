//! Deleted ext2/ext3 files put back in order from their leftover block maps.
//!
//! ext2 and ext3 list a file's blocks past the first 12 in separate indirect
//! blocks. On delete Linux clears the list in the inode, but the freed
//! indirect blocks usually keep their contents. A free block holding a list of
//! free block numbers is taken as such a map, and the 12 free blocks before
//! the first listed block as the file's start when they open with a known
//! file signature. Maps one level up (double indirect) continue a file past
//! the first map.
//!
//! Nothing is reported from the maps directly: the carving stream is
//! reordered so each file's pieces follow one another, and carving still
//! checks every file. A wrong guess only changes the order in which unrelated
//! free space is read.
// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]

use std::collections::{BTreeMap, HashMap, HashSet};
use std::io::{self, Read, Seek, SeekFrom};
use std::sync::atomic::AtomicBool;

use crate::bytes;

use super::journal::push_block;
use super::{Plan, check};

/// Blocks an ext2/ext3 inode addresses directly, before the first map.
const DIRECT: u64 = 12;
/// Most block maps kept from one volume.
const MAX_MAPS: usize = 200_000;
/// Most block runs kept across all maps (16 bytes each).
const MAX_RUNS: usize = 2_000_000;
/// Free space read per request while looking for maps.
const CHUNK: u64 = 1 << 20;

/// Where an ext2/ext3 volume's blocks are, for reading block maps.
#[derive(Clone, Copy, Debug)]
pub(crate) struct Geometry {
    pub block_size: u64,
    pub blocks: u64,
}

/// The blocks a free block lists, as `(first block, count)` runs, when it
/// reads as a block map: a run of nonzero entries, then only zeros, every
/// entry a free block of the volume.
fn parse_map(data: &[u8], geometry: Geometry, plan: &Plan) -> Option<Vec<(u64, u64)>> {
    let entries = data.as_chunks::<4>().0;
    let used = entries
        .iter()
        .position(|entry| *entry == [0; 4])
        .unwrap_or(entries.len());
    let (list, rest) = entries.split_at_checked(used)?;
    if list.is_empty() || rest.iter().any(|entry| *entry != [0; 4]) {
        return None;
    }
    let mut runs = Vec::new();
    for entry in list {
        let block = u64::from(u32::from_le_bytes(*entry));
        let at = block.checked_mul(geometry.block_size)?;
        if block >= geometry.blocks || !plan.is_unused(at, geometry.block_size) {
            return None;
        }
        push_block(&mut runs, block);
    }
    Some(runs)
}

/// Every block map in the free space of `plan`, by the block it was found in.
fn find_maps(
    input: &mut (impl Read + Seek),
    plan: &Plan,
    geometry: Geometry,
    cancel: &AtomicBool,
    progress: &mut impl FnMut(u64),
) -> io::Result<BTreeMap<u64, Vec<(u64, u64)>>> {
    let block_size = geometry.block_size;
    let chunk = CHUNK
        .checked_div(block_size)
        .unwrap_or(1)
        .max(1)
        .saturating_mul(block_size);
    // Ordered, so which map claims a shared child does not depend on hashing.
    let mut maps = BTreeMap::new();
    let mut runs = 0usize;
    let mut done = 0u64;
    let mut buffer = Vec::new();
    for &(start, length) in &plan.ranges {
        let first = start.div_ceil(block_size);
        let end = start
            .saturating_add(length)
            .checked_div(block_size)
            .unwrap_or(0);
        let mut block = first;
        while block < end {
            check(cancel)?;
            let count = end
                .saturating_sub(block)
                .min(chunk.checked_div(block_size).unwrap_or(1));
            let size = count.saturating_mul(block_size);
            buffer.resize(bytes::to_usize(size), 0);
            let read = input
                .seek(SeekFrom::Start(block.saturating_mul(block_size)))
                .and_then(|_| input.read_exact(&mut buffer));
            // An unreadable stretch is reported by carving later; skip it here.
            if read.is_ok() {
                for (number, data) in
                    (block..).zip(buffer.chunks_exact(bytes::to_usize(block_size)))
                {
                    if maps.len() >= MAX_MAPS || runs >= MAX_RUNS {
                        break;
                    }
                    if let Some(list) = parse_map(data, geometry, plan) {
                        runs = runs.saturating_add(list.len());
                        maps.insert(number, list);
                    }
                }
            }
            block = block.saturating_add(count);
            done = done.saturating_add(size);
            progress(done);
        }
    }
    Ok(maps)
}

fn blocks_of(runs: &[(u64, u64)]) -> impl Iterator<Item = u64> + '_ {
    runs.iter()
        .flat_map(|(start, count)| *start..start.saturating_add(*count))
}

/// Deleted files rebuilt from leftover block maps, each as physical
/// `(offset, length)` ranges in file order. Every range lies in the free
/// space of `plan`, and no two files share a range.
pub(super) fn chains(
    input: &mut (impl Read + Seek),
    plan: &Plan,
    geometry: Geometry,
    cancel: &AtomicBool,
    mut progress: impl FnMut(u64),
) -> io::Result<Vec<Vec<(u64, u64)>>> {
    let maps = find_maps(input, plan, geometry, cancel, &mut progress)?;
    let block_size = geometry.block_size;
    let per_map = block_size.checked_div(4).unwrap_or(0);
    // A map that lists only other maps is one level up (double indirect).
    let mut parents: HashMap<u64, u64> = HashMap::new();
    let mut children: HashSet<u64> = HashSet::new();
    for (&block, runs) in &maps {
        if blocks_of(runs).all(|child| maps.contains_key(&child)) {
            let first_data = blocks_of(runs)
                .next()
                .and_then(|child| maps.get(&child))
                .and_then(|child| child.first())
                .map(|(start, _)| *start);
            if let Some(first_data) = first_data {
                parents.insert(first_data, block);
                children.extend(blocks_of(runs));
            }
        }
    }
    let parent_blocks: HashSet<u64> = parents.values().copied().collect();
    let mut numbers: Vec<u64> = maps
        .keys()
        .copied()
        .filter(|block| !parent_blocks.contains(block) && !children.contains(block))
        .collect();
    numbers.sort_unstable();
    let mut files: Vec<Vec<(u64, u64)>> = Vec::new();
    let mut signature = vec![0u8; 64.min(bytes::to_usize(block_size))];
    for number in numbers {
        check(cancel)?;
        let Some(runs) = maps.get(&number) else {
            continue;
        };
        let Some(&(first, _)) = runs.first() else {
            continue;
        };
        let Some(head) = first.checked_sub(DIRECT).filter(|head| *head > 0) else {
            continue;
        };
        let (Some(head_at), Some(head_length)) =
            (head.checked_mul(block_size), DIRECT.checked_mul(block_size))
        else {
            continue;
        };
        if !plan.is_unused(head_at, head_length) {
            continue;
        }
        let opened = input
            .seek(SeekFrom::Start(head_at))
            .and_then(|_| input.read_exact(&mut signature));
        if opened.is_err() || crate::sniff_kind(&signature).is_none() {
            continue;
        }
        let mut file = vec![(head, DIRECT)];
        let mut listed = 0u64;
        for block in blocks_of(runs) {
            push_block(&mut file, block);
            listed = listed.saturating_add(1);
        }
        if listed == per_map {
            let last = blocks_of(runs).last().unwrap_or(0);
            // The double-indirect map, if written in line, sits between the pieces.
            let parent = (1..=3u64).find_map(|gap| parents.get(&last.saturating_add(gap)));
            if let Some(child_runs) = parent.and_then(|parent| maps.get(parent)) {
                for child in blocks_of(child_runs) {
                    for block in maps
                        .get(&child)
                        .map(Vec::as_slice)
                        .map(blocks_of)
                        .into_iter()
                        .flatten()
                    {
                        push_block(&mut file, block);
                    }
                }
            }
        }
        let ranges: Option<Vec<(u64, u64)>> = file
            .iter()
            .map(|(start, count)| {
                Some((
                    start.checked_mul(block_size)?,
                    count.checked_mul(block_size)?,
                ))
            })
            .collect();
        if let Some(ranges) = ranges
            && ranges
                .iter()
                .all(|(at, length)| plan.is_unused(*at, *length))
        {
            files.push(ranges);
        }
    }
    Ok(without_overlaps(files))
}

/// Whether two of a file's own ranges share any byte.
fn overlaps_itself(file: &[(u64, u64)]) -> bool {
    let mut ranges = file.to_vec();
    ranges.sort_unstable();
    ranges
        .windows(2)
        .any(|pair| matches!(pair, [(at, size), (next, _)] if at.saturating_add(*size) > *next))
}

/// Keeps files that share no space. Where maps disagree, the one listing
/// more blocks wins: the kernel can leave an older, shorter copy of a map
/// that lists only the start of the same file.
fn without_overlaps(mut files: Vec<Vec<(u64, u64)>>) -> Vec<Vec<(u64, u64)>> {
    let length = |file: &Vec<(u64, u64)>| -> u64 { file.iter().map(|(_, size)| *size).sum() };
    files.sort_by_key(|file| std::cmp::Reverse(length(file)));
    // Accepted space: range start to range end.
    let mut claimed: BTreeMap<u64, u64> = BTreeMap::new();
    let mut kept = Vec::new();
    for file in files {
        // A damaged or hostile map can list the same block twice; that file
        // would read it twice, so it is not a file.
        if overlaps_itself(&file) {
            continue;
        }
        let clear = file.iter().all(|&(at, size)| {
            let end = at.saturating_add(size);
            claimed
                .range(..end)
                .next_back()
                .is_none_or(|(_, &claimed_end)| claimed_end <= at)
        });
        if clear {
            for &(at, size) in &file {
                claimed.insert(at, at.saturating_add(size));
            }
            kept.push(file);
        }
    }
    kept
}

/// The carving order: free space in disk order, except that each rebuilt
/// file is read whole where its first piece lies. `ranges` is sorted; every
/// file range lies inside it and no two files overlap.
pub(super) fn ordered(ranges: &[(u64, u64)], files: &[Vec<(u64, u64)>]) -> Vec<(u64, u64)> {
    let mut claimed: Vec<(u64, u64)> = files.iter().flatten().copied().collect();
    claimed.sort_unstable();
    let rest = super::subtract(ranges, &claimed);
    let mut items: Vec<(u64, &[(u64, u64)])> = rest
        .iter()
        .map(|piece| (piece.0, std::slice::from_ref(piece)))
        .chain(
            files
                .iter()
                .filter_map(|file| Some((file.first()?.0, file.as_slice()))),
        )
        .collect();
    items.sort_unstable_by_key(|(key, _)| *key);
    let mut order: Vec<(u64, u64)> = Vec::with_capacity(rest.len().saturating_add(claimed.len()));
    for (at, length) in items
        .into_iter()
        .flat_map(|(_, pieces)| pieces.iter().copied())
    {
        match order.last_mut() {
            Some((start, size)) if start.checked_add(*size) == Some(at) => {
                *size = size.saturating_add(length);
            }
            _ => order.push((at, length)),
        }
    }
    order
}

#[cfg(test)]
mod tests {
    use super::{Geometry, Plan, chains, ordered, parse_map, without_overlaps};
    use std::io::Cursor;
    use std::sync::atomic::AtomicBool;

    const GEOMETRY: Geometry = Geometry {
        block_size: 1024,
        blocks: 64,
    };

    fn plan(free: &[(u64, u64)]) -> Plan {
        let mut plan = Plan::new("ext2");
        for &(block, count) in free {
            plan.add(block * 1024, count * 1024).unwrap();
        }
        plan
    }

    fn map(entries: &[u32]) -> Vec<u8> {
        let mut block = vec![0u8; 1024];
        for (slot, entry) in block.as_chunks_mut::<4>().0.iter_mut().zip(entries) {
            slot.copy_from_slice(&entry.to_le_bytes());
        }
        block
    }

    #[test]
    fn a_map_lists_free_blocks_then_zeros() {
        let free = plan(&[(10, 40)]);
        assert_eq!(
            parse_map(&map(&[20, 21, 22, 30]), GEOMETRY, &free),
            Some(vec![(20, 3), (30, 1)])
        );
        // Empty, a gap of zeros inside the list, used or out-of-range blocks.
        assert_eq!(parse_map(&map(&[]), GEOMETRY, &free), None);
        let mut gap = map(&[20, 21]);
        gap[12..16].copy_from_slice(&22u32.to_le_bytes());
        assert_eq!(parse_map(&gap, GEOMETRY, &free), None);
        assert_eq!(parse_map(&map(&[20, 5]), GEOMETRY, &free), None);
        assert_eq!(parse_map(&map(&[20, 64]), GEOMETRY, &free), None);
        assert_eq!(parse_map(&map(&[u32::MAX]), GEOMETRY, &free), None);
        assert_eq!(parse_map(&[0xff; 7], GEOMETRY, &free), None);
    }

    #[test]
    fn hostile_free_space_never_panics() {
        // Every free block holds a map pointing at every other free block.
        let mut image = vec![0u8; 64 * 1024];
        let entries: Vec<u32> = (1..64).collect();
        for block in image.as_chunks_mut::<1024>().0 {
            block.copy_from_slice(&map(&entries));
        }
        let free = plan(&[(1, 63)]);
        let files = chains(
            &mut Cursor::new(image),
            &free,
            GEOMETRY,
            &AtomicBool::new(false),
            |_| {},
        )
        .unwrap();
        assert!(files.is_empty(), "{files:?}");
    }

    #[test]
    fn the_longer_of_two_overlapping_files_is_kept() {
        let short = vec![(0, 10)];
        let long = vec![(0, 10), (50, 10)];
        let apart = vec![(100, 5)];
        let kept = without_overlaps(vec![short, long.clone(), apart.clone()]);
        assert_eq!(kept, vec![long, apart]);
    }

    #[test]
    fn a_map_listing_the_same_blocks_twice_is_not_a_file() {
        // [20, 8] with a signature at block 8: (8, 13) then (8, 1).
        let twice = vec![(8, 13), (8, 1)];
        let fine = vec![(100, 5), (90, 5)];
        let kept = without_overlaps(vec![twice, fine.clone()]);
        assert_eq!(kept, vec![fine]);
    }

    #[test]
    fn each_rebuilt_file_is_read_whole_where_it_starts() {
        let ranges = [(0, 100), (200, 100)];
        let files = [vec![(10, 10), (250, 10), (40, 10)]];
        assert_eq!(
            ordered(&ranges, &files),
            vec![
                (0, 20),
                (250, 10),
                (40, 10),
                (20, 20),
                (50, 50),
                (200, 50),
                (260, 40)
            ]
        );
    }
}
