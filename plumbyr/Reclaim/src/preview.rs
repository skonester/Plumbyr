// Hardened module: no unchecked indexing or arithmetic in shipped code (CWE-125, CWE-190).
#![cfg_attr(
    not(test),
    deny(clippy::indexing_slicing, clippy::arithmetic_side_effects)
)]
use anyhow::{Context, bail};
use reclaim::Found;
use std::io::{Read, Seek, SeekFrom};
use std::sync::Arc;

pub fn supported(kind: &str) -> bool {
    matches!(
        kind,
        "png" | "jpg" | "jpeg" | "gif" | "webp" | "bmp" | "ico"
    )
}

/// A reader with its own position on the shared source handle.
///
/// A cloned handle shares one file cursor with the running scan on Linux and
/// Windows, so a seek here would silently move the scanner and corrupt both
/// reads. Previews and recovery, which run while a scan is active, use this.
pub(crate) struct Input {
    file: Arc<std::fs::File>,
    position: u64,
}
impl Input {
    #[cfg_attr(
        not(windows),
        expect(clippy::unnecessary_wraps, reason = "only the Windows reopen can fail")
    )]
    #[cfg_attr(
        windows,
        expect(
            clippy::needless_pass_by_value,
            reason = "kept elsewhere; on Windows it is reopened with its own cursor"
        )
    )]
    pub(crate) fn new(file: Arc<std::fs::File>) -> std::io::Result<Self> {
        #[cfg(windows)]
        let file = {
            use std::os::windows::io::{AsRawHandle, FromRawHandle};
            use windows_sys::Win32::{
                Foundation::{GENERIC_READ, INVALID_HANDLE_VALUE},
                Storage::FileSystem::{
                    FILE_FLAG_RANDOM_ACCESS, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE,
                    ReOpenFile,
                },
            };
            // A duplicated Windows handle shares its cursor. ReOpenFile creates
            // an independent cursor on the same authorized object, without
            // resolving the source path again or requesting write access.
            // SAFETY: file keeps the input handle alive during ReOpenFile.
            let handle = unsafe {
                ReOpenFile(
                    file.as_raw_handle(),
                    GENERIC_READ,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    FILE_FLAG_RANDOM_ACCESS,
                )
            };
            if handle == INVALID_HANDLE_VALUE {
                return Err(std::io::Error::last_os_error());
            }
            // SAFETY: ReOpenFile returned a new owned handle; transfer it once.
            let file = unsafe { std::fs::File::from_raw_handle(handle) };
            // Extended volume reads are a per-handle setting: the scan enabled
            // it on its own handle, and without it reads near the end of a
            // volume can stop early. A plain file (a disk image) refuses this
            // control, which changes nothing, so the result is not needed.
            let mut returned = 0u32;
            // SAFETY: file is a live read-only handle. The control has no input
            // or output buffers, never writes to or dismounts the volume, and
            // returned is a valid writable u32.
            unsafe {
                windows_sys::Win32::System::IO::DeviceIoControl(
                    file.as_raw_handle(),
                    windows_sys::Win32::System::Ioctl::FSCTL_ALLOW_EXTENDED_DASD_IO,
                    std::ptr::null(),
                    0,
                    std::ptr::null_mut(),
                    0,
                    &raw mut returned,
                    std::ptr::null_mut(),
                );
            }
            Arc::new(file)
        };
        Ok(Self { file, position: 0 })
    }
}
impl Read for Input {
    fn read(&mut self, buffer: &mut [u8]) -> std::io::Result<usize> {
        #[cfg(unix)]
        let count = {
            use std::os::unix::fs::FileExt;
            self.file.read_at(buffer, self.position)?
        };
        #[cfg(windows)]
        let count = {
            use std::os::windows::fs::FileExt;
            // This is safe only on our independently reopened handle:
            // seek_read changes the Windows file cursor.
            self.file.seek_read(buffer, self.position)?
        };
        self.position = self.position.saturating_add(count as u64);
        Ok(count)
    }
}
impl Seek for Input {
    fn seek(&mut self, from: SeekFrom) -> std::io::Result<u64> {
        let position = match from {
            SeekFrom::Start(value) => i128::from(value),
            // i128 holds any u64 plus any i64, so these sums cannot overflow.
            SeekFrom::Current(value) => i128::from(self.position).saturating_add(i128::from(value)),
            SeekFrom::End(value) => {
                i128::from(self.file.metadata()?.len()).saturating_add(i128::from(value))
            }
        };
        // The user sees this message translated; the conversion error adds nothing.
        #[expect(
            clippy::map_err_ignore,
            reason = "a catalog message replaces the detail"
        )]
        let position =
            u64::try_from(position).map_err(|_| std::io::Error::other("Invalid preview position"));
        self.position = position?;
        Ok(self.position)
    }
}

pub fn load(
    found: &Found,
    handle: Option<Arc<std::fs::File>>,
    alignment: usize,
    size: Option<u64>,
) -> anyhow::Result<image::RgbaImage> {
    if found.size > 32 * 1024 * 1024 {
        bail!("This image is too large to preview. Recover it to view the full image.");
    }
    let bytes = if let Some(bytes) = &found.resident_data {
        std::borrow::Cow::Borrowed(bytes.as_ref())
    } else {
        let file = handle.context("The source is no longer available.")?;
        let mut input = reclaim::SourceReader::new(Input::new(file)?, alignment, size)?;
        // audit-allow(DOS-ALLOC): previews are refused above 32 MiB at the top of this function.
        let mut bytes = vec![0; usize::try_from(found.size)?];
        reclaim::FoundReader::new(&mut input, found)
            .and_then(|mut reader| reader.read_exact(&mut bytes))?;
        std::borrow::Cow::Owned(bytes)
    };
    let mut reader = image::ImageReader::new(std::io::Cursor::new(bytes)).with_guessed_format()?;
    let mut limits = image::Limits::default();
    limits.max_image_width = Some(16384);
    limits.max_image_height = Some(16384);
    limits.max_alloc = Some(128 * 1024 * 1024);
    reader.limits(limits);
    let image = reader
        .decode()
        .ok()
        .context("This image could not be previewed. Its contents may be incomplete or damaged.")?;
    let image = if image.width() > 512 || image.height() > 512 {
        image.thumbnail(512, 512)
    } else {
        image
    }
    .to_rgba8();
    Ok(image)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn candidate(bytes: &[u8]) -> Found {
        Found {
            offset: 0,
            size: bytes.len() as u64,
            kind: "png",
            selected: false,
            validation: reclaim::Validation::Unverified,
            original_name: None,
            resident_data: Some(Arc::from(bytes)),
            ntfs: None,
            extents: None,
        }
    }

    #[test]
    fn decodes_resident_image_and_rejects_damaged_data() {
        let image = image::DynamicImage::new_rgba8(2, 3);
        let mut bytes = std::io::Cursor::new(Vec::new());
        image.write_to(&mut bytes, image::ImageFormat::Png).unwrap();
        let preview = load(&candidate(bytes.get_ref()), None, 1, None).unwrap();
        assert_eq!(preview.dimensions(), (2, 3));
        load(&candidate(b"damaged"), None, 1, None).unwrap_err();
        let mut large = candidate(b"");
        large.size = 33 * 1024 * 1024;
        assert!(
            load(&large, None, 1, None)
                .unwrap_err()
                .to_string()
                .contains("too large")
        );
    }

    #[test]
    fn positional_reads_preserve_scanner_cursor() {
        // Anonymous temporary files deny sharing on Windows, which prevents
        // ReOpenFile. Use a regular source file with the scanner's open mode.
        let directory = tempfile::tempdir().unwrap();
        let path = directory.path().join("source.bin");
        let mut file = std::fs::File::create(&path).unwrap();
        file.write_all(b"0123456789").unwrap();
        drop(file);
        let mut file = crate::drives::open_source(&path).unwrap();
        file.seek(SeekFrom::Start(7)).unwrap();
        let file = Arc::new(file);
        let mut input = Input::new(Arc::clone(&file)).unwrap();
        input.seek(SeekFrom::Start(2)).unwrap();
        let mut bytes = [0; 3];
        input.read_exact(&mut bytes).unwrap();
        assert_eq!(&bytes, b"234");
        assert_eq!(file.try_clone().unwrap().stream_position().unwrap(), 7);
    }
}
