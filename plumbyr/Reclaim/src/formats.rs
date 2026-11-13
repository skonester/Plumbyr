//! Format-specific bounded probes. See `docs/FILE_FORMAT_RESEARCH.md` for sources
//! and recovery limits. No recovered program or archive is ever executed.
use crate::bytes::to_usize;
use crate::{CandidateEnd, Validation, detection::LIMIT};
use std::sync::atomic::{AtomicBool, Ordering};
mod cfb;
mod gzip;
mod media;
mod xar;
pub(crate) use gzip::Gzip;
type Probe = Result<usize, CandidateEnd>;
fn need(bytes: &[u8], end: usize) -> Result<(), CandidateEnd> {
    if end > LIMIT {
        Err(CandidateEnd::Invalid)
    } else if end > bytes.len() {
        Err(CandidateEnd::More)
    } else {
        Ok(())
    }
}
fn le16(b: &[u8], p: usize) -> usize {
    crate::bytes::u16_le(b, p) as usize
}
fn le32(b: &[u8], p: usize) -> usize {
    crate::bytes::u32_le(b, p) as usize
}
fn be32(b: &[u8], p: usize) -> usize {
    crate::bytes::u32_be(b, p) as usize
}
fn le64(b: &[u8], p: usize) -> u64 {
    crate::bytes::u64_le(b, p)
}
fn span(offset: u64, size: u64) -> Result<usize, CandidateEnd> {
    offset
        .checked_add(size)
        .filter(|n| *n <= LIMIT as u64)
        .map(to_usize)
        .ok_or(CandidateEnd::Invalid)
}
// MZ is only two bytes and common in arbitrary disk data. Reject impossible
// PE pointers from the scan buffer before doing a seek/read for every match.
pub(crate) fn plausible(kind: &str, bytes: &[u8]) -> bool {
    if kind != "pe" || bytes.len() < 64 {
        return true;
    }
    let p = le32(bytes, 60);
    if !(64..=1024 * 1024).contains(&p) {
        return false;
    }
    bytes.len() < p + 24
        || (&bytes[p..p + 4] == b"PE\0\0" && (1..=96).contains(&le16(bytes, p + 6)))
}
pub(crate) fn supported(kind: &str) -> bool {
    matches!(
        kind,
        "pe" | "exe"
            | "dll"
            | "elf"
            | "cfb"
            | "ole"
            | "msi"
            | "msp"
            | "mst"
            | "7z"
            | "deb"
            | "rpm"
            | "cab"
            | "rar"
            | "tar"
            | "qoi"
            | "dex"
            | "gz"
            | "xar"
            | "pkg"
            | "mid"
            | "swf"
    )
}
pub(crate) fn end(kind: &str, bytes: &[u8], cancel: &AtomicBool) -> Option<CandidateEnd> {
    if !supported(kind) {
        return None;
    }
    let result = if cancel.load(Ordering::Relaxed) {
        Err(CandidateEnd::Invalid)
    } else {
        match kind {
            "mid" => media::midi(bytes, cancel),
            "swf" => media::swf(bytes, cancel),
            "pe" | "exe" | "dll" => pe(bytes),
            "elf" => elf(bytes),
            "cfb" | "ole" | "msi" | "msp" | "mst" => cfb::end(bytes, cancel),
            "7z" => seven(bytes),
            "deb" => deb(bytes, cancel),
            "rpm" => rpm(bytes),
            "cab" => cab(bytes, cancel),
            "rar" => rar(bytes, cancel),
            "tar" => tar(bytes, cancel),
            "qoi" => qoi(bytes, cancel),
            "dex" => dex(bytes),
            "gz" => match Gzip::default().push(bytes, cancel) {
                CandidateEnd::Complete(n) => Ok(n),
                state @ (CandidateEnd::More | CandidateEnd::Invalid) => Err(state),
            },
            "xar" | "pkg" => xar::inspect(bytes, cancel).map(|(size, _)| size),
            // Only the kinds listed above reach this function.
            _ => Err(CandidateEnd::Invalid),
        }
    };
    Some(match result {
        Ok(size) => CandidateEnd::Complete(size),
        Err(state) => state,
    })
}
pub(crate) fn kind(base: &'static str, bytes: &[u8]) -> &'static str {
    match base {
        "pe" => {
            if le16(bytes, le32(bytes, 60) + 22) & 0x2000 != 0 {
                "dll"
            } else {
                "exe"
            }
        }
        "cfb" => cfb::kind(bytes),
        "xar" => {
            if xar::inspect(bytes, &AtomicBool::new(false)).is_ok_and(|(_, package)| package) {
                "pkg"
            } else {
                "xar"
            }
        }
        _ => base,
    }
}
pub(crate) fn validate(kind: &str, bytes: &[u8], cancel: &AtomicBool) -> Option<Validation> {
    if !matches!(end(kind, bytes, cancel)?, CandidateEnd::Complete(size) if size == bytes.len()) {
        return None;
    }
    if matches!(kind, "exe" | "dll") && self::kind("pe", bytes) != kind {
        return None;
    }
    if matches!(kind, "ole" | "msi" | "msp" | "mst") && cfb::kind(bytes) != kind {
        return None;
    }
    if matches!(kind, "xar" | "pkg") && xar::inspect(bytes, cancel).ok()?.1 != (kind == "pkg") {
        return None;
    }
    Some(
        if matches!(
            kind,
            "exe"
                | "dll"
                | "elf"
                | "ole"
                | "msi"
                | "msp"
                | "mst"
                | "rpm"
                | "dex"
                | "xar"
                | "pkg"
                | "swf"
        ) {
            Validation::Unverified
        } else {
            Validation::StructureChecked
        },
    )
}

fn pe(b: &[u8]) -> Probe {
    need(b, 64)?;
    if !b.starts_with(b"MZ") {
        return Err(CandidateEnd::Invalid);
    }
    let p = le32(b, 60);
    if !(64..=1024 * 1024).contains(&p) {
        return Err(CandidateEnd::Invalid);
    }
    need(b, p + 24)?;
    let sections = le16(b, p + 6);
    let optional = le16(b, p + 20);
    let o = p + 24;
    if &b[p..p + 4] != b"PE\0\0" || !(1..=96).contains(&sections) || le16(b, p + 22) & 2 == 0 {
        return Err(CandidateEnd::Invalid);
    }
    if optional < 2 {
        return Err(CandidateEnd::Invalid);
    }
    need(b, o + optional)?;
    let (directory, count) = match le16(b, o) {
        0x10b if optional >= 96 => (96, 92),
        0x20b if optional >= 112 => (112, 108),
        _ => return Err(CandidateEnd::Invalid),
    };
    let table = o + optional;
    let table_end = table + sections * 40;
    need(b, table_end)?;
    let alignment = le32(b, o + 36);
    let mut end = le32(b, o + 60);
    if alignment == 0 || !alignment.is_power_of_two() || end < table_end || end > LIMIT {
        return Err(CandidateEnd::Invalid);
    }
    let mut ranges = Vec::new();
    for i in 0..sections {
        let at = table + i * 40;
        let size = le32(b, at + 16);
        let offset = le32(b, at + 20);
        if size > 0 {
            if offset < le32(b, o + 60) || !offset.is_multiple_of(alignment) {
                return Err(CandidateEnd::Invalid);
            }
            let stop = span(offset as u64, size as u64)?;
            ranges.push((offset, stop));
            end = end.max(stop);
        }
    }
    ranges.sort_unstable();
    if ranges.windows(2).any(|w| w[0].1 > w[1].0) {
        return Err(CandidateEnd::Invalid);
    }
    let n = le32(b, o + count);
    if n > 16 || directory + n * 8 > optional {
        return Err(CandidateEnd::Invalid);
    }
    if n > 4 {
        let cert = o + directory + 32;
        let offset = le32(b, cert);
        let size = le32(b, cert + 4);
        if size > 0 {
            if offset < end || !offset.is_multiple_of(8) || size < 8 {
                return Err(CandidateEnd::Invalid);
            }
            let stop = span(offset as u64, size as u64)?;
            need(b, stop)?;
            let mut at = offset;
            while at < stop {
                if at + 8 > stop {
                    return Err(CandidateEnd::Invalid);
                }
                let len = le32(b, at);
                if len < 8 || len > stop - at {
                    return Err(CandidateEnd::Invalid);
                }
                at += len.div_ceil(8) * 8;
            }
            if at != stop {
                return Err(CandidateEnd::Invalid);
            }
            end = stop;
        }
    }
    let symbols = le32(b, p + 12);
    let count = le32(b, p + 16);
    if symbols != 0 || count != 0 {
        let strings = span(symbols as u64, count as u64 * 18)?;
        need(b, strings + 4)?;
        let len = le32(b, strings);
        if len < 4 {
            return Err(CandidateEnd::Invalid);
        }
        end = end.max(span(strings as u64, len as u64)?);
    }
    need(b, end)?;
    Ok(end)
}

fn elf(b: &[u8]) -> Probe {
    need(b, 16)?;
    if !b.starts_with(b"\x7fELF") || !matches!(b[4], 1 | 2) || !matches!(b[5], 1 | 2) || b[6] != 1 {
        return Err(CandidateEnd::Invalid);
    }
    let wide = b[4] == 2;
    let little = b[5] == 1;
    let u16at = |p| {
        if little {
            le16(b, p)
        } else {
            crate::bytes::u16_be(b, p) as usize
        }
    };
    let u32at = |p| if little { le32(b, p) } else { be32(b, p) };
    let word = |p| {
        if wide {
            if little {
                le64(b, p)
            } else {
                crate::bytes::u64_be(b, p)
            }
        } else {
            u32at(p) as u64
        }
    };
    let header = if wide { 64 } else { 52 };
    need(b, header)?;
    let h = if wide { 52 } else { 40 };
    if !matches!(u16at(16), 1..=4) || u16at(18) == 0 || u32at(20) != 1 || u16at(h) != header {
        return Err(CandidateEnd::Invalid);
    }
    let ph = word(if wide { 32 } else { 28 });
    let sh = word(if wide { 40 } else { 32 });
    let ps = u16at(h + 2);
    let pn = u16at(h + 4);
    let ss = u16at(h + 6);
    let sn = u16at(h + 8);
    if (pn == 0 && sn == 0)
        || pn == 65535
        || (sn == 0 && sh != 0)
        || sn >= 0xff00
        || (pn > 0 && ps != if wide { 56 } else { 32 })
        || (sn > 0 && ss != if wide { 64 } else { 40 })
    {
        return Err(CandidateEnd::Invalid);
    }
    let mut end = header;
    for (offset, count, size) in [(ph, pn, ps), (sh, sn, ss)] {
        if count > 0 {
            if offset < header as u64 {
                return Err(CandidateEnd::Invalid);
            }
            end = end.max(span(offset, count as u64 * size as u64)?);
        }
    }
    need(b, end)?;
    for i in 0..pn {
        let at = to_usize(ph) + i * ps;
        let offset = word(at + if wide { 8 } else { 4 });
        let size = word(at + if wide { 32 } else { 16 });
        if u32at(at) == 1 && size > word(at + if wide { 40 } else { 20 }) {
            return Err(CandidateEnd::Invalid);
        }
        if size > 0 {
            end = end.max(span(offset, size)?);
        }
    }
    for i in 1..sn {
        let at = to_usize(sh) + i * ss;
        if u32at(at + 4) != 8 {
            // SHT_NOBITS is memory-only, not file data.
            let offset = word(at + if wide { 24 } else { 16 });
            let size = word(at + if wide { 32 } else { 20 });
            if size > 0 {
                end = end.max(span(offset, size)?);
            }
        }
    }
    need(b, end)?;
    Ok(end)
}

fn seven(b: &[u8]) -> Probe {
    need(b, 32)?;
    if !b.starts_with(b"7z\xbc\xaf\x27\x1c")
        || b[6] != 0
        || crc32fast::hash(&b[12..32]) != crate::bytes::u32_le(b, 8)
    {
        return Err(CandidateEnd::Invalid);
    }
    let start = span(32, le64(b, 12))?;
    let end = span(start as u64, le64(b, 20))?;
    if end == start {
        return Err(CandidateEnd::Invalid);
    }
    need(b, end)?;
    if !matches!(b[start], 1 | 0x17)
        || crc32fast::hash(&b[start..end]) != crate::bytes::u32_le(b, 28)
    {
        return Err(CandidateEnd::Invalid);
    }
    Ok(end)
}
fn decimal(b: &[u8]) -> Option<usize> {
    let text = std::str::from_utf8(b).ok()?.trim();
    if text.is_empty() || !text.bytes().all(|c| c.is_ascii_digit()) {
        None
    } else {
        text.parse().ok()
    }
}
fn deb(b: &[u8], cancel: &AtomicBool) -> Probe {
    need(b, 8)?;
    if !b.starts_with(b"!<arch>\n") {
        return Err(CandidateEnd::Invalid);
    }
    let mut at = 8;
    let mut stage = 0;
    for _ in 0..1024 {
        if cancel.load(Ordering::Relaxed) {
            return Err(CandidateEnd::Invalid);
        }
        need(b, at + 60)?;
        if &b[at + 58..at + 60] != b"`\n" {
            return Err(CandidateEnd::Invalid);
        }
        let name = std::str::from_utf8(&b[at..at + 16])
            .map_err(CandidateEnd::malformed)?
            .trim_end()
            .trim_end_matches('/');
        let size = decimal(&b[at + 48..at + 58]).ok_or(CandidateEnd::Invalid)?;
        let stop = span((at + 60) as u64, size as u64)?;
        need(b, stop)?;
        match stage {
            0 if name == "debian-binary"
                && size >= 4
                && b[at + 60..stop].starts_with(b"2.")
                && b[stop - 1] == b'\n' =>
            {
                stage = 1;
            }
            1 if matches!(
                name,
                "control.tar" | "control.tar.gz" | "control.tar.xz" | "control.tar.zst"
            ) && size > 0 =>
            {
                stage = 2;
            }
            2 if matches!(
                name,
                "data.tar"
                    | "data.tar.gz"
                    | "data.tar.xz"
                    | "data.tar.zst"
                    | "data.tar.bz2"
                    | "data.tar.lzma"
            ) && size > 0 =>
            {
                let end = stop + size % 2;
                need(b, end)?;
                return Ok(end);
            }
            1 | 2 if name.starts_with('_') => {}
            _ => return Err(CandidateEnd::Invalid),
        }
        at = stop + size % 2;
    }
    Err(CandidateEnd::Invalid)
}
fn rpm_header(b: &[u8], at: usize) -> Result<(usize, usize, usize), CandidateEnd> {
    need(b, at + 16)?;
    if &b[at..at + 8] != b"\x8e\xad\xe8\x01\0\0\0\0" {
        return Err(CandidateEnd::Invalid);
    }
    let count = be32(b, at + 8);
    let store = be32(b, at + 12);
    if count == 0 || count > 65536 {
        return Err(CandidateEnd::Invalid);
    }
    let data = span((at + 16) as u64, count as u64 * 16)?;
    let end = span(data as u64, store as u64)?;
    need(b, end)?;
    for i in 0..count {
        if be32(b, at + 16 + i * 16 + 8) > store {
            return Err(CandidateEnd::Invalid);
        }
    }
    Ok((count, data, end))
}
fn rpm(b: &[u8]) -> Probe {
    need(b, 112)?;
    if !b.starts_with(b"\xed\xab\xee\xdb") || b[4] != 3 || b[5] != 0 || le16(b, 78) != 0x0500 {
        return Err(CandidateEnd::Invalid);
    }
    let (count, data, signature_end) = rpm_header(b, 96)?;
    let main = signature_end.div_ceil(8) * 8;
    let (_, _, header_end) = rpm_header(b, main)?;
    let mut size = None;
    for i in 0..count {
        let p = 112 + i * 16;
        let tag = be32(b, p);
        let kind = be32(b, p + 4);
        let off = be32(b, p + 8);
        let n = be32(b, p + 12);
        let width = match (tag, kind, n) {
            (1000, 4, 1) => 4,
            (270, 5, 1) => 8,
            _ => continue,
        };
        if data + off + width > signature_end {
            return Err(CandidateEnd::Invalid);
        }
        let value = if width == 4 {
            be32(b, data + off) as u64
        } else {
            crate::bytes::u64_be(b, data + off)
        };
        if size.is_some_and(|old| old != value) {
            return Err(CandidateEnd::Invalid);
        }
        size = Some(value);
    }
    let end = span(main as u64, size.ok_or(CandidateEnd::Invalid)?)?;
    if end <= header_end {
        return Err(CandidateEnd::Invalid);
    }
    need(b, end)?;
    Ok(end)
}

fn cab(b: &[u8], cancel: &AtomicBool) -> Probe {
    need(b, 36)?;
    let size = le32(b, 8);
    let files = le32(b, 16);
    let folders = le16(b, 26);
    let count = le16(b, 28);
    let flags = le16(b, 30);
    if !b.starts_with(b"MSCF")
        || le32(b, 4) != 0
        || le32(b, 12) != 0
        || le32(b, 20) != 0
        || b[24] != 3
        || b[25] != 1
        || folders == 0
        || folders > 4096
        || count == 0
        || flags & !4 != 0
        || size < 36
    {
        return Err(CandidateEnd::Invalid);
    }
    let mut at = 36;
    let mut folder_reserved = 0;
    let mut data_reserved = 0;
    if flags & 4 != 0 {
        need(b, 40)?;
        folder_reserved = b[38] as usize;
        data_reserved = b[39] as usize;
        at = 40 + le16(b, 36);
    }
    let folder_end = at + folders * (8 + folder_reserved);
    if files < folder_end || files >= size {
        return Err(CandidateEnd::Invalid);
    }
    need(b, size)?;
    let mut file_at = files;
    for _ in 0..count {
        if cancel.load(Ordering::Relaxed) {
            return Err(CandidateEnd::Invalid);
        }
        if file_at + 16 >= size || le16(b, file_at + 8) >= folders {
            return Err(CandidateEnd::Invalid);
        }
        let name = b[file_at + 16..size]
            .iter()
            .position(|c| *c == 0)
            .ok_or(CandidateEnd::Invalid)?;
        if name == 0 {
            return Err(CandidateEnd::Invalid);
        }
        file_at += 17 + name;
    }
    for _ in 0..folders {
        let mut data = le32(b, at);
        let blocks = le16(b, at + 4);
        let compression = le16(b, at + 6) & 15;
        if data < file_at || compression > 3 {
            return Err(CandidateEnd::Invalid);
        }
        for _ in 0..blocks {
            if cancel.load(Ordering::Relaxed) {
                return Err(CandidateEnd::Invalid);
            }
            if data + 8 + data_reserved > size {
                return Err(CandidateEnd::Invalid);
            }
            let packed = le16(b, data + 4);
            let unpacked = le16(b, data + 6);
            if packed == 0 || unpacked == 0 || unpacked > 32768 {
                return Err(CandidateEnd::Invalid);
            }
            data += 8 + data_reserved + packed;
            if data > size {
                return Err(CandidateEnd::Invalid);
            }
        }
        at += 8 + folder_reserved;
    }
    Ok(size)
}
fn vint(b: &[u8], at: &mut usize) -> Result<u64, CandidateEnd> {
    let mut value = 0;
    for shift in (0..70).step_by(7) {
        need(b, *at + 1)?;
        let byte = b[*at];
        *at += 1;
        if shift == 63 && byte > 1 {
            return Err(CandidateEnd::Invalid);
        }
        value |= u64::from(byte & 127) << shift;
        if byte & 128 == 0 {
            return Ok(value);
        }
    }
    Err(CandidateEnd::Invalid)
}
fn rar(b: &[u8], cancel: &AtomicBool) -> Probe {
    need(b, 8)?;
    if !b.starts_with(b"Rar!\x1a\x07\x01\0") {
        return Err(CandidateEnd::Invalid);
    }
    let mut at = 8;
    let mut main = false;
    for _ in 0..100_000 {
        if cancel.load(Ordering::Relaxed) {
            return Err(CandidateEnd::Invalid);
        }
        need(b, at + 5)?;
        let mut p = at + 4;
        let length = vint(b, &mut p)?;
        if p > at + 7 || length > 2 * 1024 * 1024 {
            return Err(CandidateEnd::Invalid);
        }
        let end = span(p as u64, length)?;
        need(b, end)?;
        if crc32fast::hash(&b[at + 4..end]) != crate::bytes::u32_le(b, at) {
            return Err(CandidateEnd::Invalid);
        }
        // Parse within this header only; malformed fields must not consume data.
        let header = &b[..end];
        let kind = vint(header, &mut p).map_err(CandidateEnd::malformed)?;
        let flags = vint(header, &mut p).map_err(CandidateEnd::malformed)?;
        if flags & 0x18 != 0 || flags & !0x7f != 0 {
            return Err(CandidateEnd::Invalid);
        }
        let extra = if flags & 1 != 0 {
            vint(header, &mut p).map_err(CandidateEnd::malformed)?
        } else {
            0
        };
        let data = if flags & 2 != 0 {
            vint(header, &mut p).map_err(CandidateEnd::malformed)?
        } else {
            0
        };
        if extra > end.saturating_sub(p) as u64 {
            return Err(CandidateEnd::Invalid);
        }
        match kind {
            1 if !main && at == 8 => {
                let archive_flags = vint(header, &mut p).map_err(CandidateEnd::malformed)?;
                if archive_flags & 1 != 0 {
                    return Err(CandidateEnd::Invalid);
                }
                main = true;
            }
            2 | 3 if main => {
                let f = vint(header, &mut p).map_err(CandidateEnd::malformed)?;
                vint(header, &mut p).map_err(CandidateEnd::malformed)?;
                vint(header, &mut p).map_err(CandidateEnd::malformed)?;
                if f & 2 != 0 {
                    p += 4;
                }
                if f & 4 != 0 {
                    p += 4;
                }
                if p > header.len() {
                    return Err(CandidateEnd::Invalid);
                }
                vint(header, &mut p).map_err(CandidateEnd::malformed)?;
                vint(header, &mut p).map_err(CandidateEnd::malformed)?;
                let name = vint(header, &mut p).map_err(CandidateEnd::malformed)?;
                if name == 0 || name > end.saturating_sub(p) as u64 {
                    return Err(CandidateEnd::Invalid);
                }
                p += to_usize(name);
            }
            5 if main && data == 0 => {
                if vint(header, &mut p).map_err(CandidateEnd::malformed)? != 0
                    || p + to_usize(extra) != end
                {
                    return Err(CandidateEnd::Invalid);
                }
                return Ok(end);
            }
            _ => return Err(CandidateEnd::Invalid),
        }
        if p + to_usize(extra) > end {
            return Err(CandidateEnd::Invalid);
        }
        at = span(end as u64, data)?;
        need(b, at)?;
    }
    Err(CandidateEnd::Invalid)
}
fn octal(b: &[u8]) -> Option<usize> {
    let text = std::str::from_utf8(b).ok()?.trim_matches(['\0', ' ']);
    if text.is_empty() {
        return Some(0);
    }
    usize::from_str_radix(text, 8).ok()
}
fn tar(b: &[u8], cancel: &AtomicBool) -> Probe {
    let mut at = 0;
    for entries in 0..100_000 {
        if cancel.load(Ordering::Relaxed) {
            return Err(CandidateEnd::Invalid);
        }
        need(b, at + 512)?;
        let header = &b[at..at + 512];
        if header == [0; 512] {
            need(b, at + 1024)?;
            return if entries > 0 && b[at + 512..at + 1024] == [0; 512] {
                Ok(at + 1024)
            } else {
                Err(CandidateEnd::Invalid)
            };
        }
        if header[0] == 0
            || &header[257..262] != b"ustar"
            || !matches!(header[156], 0 | b'0'..=b'7' | b'x' | b'g' | b'L' | b'K')
        {
            return Err(CandidateEnd::Invalid);
        }
        let sum: usize = header
            .iter()
            .enumerate()
            .map(|(i, c)| {
                if (148..156).contains(&i) {
                    32
                } else {
                    *c as usize
                }
            })
            .sum();
        if octal(&header[148..156]) != Some(sum) {
            return Err(CandidateEnd::Invalid);
        }
        let size = octal(&header[124..136]).ok_or(CandidateEnd::Invalid)?;
        at = span((at + 512) as u64, (size as u64).div_ceil(512) * 512)?;
    }
    Err(CandidateEnd::Invalid)
}
fn qoi(b: &[u8], cancel: &AtomicBool) -> Probe {
    need(b, 14)?;
    let width = be32(b, 4);
    let height = be32(b, 8);
    if !b.starts_with(b"qoif")
        || width == 0
        || height == 0
        || width > 16384
        || height > 16384
        || !matches!(b[12], 3 | 4)
        || b[13] > 1
    {
        return Err(CandidateEnd::Invalid);
    }
    let mut pixels = width as u64 * height as u64;
    if pixels > 64 * 1024 * 1024 {
        return Err(CandidateEnd::Invalid);
    }
    let mut at: usize = 14;
    while pixels > 0 {
        if at.is_multiple_of(4096) && cancel.load(Ordering::Relaxed) {
            return Err(CandidateEnd::Invalid);
        }
        need(b, at + 1)?;
        let op = b[at];
        let (bytes, run) = match op {
            0xfe => (4, 1),
            0xff => (5, 1),
            0x80..=0xbf => (2, 1),
            0xc0..=0xfd => (1, u64::from(op & 63) + 1),
            _ => (1, 1),
        };
        if run > pixels {
            return Err(CandidateEnd::Invalid);
        }
        at += bytes;
        need(b, at)?;
        pixels -= run;
    }
    need(b, at + 8)?;
    if b[at..at + 8] != [0, 0, 0, 0, 0, 0, 0, 1] {
        return Err(CandidateEnd::Invalid);
    }
    Ok(at + 8)
}
fn dex(b: &[u8]) -> Probe {
    need(b, 112)?;
    if !b.starts_with(b"dex\n")
        || !matches!(
            &b[4..8],
            b"035\0" | b"037\0" | b"038\0" | b"039\0" | b"040\0"
        )
        || le32(b, 36) != 112
        || le32(b, 40) != 0x1234_5678
    {
        return Err(CandidateEnd::Invalid);
    }
    let size = le32(b, 32);
    if size < 112 {
        return Err(CandidateEnd::Invalid);
    }
    need(b, size)?;
    for (p, width) in [
        (56, 4),
        (64, 4),
        (72, 12),
        (80, 8),
        (88, 8),
        (96, 32),
        (104, 1),
    ] {
        let count = le32(b, p);
        let offset = le32(b, p + 4);
        if count > 0 && (offset < 112 || offset as u64 + count as u64 * width > size as u64) {
            return Err(CandidateEnd::Invalid);
        }
    }
    let map = le32(b, 52);
    if map < 112 || map + 4 > size {
        return Err(CandidateEnd::Invalid);
    }
    if map as u64 + 4 + le32(b, map) as u64 * 12 > size as u64 {
        return Err(CandidateEnd::Invalid);
    }
    let (mut a, mut c) = (1u32, 0u32);
    for chunk in b[12..size].chunks(5552) {
        for byte in chunk {
            a += u32::from(*byte);
            c += a;
        }
        a %= 65521;
        c %= 65521;
    }
    if (c << 16 | a) != crate::bytes::u32_le(b, 8) {
        return Err(CandidateEnd::Invalid);
    }
    Ok(size)
}
