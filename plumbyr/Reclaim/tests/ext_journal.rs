//! Deleted files on ext3/ext4 come back from the inode copies in the journal,
//! whole and with their names, even when they were stored in pieces.
use reclaim::{Event, Found, FoundReader, scan_unused_reader};
use std::{
    io::{Cursor, Read},
    sync::atomic::AtomicBool,
};

/// The document the fixtures stored, split across freed gaps, then deleted.
fn document() -> Vec<u8> {
    let mut bytes = b"%PDF-1.4\n".to_vec();
    for line in 0..2000 {
        bytes.extend(format!("line {line:05} of the deleted journal test document\n").bytes());
    }
    bytes.extend(b"%%EOF\n");
    bytes
}

#[cfg(test)]
fn fixture(name: &str) -> Vec<u8> {
    let path = format!("{}/tests/fixtures/{name}", env!("CARGO_MANIFEST_DIR"));
    let mut bytes = Vec::new();
    flate2::read::GzDecoder::new(std::fs::File::open(path).unwrap())
        .read_to_end(&mut bytes)
        .unwrap();
    bytes
}

#[cfg(test)]
fn scan(image: &[u8]) -> Vec<Found> {
    let mut found = Vec::new();
    scan_unused_reader(
        &mut Cursor::new(image),
        image.len() as u64,
        &AtomicBool::new(false),
        |event| {
            if let Event::Found(file) = event {
                found.push(file);
            }
        },
    )
    .unwrap();
    found
}

#[test]
fn fragmented_deleted_files_return_whole_from_the_ext_journal() {
    for name in ["deleted-journal-ext4.img.gz", "deleted-journal-ext3.img.gz"] {
        let image = fixture(name);
        let found = scan(&image);
        let file = found
            .iter()
            .find(|file| file.original_name.as_deref() == Some("Holiday plan.pdf"))
            .unwrap_or_else(|| panic!("{name}: deleted document not found by name"));
        assert!(
            file.extents
                .as_ref()
                .is_some_and(|extents| extents.len() > 1),
            "{name}: the fixture document should be stored in pieces"
        );
        let mut input = Cursor::new(&image);
        let mut recovered = Vec::new();
        FoundReader::new(&mut input, file)
            .unwrap()
            .read_to_end(&mut recovered)
            .unwrap();
        assert!(recovered == document(), "{name}: recovered bytes differ");
    }
}
