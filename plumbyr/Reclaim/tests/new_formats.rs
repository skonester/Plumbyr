use reclaim::{Event, Found, Validation, recover_reader, scan_reader};
use std::{
    io::{self, Cursor, Read, Seek, SeekFrom, Write},
    sync::atomic::{AtomicBool, Ordering},
};
const FIXTURES: &[(&str, &[u8])] = &[
    ("mid", include_bytes!("fixtures/formats/sample.mid")),
    ("swf", include_bytes!("fixtures/formats/sample.swf")),
    ("exe", include_bytes!("fixtures/formats/pe32.exe")),
    ("exe", include_bytes!("fixtures/formats/certificate.exe")),
    ("dll", include_bytes!("fixtures/formats/pe64.dll")),
    ("elf", include_bytes!("fixtures/formats/elf32-le.elf")),
    ("elf", include_bytes!("fixtures/formats/elf32-be.elf")),
    ("elf", include_bytes!("fixtures/formats/elf64-le.elf")),
    ("elf", include_bytes!("fixtures/formats/elf64-be.elf")),
    ("msi", include_bytes!("fixtures/formats/cfb3.msi")),
    ("msp", include_bytes!("fixtures/formats/cfb3.msp")),
    ("mst", include_bytes!("fixtures/formats/cfb3.mst")),
    ("ole", include_bytes!("fixtures/formats/cfb3.ole")),
    ("msi", include_bytes!("fixtures/formats/cfb4.msi")),
    ("msp", include_bytes!("fixtures/formats/cfb4.msp")),
    ("mst", include_bytes!("fixtures/formats/cfb4.mst")),
    ("ole", include_bytes!("fixtures/formats/cfb4.ole")),
    ("7z", include_bytes!("fixtures/formats/empty.7z")),
    ("cab", include_bytes!("fixtures/formats/sample.cab")),
    ("deb", include_bytes!("fixtures/formats/sample.deb")),
    ("rpm", include_bytes!("fixtures/formats/sample.rpm")),
    ("dex", include_bytes!("fixtures/formats/sample.dex")),
    ("gz", include_bytes!("fixtures/formats/sample.gz")),
    ("pkg", include_bytes!("fixtures/formats/sample.pkg")),
    ("xar", include_bytes!("fixtures/formats/sample.xar")),
    ("qoi", include_bytes!("fixtures/formats/sample.qoi")),
    ("rar", include_bytes!("fixtures/formats/sample.rar")),
    ("tar", include_bytes!("fixtures/formats/sample.tar")),
];
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
#[test]
fn all_added_formats_carve_across_blocks_and_export_exact_bytes() {
    let dir = tempfile::tempdir().unwrap();
    for (index, (kind, bytes)) in FIXTURES.iter().enumerate() {
        // TAR's marker is at +257; this also puts that marker across the boundary.
        let prefix = 1024 * 1024 - 260;
        let disk = [vec![0; prefix], bytes.to_vec(), vec![0; 320]].concat();
        let mut input = Cursor::new(&disk);
        let files = collect(&mut input);
        let matched: Vec<_> = files
            .iter()
            .filter(|f| f.offset == prefix as u64 && f.kind == *kind)
            .collect();
        assert_eq!(matched.len(), 1, "{kind}: {files:?}");
        let found = matched[0];
        assert_eq!(found.size, bytes.len() as u64, "{kind}");
        let expected = if matches!(
            *kind,
            "exe"
                | "dll"
                | "elf"
                | "msi"
                | "msp"
                | "mst"
                | "ole"
                | "rpm"
                | "dex"
                | "xar"
                | "pkg"
                | "swf"
        ) {
            Validation::Unverified
        } else {
            Validation::StructureChecked
        };
        assert_eq!(found.validation, expected, "{kind}");
        let output = dir.path().join(format!("{index}.{kind}"));
        recover_reader(&mut input, &output, found).unwrap();
        assert_eq!(std::fs::read(output).unwrap(), *bytes, "{kind}");
    }
}
#[test]
fn truncated_candidates_and_corrupt_metadata_do_not_become_results() {
    for (kind, bytes) in FIXTURES {
        let files = collect(&mut Cursor::new(&bytes[..bytes.len() - 1]));
        assert!(
            !files.iter().any(|f| f.offset == 0 && f.kind == *kind),
            "truncated {kind}: {files:?}"
        );
    }
    let cases: &[(&str, usize)] = &[
        ("mid", 8),
        ("swf", 4),
        ("exe", 60),
        ("elf", 4),
        ("msi", 30),
        ("7z", 8),
        ("cab", 8),
        ("deb", 66),
        ("rpm", 108),
        ("dex", 32),
        ("gz", 3),
        ("pkg", 8),
        ("qoi", 4),
        ("rar", 8),
        ("tar", 148),
    ];
    for (kind, at) in cases {
        let (_, fixture) = FIXTURES.iter().find(|(k, _)| k == kind).unwrap();
        let mut bad = fixture.to_vec();
        bad[*at] ^= 0x80;
        let files = collect(&mut Cursor::new(bad));
        assert!(
            !files.iter().any(|f| f.offset == 0 && f.kind == *kind),
            "corrupt {kind}: {files:?}"
        );
    }
}
struct Short(Cursor<Vec<u8>>);
impl Read for Short {
    fn read(&mut self, b: &mut [u8]) -> io::Result<usize> {
        let n = b.len().min(3);
        self.0.read(&mut b[..n])
    }
}
impl Seek for Short {
    fn seek(&mut self, p: SeekFrom) -> io::Result<u64> {
        self.0.seek(p)
    }
}
#[test]
fn short_device_reads_and_cancellation_are_supported() {
    for (kind, bytes) in FIXTURES {
        let files = collect(&mut Short(Cursor::new(bytes.to_vec())));
        assert!(
            files.iter().any(|f| f.offset == 0 && f.kind == *kind),
            "{kind}"
        );
    }
    let cancel = AtomicBool::new(false);
    let mut found = 0;
    scan_reader(&mut Cursor::new(FIXTURES[0].1), &cancel, |e| match e {
        Event::Checking { .. } => cancel.store(true, Ordering::Relaxed),
        Event::Found(_) => found += 1,
        Event::Unreadable { .. } | Event::UnusedSpace { .. } | Event::Progress(_) => {}
    })
    .unwrap();
    assert_eq!(found, 0);
}
#[test]
fn altered_executables_are_rechecked_before_export() {
    let mut bytes = FIXTURES
        .iter()
        .find(|(kind, _)| *kind == "exe")
        .unwrap()
        .1
        .to_vec();
    let found = collect(&mut Cursor::new(&bytes)).remove(0);
    bytes[60..64].fill(0xff);
    let dir = tempfile::tempdir().unwrap();
    let output = dir.path().join("bad.exe");
    assert!(recover_reader(&mut Cursor::new(bytes), &output, &found).is_err());
    assert!(!output.exists());
}

#[test]
fn midi_and_swf_reject_every_truncated_prefix_and_recheck_changed_exports() {
    for kind in ["mid", "swf"] {
        let bytes = FIXTURES.iter().find(|(k, _)| *k == kind).unwrap().1;
        for length in 0..bytes.len() {
            assert!(
                collect(&mut Cursor::new(&bytes[..length])).is_empty(),
                "{kind} prefix {length}"
            );
        }
        let found = collect(&mut Cursor::new(bytes)).remove(0);
        let mut changed = bytes.to_vec();
        // MIDI loses its End-of-Track; SWF loses its final End tag.
        let at = changed.len() - 2;
        changed[at] ^= 0x7f;
        let directory = tempfile::tempdir().unwrap();
        assert!(
            recover_reader(
                &mut Cursor::new(changed),
                &directory.path().join("output"),
                &found
            )
            .is_err()
        );
        assert_eq!(std::fs::read_dir(directory.path()).unwrap().count(), 0);
    }
}

#[test]
fn midi_track_lengths_running_status_and_swf_tags_bound_candidates() {
    let midi = include_bytes!("fixtures/formats/sample.mid");
    // Every malformed event stream has valid outer track framing.
    for track in [
        &b"\0\x3c\x40\0\xff\x2f\0"[..],       // No preceding running status.
        &b"\x81\x81\x81\x81\0\xff\x2f\0"[..], // Delta > four bytes.
        &b"\0\x90\x3c\xff\0\xff\x2f\0"[..],   // Invalid channel data.
        &b"\0\xff\x01\x7f\0\xff\x2f\0"[..],   // Meta payload exceeds track.
        &b"\0\xff\x2f\0\0\x90\x3c\x40"[..],   // Data after End-of-Track.
    ] {
        let mut bad = midi[..18].to_vec();
        bad.extend(u32::try_from(track.len()).unwrap().to_be_bytes());
        bad.extend(track);
        assert!(collect(&mut Cursor::new(bad)).is_empty());
    }
    // Formats 1/2 support multiple tracks, and SMPTE division is signed.
    for format in [1u16, 2] {
        let mut two = midi.to_vec();
        two[8..10].copy_from_slice(&format.to_be_bytes());
        two[10..12].copy_from_slice(&2u16.to_be_bytes());
        two[12..14].copy_from_slice(&[0xe7, 40]);
        two.extend_from_slice(&midi[14..]);
        let files = collect(&mut Cursor::new(&two));
        assert_eq!(files.len(), 1);
        assert_eq!(files[0].size, two.len() as u64);
    }
    let swf = include_bytes!("fixtures/formats/sample.swf");
    for (offset, value) in [(0, b'C'), (3, 0), (8, 0), (12, 2), (14, 0x7f)] {
        let mut bad = swf.to_vec();
        bad[offset] = value;
        assert!(collect(&mut Cursor::new(bad)).is_empty());
    }
    // Two adjacent files must remain two separate recovery candidates.
    let adjacent = [midi.as_slice(), midi.as_slice()].concat();
    let files = collect(&mut Cursor::new(adjacent));
    assert_eq!(
        files.iter().map(|f| f.offset).collect::<Vec<_>>(),
        vec![0, midi.len() as u64]
    );
}

#[test]
fn swf_extended_tags_are_bounded_and_version_eight_requires_file_attributes() {
    let base = include_bytes!("fixtures/formats/sample.swf");
    let mut bytes = base[..14].to_vec();
    bytes[3] = 8;
    bytes.extend_from_slice(&((69u16 << 6) | 4).to_le_bytes());
    bytes.extend_from_slice(&[0; 4]);
    // A long-form Metadata tag with embedded signature-like payload.
    let payload = b"%PDF-1.7 not an independently framed file";
    bytes.extend_from_slice(&((77u16 << 6) | 63).to_le_bytes());
    let size_at = bytes.len();
    bytes.extend_from_slice(&u32::try_from(payload.len()).unwrap().to_le_bytes());
    bytes.extend_from_slice(payload);
    bytes.extend_from_slice(&base[14..]);
    let size = u32::try_from(bytes.len()).unwrap();
    bytes[4..8].copy_from_slice(&size.to_le_bytes());
    let found = collect(&mut Cursor::new(&bytes));
    assert!(
        found
            .iter()
            .any(|f| f.kind == "swf" && f.size == u64::from(size))
    );
    bytes[size_at..size_at + 4].copy_from_slice(&u32::MAX.to_le_bytes());
    assert!(
        !collect(&mut Cursor::new(&bytes))
            .iter()
            .any(|f| f.kind == "swf")
    );
    let mut missing_attributes = base.to_vec();
    missing_attributes[3] = 8;
    assert!(collect(&mut Cursor::new(missing_attributes)).is_empty());
}

#[test]
fn varied_header_mutations_never_panic_or_report_out_of_bounds_files() {
    let mut state = 0x3141_5926_5358_9793_u64;
    for (_, fixture) in FIXTURES {
        for _ in 0..48 {
            let mut bytes = fixture.to_vec();
            for _ in 0..4 {
                state ^= state << 13;
                state ^= state >> 7;
                state ^= state << 17;
                let at = usize::try_from(state % u64::try_from(bytes.len()).unwrap()).unwrap();
                // Take the low byte of the high half on purpose (pseudo-random flip).
                bytes[at] ^= (state >> 32).to_le_bytes()[0] | 1;
            }
            let found = collect(&mut Cursor::new(&bytes));
            for file in found {
                assert!(file.size > 0);
                assert!(file.offset + file.size <= bytes.len() as u64);
            }
        }
    }
}

#[test]
fn gzip_crc_and_xar_xml_are_checked_and_archive_members_do_not_consume_neighbours() {
    let gz = include_bytes!("fixtures/formats/sample.gz");
    let mut bad = gz.to_vec();
    let crc = bad.len() - 8;
    bad[crc] ^= 1;
    assert!(collect(&mut Cursor::new(bad)).is_empty());
    let bytes = [gz.as_slice(), gz.as_slice()].concat();
    let found = collect(&mut Cursor::new(bytes));
    assert_eq!(
        found.iter().map(|f| (f.offset, f.size)).collect::<Vec<_>>(),
        vec![(0, gz.len() as u64), (gz.len() as u64, gz.len() as u64)]
    );
    let plain = b'X';
    let xml = b"<!DOCTYPE xar [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><xar><toc/></xar>";
    let mut encoder = flate2::write::ZlibEncoder::new(Vec::new(), flate2::Compression::default());
    encoder.write_all(xml).unwrap();
    let toc = encoder.finish().unwrap();
    let mut xar = b"xar!".to_vec();
    xar.extend_from_slice(&28u16.to_be_bytes());
    xar.extend_from_slice(&1u16.to_be_bytes());
    xar.extend_from_slice(&(toc.len() as u64).to_be_bytes());
    xar.extend_from_slice(&(xml.len() as u64).to_be_bytes());
    xar.extend_from_slice(&0u32.to_be_bytes());
    xar.extend_from_slice(&toc);
    xar.push(plain);
    assert!(
        !collect(&mut Cursor::new(xar))
            .iter()
            .any(|f| matches!(f.kind, "xar" | "pkg"))
    );
}

#[test]
fn common_mz_noise_is_rejected_before_candidate_disk_reads() {
    struct Counted {
        input: Cursor<Vec<u8>>,
        read: usize,
    }
    impl Read for Counted {
        fn read(&mut self, b: &mut [u8]) -> io::Result<usize> {
            let n = self.input.read(b)?;
            self.read += n;
            Ok(n)
        }
    }
    impl Seek for Counted {
        fn seek(&mut self, p: SeekFrom) -> io::Result<u64> {
            self.input.seek(p)
        }
    }
    let mut bytes = vec![0xe7; 2 * 1024 * 1024];
    for at in (0..bytes.len() - 64).step_by(512) {
        bytes[at..at + 2].copy_from_slice(b"MZ");
    }
    let length = bytes.len();
    let mut input = Counted {
        input: Cursor::new(bytes),
        read: 0,
    };
    assert!(collect(&mut input).is_empty());
    assert!(
        input.read <= length + 1024,
        "MZ noise triggered excessive lookahead: {}",
        input.read
    );
}
