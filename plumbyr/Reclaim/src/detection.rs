//! Bounded container probes. Successful framing checks do not imply that
//! application-level payloads are safe or fully decodable.
use crate::bytes::to_usize;
pub(crate) const LIMIT: usize = 64 * 1024 * 1024;

pub(crate) enum CandidateEnd {
    More,
    Invalid,
    Complete(usize),
}
impl CandidateEnd {
    /// The verdict for any failure inside data a parser already holds in full.
    ///
    /// A carving verdict needs no error detail: a bad field, a failed decode or
    /// invalid text all mean the bytes are not this format. Used as
    /// `.map_err(CandidateEnd::malformed)` so the choice is visible at each site.
    pub(crate) fn malformed<E>(_error: E) -> Self {
        Self::Invalid
    }
}
use std::{
    collections::BTreeMap,
    sync::atomic::{AtomicBool, Ordering},
};

pub(crate) fn sqlite_end(bytes: &[u8]) -> CandidateEnd {
    use CandidateEnd::{Complete, Invalid, More};
    if bytes.len() < 100 {
        return More;
    }
    if !bytes.starts_with(b"SQLite format 3\0") {
        return Invalid;
    }
    let short = |p| u16::from_be_bytes([bytes[p], bytes[p + 1]]) as usize;
    let long = |p| crate::bytes::u32_be(bytes, p);
    let page = match short(16) {
        1 => 65536,
        n => n,
    };
    let pages = long(28);
    let schema = long(44);
    let encoding = long(56);
    // The page count is authoritative only when both change counters agree.
    if !(512..=65536).contains(&page)
        || !page.is_power_of_two()
        || !matches!(bytes[18], 1 | 2)
        || !matches!(bytes[19], 1 | 2)
        || page - usize::from(bytes[20]) < 480
        || bytes[21..24] != [64, 32, 32]
        || pages == 0
        || long(24) != long(92)
        || bytes[72..92] != [0; 20]
        || !((schema == 0 && encoding == 0 && pages == 1)
            || ((1..=4).contains(&schema) && (1..=3).contains(&encoding)))
        || long(32) > pages
        || long(36) >= pages
        || long(52) > pages
    {
        return Invalid;
    }
    let Some(size) = u64::from(pages)
        .checked_mul(page as u64)
        .filter(|n| *n <= LIMIT as u64)
    else {
        return Invalid;
    };
    if bytes.len() < 108 {
        return More;
    }
    // Page one contains the sqlite_schema table, so its root must be a table
    // b-tree. Check its cell directory before accepting the declared file size.
    let header = match bytes[100] {
        5 => 12,
        13 => 8,
        _ => return Invalid,
    };
    let usable = page - usize::from(bytes[20]);
    let cells_end = 100 + header + short(103) * 2;
    let content = match short(105) {
        0 if page == 65536 => 65536,
        n => n,
    };
    let free = short(101);
    if cells_end > content
        || content > usable
        || bytes[107] > 60
        || (free != 0 && (free < cells_end || free + 4 > usable))
    {
        return Invalid;
    }
    if bytes.len() < page {
        return More;
    }
    if header == 12 && (long(108) == 0 || long(108) > pages) {
        return Invalid;
    }
    for at in (100 + header..cells_end).step_by(2) {
        let cell = short(at);
        if cell < content || cell >= usable {
            return Invalid;
        }
    }
    if bytes.len() < to_usize(size) {
        More
    } else {
        Complete(to_usize(size))
    }
}

const fn crc_table() -> [u32; 256] {
    let mut table = [0; 256];
    let mut i: u32 = 0;
    while i < 256 {
        let mut value = i << 24;
        let mut bit = 0;
        while bit < 8 {
            value = (value << 1)
                ^ if value & 0x8000_0000 != 0 {
                    0x04c1_1db7
                } else {
                    0
                };
            bit += 1;
        }
        table[i as usize] = value;
        i += 1;
    }
    table
}
const CRC: [u32; 256] = crc_table();

#[derive(Default)]
struct Stream {
    next_sequence: u32,
    continued: bool,
    ended: bool,
}

/// Incremental Ogg framing: verified pages are never rehashed after appending
/// input. Track multiplexed serials separately and stop when every stream ends.
#[derive(Default)]
pub(crate) struct Ogg {
    cursor: usize,
    streams: BTreeMap<u32, Stream>,
    payload: bool,
    kind: &'static str,
}
impl Ogg {
    pub(crate) fn kind(&self) -> &'static str {
        if self.kind.is_empty() {
            "ogg"
        } else {
            self.kind
        }
    }

    pub(crate) fn push(&mut self, bytes: &[u8], cancel: &AtomicBool) -> CandidateEnd {
        use CandidateEnd::{Complete, Invalid, More};
        loop {
            if cancel.load(Ordering::Relaxed) {
                return Invalid;
            }
            let at = self.cursor;
            if bytes.len().saturating_sub(at) < 27 {
                return More;
            }
            let header = &bytes[at..at + 27];
            if &header[..4] != b"OggS" || header[4] != 0 || header[5] & !7 != 0 {
                return Invalid;
            }
            let flags = header[5];
            let long = |p| crate::bytes::u32_le(header, p);
            let serial = long(14);
            let sequence = long(18);
            let segments_end = at + 27 + usize::from(header[26]);
            if bytes.len() < segments_end {
                return More;
            }
            let lacing = &bytes[at + 27..segments_end];
            let end = segments_end + lacing.iter().map(|n| usize::from(*n)).sum::<usize>();
            if end > LIMIT {
                return Invalid;
            }
            if bytes.len() < end {
                return More;
            }
            let mut checksum = 0u32;
            for (index, &byte) in bytes[at..end].iter().enumerate() {
                let byte = if (22..26).contains(&index) { 0 } else { byte };
                checksum = (checksum << 8) ^ CRC[((checksum >> 24) as u8 ^ byte) as usize];
            }
            if checksum != long(22) {
                return Invalid;
            }
            if !self.streams.contains_key(&serial) {
                if self.streams.len() >= 32 || flags & 3 != 2 || sequence != 0 {
                    return Invalid;
                }
                self.streams.insert(serial, Stream::default());
            } else if flags & 2 != 0 {
                return Invalid;
            }
            let Some(stream) = self.streams.get_mut(&serial) else {
                return Invalid;
            };
            if stream.ended
                || sequence != stream.next_sequence
                || (flags & 1 != 0) != stream.continued
                || (stream.continued && lacing.is_empty())
            {
                return Invalid;
            }
            stream.next_sequence = sequence.wrapping_add(1);
            stream.continued = lacing.last() == Some(&255);
            stream.ended = flags & 4 != 0;
            if stream.ended && stream.continued {
                return Invalid;
            }
            if flags & 2 != 0 {
                let packet = &bytes[segments_end..end];
                if packet.starts_with(b"\x80theora") {
                    self.kind = "ogv";
                } else if packet.starts_with(b"OpusHead") && self.kind != "ogv" {
                    self.kind = "opus";
                }
            }
            self.payload |= end > segments_end;
            self.cursor = end;
            if self.streams.values().all(|s| s.ended) {
                return if self.payload { Complete(end) } else { Invalid };
            }
        }
    }
}
