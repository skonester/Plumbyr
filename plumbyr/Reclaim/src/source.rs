use crate::bytes::to_usize;
use std::io::{self, Read, Seek, SeekFrom};

/// Adapts byte-level carving to devices that require sector-aligned reads.
/// The underlying handle is read-only; seeks here are logical, not raw seeks.
pub struct SourceReader<R> {
    inner: R,
    position: u64,
    size: Option<u64>,
    alignment: usize,
    cache: Vec<u8>,
    cache_start: usize,
    window_size: usize,
    cached_at: Option<u64>,
    cached_len: usize,
}
impl<R> std::fmt::Debug for SourceReader<R> {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SourceReader")
            .field("position", &self.position)
            .field("size", &self.size)
            .field("alignment", &self.alignment)
            .finish_non_exhaustive()
    }
}
impl<R: Read + Seek> SourceReader<R> {
    pub fn new(inner: R, alignment: usize, size: Option<u64>) -> io::Result<Self> {
        if !alignment.is_power_of_two() || alignment > 65536 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidInput,
                "Invalid source sector size",
            ));
        }
        let window_size = if alignment == 1 { 0 } else { 1024 * 1024 };
        // audit-allow(DOS-ALLOC): at most 1 MiB plus an alignment checked to be <= 64 KiB.
        let cache = vec![0; window_size + alignment];
        let cache_start = (alignment - (cache.as_ptr() as usize % alignment)) % alignment;
        Ok(Self {
            inner,
            position: 0,
            size,
            alignment,
            cache,
            cache_start,
            window_size,
            cached_at: None,
            cached_len: 0,
        })
    }
}
impl<R: Read + Seek> Read for SourceReader<R> {
    fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
        if bytes.is_empty() || self.size.is_some_and(|n| self.position >= n) {
            return Ok(0);
        }
        if self.alignment == 1 {
            let mut count = bytes.len();
            if let Some(size) = self.size {
                count = count.min(to_usize((size - self.position).min(usize::MAX as u64)));
            }
            self.inner.seek(SeekFrom::Start(self.position))?;
            let count = self.inner.read(&mut bytes[..count])?;
            if count == 0 && self.size.is_some_and(|size| self.position < size) {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "The source stopped returning data before its reported end. It may be truncated, disconnected, or restricted by the operating system. Files already found can still be recovered.",
                ));
            }
            self.position += count as u64;
            self.cached_at = None;
            return Ok(count);
        }
        let aligned = self.position / self.alignment as u64 * self.alignment as u64;
        if self.cached_at.is_none_or(|start| {
            self.position < start || self.position >= start + self.cached_len as u64
        }) {
            self.inner.seek(SeekFrom::Start(aligned))?;
            let mut required = self.size.map_or(self.window_size, |n| {
                to_usize((n - aligned).min(self.window_size as u64))
            });
            // A failed refill must not expose new bytes under the old offset.
            self.cached_at = None;
            self.cached_len = 0;
            while self.cached_len < required {
                match self.inner.read(
                    &mut self.cache
                        [self.cache_start + self.cached_len..self.cache_start + required],
                ) {
                    Ok(0) if self.size.is_some() && self.cached_len < required => {
                        return Err(io::Error::new(
                            io::ErrorKind::UnexpectedEof,
                            "The source stopped returning data before its reported end. It may be truncated, disconnected, or restricted by the operating system. Files already found can still be recovered.",
                        ));
                    }
                    Ok(0) => break,
                    Ok(n) => self.cached_len += n,
                    Err(e) if e.kind() == io::ErrorKind::Interrupted => {}
                    Err(e) if is_media_error(&e) && required > self.alignment => {
                        // A large read may include a bad sector beyond the requested bytes.
                        // Retry only the sector at the current position before failing.
                        required = self.alignment;
                        self.cached_len = 0;
                        self.inner.seek(SeekFrom::Start(aligned))?;
                    }
                    Err(e) => return Err(e),
                }
            }
            self.cached_at = Some(aligned);
        }
        let Some(cached_at) = self.cached_at else {
            return Ok(0);
        };
        let start = to_usize(self.position - cached_at);
        let count = bytes.len().min(self.cached_len.saturating_sub(start));
        if count == 0 {
            return Ok(0);
        }
        bytes[..count].copy_from_slice(
            &self.cache[self.cache_start + start..self.cache_start + start + count],
        );
        self.position += count as u64;
        Ok(count)
    }
}
impl<R: Read + Seek> Seek for SourceReader<R> {
    fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
        let position = match from {
            SeekFrom::Start(n) => n,
            SeekFrom::Current(n) => self.position.checked_add_signed(n).ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidInput, "Invalid source offset")
            })?,
            SeekFrom::End(n) => {
                let size = if let Some(size) = self.size {
                    size
                } else {
                    self.inner.seek(SeekFrom::End(0))?
                };
                size.checked_add_signed(n).ok_or_else(|| {
                    io::Error::new(io::ErrorKind::InvalidInput, "Invalid source offset")
                })?
            }
        };
        self.position = position;
        Ok(position)
    }
}

/// Only media read errors are skippable; permission, seek and programming errors remain fatal.
pub(crate) fn is_media_error(error: &io::Error) -> bool {
    #[cfg(windows)]
    return matches!(error.raw_os_error(), Some(23 | 27 | 1117));
    #[cfg(not(windows))]
    matches!(error.raw_os_error(), Some(5))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn failed_partial_refill_does_not_relabel_new_bytes_with_the_old_offset() {
        struct FailingReader {
            data: io::Cursor<Vec<u8>>,
            eof: bool,
        }
        impl Read for FailingReader {
            fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
                if self.data.position() >= 1024 * 1024 + 512 {
                    return if self.eof {
                        Ok(0)
                    } else {
                        Err(io::Error::new(
                            io::ErrorKind::PermissionDenied,
                            "read failed",
                        ))
                    };
                }
                let count = if self.data.position() >= 1024 * 1024 {
                    bytes.len().min(512)
                } else {
                    bytes.len()
                };
                self.data.read(&mut bytes[..count])
            }
        }
        impl Seek for FailingReader {
            fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
                self.data.seek(from)
            }
        }
        for alignment in [512, 4096] {
            for eof in [false, true] {
                let mut data = vec![1; 2 * 1024 * 1024];
                data[1024 * 1024..].fill(2);
                let input = FailingReader {
                    data: io::Cursor::new(data),
                    eof,
                };
                let mut reader =
                    SourceReader::new(input, alignment, Some(2 * 1024 * 1024)).unwrap();
                let mut byte = [0];
                reader.read_exact(&mut byte).unwrap();
                assert_eq!(byte, [1]);
                reader.seek(SeekFrom::Start(1024 * 1024)).unwrap();
                assert_eq!(
                    reader.read(&mut byte).unwrap_err().kind(),
                    if eof {
                        io::ErrorKind::UnexpectedEof
                    } else {
                        io::ErrorKind::PermissionDenied
                    }
                );
                reader.seek(SeekFrom::Start(0)).unwrap();
                reader.read_exact(&mut byte).unwrap();
                assert_eq!(byte, [1], "alignment {alignment}, eof {eof}");
            }
        }
    }

    #[test]
    fn an_unreadable_nonempty_device_is_an_error_not_an_empty_scan() {
        for alignment in [1, 512, 4096] {
            let mut reader =
                SourceReader::new(io::Cursor::new(Vec::<u8>::new()), alignment, Some(4096))
                    .unwrap();
            assert_eq!(
                reader.read(&mut [0; 128]).unwrap_err().kind(),
                io::ErrorKind::UnexpectedEof
            );
        }
        let mut empty = SourceReader::new(io::Cursor::new(Vec::<u8>::new()), 1, Some(0)).unwrap();
        assert_eq!(empty.read(&mut [0; 128]).unwrap(), 0);
    }

    struct SectorDevice(io::Cursor<Vec<u8>>);
    impl Read for SectorDevice {
        #[expect(
            clippy::panic_in_result_fn,
            reason = "test double: a failed assertion is the test failure"
        )]
        fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
            assert!(self.0.position().is_multiple_of(4096));
            assert!(bytes.len().is_multiple_of(4096));
            assert!((bytes.as_ptr() as usize).is_multiple_of(4096));
            self.0.read(bytes)
        }
    }
    impl Seek for SectorDevice {
        fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
            self.0.seek(from)
        }
    }
    #[test]
    fn carve_and_export_unaligned_candidate_on_a_sector_device() {
        let mut bytes = vec![0; 12288];
        let content = b"%PDF-1.7\nexample\n%%EOF";
        bytes[4093..4093 + content.len()].copy_from_slice(content);
        let mut reader =
            SourceReader::new(SectorDevice(io::Cursor::new(bytes)), 4096, Some(12288)).unwrap();
        let mut found = Vec::new();
        crate::scan_reader(
            &mut reader,
            &std::sync::atomic::AtomicBool::new(false),
            |e| {
                if let crate::Event::Found(f) = e {
                    found.push(f);
                }
            },
        )
        .unwrap();
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].offset, 4093);
        let out_temp = tempfile::tempdir().unwrap();
        let out = out_temp.path().join("aligned.pdf");
        crate::recover_reader(&mut reader, &out, &found[0]).unwrap();
        assert_eq!(std::fs::read(&out).unwrap(), content);
        reader.seek(SeekFrom::Current(-100_000)).unwrap_err();
    }
}
