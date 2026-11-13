//! Fixed-width integer fields read from untrusted bytes.
//!
//! Scanned data is attacker-controlled, so a field may lie past the end of the
//! buffer. Instead of panicking (which would stop the scan, CWE-125/CWE-248), an
//! out-of-range field reads as zero. Every format here already treats a zero
//! length, count or offset as invalid, so the candidate is rejected normally.
// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]

// Reclaim ships 64-bit builds only. This makes a build for a narrower target
// fail to compile instead of quietly depending on `to_usize`'s fallback.
const _: () = assert!(usize::BITS >= 64, "Reclaim supports 64-bit targets only");

/// A size or offset read from scanned data, as an index type. Lossless on
/// every supported target; the saturating fallback means a value that could
/// not fit is rejected by the bounds check that follows instead of wrapping.
pub(crate) fn to_usize(value: u64) -> usize {
    usize::try_from(value).unwrap_or(usize::MAX)
}

fn field<const N: usize>(bytes: &[u8], at: usize) -> [u8; N] {
    at.checked_add(N)
        .and_then(|end| bytes.get(at..end))
        .and_then(|slice| slice.try_into().ok())
        .unwrap_or([0; N])
}

pub(crate) fn u16_le(bytes: &[u8], at: usize) -> u16 {
    u16::from_le_bytes(field(bytes, at))
}
pub(crate) fn u16_be(bytes: &[u8], at: usize) -> u16 {
    u16::from_be_bytes(field(bytes, at))
}
pub(crate) fn u32_le(bytes: &[u8], at: usize) -> u32 {
    u32::from_le_bytes(field(bytes, at))
}
pub(crate) fn u32_be(bytes: &[u8], at: usize) -> u32 {
    u32::from_be_bytes(field(bytes, at))
}
pub(crate) fn u64_le(bytes: &[u8], at: usize) -> u64 {
    u64::from_le_bytes(field(bytes, at))
}
pub(crate) fn u64_be(bytes: &[u8], at: usize) -> u64 {
    u64::from_be_bytes(field(bytes, at))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fields_inside_the_buffer_decode_in_both_byte_orders() {
        let bytes = [1, 2, 3, 4, 5, 6, 7, 8];
        assert_eq!(u16_le(&bytes, 0), 0x0201);
        assert_eq!(u16_be(&bytes, 0), 0x0102);
        assert_eq!(u32_le(&bytes, 4), 0x0807_0605);
        assert_eq!(u32_be(&bytes, 4), 0x0506_0708);
        assert_eq!(u64_le(&bytes, 0), 0x0807_0605_0403_0201);
        assert_eq!(u64_be(&bytes, 0), 0x0102_0304_0506_0708);
    }

    #[test]
    fn sizes_convert_without_loss() {
        assert_eq!(to_usize(0), 0);
        assert_eq!(to_usize(u64::MAX), usize::MAX);
    }

    #[test]
    fn fields_past_the_end_read_as_zero_instead_of_panicking() {
        let bytes = [0xff; 3];
        assert_eq!(u32_le(&bytes, 0), 0);
        assert_eq!(u16_le(&bytes, 2), 0);
        assert_eq!(u64_be(&bytes, usize::MAX - 2), 0);
    }
}
