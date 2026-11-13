use reclaim::{Event, Validation, recover_reader, scan_reader};
use std::{
    io::{Read, Seek, SeekFrom, Write},
    sync::atomic::AtomicBool,
};

// A real, stored ZIP entry containing 65 MiB of zeros, written sparsely.
#[cfg(test)]
fn archive(file: &mut std::fs::File, prefix: u64) -> u64 {
    let size = 65 * 1024 * 1024u32;
    let mut crc = crc32fast::Hasher::new();
    for _ in 0..65 {
        crc.update(&vec![0; 1024 * 1024]);
    }
    let crc = crc.finalize();
    let mut local = [0; 30];
    local[..4].copy_from_slice(b"PK\x03\x04");
    local[4..6].copy_from_slice(&20u16.to_le_bytes());
    local[14..18].copy_from_slice(&crc.to_le_bytes());
    local[18..22].copy_from_slice(&size.to_le_bytes());
    local[22..26].copy_from_slice(&size.to_le_bytes());
    local[26..28].copy_from_slice(&1u16.to_le_bytes());
    file.seek(SeekFrom::Start(prefix)).unwrap();
    file.write_all(&local).unwrap();
    file.write_all(b"a").unwrap();
    let directory = 31 + u64::from(size);
    file.seek(SeekFrom::Start(prefix + directory)).unwrap();
    let mut central = [0; 46];
    central[..4].copy_from_slice(b"PK\x01\x02");
    central[6..8].copy_from_slice(&20u16.to_le_bytes());
    central[8..28].copy_from_slice(&local[6..26]);
    central[28..30].copy_from_slice(&1u16.to_le_bytes());
    file.write_all(&central).unwrap();
    file.write_all(b"a").unwrap();
    let mut end = [0; 22];
    end[..4].copy_from_slice(b"PK\x05\x06");
    end[8..10].copy_from_slice(&1u16.to_le_bytes());
    end[10..12].copy_from_slice(&1u16.to_le_bytes());
    end[12..16].copy_from_slice(&47u32.to_le_bytes());
    end[16..20].copy_from_slice(&(u32::try_from(directory).unwrap()).to_le_bytes());
    file.write_all(&end).unwrap();
    directory + 47 + 22
}

#[test]
fn large_zip_is_detected_exported_and_does_not_hide_the_next_archive() {
    let mut disk = tempfile::tempfile().unwrap();
    let size = archive(&mut disk, 512);
    let small = include_bytes!("fixtures/plain.zip");
    disk.write_all(small).unwrap();
    let mut found = vec![];
    scan_reader(&mut disk, &AtomicBool::new(false), |event| {
        if let Event::Found(file) = event {
            found.push(file);
        }
    })
    .unwrap();
    assert_eq!(found.len(), 2);
    assert_eq!(
        (
            found[0].offset,
            found[0].size,
            found[0].kind,
            found[0].validation
        ),
        (512, size, "zip", Validation::StructureChecked)
    );
    assert_eq!(found[1].offset, 512 + size);
    let dir = tempfile::tempdir().unwrap();
    let output = dir.path().join("large.zip");
    recover_reader(&mut disk, &output, &found[0]).unwrap();
    let mut recovered = std::fs::File::open(&output).unwrap();
    assert_eq!(recovered.metadata().unwrap().len(), size);
    recovered.seek(SeekFrom::End(-22)).unwrap();
    let mut end = [0; 4];
    recovered.read_exact(&mut end).unwrap();
    assert_eq!(&end, b"PK\x05\x06");
    // Changed directory names must fail revalidation and never publish a file.
    disk.seek(SeekFrom::Start(512 + size - 23)).unwrap();
    disk.write_all(b"b").unwrap();
    let bad = dir.path().join("bad.zip");
    assert!(recover_reader(&mut disk, &bad, &found[0]).is_err());
    assert!(!bad.exists());
}
