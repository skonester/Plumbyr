//! Bounded MIDI event framing and uncompressed SWF tag framing.
//! SWF payloads are never executed or described as verified applications.
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]
use super::{CandidateEnd, Probe, be32, le16, le32, need};
use std::sync::atomic::{AtomicBool, Ordering};

struct Events<'a> {
    bytes: &'a [u8],
    at: usize,
}
impl Events<'_> {
    fn take(&mut self, size: usize) -> Result<&[u8], CandidateEnd> {
        let end = self.at.checked_add(size).ok_or(CandidateEnd::Invalid)?;
        let bytes = self.bytes.get(self.at..end).ok_or(CandidateEnd::Invalid)?;
        self.at = end;
        Ok(bytes)
    }
    fn byte(&mut self) -> Result<u8, CandidateEnd> {
        self.take(1)?.first().copied().ok_or(CandidateEnd::Invalid)
    }
    fn variable(&mut self) -> Result<usize, CandidateEnd> {
        let mut value = 0usize;
        for _ in 0..4 {
            let byte = self.byte()?;
            value = (value << 7) | usize::from(byte & 127);
            if byte & 128 == 0 {
                return Ok(value);
            }
        }
        Err(CandidateEnd::Invalid)
    }
}

fn track(bytes: &[u8], cancel: &AtomicBool) -> Result<(), CandidateEnd> {
    let mut input = Events { bytes, at: 0 };
    let mut running = None;
    for _ in 0..1_000_000 {
        if cancel.load(Ordering::Relaxed) {
            return Err(CandidateEnd::Invalid);
        }
        input.variable()?; // Delta time, at most four bytes.
        let first = input.byte()?;
        let (status, consumed) = if first < 128 {
            (running.ok_or(CandidateEnd::Invalid)?, 1usize)
        } else {
            (first, 0usize)
        };
        match status {
            0x80..=0xef => {
                running = Some(status);
                let size: usize = if matches!(status >> 4, 0xc | 0xd) {
                    1
                } else {
                    2
                };
                if input
                    .take(size.saturating_sub(consumed))?
                    .iter()
                    .any(|n| *n >= 128)
                {
                    return Err(CandidateEnd::Invalid);
                }
            }
            0xff => {
                running = None;
                let kind = input.byte()?;
                if kind >= 128 {
                    return Err(CandidateEnd::Invalid);
                }
                let length = input.variable()?;
                input.take(length)?;
                if kind == 0x2f {
                    return if length == 0 && input.at == bytes.len() {
                        Ok(())
                    } else {
                        Err(CandidateEnd::Invalid)
                    };
                }
            }
            0xf0 | 0xf7 => {
                running = None;
                let length = input.variable()?;
                input.take(length)?;
            }
            _ => return Err(CandidateEnd::Invalid),
        }
    }
    Err(CandidateEnd::Invalid)
}

pub(super) fn midi(bytes: &[u8], cancel: &AtomicBool) -> Probe {
    need(bytes, 14)?;
    let header = be32(bytes, 4);
    let format = crate::bytes::u16_be(bytes, 8);
    let tracks = crate::bytes::u16_be(bytes, 10);
    let division = crate::bytes::u16_be(bytes, 12);
    let [frames, ticks] = division.to_be_bytes();
    if !bytes.starts_with(b"MThd")
        || header < 6
        || format > 2
        || !(1..=4096).contains(&tracks)
        || (format == 0 && tracks != 1)
        || division == 0
        || (division & 0x8000 != 0 && (!matches!(frames, 0xe8 | 0xe7 | 0xe3 | 0xe2) || ticks == 0))
    {
        return Err(CandidateEnd::Invalid);
    }
    let mut at = header.checked_add(8).ok_or(CandidateEnd::Invalid)?;
    need(bytes, at)?;
    for _ in 0..tracks {
        if cancel.load(Ordering::Relaxed) {
            return Err(CandidateEnd::Invalid);
        }
        let body = at.checked_add(8).ok_or(CandidateEnd::Invalid)?;
        need(bytes, body)?;
        if bytes.get(at..at.saturating_add(4)) != Some(b"MTrk") {
            return Err(CandidateEnd::Invalid);
        }
        let end = body
            .checked_add(be32(bytes, at.saturating_add(4)))
            .ok_or(CandidateEnd::Invalid)?;
        need(bytes, end)?;
        track(bytes.get(body..end).ok_or(CandidateEnd::Invalid)?, cancel)?;
        at = end;
    }
    Ok(at)
}

pub(super) fn swf(bytes: &[u8], cancel: &AtomicBool) -> Probe {
    need(bytes, 9)?;
    let version = bytes.get(3).copied().ok_or(CandidateEnd::Invalid)?;
    let end = le32(bytes, 4);
    if !bytes.starts_with(b"FWS") || !(1..=19).contains(&version) || end < 16 {
        return Err(CandidateEnd::Invalid);
    }
    need(bytes, end)?;
    let rect_bits = usize::from(bytes.get(8).copied().ok_or(CandidateEnd::Invalid)? >> 3);
    if rect_bits == 0 {
        return Err(CandidateEnd::Invalid);
    }
    let rect_bytes = rect_bits.saturating_mul(4).saturating_add(5).div_ceil(8);
    let mut at = 8usize.saturating_add(rect_bytes);
    let frames = le16(bytes, at.saturating_add(2));
    if frames == 0 || le16(bytes, at) == 0 {
        return Err(CandidateEnd::Invalid);
    }
    at = at.saturating_add(4);
    let mut shown = 0usize;
    for index in 0..1_000_000 {
        if cancel.load(Ordering::Relaxed) || at.saturating_add(2) > end {
            return Err(CandidateEnd::Invalid);
        }
        let tag = le16(bytes, at);
        let code = tag >> 6;
        let mut size = tag & 63;
        at = at.saturating_add(2);
        if size == 63 {
            if at.saturating_add(4) > end {
                return Err(CandidateEnd::Invalid);
            }
            size = le32(bytes, at);
            at = at.saturating_add(4);
        }
        at = at
            .checked_add(size)
            .filter(|n| *n <= end)
            .ok_or(CandidateEnd::Invalid)?;
        if index == 0 && version >= 8 && (code != 69 || size != 4) {
            return Err(CandidateEnd::Invalid);
        }
        if code == 0 {
            return if size == 0 && at == end && shown == frames {
                Ok(end)
            } else {
                Err(CandidateEnd::Invalid)
            };
        }
        if code == 1 {
            if size != 0 {
                return Err(CandidateEnd::Invalid);
            }
            shown = shown.saturating_add(1);
        }
    }
    Err(CandidateEnd::Invalid)
}
