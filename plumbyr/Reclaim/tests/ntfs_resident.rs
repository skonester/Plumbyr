use reclaim::{Event, Found, Validation, recover_reader, recover_unique, scan_reader};
use std::{
    io::{self, Cursor, Read, Seek, SeekFrom},
    sync::atomic::AtomicBool,
};
#[cfg(test)]
fn put16(bytes: &mut [u8], at: usize, n: u16) {
    bytes[at..at + 2].copy_from_slice(&n.to_le_bytes());
}
#[cfg(test)]
fn put32(bytes: &mut [u8], at: usize, n: u32) {
    bytes[at..at + 4].copy_from_slice(&n.to_le_bytes());
}
#[cfg(test)]
fn attribute(record: &mut [u8], at: &mut usize, kind: u32, value: &[u8]) -> usize {
    let start = *at;
    let length = (24 + value.len()).next_multiple_of(8);
    put32(record, start, kind);
    put32(record, start + 4, u32::try_from(length).unwrap());
    put32(record, start + 16, u32::try_from(value.len()).unwrap());
    put16(record, start + 20, 24);
    record[start + 24..start + 24 + value.len()].copy_from_slice(value);
    *at += length;
    start
}
#[cfg(test)]
fn record(name: &str, contents: &[u8], flags: u16, external: bool) -> Vec<u8> {
    let mut bytes = vec![0; 1024];
    bytes[..4].copy_from_slice(b"FILE");
    put16(&mut bytes, 4, 48);
    put16(&mut bytes, 6, 3);
    put16(&mut bytes, 16, 7);
    put16(&mut bytes, 20, 64);
    put16(&mut bytes, 22, flags);
    put32(&mut bytes, 28, 1024);
    put32(&mut bytes, 44, 64);
    let mut at = 64;
    attribute(&mut bytes, &mut at, 0x10, &[0; 48]);
    let utf16: Vec<_> = name.encode_utf16().collect();
    let mut filename = vec![0; 66 + utf16.len() * 2];
    filename[64] = u8::try_from(utf16.len()).unwrap();
    filename[65] = 1;
    for (i, c) in utf16.iter().enumerate() {
        put16(&mut filename, 66 + i * 2, *c);
    }
    attribute(&mut bytes, &mut at, 0x30, &filename);
    let data_at = attribute(&mut bytes, &mut at, 0x80, contents);
    if external {
        bytes[data_at + 8] = 1;
    }
    put32(&mut bytes, at, u32::MAX);
    put32(&mut bytes, 24, u32::try_from(at + 4).unwrap());
    put16(&mut bytes, 48, 0xbeef);
    for (i, tail) in [510, 1022].into_iter().enumerate() {
        let original = [bytes[tail], bytes[tail + 1]];
        bytes[50 + i * 2..52 + i * 2].copy_from_slice(&original);
        put16(&mut bytes, tail, 0xbeef);
    }
    bytes
}
#[cfg(test)]
fn scan(bytes: &[u8]) -> Vec<Found> {
    let mut found = vec![];
    scan_reader(&mut Cursor::new(bytes), &AtomicBool::new(false), |e| {
        if let Event::Found(f) = e {
            found.push(f);
        }
    })
    .unwrap();
    found
}
#[cfg(test)]
struct Unreadable;
#[cfg(test)]
impl Read for Unreadable {
    #[expect(
        clippy::panic_in_result_fn,
        reason = "test double: a failed assertion is the test failure"
    )]
    fn read(&mut self, _: &mut [u8]) -> io::Result<usize> {
        panic!("Resident exports must not read the raw source");
    }
}
#[cfg(test)]
impl Seek for Unreadable {
    #[expect(
        clippy::panic_in_result_fn,
        reason = "test double: a failed assertion is the test failure"
    )]
    fn seek(&mut self, _: SeekFrom) -> io::Result<u64> {
        panic!("Resident exports must not seek the raw source");
    }
}

#[test]
fn original_unicode_name_and_sector_fixups_recover_exactly() {
    let contents = b"resident content crossing sector tails\n".repeat(12);
    let raw = record("r\u{e9}sum\u{e9}.txt", &contents, 0, false);
    let mut image = vec![0; 1024 * 1024];
    image.extend_from_slice(&raw);
    let found = scan(&image);
    assert_eq!(found.len(), 1);
    assert_eq!(
        found[0].original_name.as_deref(),
        Some("r\u{e9}sum\u{e9}.txt")
    );
    assert_eq!(found[0].recovery_name(123), "r\u{e9}sum\u{e9}.txt");
    assert_eq!(found[0].validation, Validation::MetadataChecked);
    assert_eq!(found[0].resident_data.as_deref(), Some(contents.as_slice()));
    let dir_temp = tempfile::tempdir().unwrap();
    let dir = dir_temp.path().join("ntfs");
    std::fs::create_dir_all(&dir).unwrap();
    let one = recover_unique(&mut Unreadable, &dir, &found[0], 1).unwrap();
    let two = recover_unique(&mut Unreadable, &dir, &found[0], 1).unwrap();
    assert_eq!(
        two.file_name().unwrap().to_string_lossy(),
        "r\u{e9}sum\u{e9} (2).txt"
    );
    assert_eq!(std::fs::read(&one).unwrap(), contents);
    assert_eq!(std::fs::read(&two).unwrap(), contents);
}

#[test]
fn rejects_active_directory_external_torn_truncated_and_path_like_records() {
    for raw in [
        record("active.txt", b"data", 1, false),
        record("folder", b"data", 2, false),
        record("external.txt", b"data", 0, true),
        record("../escape.txt", b"data", 0, false),
    ] {
        assert!(scan(&raw).is_empty());
    }
    let mut torn = record("torn.txt", b"data", 0, false);
    torn[510] ^= 1;
    assert!(scan(&torn).is_empty());
    let valid = record("short.txt", b"data", 0, false);
    assert!(scan(&valid[..900]).is_empty());
}

#[test]
fn resident_images_are_validated_and_dont_become_duplicate_signature_results() {
    let png = include_bytes!("fixtures/real.png");
    let found = scan(&record("photo.png", png, 0, false));
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].validation, Validation::ImageDecoded);
    assert_eq!(found[0].recovery_name(1), "photo.png");
    let mut bad = png.to_vec();
    bad[29] ^= 1;
    assert!(scan(&record("broken.png", &bad, 0, false)).is_empty());
}

#[test]
fn empty_files_and_portable_original_names() {
    let found = scan(&record("CON.txt", b"", 0, false));
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].recovery_name(1), "_CON.txt");
    let path_temp = tempfile::tempdir().unwrap();
    let path = path_temp.path().join("empty-resident.txt");
    recover_reader(&mut Unreadable, &path, &found[0]).unwrap();
    assert_eq!(std::fs::metadata(&path).unwrap().len(), 0);
}

#[test]
fn deleted_records_preserve_standard_information_dates() {
    let mut raw = record("notes.txt", b"hello", 0, false);
    let seconds = [
        1_704_067_200i64,
        1_704_153_600,
        1_704_240_000,
        1_704_326_400,
    ];
    // The fixture's first resident attribute is $STANDARD_INFORMATION.
    let value = 64 + 24;
    for (index, seconds) in seconds.iter().enumerate() {
        let filetime = u64::try_from(*seconds + 11_644_473_600).unwrap() * 10_000_000;
        raw[value + index * 8..value + index * 8 + 8].copy_from_slice(&filetime.to_le_bytes());
    }
    let rows = scan(&raw);
    assert_eq!(rows.len(), 1);
    let metadata = rows[0].ntfs.as_ref().unwrap();
    assert_eq!(metadata.created, Some(seconds[0]));
    assert_eq!(metadata.modified, Some(seconds[1]));
    assert_eq!(metadata.record_changed, Some(seconds[2]));
    assert_eq!(metadata.accessed, Some(seconds[3]));
    assert_eq!(metadata.sequence_number, 7);
    assert_eq!(rows[0].resident_data.as_deref(), Some(&b"hello"[..]));
}

#[test]
fn missing_or_invalid_dates_do_not_invalidate_recoverable_content() {
    let mut raw = record("notes.txt", b"hello", 0, false);
    raw[88..96].copy_from_slice(&u64::MAX.to_le_bytes());
    let rows = scan(&raw);
    assert_eq!(rows.len(), 1);
    let metadata = rows[0].ntfs.as_ref().unwrap();
    assert_eq!(metadata.created, None);
    assert_eq!(metadata.modified, None);
    assert_eq!(metadata.accessed, None);
    assert_eq!(metadata.record_changed, None);
    assert_eq!(rows[0].resident_data.as_deref(), Some(&b"hello"[..]));
}

#[test]
fn new_resident_formats_keep_their_names_and_export_after_rechecking() {
    for (name, contents, kind, expected) in [
        (
            "installer.deb",
            include_bytes!("fixtures/formats/sample.deb").as_slice(),
            "deb",
            Validation::StructureChecked,
        ),
        (
            "classes.dex",
            include_bytes!("fixtures/formats/sample.dex").as_slice(),
            "dex",
            Validation::MetadataChecked,
        ),
    ] {
        let raw = record(name, contents, 0, false);
        let found = scan(&raw);
        assert_eq!(found.len(), 1);
        assert_eq!(found[0].kind, kind);
        assert_eq!(found[0].validation, expected);
        assert_eq!(found[0].recovery_name(1), name);
        let dir = tempfile::tempdir().unwrap();
        let output = dir.path().join(name);
        recover_reader(&mut Unreadable, &output, &found[0]).unwrap();
        assert_eq!(std::fs::read(output).unwrap(), contents);
    }
}
