use reclaim::{Event, scan_reader};
use std::{
    io::{self, Cursor, Read, Seek, SeekFrom},
    sync::atomic::AtomicBool,
};

struct Counted {
    inner: Cursor<Vec<u8>>,
    read: usize,
}
impl Read for Counted {
    fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
        let count = self.inner.read(bytes)?;
        self.read += count;
        Ok(count)
    }
}
impl Seek for Counted {
    fn seek(&mut self, position: SeekFrom) -> io::Result<u64> {
        self.inner.seek(position)
    }
}

#[test]
fn damaged_zip_headers_do_not_trigger_repeated_full_candidate_reads() {
    let plain = include_bytes!("fixtures/plain.zip");
    let mut disk = vec![0; 8 * 1024 * 1024];
    // Plausible local entries with missing directory/next record.
    for index in 0..128 {
        let at = index * 1024;
        disk[at..at + 61].copy_from_slice(&plain[..61]);
        disk[at + 61..at + 65].copy_from_slice(b"bad!");
    }
    // Rejecting a ZIP must not skip nearby recoverable data.
    let png = include_bytes!("fixtures/real.png");
    disk[512..512 + png.len()].copy_from_slice(png);
    let last = 128 * 1024;
    disk[last..last + plain.len()].copy_from_slice(plain);
    let length = disk.len();
    let mut input = Counted {
        inner: Cursor::new(disk),
        read: 0,
    };
    let mut found = vec![];
    scan_reader(&mut input, &AtomicBool::new(false), |event| {
        if let Event::Found(file) = event {
            found.push((file.kind, file.offset));
        }
    })
    .unwrap();
    assert!(found.contains(&("png", 512)));
    assert!(found.contains(&("zip", last as u64)));
    assert!(
        input.read < length * 4,
        "Repeated ZIP lookahead read {} bytes for a {} byte disk",
        input.read,
        length
    );
}

#[test]
fn streaming_zip_entries_keep_the_fallback_detection_path() {
    let original = include_bytes!("fixtures/plain.zip");
    let central = original
        .windows(4)
        .position(|w| w == b"PK\x01\x02")
        .unwrap();
    let mut bytes = original[..central].to_vec();
    let descriptor = [b"PK\x07\x08".as_slice(), &original[14..26]].concat();
    bytes.extend_from_slice(&descriptor);
    bytes.extend_from_slice(&original[central..]);
    bytes[6..8].copy_from_slice(&8u16.to_le_bytes());
    bytes[14..26].fill(0);
    let central = central + descriptor.len();
    bytes[central + 8..central + 10].copy_from_slice(&8u16.to_le_bytes());
    let end = bytes.windows(4).position(|w| w == b"PK\x05\x06").unwrap();
    bytes[end + 16..end + 20].copy_from_slice(&(u32::try_from(central).unwrap()).to_le_bytes());
    let mut found = vec![];
    scan_reader(&mut Cursor::new(&bytes), &AtomicBool::new(false), |event| {
        if let Event::Found(file) = event {
            found.push(file);
        }
    })
    .unwrap();
    assert_eq!(found.len(), 1);
    assert_eq!((found[0].kind, found[0].size), ("zip", bytes.len() as u64));
}
