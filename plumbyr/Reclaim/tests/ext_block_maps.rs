//! Deleted files on ext2 come back whole from the block maps the kernel
//! leaves behind, even when they were stored in many pieces.
use reclaim::{Event, Found, FoundReader, scan_unused_reader};
use std::{
    io::{Cursor, Read},
    sync::atomic::AtomicBool,
};

/// The document the fixture stored across freed gaps, then deleted. It is
/// longer than one block map covers, so the second map level is needed too.
#[cfg(test)]
fn document() -> Vec<u8> {
    let mut bytes = b"%PDF-1.4\n".to_vec();
    for line in 0..7000 {
        bytes.extend(format!("line {line:05} of the deleted ext2 block map test\n").bytes());
    }
    bytes.extend(b"%%EOF\n");
    bytes
}

#[cfg(test)]
fn fixture() -> Vec<u8> {
    let path = format!(
        "{}/tests/fixtures/deleted-blockmap-ext2.img.gz",
        env!("CARGO_MANIFEST_DIR")
    );
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
fn fragmented_deleted_file_returns_whole_from_ext2_block_maps() {
    let image = fixture();
    let expected = document();
    let mut input = Cursor::new(&image);
    let whole = scan(&image).into_iter().find(|file| {
        let mut recovered = Vec::new();
        FoundReader::new(&mut input, file)
            .unwrap()
            .read_to_end(&mut recovered)
            .unwrap();
        recovered == expected
    });
    let file = whole.expect("the deleted document did not come back whole");
    assert!(
        file.extents
            .as_ref()
            .is_some_and(|extents| extents.len() > 2),
        "the fixture document should be stored in many pieces"
    );
}
