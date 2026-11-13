// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]
use super::{CandidateEnd, le16, need};
use crate::bytes::to_usize;
use std::sync::atomic::{AtomicBool, Ordering};
#[derive(Default)]
pub(crate) struct Gzip {
    decoder: Option<flate2::Decompress>,
    consumed: usize,
    finished: bool,
    crc: crc32fast::Hasher,
}
impl Gzip {
    pub(crate) fn push(&mut self, b: &[u8], cancel: &AtomicBool) -> CandidateEnd {
        match self.parse(b, cancel) {
            Ok(size) => CandidateEnd::Complete(size),
            Err(state) => state,
        }
    }
    fn parse(&mut self, b: &[u8], cancel: &AtomicBool) -> Result<usize, CandidateEnd> {
        if self.decoder.is_none() {
            need(b, 10)?;
            let flags = b.get(3).copied().ok_or(CandidateEnd::More)?;
            if !b.starts_with(b"\x1f\x8b\x08") || flags & 0xe0 != 0 {
                return Err(CandidateEnd::Invalid);
            }
            let mut at = 10;
            if flags & 4 != 0 {
                need(b, add(at, 2)?)?;
                at = add(add(at, 2)?, le16(b, at))?;
                need(b, at)?;
            }
            for mask in [8, 16] {
                if flags & mask != 0 {
                    if at >= 65536 {
                        return Err(CandidateEnd::Invalid);
                    }
                    let available = b.len().min(65536);
                    let missing = if available == 65536 {
                        CandidateEnd::Invalid
                    } else {
                        CandidateEnd::More
                    };
                    let count = b
                        .get(at..available)
                        .and_then(|field| field.iter().position(|c| *c == 0))
                        .ok_or(missing)?;
                    at = add(add(at, count)?, 1)?;
                }
            }
            if flags & 2 != 0 {
                need(b, add(at, 2)?)?;
                let header = b.get(..at).ok_or(CandidateEnd::More)?;
                if le16(b, at) != (crc32fast::hash(header) & 0xffff) as usize {
                    return Err(CandidateEnd::Invalid);
                }
                at = add(at, 2)?;
            }
            self.consumed = at;
            self.decoder = Some(flate2::Decompress::new(false));
        }
        let Some(decoder) = self.decoder.as_mut() else {
            return Err(CandidateEnd::Invalid);
        };
        let mut output = vec![0; 32768];
        while !self.finished {
            if cancel.load(Ordering::Relaxed) {
                return Err(CandidateEnd::Invalid);
            }
            let before_in = decoder.total_in();
            let before_out = decoder.total_out();
            let status = decoder
                .decompress(
                    b.get(self.consumed..).ok_or(CandidateEnd::Invalid)?,
                    &mut output,
                    flate2::FlushDecompress::None,
                )
                .map_err(CandidateEnd::malformed)?;
            // flate2's totals only grow; a decrease would be a decoder bug.
            let consumed = to_usize(decoder.total_in().saturating_sub(before_in));
            let produced = to_usize(decoder.total_out().saturating_sub(before_out));
            self.consumed = add(self.consumed, consumed)?;
            self.crc
                .update(output.get(..produced).ok_or(CandidateEnd::Invalid)?);
            if decoder.total_out() > 256 * 1024 * 1024 {
                return Err(CandidateEnd::Invalid);
            }
            if status == flate2::Status::StreamEnd {
                self.finished = true;
                break;
            }
            if consumed == 0 && produced == 0 {
                return Err(CandidateEnd::More);
            }
        }
        let end = add(self.consumed, 8)?;
        need(b, end)?;
        // ISIZE is the uncompressed length modulo 2^32 (RFC 1952).
        let size_mod_2_32 = u32::try_from(decoder.total_out() & u64::from(u32::MAX)).ok();
        if self.crc.clone().finalize() != crate::bytes::u32_le(b, self.consumed)
            || size_mod_2_32 != Some(crate::bytes::u32_le(b, add(self.consumed, 4)?))
        {
            return Err(CandidateEnd::Invalid);
        }
        Ok(end)
    }
}

/// Offsets inside a candidate never wrap; an overflow means a hostile header.
fn add(a: usize, b: usize) -> Result<usize, CandidateEnd> {
    a.checked_add(b).ok_or(CandidateEnd::Invalid)
}
