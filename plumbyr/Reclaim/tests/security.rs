use reclaim::{Found, Validation, recover_reader, recover_unique};
use std::io::{self, Cursor};

fn candidate() -> Found {
    Found {
        offset: 0,
        size: 4,
        kind: "bin",
        selected: true,
        validation: Validation::Unverified,
        original_name: None,
        resident_data: None,
        ntfs: None,
        extents: None,
    }
}

#[test]
fn failed_exports_never_publish_partial_or_invalid_files() {
    let directory = tempfile::tempdir().unwrap();
    let destination = directory.path().join("recovered");
    let mut found = candidate();
    assert_eq!(
        recover_reader(&mut Cursor::new(b"ab"), &destination, &found)
            .unwrap_err()
            .kind(),
        io::ErrorKind::UnexpectedEof
    );
    found.validation = Validation::ImageDecoded;
    found.kind = "png";
    assert!(recover_reader(&mut Cursor::new(b"abcd"), &destination, &found).is_err());
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 0);
}

#[cfg(unix)]
#[test]
fn exports_preserve_symlink_targets_and_are_private() {
    use std::os::unix::{fs::PermissionsExt, fs::symlink};
    let directory = tempfile::tempdir().unwrap();
    let victim = directory.path().join("victim");
    let destination = directory.path().join("recovered");
    std::fs::write(&victim, b"keep").unwrap();
    symlink(&victim, &destination).unwrap();
    assert_eq!(
        recover_reader(&mut Cursor::new(b"data"), &destination, &candidate())
            .unwrap_err()
            .kind(),
        io::ErrorKind::AlreadyExists
    );
    assert_eq!(std::fs::read(&victim).unwrap(), b"keep");
    assert!(destination.is_symlink());
    let output =
        recover_unique(&mut Cursor::new(b"data"), directory.path(), &candidate(), 1).unwrap();
    assert_eq!(
        std::fs::metadata(output).unwrap().permissions().mode() & 0o777,
        0o600
    );
}

#[test]
fn public_candidate_names_cannot_escape_export_directory_or_spoof_direction() {
    let directory = tempfile::tempdir().unwrap();
    let mut found = candidate();
    for name in [
        "../../outside",
        "..\\outside",
        "report\u{202e}exe.pdf",
        "\u{2067}hidden",
    ] {
        found.original_name = Some(name.into());
        let output =
            recover_unique(&mut Cursor::new(b"data"), directory.path(), &found, 1).unwrap();
        assert_eq!(output.parent(), Some(directory.path()));
        assert!(!output.to_string_lossy().contains(['\u{202e}', '\u{2067}']));
    }
    found.original_name = None;
    found.kind = "/../../outside";
    let output = recover_unique(&mut Cursor::new(b"data"), directory.path(), &found, 1).unwrap();
    assert_eq!(output.parent(), Some(directory.path()));
}

#[test]
fn source_reader_preserves_bytes_and_checks_seek_bounds() {
    use std::io::{Read, Seek, SeekFrom};
    let data: Vec<u8> = (0..2048).map(|n| u8::try_from(n % 251).unwrap()).collect();
    for alignment in [1, 512, 4096] {
        let mut source =
            reclaim::SourceReader::new(Cursor::new(&data), alignment, Some(data.len() as u64))
                .unwrap();
        source.seek(SeekFrom::Start(511)).unwrap();
        let mut buffer = [0; 7];
        source.read_exact(&mut buffer).unwrap();
        assert_eq!(buffer, data[511..518]);
        source.seek(SeekFrom::Current(-519)).unwrap_err();
        assert_eq!(source.stream_position().unwrap(), 518);
        source.seek(SeekFrom::End(-7)).unwrap();
        source.read_exact(&mut buffer).unwrap();
        assert_eq!(buffer, data[2041..]);
        assert_eq!(source.read(&mut buffer).unwrap(), 0);
        source.seek(SeekFrom::Start(u64::MAX)).unwrap();
        source.seek(SeekFrom::Current(1)).unwrap_err();
        assert_eq!(source.read(&mut buffer).unwrap(), 0);
    }
}

#[test]
fn disconnect_after_partial_export_preserves_existing_files_and_cleans_staging() {
    use std::io::{Read, Seek, SeekFrom};
    struct Disconnect(Cursor<&'static [u8]>);
    impl Read for Disconnect {
        fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
            if self.0.position() >= 2 {
                return Err(io::Error::new(io::ErrorKind::NotConnected, "USB removed"));
            }
            let count = bytes.len().min(2);
            self.0.read(&mut bytes[..count])
        }
    }
    impl Seek for Disconnect {
        fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
            self.0.seek(from)
        }
    }
    for existing in [false, true] {
        let directory = tempfile::tempdir().unwrap();
        let destination = directory.path().join("recovered.bin");
        if existing {
            std::fs::write(&destination, b"irreplaceable").unwrap();
        }
        let error = recover_reader(
            &mut Disconnect(Cursor::new(b"data")),
            &destination,
            &candidate(),
        )
        .unwrap_err();
        assert_eq!(error.kind(), io::ErrorKind::NotConnected);
        assert_eq!(
            std::fs::read_dir(directory.path()).unwrap().count(),
            usize::from(existing)
        );
        if existing {
            assert_eq!(std::fs::read(&destination).unwrap(), b"irreplaceable");
        } else {
            assert!(!destination.exists());
        }
    }
}

#[test]
fn simultaneous_exports_never_replace_each_other_or_existing_data() {
    use std::sync::{Arc, Barrier};
    let directory = tempfile::tempdir().unwrap();
    let existing = directory.path().join(candidate().recovery_name(1));
    std::fs::write(&existing, b"irreplaceable").unwrap();
    let barrier = Arc::new(Barrier::new(8));
    let outputs = std::thread::scope(|scope| {
        // Collected first: every worker must be spawned before any is joined,
        // or the barrier never fills.
        #[expect(
            clippy::needless_collect,
            reason = "all threads must start before the first join"
        )]
        let workers: Vec<_> = (0u8..8)
            .map(|value| {
                let barrier = Arc::clone(&barrier);
                let directory = directory.path();
                scope.spawn(move || {
                    barrier.wait();
                    let data = [value; 4];
                    let path =
                        recover_unique(&mut Cursor::new(data), directory, &candidate(), 1).unwrap();
                    assert_eq!(std::fs::read(&path).unwrap(), data);
                    path
                })
            })
            .collect();
        workers
            .into_iter()
            .map(|worker| worker.join().unwrap())
            .collect::<Vec<_>>()
    });
    let unique: std::collections::HashSet<_> = outputs.iter().collect();
    assert_eq!(unique.len(), 8);
    assert_eq!(std::fs::read(&existing).unwrap(), b"irreplaceable");
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 9);
}

#[test]
fn invalid_candidate_ranges_never_touch_the_destination() {
    let directory = tempfile::tempdir().unwrap();
    let destination = directory.path().join("recovered.bin");
    for (offset, size) in [(u64::MAX, 4), (0, 0), (0, u64::MAX)] {
        let mut found = candidate();
        found.offset = offset;
        found.size = size;
        assert_eq!(
            recover_reader(&mut Cursor::new(b"data"), &destination, &found)
                .unwrap_err()
                .kind(),
            io::ErrorKind::InvalidInput
        );
        assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 0);
    }
}

#[test]
fn physical_byte_offsets_above_four_gib_and_near_u64_limit_export_exactly() {
    use std::io::{Read, Seek, SeekFrom};
    // A virtual read-only disk: no enormous allocation or physical disk write.
    struct Disk {
        position: u64,
    }
    fn byte(at: u64) -> u8 {
        at.to_le_bytes()
            .iter()
            .fold(0u8, |value, part| value.rotate_left(1) ^ part)
    }
    impl Read for Disk {
        fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
            let count = bytes
                .len()
                .min(usize::try_from(u64::MAX - self.position).unwrap_or(usize::MAX));
            for value in bytes.iter_mut().take(count) {
                *value = byte(self.position);
                self.position += 1;
            }
            Ok(count)
        }
    }
    impl Seek for Disk {
        fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
            self.position = match from {
                SeekFrom::Start(value) => Some(value),
                SeekFrom::Current(value) => self.position.checked_add_signed(value),
                SeekFrom::End(value) => u64::MAX.checked_add_signed(value),
            }
            .ok_or_else(|| io::Error::from(io::ErrorKind::InvalidInput))?;
            Ok(self.position)
        }
    }
    let directory = tempfile::tempdir().unwrap();
    for alignment in [1, 512, 4096, 65536] {
        let mut disk =
            reclaim::SourceReader::new(Disk { position: 0 }, alignment, Some(u64::MAX)).unwrap();
        for offset in [
            (1u64 << 32) - 1,
            (1u64 << 32) + 1,
            (1u64 << 63) + 511,
            u64::MAX - 4,
        ] {
            let mut found = candidate();
            found.offset = offset;
            let output = recover_unique(&mut disk, directory.path(), &found, 1).unwrap();
            let expected: Vec<_> = (offset..offset + 4).map(byte).collect();
            assert_eq!(
                std::fs::read(output).unwrap(),
                expected,
                "offset={offset}, alignment={alignment}"
            );
        }
    }
}
