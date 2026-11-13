use reclaim::{Event, Found, Validation, recover_reader, scan_reader};
use std::io::{self, Cursor, Read, Seek, SeekFrom};
use std::sync::atomic::AtomicBool;

#[cfg(test)]
fn collect(input: &mut (impl Read + Seek)) -> Vec<Found> {
    let mut found = Vec::new();
    scan_reader(input, &AtomicBool::new(false), |e| {
        if let Event::Found(f) = e {
            found.push(f);
        }
    })
    .unwrap();
    found
}
fn scan(bytes: &[u8]) -> Vec<Found> {
    collect(&mut Cursor::new(bytes))
}

const FIXTURES: &[(&str, &[u8])] = &[
    ("sqlite", include_bytes!("fixtures/real.sqlite")),
    ("sqlite", include_bytes!("fixtures/large-page.sqlite")),
    ("ogg", include_bytes!("fixtures/real.ogg")),
    ("opus", include_bytes!("fixtures/real.opus")),
    ("ogv", include_bytes!("fixtures/real.ogv")),
];

#[test]
fn real_containers_cross_scan_boundaries_and_export_exactly() {
    let directory = tempfile::tempdir().unwrap();
    for (index, (kind, bytes)) in FIXTURES.iter().enumerate() {
        let prefix = 1024 * 1024 - 7;
        let mut image = vec![0; prefix];
        image.extend_from_slice(bytes);
        image.extend_from_slice(&[0; 19]);
        let found = scan(&image);
        assert_eq!(found.len(), 1, "{kind}: {found:?}");
        let file = &found[0];
        assert_eq!(
            (file.kind, file.offset, file.size),
            (*kind, prefix as u64, bytes.len() as u64)
        );
        assert_eq!(file.validation, Validation::StructureChecked);
        let output = directory.path().join(format!("{index}.{kind}"));
        recover_reader(&mut Cursor::new(&image), &output, file).unwrap();
        assert_eq!(std::fs::read(output).unwrap(), *bytes);
        assert!(
            scan(&bytes[..bytes.len() - 1])
                .iter()
                .all(|f| f.kind != *kind)
        );
    }
}

struct ShortReads<'a>(Cursor<&'a [u8]>);
impl Read for ShortReads<'_> {
    fn read(&mut self, output: &mut [u8]) -> io::Result<usize> {
        let count = output.len().min(3);
        self.0.read(&mut output[..count])
    }
}
impl Seek for ShortReads<'_> {
    fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
        self.0.seek(from)
    }
}

#[test]
fn arbitrarily_short_reads_preserve_long_and_short_signatures() {
    for bytes in [
        include_bytes!("fixtures/real.sqlite").as_slice(),
        include_bytes!("fixtures/real.opus").as_slice(),
        include_bytes!("fixtures/real.png").as_slice(),
    ] {
        let mut input = b"padding".to_vec();
        input.extend_from_slice(bytes);
        let expected = scan(&input);
        let actual = collect(&mut ShortReads(Cursor::new(&input)));
        assert_eq!(actual.len(), 1);
        assert_eq!(
            (actual[0].kind, actual[0].offset, actual[0].size),
            (expected[0].kind, 7, bytes.len() as u64)
        );
    }
}

#[test]
fn sqlite_rejects_untrustworthy_sizes_headers_and_root_directories() {
    let real = include_bytes!("fixtures/real.sqlite");
    for (at, replacement) in [
        (16, vec![0, 3]),
        (18, vec![3]),
        (21, vec![0]),
        (28, u32::MAX.to_be_bytes().to_vec()),
        (92, u32::MAX.to_be_bytes().to_vec()),
        (56, 7u32.to_be_bytes().to_vec()),
        (72, vec![1]),
        (100, vec![2]),
        (103, u16::MAX.to_be_bytes().to_vec()),
        (105, vec![0, 1]),
        (107, vec![61]),
    ] {
        let mut corrupt = real.to_vec();
        corrupt[at..at + replacement.len()].copy_from_slice(&replacement);
        assert!(
            scan(&corrupt).iter().all(|f| f.kind != "sqlite"),
            "offset {at}"
        );
    }
}

// Independent bit-at-a-time reference CRC to build valid framing mutations.
fn fix_crc(page: &mut [u8]) {
    page[22..26].fill(0);
    let mut crc = 0u32;
    for &byte in page.iter() {
        crc ^= u32::from(byte) << 24;
        for _ in 0..8 {
            crc = (crc << 1)
                ^ if crc & 0x8000_0000 != 0 {
                    0x04c1_1db7
                } else {
                    0
                };
        }
    }
    page[22..26].copy_from_slice(&crc.to_le_bytes());
}
#[cfg(test)]
fn page(serial: u32, sequence: u32, flags: u8, lacing: &[u8], body: &[u8]) -> Vec<u8> {
    assert_eq!(
        lacing.iter().map(|&n| usize::from(n)).sum::<usize>(),
        body.len()
    );
    let mut bytes = vec![0; 27];
    bytes[..4].copy_from_slice(b"OggS");
    bytes[5] = flags;
    bytes[14..18].copy_from_slice(&serial.to_le_bytes());
    bytes[18..22].copy_from_slice(&sequence.to_le_bytes());
    bytes[26] = u8::try_from(lacing.len()).unwrap();
    bytes.extend_from_slice(lacing);
    bytes.extend_from_slice(body);
    fix_crc(&mut bytes);
    bytes
}

#[test]
fn ogg_validates_crc_sequence_and_packet_continuation() {
    let start = page(42, 0, 2, &[255], &[7; 255]);
    let end = page(42, 1, 5, &[1], &[8]);
    let bytes = [start.as_slice(), end.as_slice()].concat();
    assert_eq!(scan(&bytes).len(), 1);
    for offset in [4, 5, 14, 18, 22, 27, bytes.len() - 1] {
        let mut corrupt = bytes.clone();
        corrupt[offset] ^= 0x40;
        assert!(scan(&corrupt).is_empty(), "corruption at {offset}");
    }
    for invalid in [
        page(42, 2, 5, &[1], &[8]),
        page(42, 1, 4, &[1], &[8]),
        page(42, 1, 5, &[255], &[8; 255]),
        page(99, 1, 5, &[1], &[8]),
    ] {
        assert!(scan(&[start.as_slice(), invalid.as_slice()].concat()).is_empty());
    }
}

#[test]
fn multiplexed_ogg_waits_for_all_streams_and_incremental_pages() {
    let mut bytes = page(1, 0, 2, &[1], b"a");
    bytes.extend(page(2, 0, 2, &[1], b"b"));
    for sequence in 1..100 {
        bytes.extend(page(1, sequence, 0, &[200], &[0x55; 200]));
    }
    bytes.extend(page(1, 100, 4, &[1], b"c"));
    let incomplete = bytes.clone();
    bytes.extend(page(2, 1, 4, &[1], b"d"));
    assert!(scan(&incomplete).is_empty());
    let found = scan(&bytes);
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].size, bytes.len() as u64);
    assert!(scan(&bytes[..bytes.len() - 1]).is_empty());
}

#[test]
fn valid_archives_hide_embedded_signatures_but_damaged_archives_do_not() {
    let archive = include_bytes!("fixtures/embedded-image.zip");
    let png = include_bytes!("fixtures/real.png");
    let image = [archive.as_slice(), png.as_slice()].concat();
    let found = scan(&image);
    assert_eq!(
        found.iter().map(|f| f.kind).collect::<Vec<_>>(),
        vec!["zip", "png"]
    );
    assert_eq!(found[1].offset, archive.len() as u64);
    let damaged = scan(&archive[..archive.len() - 1]);
    assert_eq!(damaged.len(), 1);
    assert_eq!(damaged[0].kind, "png");
}

#[test]
fn checked_containers_are_revalidated_before_export() {
    let directory = tempfile::tempdir().unwrap();
    for (index, (kind, bytes)) in FIXTURES.iter().enumerate() {
        let found = scan(bytes);
        let mut changed = bytes.to_vec();
        changed[0] ^= 0xff;
        let output = directory.path().join(index.to_string());
        assert!(
            recover_reader(&mut Cursor::new(changed), &output, &found[0]).is_err(),
            "{kind}"
        );
        assert!(!output.exists());
    }
    assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 0);
}

#[test]
fn zero_padding_keeps_icon_headers_at_every_stream_boundary() {
    let icon = include_bytes!("fixtures/real.ico");
    for prefix in [16, 17, 1024 * 1024 - 1, 1024 * 1024 - 2, 1024 * 1024 - 3] {
        let mut bytes = vec![0; prefix];
        bytes.extend_from_slice(icon);
        let found = scan(&bytes);
        assert_eq!(found.len(), 1, "{prefix}: {found:?}");
        assert_eq!((found[0].offset, found[0].kind), (prefix as u64, "ico"));
    }
}

#[test]
fn interrupted_reads_retry_without_losing_the_candidate() {
    struct Interrupting<'a> {
        input: ShortReads<'a>,
        interrupted: bool,
    }
    impl Read for Interrupting<'_> {
        fn read(&mut self, bytes: &mut [u8]) -> io::Result<usize> {
            self.interrupted = !self.interrupted;
            if self.interrupted {
                return Err(io::ErrorKind::Interrupted.into());
            }
            self.input.read(bytes)
        }
    }
    impl Seek for Interrupting<'_> {
        fn seek(&mut self, from: SeekFrom) -> io::Result<u64> {
            self.input.seek(from)
        }
    }
    let bytes = include_bytes!("fixtures/real.opus");
    let found = collect(&mut Interrupting {
        input: ShortReads(Cursor::new(bytes)),
        interrupted: false,
    });
    assert_eq!(found.len(), 1);
    assert_eq!((found[0].kind, found[0].size), ("opus", bytes.len() as u64));
}
