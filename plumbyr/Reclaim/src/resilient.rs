//! Tolerant carving only: allocation metadata and recovery reads remain strict.
// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]
use crate::bytes::to_usize;
use crate::{Event, source::is_media_error};
use std::{
    cell::RefCell,
    collections::BTreeMap,
    io::{self, Read, Seek, SeekFrom},
    rc::Rc,
    sync::atomic::{AtomicBool, Ordering},
};

#[derive(Default)]
struct Damage {
    ranges: BTreeMap<u64, u64>,
    pending: Vec<(u64, u64)>,
}
struct Reader<'a, R> {
    input: &'a mut R,
    damage: Rc<RefCell<Damage>>,
    cancel: &'a AtomicBool,
    /// End of the physically contiguous area holding an offset. A retry, and
    /// so a damage mark, never crosses it: in joined free space the next area
    /// is elsewhere on the disk and may be healthy.
    area_end: &'a dyn Fn(u64) -> u64,
}
impl<R: Read + Seek> Read for Reader<'_, R> {
    fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
        if bytes.is_empty() {
            return Ok(0);
        }
        if self.cancel.load(Ordering::Relaxed) {
            return Ok(0);
        }
        let at = self.input.stream_position()?;
        // Never repeatedly probe a block already known to be unreadable.
        if let Some((_, end)) = self
            .damage
            .borrow()
            .ranges
            .range(..=at)
            .next_back()
            .filter(|&(_, &end)| at < end)
        {
            let count = bytes.len().min(to_usize(end.saturating_sub(at)));
            bytes.iter_mut().take(count).for_each(|byte| *byte = 0);
            self.input.seek(SeekFrom::Start(offset_after(at, count)?))?;
            return Ok(count);
        }
        let before_next = self
            .damage
            .borrow()
            .ranges
            .range(at..)
            .next()
            .map_or(bytes.len(), |(&start, _)| {
                to_usize(start.saturating_sub(at).min(bytes.len() as u64))
            });
        let Some(bytes) = bytes.get_mut(..before_next) else {
            return Err(io::Error::from(io::ErrorKind::InvalidInput));
        };
        match self.input.read(bytes) {
            Err(error) if is_media_error(&error) => {
                self.input.seek(SeekFrom::Start(at))?;
                // Retry one bounded block. Preserve healthy data surrounding the fault.
                let count = bytes
                    .len()
                    .min(to_usize(4096u64.saturating_sub(at % 4096)))
                    .min(to_usize((self.area_end)(at).saturating_sub(at)).max(1));
                let Some(block) = bytes.get_mut(..count) else {
                    return Err(io::Error::from(io::ErrorKind::InvalidInput));
                };
                match self.input.read(block) {
                    Err(error) if is_media_error(&error) => {
                        let end = offset_after(at, count)?;
                        let mut damage = self.damage.borrow_mut();
                        if damage.ranges.len() >= 1_000_000 {
                            return Err(io::Error::other(
                                "Too many unreadable ranges to track safely",
                            ));
                        }
                        let mut start = at;
                        let mut stop = end;
                        if let Some((&previous, &previous_end)) =
                            damage.ranges.range(..at).next_back()
                            && previous_end == at
                        {
                            start = previous;
                            damage.ranges.remove(&previous);
                        }
                        if let Some(next_end) = damage.ranges.remove(&end) {
                            stop = next_end;
                        }
                        damage.ranges.insert(start, stop);
                        damage.pending.push((at, count as u64));
                        bytes.iter_mut().take(count).for_each(|byte| *byte = 0);
                        self.input.seek(SeekFrom::Start(end))?;
                        Ok(count)
                    }
                    result => result,
                }
            }
            result => result,
        }
    }
}
/// The source offset just past `count` bytes read at `at`.
fn offset_after(at: u64, count: usize) -> io::Result<u64> {
    at.checked_add(count as u64)
        .ok_or_else(|| io::Error::from(io::ErrorKind::InvalidInput))
}
impl<R: Seek> Seek for Reader<'_, R> {
    fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
        self.input.seek(from)
    }
}
pub(crate) fn scan(
    input: &mut (impl Read + Seek),
    cancel: &AtomicBool,
    mut event: impl FnMut(Event),
    buffer_size: usize,
    area_end: &dyn Fn(u64) -> u64,
) -> io::Result<()> {
    let damage = Rc::new(RefCell::new(Damage::default()));
    let mut reader = Reader {
        input,
        damage: Rc::clone(&damage),
        cancel,
        area_end,
    };
    let result = crate::scan_reader_with_filter(
        &mut reader,
        cancel,
        |e| {
            let mut damage = damage.borrow_mut();
            for (offset, bytes) in damage.pending.drain(..) {
                event(Event::Unreadable { offset, bytes });
            }
            drop(damage);
            event(e);
        },
        buffer_size,
        |offset, bytes| {
            let end = offset.saturating_add(bytes);
            !damage
                .borrow()
                .ranges
                .iter()
                .any(|(&start, &stop)| offset < stop && start < end)
        },
    );
    for (offset, bytes) in damage.borrow_mut().pending.drain(..) {
        event(Event::Unreadable { offset, bytes });
    }
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    struct Faulty {
        data: io::Cursor<Vec<u8>>,
        bad: std::ops::Range<u64>,
        reads: usize,
        error: i32,
    }
    impl Read for Faulty {
        fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
            self.reads += 1;
            let at = self.data.position();
            if at < self.bad.end && self.bad.start < at + bytes.len() as u64 {
                return Err(io::Error::from_raw_os_error(self.error));
            }
            self.data.read(bytes)
        }
    }
    impl Seek for Faulty {
        fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
            self.data.seek(from)
        }
    }
    fn device() -> Faulty {
        let mut bytes = vec![0; 32768];
        let pdf = b"%PDF-1.7\nhello\n%%EOF";
        for at in [0, 12288, 28672] {
            bytes[at..at + pdf.len()].copy_from_slice(pdf);
        }
        // A syntactically complete PDF straddling the unreadable block must be discarded.
        bytes[8192 - 12..8192].copy_from_slice(b"%PDF-1.7\nabc");
        bytes[12288 - 5..12288].copy_from_slice(b"%%EOF");
        Faulty {
            data: io::Cursor::new(bytes),
            bad: 8192..12288,
            reads: 0,
            error: if cfg!(windows) { 1117 } else { 5 },
        }
    }
    #[test]
    fn continues_after_media_errors_and_excludes_files_crossing_damage() {
        for alignment in [1, 512, 4096] {
            let mut source = crate::SourceReader::new(device(), alignment, Some(32768)).unwrap();
            let mut found = Vec::new();
            let mut skipped = Vec::new();
            scan(
                &mut source,
                &AtomicBool::new(false),
                |e| match e {
                    Event::Found(f) => found.push(f.offset),
                    Event::Unreadable { offset, bytes } => skipped.push((offset, bytes)),
                    Event::UnusedSpace { .. } | Event::Progress(_) | Event::Checking { .. } => {}
                },
                32768,
                &|_| u64::MAX,
            )
            .unwrap();
            assert_eq!(found, vec![0, 12288, 28672], "alignment {alignment}");
            assert_eq!(skipped, vec![(8192, 4096)]);
        }
    }
    #[test]
    fn permission_errors_remain_fatal_and_cancellation_does_not_probe() {
        let mut source = device();
        source.error = if cfg!(windows) { 5 } else { 13 };
        assert!(
            scan(&mut source, &AtomicBool::new(false), |_| {}, 32768, &|_| {
                u64::MAX
            })
            .is_err()
        );
        let mut source = device();
        scan(&mut source, &AtomicBool::new(true), |_| {}, 32768, &|_| {
            u64::MAX
        })
        .unwrap();
        assert_eq!(source.reads, 0);
    }
    #[test]
    fn known_bad_bytes_are_not_retried_and_strict_recovery_still_fails() {
        let damage = Rc::new(RefCell::new(Damage::default()));
        let cancel = AtomicBool::new(false);
        let mut input = device();
        let mut reader = Reader {
            input: &mut input,
            damage,
            cancel: &cancel,
            area_end: &|_| u64::MAX,
        };
        reader.seek(SeekFrom::Start(8192)).unwrap();
        assert_eq!(reader.read(&mut [0; 4096]).unwrap(), 4096);
        reader.seek(SeekFrom::Start(8192)).unwrap();
        assert_eq!(reader.read(&mut [0; 4096]).unwrap(), 4096);
        drop(reader);
        assert_eq!(input.reads, 2);
        input.seek(SeekFrom::Start(8192)).unwrap();
        input.read(&mut [0; 4096]).unwrap_err();
    }
}

#[cfg(test)]
mod overlap_tests {
    use super::*;
    #[test]
    fn synthetic_padding_cannot_suppress_healthy_files_inside_a_damaged_candidate() {
        struct Fault(io::Cursor<Vec<u8>>);
        impl Read for Fault {
            fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
                let at = self.0.position();
                if at < 8192 && at + bytes.len() as u64 > 4096 {
                    return Err(io::Error::from_raw_os_error(if cfg!(windows) {
                        1117
                    } else {
                        5
                    }));
                }
                self.0.read(bytes)
            }
        }
        impl Seek for Fault {
            fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
                self.0.seek(from)
            }
        }
        let mut bytes = vec![0u8; 16384];
        bytes[..4].copy_from_slice(b"RIFF");
        bytes[4..8].copy_from_slice(&(16384u32 - 8).to_le_bytes());
        bytes[8..16].copy_from_slice(b"WAVEfmt ");
        bytes[16..20].copy_from_slice(&16u32.to_le_bytes());
        bytes[20..22].copy_from_slice(&1u16.to_le_bytes());
        bytes[22..24].copy_from_slice(&1u16.to_le_bytes());
        bytes[24..28].copy_from_slice(&8000u32.to_le_bytes());
        bytes[28..32].copy_from_slice(&8000u32.to_le_bytes());
        bytes[32..34].copy_from_slice(&1u16.to_le_bytes());
        bytes[34..36].copy_from_slice(&8u16.to_le_bytes());
        bytes[36..40].copy_from_slice(b"data");
        bytes[40..44].copy_from_slice(&(16384u32 - 44).to_le_bytes());
        let pdf = b"%PDF-1.7\nhello\n%%EOF";
        bytes[12288..12288 + pdf.len()].copy_from_slice(pdf);
        let mut found = Vec::new();
        scan(
            &mut Fault(io::Cursor::new(bytes)),
            &AtomicBool::new(false),
            |e| {
                if let Event::Found(f) = e {
                    found.push((f.offset, f.kind));
                }
            },
            16384,
            &|_| u64::MAX,
        )
        .unwrap();
        assert_eq!(found, vec![(12288, "pdf")]);
    }
}
