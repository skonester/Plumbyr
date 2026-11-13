use super::directory;
use super::{
    AtomicBool, BitOrder, MAX_MAP, Plan, Read, Seek, check, files_or_none, from_bitmap, invalid,
    io, product, read_at, u16at, u32at, u64at,
};
use crate::bytes::to_usize;
use std::collections::HashSet;
struct Table {
    start: u64,
    length: u64,
    cache: Vec<u8>,
    cached_at: u64,
}
impl Table {
    fn new(start: u64, length: u64) -> Self {
        Self {
            start,
            length,
            cache: Vec::new(),
            cached_at: u64::MAX,
        }
    }
    fn bytes(
        &mut self,
        input: &mut (impl Read + Seek),
        at: u64,
        count: usize,
        total: u64,
        cancel: &AtomicBool,
    ) -> io::Result<[u8; 4]> {
        if at.checked_add(count as u64).is_none_or(|n| n > self.length) {
            return Err(invalid("Allocation table is truncated"));
        }
        // Eight-byte overlap handles FAT12 entries straddling cache boundaries.
        let page = at / 65536 * 65536;
        if self.cached_at != page {
            let len = (self.length - page).min(65544) as usize;
            self.cache = read_at(input, self.start + page, len, total, cancel)?;
            self.cached_at = page;
        }
        let offset = to_usize(at - page);
        let mut value = [0; 4];
        value[..count].copy_from_slice(&self.cache[offset..offset + count]);
        Ok(value)
    }
    fn entry(
        &mut self,
        input: &mut (impl Read + Seek),
        cluster: u64,
        bits: u32,
        total: u64,
        cancel: &AtomicBool,
    ) -> io::Result<u64> {
        Ok(match bits {
            12 => {
                let b = self.bytes(input, cluster + cluster / 2, 2, total, cancel)?;
                let n = u16at(&b, 0);
                if cluster.is_multiple_of(2) {
                    n & 0xfff
                } else {
                    n >> 4
                }
            }
            16 => u16at(&self.bytes(input, cluster * 2, 2, total, cancel)?, 0),
            32 => u32at(&self.bytes(input, cluster * 4, 4, total, cancel)?, 0) & 0x0fff_ffff,
            _ => u32at(&self.bytes(input, cluster * 4, 4, total, cancel)?, 0),
        })
    }
}
pub(super) fn classic(
    input: &mut (impl Read + Seek),
    b: &[u8],
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Plan> {
    let sector = u16at(b, 11);
    let per = u64::from(b[13]);
    let reserved = u16at(b, 14);
    let fats = u64::from(b[16]);
    let roots = u16at(b, 17);
    let sectors = if u16at(b, 19) != 0 {
        u16at(b, 19)
    } else {
        u32at(b, 32)
    };
    let fat_sectors = if u16at(b, 22) != 0 {
        u16at(b, 22)
    } else {
        u32at(b, 36)
    };
    if reserved == 0
        || !(1..=2).contains(&fats)
        || per == 0
        || per > 128
        || fat_sectors == 0
        || product(sector, sectors)? > total
    {
        return Err(invalid("Invalid FAT volume geometry"));
    }
    let root_sectors = (roots * 32).div_ceil(sector);
    let first = reserved + fats * fat_sectors + root_sectors;
    if first >= sectors {
        return Err(invalid("FAT volume has no valid data area"));
    }
    let clusters = (sectors - first) / per;
    let (bits, name) = if clusters < 4085 {
        (12, "FAT12")
    } else if clusters < 65525 {
        (16, "FAT16")
    } else {
        (32, "FAT32")
    };
    if clusters == 0
        || clusters > 0x0fff_fff5
        || (bits == 32 && (roots != 0 || u16at(b, 22) != 0 || u16at(b, 42) != 0))
        || (bits != 32 && (roots == 0 || u16at(b, 22) == 0))
    {
        return Err(invalid("Inconsistent FAT type and allocation geometry"));
    }
    let fat_length = product(fat_sectors, sector)?;
    let required = ((clusters + 2) * u64::from(bits)).div_ceil(8);
    if required > fat_length {
        return Err(invalid("FAT cannot describe every data cluster"));
    }
    let flags = if bits == 32 { u16at(b, 40) } else { 0 };
    let active = if flags & 0x80 != 0 { flags & 15 } else { 0 };
    if active >= fats {
        return Err(invalid("Invalid active FAT"));
    }
    let mut tables = vec![Table::new(
        (reserved + active * fat_sectors) * sector,
        fat_length,
    )];
    if fats == 2 && flags & 0x80 == 0 {
        tables.push(Table::new((reserved + fat_sectors) * sector, fat_length));
    }
    for table in &mut tables {
        if table.entry(input, 0, bits, total, cancel)? & 255 != u64::from(b[21])
            || table.entry(input, 1, bits, total, cancel)? == 0
        {
            return Err(invalid("FAT reserved entries are damaged"));
        }
    }
    let root = if bits == 32 { u32at(b, 44) } else { 0 };
    if bits == 32 && (root < 2 || root > clusters + 1) {
        return Err(invalid("Invalid FAT32 root directory"));
    }
    let cluster_size = product(sector, per)?;
    let mut plan = Plan::new(name);
    for cluster in 2..clusters + 2 {
        if cluster.is_multiple_of(8192) {
            check(cancel)?;
        }
        let mut free = true;
        for table in &mut tables {
            free &= table.entry(input, cluster, bits, total, cancel)? == 0;
        }
        if cluster == root && free {
            return Err(invalid("FAT root directory is incorrectly marked free"));
        }
        if free {
            plan.add(first * sector + (cluster - 2) * cluster_size, cluster_size)?;
        }
    }
    let heap = directory::Heap {
        start: first * sector,
        cluster_size,
        last: clusters + 1,
    };
    let root_directory = if bits == 32 {
        Root::Chain(root)
    } else {
        Root::Fixed((reserved + fats * fat_sectors) * sector, roots * 32)
    };
    let Some(table) = tables.first_mut() else {
        return Ok(plan);
    };
    plan.files = files_or_none(
        classic_files(
            input,
            table,
            bits,
            &root_directory,
            heap,
            &plan,
            total,
            cancel,
        ),
        "FAT",
        cancel,
    )?;
    Ok(plan)
}

/// Where a FAT volume keeps its root directory.
enum Root {
    /// FAT12/16: a fixed region (byte offset, length) before the data heap.
    Fixed(u64, u64),
    /// FAT32: an ordinary cluster chain.
    Chain(u64),
}

/// Follows a cluster chain from `start`, bounded and loop-safe. Returns the
/// clusters in order, or `None` for a broken chain.
fn chain(
    input: &mut (impl Read + Seek),
    table: &mut Table,
    start: u64,
    bits: u32,
    heap: directory::Heap,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Option<Vec<u64>>> {
    let end_of_chain = match bits {
        12 => 0xff8,
        16 => 0xfff8,
        32 => 0x0fff_fff8,
        _ => 0xffff_fff8,
    };
    let mut clusters = Vec::new();
    let mut seen = HashSet::new();
    let mut current = start;
    loop {
        if current < 2 || current > heap.last || !seen.insert(current) {
            return Ok(None);
        }
        clusters.push(current);
        if clusters.len() >= directory::MAX_CHAIN {
            return Ok(None);
        }
        if clusters.len().is_multiple_of(4096) {
            check(cancel)?;
        }
        let next = table.entry(input, current, bits, total, cancel)?;
        if next >= end_of_chain {
            return Ok(Some(clusters));
        }
        current = next;
    }
}

/// Reads whole clusters of directory data within the walk budget.
fn read_clusters(
    input: &mut (impl Read + Seek),
    clusters: &[u64],
    heap: directory::Heap,
    budget: &mut directory::Budget,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Option<Vec<u8>>> {
    let Some(length) = (clusters.len() as u64).checked_mul(heap.cluster_size) else {
        return Ok(None);
    };
    if !budget.take(length) || clusters.iter().any(|c| !budget.visited.insert(*c)) {
        return Ok(None);
    }
    let mut data = Vec::new();
    for (start, length) in heap.extents(clusters).unwrap_or_default() {
        data.extend(read_at(input, start, to_usize(length), total, cancel)?);
    }
    Ok(Some(data))
}

/// The clusters a deleted file stored in one piece from `start` would use,
/// if all of them are still unused.
fn free_run(plan: &Plan, heap: directory::Heap, start: u64, size: u64) -> Option<Vec<u64>> {
    let count = heap.cluster_count(size);
    let end = start.checked_add(count)?;
    if end.checked_sub(1)? > heap.last {
        return None;
    }
    let clusters: Vec<u64> = (start..end).collect();
    let extents = heap.extents(&clusters)?;
    extents
        .iter()
        .all(|(at, length)| plan.is_unused(*at, *length))
        .then_some(clusters)
}

/// Deleted files named in FAT12/16/32 directories, assumed stored in one
/// piece: the FAT chain of a deleted file is cleared, so its order is lost.
#[expect(
    clippy::too_many_arguments,
    reason = "volume geometry passed once, read-only"
)]
fn classic_files(
    input: &mut (impl Read + Seek),
    table: &mut Table,
    bits: u32,
    root: &Root,
    heap: directory::Heap,
    plan: &Plan,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Vec<crate::Found>> {
    let mut budget = directory::Budget::new();
    let mut files = Vec::new();
    let mut pending: Vec<(Option<u64>, usize)> = vec![(None, 0)];
    while let Some((start, depth)) = pending.pop() {
        check(cancel)?;
        let data = match (start.as_ref(), root) {
            (None, Root::Fixed(at, length)) => {
                if !budget.take(*length) {
                    break;
                }
                read_at(input, *at, to_usize(*length), total, cancel)?
            }
            (None, Root::Chain(cluster)) | (Some(cluster), _) => {
                let cluster = *cluster;
                let Some(clusters) = chain(input, table, cluster, bits, heap, total, cancel)?
                else {
                    continue;
                };
                match read_clusters(input, &clusters, heap, &mut budget, total, cancel)? {
                    Some(data) => data,
                    None => continue,
                }
            }
        };
        for entry in directory::fat_entries(&data, bits == 32) {
            match entry {
                directory::FatEntry::Directory(cluster) => {
                    if depth < directory::MAX_DEPTH {
                        pending.push((Some(cluster), depth + 1));
                    }
                }
                directory::FatEntry::Deleted(name, size, cluster) => {
                    let Some(clusters) = free_run(plan, heap, cluster, size) else {
                        continue;
                    };
                    if let Some(found) = heap
                        .extents(&clusters)
                        .and_then(|extents| directory::found(name, size, extents))
                    {
                        files.push(found);
                    }
                    if files.len() >= directory::MAX_FILES {
                        return Ok(files);
                    }
                }
            }
        }
    }
    Ok(files)
}

pub(super) fn exfat(
    input: &mut (impl Read + Seek),
    b: &[u8],
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Plan> {
    if b[11..64].iter().any(|n| *n != 0)
        || !(9..=12).contains(&b[108])
        || b[109] > 25 - b[108]
        || !matches!(b[110], 1 | 2)
        || u16at(b, 104) != 0x100
    {
        return Err(invalid("Invalid exFAT boot sector"));
    }
    let sector = 1u64 << b[108];
    let per = 1u64 << b[109];
    let cluster_size = sector * per;
    let sectors = u64at(b, 72);
    let fat_start = u32at(b, 80);
    let fat_length = u32at(b, 84);
    let heap = u32at(b, 88);
    let clusters = u32at(b, 92);
    let root = u32at(b, 96);
    let flags = u16at(b, 106);
    if clusters == 0
        || clusters > 0xffff_fff5
        || clusters.div_ceil(8) > MAX_MAP as u64
        || fat_start < 24
        || fat_length == 0
        || fat_start + fat_length * u64::from(b[110]) > heap
        || heap + clusters * per > sectors
        || product(sectors, sector)? > total
        || (clusters + 2) * 4 > fat_length * sector
        || root < 2
        || root > clusters + 1
    {
        return Err(invalid("Invalid exFAT allocation geometry"));
    }
    if flags & 4 != 0 {
        return Err(invalid(
            "exFAT reports a media failure; its allocation map cannot be trusted",
        ));
    }
    let active = flags & 1;
    if active >= u64::from(b[110]) {
        return Err(invalid("Invalid active exFAT bitmap"));
    }
    // Verify the main boot-region checksum (mutable VolumeFlags/PercentInUse excluded).
    let boot = read_at(input, 0, to_usize(12 * sector), total, cancel)?;
    let mut sum = 0u32;
    for (i, byte) in boot[..to_usize(11 * sector)].iter().enumerate() {
        if !matches!(i, 106 | 107 | 112) {
            sum = sum.rotate_right(1).wrapping_add(u32::from(*byte));
        }
    }
    if boot[to_usize(11 * sector)..]
        .as_chunks::<4>()
        .0
        .iter()
        .any(|chunk| u32::from_le_bytes(*chunk) != sum)
    {
        return Err(invalid("exFAT boot-region checksum failed"));
    }
    let mut table = Table::new(
        (fat_start + active * fat_length) * sector,
        fat_length * sector,
    );
    let mut seen = HashSet::new();
    let mut current = root;
    let mut bitmap = None;
    let mut terminated = false;
    // Root directory is metadata; read it only to locate the active bitmap.
    while !terminated {
        check(cancel)?;
        if current < 2
            || current > clusters + 1
            || !seen.insert(current)
            || seen.len() as u64 * cluster_size > MAX_MAP as u64
        {
            return Err(invalid("Invalid exFAT root directory chain"));
        }
        let bytes = read_at(
            input,
            heap * sector + (current - 2) * cluster_size,
            to_usize(cluster_size),
            total,
            cancel,
        )?;
        for entry in bytes.as_chunks::<32>().0 {
            if entry[0] == 0 {
                terminated = true;
                break;
            }
            if entry[0] == 0x81
                && u64::from(entry[1] & 1) == active
                && bitmap
                    .replace((u32at(entry, 20), u64at(entry, 24)))
                    .is_some()
            {
                return Err(invalid("Duplicate active exFAT allocation bitmap"));
            }
        }
        if terminated {
            break;
        }
        let next = table.entry(input, current, 64, total, cancel)?;
        // exFAT uses the full 32-bit value, unlike FAT32's 28-bit entries.
        if next >= 0xffff_fff8 {
            break;
        }
        current = next;
    }
    let (start, length) =
        bitmap.ok_or_else(|| invalid("Active exFAT allocation bitmap is missing"))?;
    let needed = clusters.div_ceil(8);
    if length < needed || length > MAX_MAP as u64 {
        return Err(invalid("Invalid exFAT bitmap length"));
    }
    // audit-allow(DOS-ALLOC): needed is at most length, which is checked against MAX_MAP.
    let mut bytes = Vec::with_capacity(to_usize(needed));
    let mut bitmap_clusters = Vec::new();
    let mut current = start;
    let mut visited = HashSet::new();
    while bytes.len() < to_usize(needed) {
        if current < 2 || current > clusters + 1 || !visited.insert(current) {
            return Err(invalid("Invalid exFAT bitmap chain"));
        }
        bitmap_clusters.push(current);
        let count = to_usize((needed - bytes.len() as u64).min(cluster_size));
        bytes.extend(read_at(
            input,
            heap * sector + (current - 2) * cluster_size,
            count,
            total,
            cancel,
        )?);
        if bytes.len() < to_usize(needed) {
            current = table.entry(input, current, 64, total, cancel)?;
        }
    }
    for cluster in seen.into_iter().chain(bitmap_clusters) {
        let bit = cluster - 2;
        if bytes[(bit / 8) as usize] & (1 << (bit % 8)) == 0 {
            return Err(invalid("exFAT metadata is incorrectly marked unused"));
        }
    }
    let mut plan = from_bitmap(
        &bytes,
        clusters,
        heap * sector,
        cluster_size,
        BitOrder::LeastSignificantFirst,
        "exFAT",
        cancel,
    )?;
    let heap = directory::Heap {
        start: heap * sector,
        cluster_size,
        last: clusters + 1,
    };
    plan.files = files_or_none(
        exfat_files(input, &mut table, root, heap, &plan, total, cancel),
        "exFAT",
        cancel,
    )?;
    Ok(plan)
}

/// Deleted files named in exFAT directories. Files flagged as stored in one
/// piece come back exactly; others follow their FAT chain when it is intact
/// and unused, and otherwise are assumed contiguous.
fn exfat_files(
    input: &mut (impl Read + Seek),
    table: &mut Table,
    root: u64,
    heap: directory::Heap,
    plan: &Plan,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Vec<crate::Found>> {
    let mut budget = directory::Budget::new();
    let mut files = Vec::new();
    // (first cluster, length or None for a chained root, contiguous, depth)
    let mut pending: Vec<(u64, Option<u64>, bool, usize)> = vec![(root, None, false, 0)];
    while let Some((start, length, contiguous, depth)) = pending.pop() {
        check(cancel)?;
        let clusters = if contiguous {
            let Some(count) = length.map(|length| heap.cluster_count(length)) else {
                continue;
            };
            let Some(end) = start.checked_add(count) else {
                continue;
            };
            if end.saturating_sub(1) > heap.last || count > directory::MAX_CHAIN as u64 {
                continue;
            }
            (start..end).collect()
        } else {
            match chain(input, table, start, 64, heap, total, cancel)? {
                Some(clusters) => clusters,
                None => continue,
            }
        };
        let Some(data) = read_clusters(input, &clusters, heap, &mut budget, total, cancel)? else {
            continue;
        };
        for entry in directory::exfat_entries(&data) {
            match entry {
                directory::ExfatEntry::Directory(cluster, length, contiguous) => {
                    if depth < directory::MAX_DEPTH {
                        pending.push((cluster, Some(length), contiguous, depth + 1));
                    }
                }
                directory::ExfatEntry::Deleted(name, size, cluster, contiguous) => {
                    let clusters = if !contiguous
                        && let Some(found) = chain(input, table, cluster, 64, heap, total, cancel)?
                        && heap.cluster_count(size) == found.len() as u64
                        && heap.extents(&found).is_some_and(|extents| {
                            extents.iter().all(|(at, len)| plan.is_unused(*at, *len))
                        }) {
                        Some(found)
                    } else {
                        None
                    };
                    let Some(clusters) = clusters.or_else(|| free_run(plan, heap, cluster, size))
                    else {
                        continue;
                    };
                    if let Some(found) = heap
                        .extents(&clusters)
                        .and_then(|extents| directory::found(name, size, extents))
                    {
                        files.push(found);
                    }
                    if files.len() >= directory::MAX_FILES {
                        return Ok(files);
                    }
                }
            }
        }
    }
    Ok(files)
}
