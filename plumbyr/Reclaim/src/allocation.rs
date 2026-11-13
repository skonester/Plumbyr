//! Read-only, fail-closed allocation maps for formatted volume sources.
//! The map is a start-of-scan view, not a snapshot of a changing mounted volume.
use std::{
    io::{self, Read, Seek, SeekFrom},
    sync::atomic::{AtomicBool, Ordering},
};
mod directory;
mod ext;
mod fat;
mod hfs;
mod indirect;
mod journal;
mod ntfs;
const MAX_MAP: usize = 64 * 1024 * 1024;
const MAX_RANGES: usize = 1_000_000;
#[derive(Debug)]
pub(crate) struct Plan {
    pub ranges: Vec<(u64, u64)>,
    pub unused: u64,
    pub filesystem: &'static str,
    /// Deleted files rebuilt from filesystem records, reported before carving.
    pub files: Vec<crate::Found>,
    /// Set on ext2/ext3, whose freed block maps can put split files back in order.
    pub indirect: Option<indirect::Geometry>,
}
fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}
fn unsupported() -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        "Unused-space scanning needs a supported formatted volume (NTFS, FAT12/16/32, exFAT, ext2/3/4, or HFS+). Select the volume/partition rather than a whole-disk image. Occupied space was not scanned.",
    )
}
fn refs_unsupported() -> io::Error {
    io::Error::new(
        io::ErrorKind::Unsupported,
        "This is a ReFS volume. Reclaim cannot read the ReFS free-space map yet, so this volume was not scanned.",
    )
}
/// Bit order inside each byte of an allocation bitmap.
#[derive(Clone, Copy)]
enum BitOrder {
    /// NTFS, exFAT and ext: bit 0 of byte 0 is the first cluster.
    LeastSignificantFirst,
    /// HFS+: bit 7 of byte 0 is the first block.
    MostSignificantFirst,
}
/// Named files found in filesystem metadata, or none when that metadata could
/// not be read. Names are extra on top of the free-space map: a bad sector in a
/// directory, the MFT or the journal must not throw the map away and stop the
/// whole unused-space scan. A stop request still ends the scan.
fn files_or_none(
    result: io::Result<Vec<crate::Found>>,
    source: &str,
    cancel: &AtomicBool,
) -> io::Result<Vec<crate::Found>> {
    match result {
        Err(e) if cancel.load(Ordering::Relaxed) => Err(e),
        Err(e) => {
            log::warn!("{source} metadata could not be read, named files skipped: {e}");
            Ok(Vec::new())
        }
        files => files,
    }
}
fn check(cancel: &AtomicBool) -> io::Result<()> {
    if cancel.load(Ordering::Relaxed) {
        Err(io::Error::new(io::ErrorKind::Interrupted, "Scan stopped"))
    } else {
        Ok(())
    }
}
fn u16at(b: &[u8], p: usize) -> u64 {
    u64::from(crate::bytes::u16_le(b, p))
}
fn u32at(b: &[u8], p: usize) -> u64 {
    u64::from(crate::bytes::u32_le(b, p))
}
fn u64at(b: &[u8], p: usize) -> u64 {
    crate::bytes::u64_le(b, p)
}
fn product(a: u64, b: u64) -> io::Result<u64> {
    a.checked_mul(b)
        .ok_or_else(|| invalid("Invalid filesystem geometry"))
}
fn read_at(
    input: &mut (impl Read + Seek),
    at: u64,
    len: usize,
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Vec<u8>> {
    if len > MAX_MAP || at.checked_add(len as u64).is_none_or(|end| end > total) {
        return Err(invalid(
            "Filesystem metadata extends beyond the selected volume",
        ));
    }
    input.seek(SeekFrom::Start(at))?;
    // audit-allow(DOS-ALLOC): len is checked against MAX_MAP (64 MiB) above.
    let mut bytes = vec![0; len];
    for chunk in bytes.chunks_mut(65536) {
        check(cancel)?;
        input.read_exact(chunk)?;
    }
    Ok(bytes)
}
impl Plan {
    fn new(filesystem: &'static str) -> Self {
        Self {
            ranges: Vec::new(),
            unused: 0,
            filesystem,
            files: Vec::new(),
            indirect: None,
        }
    }
    /// Whether the byte range lies wholly inside one unused range.
    fn is_unused(&self, at: u64, length: u64) -> bool {
        let next = self.ranges.partition_point(|(start, _)| *start <= at);
        let Some(end) = at.checked_add(length) else {
            return false;
        };
        next.checked_sub(1)
            .and_then(|index| self.ranges.get(index))
            .is_some_and(|(start, size)| end <= start.saturating_add(*size))
    }
    fn add(&mut self, start: u64, len: u64) -> io::Result<()> {
        if len == 0 {
            return Ok(());
        }
        self.unused = self
            .unused
            .checked_add(len)
            .ok_or_else(|| invalid("Invalid unused-space size"))?;
        if let Some((previous, size)) = self.ranges.last_mut()
            && previous.checked_add(*size) == Some(start)
        {
            *size += len;
            return Ok(());
        }
        if self.ranges.len() >= MAX_RANGES {
            return Err(invalid("Unused space is too fragmented to map safely"));
        }
        self.ranges.push((start, len));
        Ok(())
    }
}
fn from_bitmap(
    bitmap: &[u8],
    clusters: u64,
    start: u64,
    cluster_size: u64,
    order: BitOrder,
    filesystem: &'static str,
    cancel: &AtomicBool,
) -> io::Result<Plan> {
    if clusters.div_ceil(8) > bitmap.len() as u64 {
        return Err(invalid("Allocation bitmap is truncated"));
    }
    let mut plan = Plan::new(filesystem);
    for cluster in 0..clusters {
        if cluster.is_multiple_of(8192) {
            check(cancel)?;
        }
        let mask = match order {
            BitOrder::LeastSignificantFirst => 1 << (cluster % 8),
            BitOrder::MostSignificantFirst => 0x80 >> (cluster % 8),
        };
        if bitmap[(cluster / 8) as usize] & mask == 0 {
            plan.add(start + cluster * cluster_size, cluster_size)?;
        }
    }
    Ok(plan)
}
pub(crate) fn plan(
    input: &mut (impl Read + Seek),
    total: u64,
    cancel: &AtomicBool,
) -> io::Result<Plan> {
    // Boot sectors (NTFS, FAT, exFAT, ReFS) sit at offset 0; the ext superblock
    // and HFS+ volume header sit at 1024, so read enough for both.
    let head = read_at(
        input,
        0,
        crate::bytes::to_usize(total.min(2048)),
        total,
        cancel,
    )?;
    let boot = head.get(..512).ok_or_else(unsupported)?;
    let oem = boot.get(3..11);
    let signed = boot.get(510..512) == Some(b"\x55\xaa".as_slice());
    let at_header = head.get(hfs::HEADER..hfs::HEADER + 2);
    if oem == Some(b"ReFS\0\0\0\0".as_slice()) {
        Err(refs_unsupported())
    } else if signed && oem == Some(b"NTFS    ".as_slice()) {
        ntfs::plan(input, boot, total, cancel)
    } else if signed && oem == Some(b"EXFAT   ".as_slice()) {
        fat::exfat(input, boot, total, cancel)
    } else if crate::bytes::u16_le(&head, ext::SUPERBLOCK + ext::MAGIC_OFFSET) == ext::MAGIC {
        ext::plan(input, &head, total, cancel)
    } else if at_header == Some(hfs::SIGNATURE_PLUS.as_slice())
        || at_header == Some(hfs::SIGNATURE_X.as_slice())
    {
        hfs::plan(input, &head, total, cancel)
    } else if signed
        && matches!(u16at(boot, 11), 512 | 1024 | 2048 | 4096)
        && boot.get(13).is_some_and(|n| n.is_power_of_two())
    {
        fat::classic(input, boot, total, cancel)
    } else {
        Err(unsupported())
    }
}

/// All unused space as one stream: the free ranges, in disk order, with the
/// space still in use left out.
///
/// When a drive fills its gaps in order, a fragmented file continues in the
/// next free range, so carving across the joins brings it back whole. Formats
/// with an internal check (image decode, checksums, ZIP directory) reject a
/// wrong join; a file found across a join records its pieces as `extents`.
struct FreeSpace<'a, R> {
    input: &'a mut R,
    /// Physical `(start, length)` pieces and the stream offset each begins at.
    pieces: Vec<(u64, u64, u64)>,
    length: u64,
    position: u64,
}
impl<'a, R> FreeSpace<'a, R> {
    fn new(input: &'a mut R, ranges: &[(u64, u64)]) -> Self {
        let mut stream_start = 0u64;
        let pieces = ranges
            .iter()
            .map(|&(start, length)| {
                let piece = (start, length, stream_start);
                stream_start = stream_start.saturating_add(length);
                piece
            })
            .collect();
        Self {
            input,
            pieces,
            length: stream_start,
            position: 0,
        }
    }
}
/// The piece holding stream offset `at`, as (physical start, length, stream start).
fn piece(pieces: &[(u64, u64, u64)], at: u64) -> Option<(u64, u64, u64)> {
    let index = pieces
        .partition_point(|(_, _, stream_start)| *stream_start <= at)
        .checked_sub(1)?;
    pieces
        .get(index)
        .copied()
        .filter(|(_, length, stream_start)| at < stream_start.saturating_add(*length))
}
/// The physical offset of stream offset `at`.
fn physical(pieces: &[(u64, u64, u64)], at: u64) -> u64 {
    piece(pieces, at).map_or(at, |(start, _, stream_start)| start + (at - stream_start))
}
/// Physical ranges holding stream bytes `at..at + size`.
fn physical_ranges(pieces: &[(u64, u64, u64)], at: u64, size: u64) -> Vec<(u64, u64)> {
    let mut ranges = Vec::new();
    let mut cursor = at;
    let end = at.saturating_add(size);
    while cursor < end {
        let Some((start, length, stream_start)) = piece(pieces, cursor) else {
            break;
        };
        let take = (stream_start + length).min(end) - cursor;
        ranges.push((start + (cursor - stream_start), take));
        cursor += take;
    }
    ranges
}
impl<R: Read + Seek> Read for FreeSpace<'_, R> {
    fn read(&mut self, b: &mut [u8]) -> io::Result<usize> {
        let Some((start, length, stream_start)) = piece(&self.pieces, self.position) else {
            return Ok(0);
        };
        let in_piece = stream_start + length - self.position;
        let count = b.len().min(crate::bytes::to_usize(in_piece));
        if count == 0 {
            return Ok(0);
        }
        self.input
            .seek(SeekFrom::Start(start + (self.position - stream_start)))?;
        let count = self.input.read(&mut b[..count])?;
        if count == 0 {
            return Err(io::Error::new(
                io::ErrorKind::UnexpectedEof,
                "The drive stopped returning unused-space data",
            ));
        }
        self.position += count as u64;
        Ok(count)
    }
}
impl<R: Read + Seek> Seek for FreeSpace<'_, R> {
    fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
        let next = match from {
            SeekFrom::Start(n) => i128::from(n),
            SeekFrom::Current(n) => i128::from(self.position) + i128::from(n),
            SeekFrom::End(n) => i128::from(self.length) + i128::from(n),
        };
        self.position = u64::try_from(next)
            .map_err(|error| io::Error::new(io::ErrorKind::InvalidInput, error))?;
        Ok(self.position)
    }
}
/// Checks files rebuilt from records against their contents and removes the
/// space of the ones that match from the carving plan.
///
/// A record can be older than the data now in its clusters. Only a file whose
/// first bytes match its type is trusted to own that space; the others stay in
/// the list as unverified and their space is still carved, so a newer deleted
/// file stored there is not hidden.
fn exclude_known_files(input: &mut (impl Read + Seek), plan: &mut Plan) {
    let mut owned: Vec<(u64, u64)> = Vec::new();
    for file in &mut plan.files {
        let Some(extents) = file.extents.clone() else {
            continue;
        };
        let mut head = Vec::with_capacity(64);
        let read = crate::FoundReader::new(input, file)
            .and_then(|reader| reader.take(64).read_to_end(&mut head));
        if read.is_err() {
            // Unreadable start (a bad sector): keep the record, but it cannot
            // prove it owns its space, so that space is still carved.
            file.validation = crate::Validation::Unverified;
            continue;
        }
        if file.original_name.is_none() {
            // A nameless record: the bytes are the only clue to the type.
            match crate::sniff_kind(&head) {
                Some(kind) => {
                    file.kind = kind;
                    owned.extend(extents.iter().copied());
                }
                None => file.validation = crate::Validation::Unverified,
            }
        } else if crate::starts_like(file.kind, &head) {
            owned.extend(extents.iter().copied());
        } else {
            file.validation = crate::Validation::Unverified;
        }
    }
    if owned.is_empty() {
        return;
    }
    owned.sort_unstable();
    plan.ranges = subtract(&plan.ranges, &owned);
    plan.unused = plan.ranges.iter().map(|(_, length)| length).sum();
}
/// `ranges` with the sorted `owned` ranges cut out.
fn subtract(ranges: &[(u64, u64)], owned: &[(u64, u64)]) -> Vec<(u64, u64)> {
    let mut kept = Vec::with_capacity(ranges.len());
    let mut next = 0;
    for &(start, length) in ranges {
        let end = start.saturating_add(length);
        let mut cursor = start;
        while let Some(&(at, size)) = owned.get(next) {
            let owned_end = at.saturating_add(size);
            if owned_end <= cursor {
                next += 1;
                continue;
            }
            if at >= end {
                break;
            }
            if at > cursor {
                kept.push((cursor, at - cursor));
            }
            cursor = cursor.max(owned_end);
            if owned_end > end {
                break;
            }
            next += 1;
        }
        if cursor < end {
            kept.push((cursor, end - cursor));
        }
    }
    kept
}
/// A read buffer no bigger than the space to scan, at most 1 MiB.
fn to_usize_min(length: u64) -> usize {
    crate::bytes::to_usize(length.min(1024 * 1024))
}
pub(crate) fn scan(
    input: &mut (impl Read + Seek),
    total: u64,
    cancel: &AtomicBool,
    mut event: impl FnMut(crate::Event),
) -> io::Result<()> {
    let mut plan = match plan(input, total, cancel) {
        Err(e) if e.kind() == io::ErrorKind::Interrupted && cancel.load(Ordering::Relaxed) => {
            return Ok(());
        }
        result => result?,
    };
    exclude_known_files(input, &mut plan);
    event(crate::Event::UnusedSpace {
        bytes: plan.unused,
        filesystem: plan.filesystem,
    });
    let mut found_count = 0;
    let mut resident_bytes = 0usize;
    // Files rebuilt from records come first; carving must not list them again.
    let mut known = std::collections::HashSet::new();
    for file in std::mem::take(&mut plan.files) {
        // An unverified record's space is still carved; a newer file found
        // there by carving is real and must not be hidden behind the record.
        if file.validation != crate::Validation::Unverified {
            known.insert(file.offset);
        }
        found_count += 1;
        resident_bytes += file.resident_data.as_ref().map_or(0, |data| data.len());
        event(crate::Event::Found(file));
    }
    if cancel.load(Ordering::Relaxed) || plan.ranges.is_empty() {
        return Ok(());
    }
    // ext2/ext3: read free space once for leftover block maps, then carve
    // with each rebuilt file's pieces in order. Each pass is half the progress.
    let half = plan.unused / 2;
    let order = match plan.indirect {
        Some(geometry) => {
            let files = match indirect::chains(input, &plan, geometry, cancel, |done| {
                event(crate::Event::Progress((done / 2).min(half)));
            }) {
                Err(e)
                    if e.kind() == io::ErrorKind::Interrupted && cancel.load(Ordering::Relaxed) =>
                {
                    return Ok(());
                }
                result => result?,
            };
            Some(indirect::ordered(&plan.ranges, &files))
        }
        None => None,
    };
    let base = if order.is_some() { half } else { 0 };
    let scale = |at: u64| if order.is_some() { base + at / 2 } else { at };
    let mut stream = FreeSpace::new(input, order.as_deref().unwrap_or(&plan.ranges));
    let pieces = stream.pieces.clone();
    let length = stream.length;
    let mut limited = false;
    crate::resilient::scan(
        &mut stream,
        cancel,
        |e| match e {
            crate::Event::Found(mut file) => {
                found_count += 1;
                resident_bytes += file.resident_data.as_ref().map_or(0, |data| data.len());
                if found_count > 100_000 || resident_bytes > MAX_MAP {
                    limited = true;
                    return;
                }
                let ranges = physical_ranges(&pieces, file.offset, file.size);
                file.offset = physical(&pieces, file.offset);
                if known.contains(&file.offset) {
                    return;
                }
                if ranges.len() > 1 && file.resident_data.is_none() {
                    file.extents = Some(ranges.into());
                }
                event(crate::Event::Found(file));
            }
            crate::Event::Progress(bytes) => {
                event(crate::Event::Progress(scale(bytes.min(length))));
            }
            crate::Event::Checking {
                offset,
                kind,
                bytes,
            } => {
                event(crate::Event::Progress(scale(offset.min(length))));
                event(crate::Event::Checking {
                    offset: physical(&pieces, offset),
                    kind,
                    bytes,
                });
            }
            crate::Event::Unreadable { offset, bytes } => {
                event(crate::Event::Unreadable {
                    offset: physical(&pieces, offset),
                    bytes,
                });
            }
            // The carving pass over free space never reports unused space.
            crate::Event::UnusedSpace { .. } => {}
        },
        to_usize_min(length),
        &|at| {
            piece(&pieces, at).map_or(u64::MAX, |(_, length, stream_start)| {
                stream_start.saturating_add(length)
            })
        },
    )?;
    if limited {
        return Err(invalid(
            "Candidate limit reached (100,000). Recover available results.",
        ));
    }
    event(crate::Event::Progress(length));
    Ok(())
}

#[cfg(test)]
mod tests;
