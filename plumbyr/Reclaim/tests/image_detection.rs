use reclaim::{Event, Found, Validation, recover_reader, scan_reader};
use std::{io::Cursor, sync::atomic::AtomicBool};

#[cfg(test)]
fn scan(bytes: &[u8]) -> Vec<Found> {
    let mut found = vec![];
    scan_reader(&mut Cursor::new(bytes), &AtomicBool::new(false), |event| {
        if let Event::Found(f) = event {
            found.push(f);
        }
    })
    .unwrap();
    found
}

#[test]
fn real_image_formats_decode_and_recover_exactly() {
    let fixtures: &[(&str, &[u8])] = &[
        ("jpg", include_bytes!("fixtures/real.jpg")),
        ("jpg", include_bytes!("fixtures/real-progressive.jpg")),
        ("png", include_bytes!("fixtures/real.png")),
        ("gif", include_bytes!("fixtures/real.gif")),
        ("gif", include_bytes!("fixtures/real89.gif")),
        ("gif", include_bytes!("fixtures/real-animated.gif")),
        ("bmp", include_bytes!("fixtures/real.bmp")),
        ("webp", include_bytes!("fixtures/real.webp")),
        ("webp", include_bytes!("fixtures/real-animated.webp")),
        ("ico", include_bytes!("fixtures/real.ico")),
        ("ico", include_bytes!("fixtures/real-dib.ico")),
        ("ico", include_bytes!("fixtures/real256.ico")),
    ];
    let directory_temp = tempfile::tempdir().unwrap();
    let directory = directory_temp.path().join("real-images");
    std::fs::create_dir_all(&directory).unwrap();
    for (i, (kind, bytes)) in fixtures.iter().enumerate() {
        let found = scan(bytes);
        assert_eq!(
            found.len(),
            1,
            "Unexpected candidates for {kind}: {found:?}"
        );
        assert_eq!(found[0].kind, *kind);
        assert_eq!(found[0].size, bytes.len() as u64);
        assert_eq!(found[0].validation, Validation::ImageDecoded);
        let path = directory.join(format!("{i}.{kind}"));
        recover_reader(&mut Cursor::new(bytes), &path, &found[0]).unwrap();
        let recovered = std::fs::read(path).unwrap();
        assert_eq!(recovered, *bytes);
        image::load_from_memory(&recovered).unwrap();
    }
}

#[test]
fn ico_like_noise_and_bogus_directory_payloads_are_not_icons() {
    let mut image = vec![0u8; 1024 * 1024 - 2];
    for _ in 0..300 {
        image.extend_from_slice(b"\0\0\x01\0\x01\0");
        image.extend_from_slice(&[16, 16, 0, 0, 1, 0, 32, 0]);
        image.extend_from_slice(&64u32.to_le_bytes());
        image.extend_from_slice(&22u32.to_le_bytes());
        image.extend_from_slice(&[0x55; 64]);
    }
    let offset = image.len() as u64;
    image.extend_from_slice(include_bytes!("fixtures/real.png"));
    let found = scan(&image);
    assert_eq!(found.len(), 1, "Noise became candidates: {found:?}");
    assert_eq!((found[0].kind, found[0].offset), ("png", offset));
}

#[test]
fn every_icon_frame_must_match_dimensions_and_decode() {
    let real = include_bytes!("fixtures/real.ico");
    let mut wrong_dimensions = real.to_vec();
    wrong_dimensions[6] += 1;
    assert!(scan(&wrong_dimensions).iter().all(|f| f.kind != "ico"));
    let mut corrupt_first_frame = real.to_vec();
    let offset = u32::from_le_bytes(real[18..22].try_into().unwrap()) as usize;
    let idat = corrupt_first_frame[offset..]
        .windows(4)
        .position(|w| w == b"IDAT")
        .unwrap()
        + offset;
    corrupt_first_frame[idat + 4] ^= 0x55;
    // The intact second frame may survive as a PNG, but the ICO must be rejected.
    assert!(scan(&corrupt_first_frame).iter().all(|f| f.kind != "ico"));
    let mut overlap = real.to_vec();
    let first_offset = real[18..22].to_vec();
    overlap[34..38].copy_from_slice(&first_offset);
    assert!(scan(&overlap).iter().all(|f| f.kind != "ico"));
}

#[test]
fn embedded_terminators_do_not_truncate_valid_images() {
    let png = include_bytes!("fixtures/real.png");
    let payload = b"\0\0\0\0IEND\xaeB`\x82";
    let mut chunk = (u32::try_from(payload.len()).unwrap())
        .to_be_bytes()
        .to_vec();
    chunk.extend_from_slice(b"raNd");
    chunk.extend_from_slice(payload);
    let crc = crc32fast::hash(&chunk[4..]);
    chunk.extend_from_slice(&crc.to_be_bytes());
    let with_chunk = [&png[..33], chunk.as_slice(), &png[33..]].concat();
    let jpeg = include_bytes!("fixtures/real.jpg");
    let app_data = b"embedded \xff\xd9 marker";
    let mut app = b"\xff\xe3".to_vec();
    app.extend_from_slice(&(u16::try_from(app_data.len() + 2).unwrap()).to_be_bytes());
    app.extend_from_slice(app_data);
    let with_app = [&jpeg[..2], app.as_slice(), &jpeg[2..]].concat();
    let gif = include_bytes!("fixtures/real.gif");
    let mut p = 13;
    if gif[10] & 0x80 != 0 {
        p += 3 * (1 << ((gif[10] & 7) + 1));
    }
    let with_comment = [&gif[..p], b"\x21\xfe\x02\0\x3b\0", &gif[p..]].concat();
    for bytes in [with_chunk, with_app, with_comment] {
        let found = scan(&bytes);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].size, bytes.len() as u64);
        assert_eq!(found[0].validation, Validation::ImageDecoded);
    }
}

#[test]
fn corrupted_images_are_rejected_at_scan_and_export() {
    let real = include_bytes!("fixtures/real.png");
    let found = scan(real);
    let mut corrupt = real.to_vec();
    let p = corrupt.windows(4).position(|w| w == b"IDAT").unwrap();
    corrupt[p + 4] ^= 1;
    assert!(scan(&corrupt).is_empty());
    let mut bad_crc = real.to_vec();
    bad_crc[29] ^= 1;
    assert!(scan(&bad_crc).is_empty());
    let path_temp = tempfile::tempdir().unwrap();
    let path = path_temp.path().join("corrupt-export.png");
    assert!(recover_reader(&mut Cursor::new(&corrupt), &path, &found[0]).is_err());
    assert!(
        !path.exists(),
        "A failed image export must not leave a corrupt output"
    );
    for bytes in [
        b"\xff\xd8\xff\xff\xd9".as_slice(),
        b"GIF89aexample\0\x3b",
        b"BM\x12\0\0\0\0\0\0\0\0\0\0\0bad!",
    ] {
        assert!(scan(bytes).is_empty());
    }
}

#[test]
fn reports_candidate_location_before_validation_and_allows_cancellation() {
    use std::sync::atomic::Ordering;
    let png = include_bytes!("fixtures/real.png");
    let offset = 512;
    let mut disk = vec![0; offset];
    disk.extend_from_slice(png);
    let cancel = AtomicBool::new(false);
    let mut checking = false;
    let mut found = false;
    scan_reader(&mut Cursor::new(disk), &cancel, |event| match event {
        Event::Checking {
            offset: at,
            kind,
            bytes,
        } => {
            assert_eq!(at, offset as u64);
            assert_eq!(kind, "png");
            assert_eq!(bytes, png.len());
            checking = true;
            cancel.store(true, Ordering::Relaxed);
        }
        Event::Found(_) => found = true,
        Event::Progress(_) | Event::UnusedSpace { .. } | Event::Unreadable { .. } => {}
    })
    .unwrap();
    assert!(checking);
    assert!(!found);
}
