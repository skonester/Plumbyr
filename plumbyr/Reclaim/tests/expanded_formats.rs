use reclaim::{Event, Found, recover, scan};
use std::sync::atomic::AtomicBool;

#[cfg(test)]
fn carve_and_check(fixtures: &[(&str, Vec<u8>)]) {
    let dir_temp = tempfile::tempdir().unwrap();
    let dir = dir_temp.path().join("work");
    std::fs::create_dir_all(&dir).unwrap();
    let source = dir.join("source.img");
    let mut image = vec![0; 1024 * 1024 - 2];
    let mut expected = Vec::new();
    for (kind, bytes) in fixtures {
        expected.push((image.len() as u64, *kind, bytes));
        image.extend_from_slice(bytes);
        image.extend_from_slice(&[0; 31]);
    }
    std::fs::write(&source, &image).unwrap();
    let mut found: Vec<Found> = vec![];
    scan(&source, &AtomicBool::new(false), |event| {
        if let Event::Found(f) = event {
            found.push(f);
        }
    })
    .unwrap();
    assert_eq!(
        found.len(),
        expected.len(),
        "Unexpected nested or missing candidates: {found:?}"
    );
    for (f, (offset, kind, bytes)) in found.iter().zip(expected) {
        assert_eq!(
            (f.offset, f.kind, f.size),
            (offset, kind, bytes.len() as u64)
        );
        let out = dir.join(format!("{}.{}", f.offset, f.kind));
        recover(&source, &out, f).unwrap();
        assert_eq!(std::fs::read(out).unwrap(), *bytes);
    }
    assert_eq!(std::fs::read(&source).unwrap(), image);
    for (kind, bytes) in fixtures {
        std::fs::write(&source, &bytes[..bytes.len() - 1]).unwrap();
        let mut partial = Vec::new();
        scan(&source, &AtomicBool::new(false), |event| {
            if let Event::Found(f) = event {
                partial.push(f);
            }
        })
        .unwrap();
        if *kind == "ico" {
            // A damaged container may still contain independently recoverable PNG frames.
            assert!(
                partial
                    .iter()
                    .all(|f| f.kind == "png" && f.validation == reclaim::Validation::ImageDecoded)
            );
        } else {
            assert!(
                partial.is_empty(),
                "Truncated {kind} was accepted: {partial:?}"
            );
        }
    }
}

#[test]
fn zip_packages_have_correct_extensions_and_no_duplicate_local_entries() {
    let fixtures: &[(&str, &[u8])] = &[
        ("zip", include_bytes!("fixtures/plain.zip")),
        ("docx", include_bytes!("fixtures/package.docx")),
        ("xlsx", include_bytes!("fixtures/package.xlsx")),
        ("pptx", include_bytes!("fixtures/package.pptx")),
        ("docm", include_bytes!("fixtures/package.docm")),
        ("xlsm", include_bytes!("fixtures/package.xlsm")),
        ("pptm", include_bytes!("fixtures/package.pptm")),
        ("odt", include_bytes!("fixtures/package.odt")),
        ("ods", include_bytes!("fixtures/package.ods")),
        ("odp", include_bytes!("fixtures/package.odp")),
        ("epub", include_bytes!("fixtures/package.epub")),
        ("jar", include_bytes!("fixtures/package.jar")),
        ("apk", include_bytes!("fixtures/package.apk")),
    ];
    carve_and_check(
        &fixtures
            .iter()
            .map(|(k, b)| (*k, b.to_vec()))
            .collect::<Vec<_>>(),
    );
}

#[cfg(test)]
fn chunk_container(head: &[u8], kind: &[u8], little: bool) -> Vec<u8> {
    let mut bytes = head.to_vec();
    let size = 16u32;
    bytes.extend_from_slice(&if little {
        size.to_le_bytes()
    } else {
        size.to_be_bytes()
    });
    bytes.extend_from_slice(kind);
    bytes.extend_from_slice(b"TEST\0\0\0\x04data");
    bytes
}
#[test]
fn media_sizes_and_multiple_icon_images() {
    let mut au = b".snd".to_vec();
    for n in [24u32, 4, 3, 44100, 1] {
        au.extend_from_slice(&n.to_be_bytes());
    }
    au.extend_from_slice(b"data");
    let ico = include_bytes!("fixtures/real.ico").to_vec();
    carve_and_check(&[
        ("webp", include_bytes!("fixtures/real.webp").to_vec()),
        ("avi", chunk_container(b"RIFF", b"AVI ", true)),
        ("aiff", chunk_container(b"FORM", b"AIFF", false)),
        ("aifc", chunk_container(b"FORM", b"AIFC", false)),
        ("au", au),
        ("ico", ico),
    ]);
}

#[test]
fn reject_truncation_bad_sizes_and_bogus_zip_directory() {
    let dir_temp = tempfile::tempdir().unwrap();
    let dir = dir_temp.path().join("reject");
    std::fs::create_dir_all(&dir).unwrap();
    let source = dir.join("bad.img");
    let mut data = chunk_container(b"RIFF", b"WEBP", true);
    data[4..8].copy_from_slice(&u32::MAX.to_le_bytes());
    data.extend_from_slice(b"FORM\0\0\0\x18AIFFshort");
    let mut zip = include_bytes!("fixtures/plain.zip").to_vec();
    let end = zip.windows(4).position(|w| w == b"PK\x05\x06").unwrap();
    zip[end + 16..end + 20].copy_from_slice(&u32::MAX.to_le_bytes());
    data.extend_from_slice(&zip);
    std::fs::write(&source, data).unwrap();
    let mut found = vec![];
    scan(&source, &AtomicBool::new(false), |event| {
        if let Event::Found(f) = event {
            found.push(f);
        }
    })
    .unwrap();
    // The apparent AIFF is a valid bounded candidate once followed by unrelated
    // bytes: carving cannot establish payload validity. The overflowing WebP
    // size and corrupt ZIP directory must still be rejected.
    assert!(found.iter().all(|f| f.kind == "aiff"));
    std::fs::write(&source, b"FORM\0\0\0\x18AIFFshort").unwrap();
    let mut count = 0;
    scan(&source, &AtomicBool::new(false), |event| {
        if let Event::Found(_) = event {
            count += 1;
        }
    })
    .unwrap();
    assert_eq!(count, 0);
}

#[test]
fn scanning_and_recovery_use_an_existing_authorized_reader() {
    use std::io::{Cursor, Seek, SeekFrom};
    let bytes = include_bytes!("fixtures/package.docx");
    let mut reader = Cursor::new(bytes.to_vec());
    reader.seek(SeekFrom::End(0)).unwrap();
    let mut found = Vec::new();
    reclaim::scan_reader(&mut reader, &AtomicBool::new(false), |e| {
        if let Event::Found(f) = e {
            found.push(f);
        }
    })
    .unwrap();
    assert_eq!(found.len(), 1);
    let output_temp = tempfile::tempdir().unwrap();
    let output = output_temp.path().join("reader.docx");
    reclaim::recover_reader(&mut reader, &output, &found[0]).unwrap();
    assert_eq!(std::fs::read(&output).unwrap(), bytes);
    assert_eq!(reader.into_inner(), bytes);
}

#[test]
fn generated_names_are_readable_and_keep_the_correct_extension() {
    for (kind, expected) in [
        ("png", "Image 0003.png"),
        ("pdf", "Document 0003.pdf"),
        ("zip", "Archive 0003.zip"),
        ("wav", "Audio 0003.wav"),
        ("avi", "Video 0003.avi"),
        ("apk", "Application 0003.apk"),
        ("ico", "Icon 0003.ico"),
    ] {
        let found = Found {
            kind,
            offset: 8192,
            size: 32,
            selected: true,
            validation: reclaim::Validation::Unverified,
            original_name: None,
            resident_data: None,
            ntfs: None,
            extents: None,
        };
        assert_eq!(found.recovery_name(3), expected);
        assert_eq!(
            found.recovery_name(100_000),
            expected.replace("0003", "100000")
        );
    }
}
