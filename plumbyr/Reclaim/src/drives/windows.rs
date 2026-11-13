//! Win32 discovery without PowerShell, WMI, or helper executables.
use super::{Destination, Drive};
use std::{
    collections::HashSet,
    fs::File,
    os::windows::{
        ffi::OsStrExt,
        io::{AsRawHandle, FromRawHandle},
    },
    path::Path,
    ptr,
};
use windows_sys::Win32::{
    Foundation::{GENERIC_READ, INVALID_HANDLE_VALUE},
    Storage::FileSystem::{
        CreateFileW, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, GetDriveTypeW,
        GetLogicalDrives, GetVolumeInformationW, GetVolumeNameForVolumeMountPointW,
        GetVolumePathNameW, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, OPEN_EXISTING, QueryDosDeviceW,
    },
    System::{
        IO::DeviceIoControl,
        Ioctl::{
            DISK_EXTENT, DISK_GEOMETRY, DISK_GEOMETRY_EX, FSCTL_ALLOW_EXTENDED_DASD_IO,
            IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IOCTL_DISK_GET_LENGTH_INFO,
            IOCTL_STORAGE_GET_DEVICE_NUMBER, IOCTL_STORAGE_QUERY_PROPERTY,
            STORAGE_DEVICE_DESCRIPTOR, VOLUME_DISK_EXTENTS,
        },
    },
};

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(Some(0)).collect()
}

fn is_volume_device(path: &Path) -> bool {
    path.to_str().is_some_and(|path| {
        matches!(path.as_bytes(), [b'\\', b'\\', b'.', b'\\', letter, b':'] if letter.is_ascii_alphabetic())
    })
}

/// Read-only access through the end of a Windows volume. Without extended DASD
/// reads, filesystem boundary checks can return EOF before the partition ends,
/// including during `SourceReader`'s bounded read-ahead near the final clusters.
pub fn open_source(path: &Path) -> std::io::Result<File> {
    let file = File::open(path)?;
    if is_volume_device(path) {
        let mut returned = 0;
        // SAFETY: file is a live read-only volume handle. This control code has
        // no input/output buffers, does not write to or dismount the volume, and
        // retains device-driver partition bounds. returned is valid writable u32.
        let ok = unsafe {
            DeviceIoControl(
                file.as_raw_handle(),
                FSCTL_ALLOW_EXTENDED_DASD_IO,
                ptr::null(),
                0,
                ptr::null_mut(),
                0,
                &raw mut returned,
                ptr::null_mut(),
            )
        };
        if ok == 0 {
            let error = std::io::Error::last_os_error();
            return Err(std::io::Error::new(
                error.kind(),
                crate::explain(
                    "Could not prepare read-only access to this volume: {0}",
                    error.to_string(),
                ),
            ));
        }
    }
    Ok(file)
}
fn text(bytes: &[u16]) -> String {
    String::from_utf16_lossy(&bytes[..bytes.iter().position(|&b| b == 0).unwrap_or(bytes.len())])
}
fn open(path: &str) -> Option<File> {
    let name = wide(path);
    // Metadata-only access keeps enumeration available to ordinary users.
    for access in [GENERIC_READ, 0] {
        // SAFETY: name is NUL-terminated and lives through this call. No security
        // attributes or template handle; the returned handle is owned below.
        let handle = unsafe {
            CreateFileW(
                name.as_ptr(),
                access,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                ptr::null(),
                OPEN_EXISTING,
                0,
                ptr::null_mut(),
            )
        };
        if handle != INVALID_HANDLE_VALUE {
            // SAFETY: handle is a valid handle just returned by CreateFileW and owned by nothing else.
            return Some(unsafe { File::from_raw_handle(handle) });
        }
    }
    None
}
fn query(file: &File, code: u32, input: &[u8]) -> Option<Vec<u8>> {
    let mut output = vec![0u8; 65536];
    let mut returned = 0;
    // SAFETY: both buffers are allocated for the supplied lengths; synchronous
    // operation, no OVERLAPPED and no references escape the call.
    let ok = unsafe {
        DeviceIoControl(
            file.as_raw_handle(),
            code,
            input.as_ptr().cast(),
            win32_len(input.len()),
            output.as_mut_ptr().cast(),
            win32_len(output.len()),
            &raw mut returned,
            ptr::null_mut(),
        )
    };
    if ok == 0 || returned as usize > output.len() {
        return None;
    }
    output.truncate(returned as usize);
    Some(output)
}
fn u32_at(bytes: &[u8], at: usize) -> Option<u32> {
    Some(u32::from_le_bytes(bytes.get(at..at + 4)?.try_into().ok()?))
}
fn positive_i64(bytes: &[u8], at: usize) -> Option<u64> {
    u64::try_from(i64::from_le_bytes(bytes.get(at..at + 8)?.try_into().ok()?))
        .ok()
        .filter(|n| *n > 0)
}
fn product(file: &File) -> Option<String> {
    // STORAGE_PROPERTY_QUERY: StorageDeviceProperty, PropertyStandardQuery.
    let bytes = query(file, IOCTL_STORAGE_QUERY_PROPERTY, &[0; 12])?;
    let offset = u32_at(
        &bytes,
        std::mem::offset_of!(STORAGE_DEVICE_DESCRIPTOR, ProductIdOffset),
    )? as usize;
    if offset == 0 {
        return None;
    }
    let value = bytes.get(offset..)?.split(|b| *b == 0).next()?;
    let value = String::from_utf8_lossy(value).trim().to_owned();
    (!value.is_empty()).then_some(value)
}
pub fn discover() -> Result<Vec<Drive>, String> {
    let mut names = vec![0u16; 65536];
    // SAFETY: writable UTF-16 buffer, null name requests the device namespace.
    let count = unsafe { QueryDosDeviceW(ptr::null(), names.as_mut_ptr(), win32_len(names.len())) }
        as usize;
    if count == 0 {
        return Err(format!(
            "Could not list Windows drives: {}. Open a disk image instead.",
            std::io::Error::last_os_error()
        ));
    }
    let mut disks: Vec<_> = names[..count]
        .split(|b| *b == 0)
        .filter_map(|name| {
            let name = String::from_utf16_lossy(name);
            name.strip_prefix("PhysicalDrive")
                .and_then(|n| n.parse::<u32>().ok())
                .map(|number| (number, name))
        })
        .collect();
    disks.sort_by_key(|(number, _)| *number);
    let mut rows = Vec::new();
    // Ordered by disk number, so every pass over it is deterministic.
    let mut disk_rows = std::collections::BTreeMap::new();
    let mut represented_disks = HashSet::new();
    for (number, name) in disks {
        let path = format!(r"\\.\{name}");
        let Some(file) = open(&path) else {
            continue;
        };
        let Some(geometry) = query(&file, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, &[]) else {
            continue;
        };
        let Some(size) = positive_i64(&geometry, std::mem::offset_of!(DISK_GEOMETRY_EX, DiskSize))
        else {
            continue;
        };
        let sector = u32_at(
            &geometry,
            std::mem::offset_of!(DISK_GEOMETRY, BytesPerSector),
        )
        .filter(|n| n.is_power_of_two() && (512..=65536).contains(n))
        .unwrap_or(4096) as usize;
        disk_rows.insert(number, rows.len());
        rows.push(Drive {
            path: path.into(),
            name: product(&file).unwrap_or(name),
            name_is_message: false,
            size,
            kind: "Entire drive".into(),
            location: "Physical disk".into(),
            mounted: false,
            encrypted: false,
            sector_size: sector,
            phone: false,
        });
    }
    // SAFETY: GetLogicalDrives has no pointer arguments.
    let letters = unsafe { GetLogicalDrives() };
    if letters == 0 {
        return Err(format!(
            "Could not list Windows drives: {}. Open a disk image instead.",
            std::io::Error::last_os_error()
        ));
    }
    let mut unknown_backing_disk = false;
    for index in 0..26 {
        if letters & (1 << index) == 0 {
            continue;
        }
        let letter = char::from(b'A' + index);
        let root = wide(&format!("{letter}:\\"));
        // SAFETY: root is NUL-terminated for the duration of each call.
        let kind = unsafe { GetDriveTypeW(root.as_ptr()) };
        if kind != 2 && kind != 3 {
            continue;
        } // local/removable only
        let path = format!(r"\\.\{letter}:");
        let Some(file) = open(&path) else {
            unknown_backing_disk = true;
            continue;
        };
        let Some(extents) = query(&file, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, &[]) else {
            unknown_backing_disk = true;
            continue;
        };
        let extent_offset = std::mem::offset_of!(VOLUME_DISK_EXTENTS, Extents);
        // All backing disks receive mounted warnings, even when a spanned
        // volume is deliberately omitted from recovery sources.
        let count = u32_at(&extents, 0).unwrap_or(0) as usize;
        for i in 0..count.min(extents.len() / size_of::<DISK_EXTENT>()) {
            let offset = extent_offset + i * size_of::<DISK_EXTENT>();
            if let Some(row) = u32_at(&extents, offset).and_then(|n| disk_rows.get(&n)) {
                rows[*row].mounted = true;
                rows[*row].location = "Contains mounted volumes".into();
            }
        }
        if count != 1 {
            continue;
        }
        let Some(size) = positive_i64(
            &extents,
            extent_offset + std::mem::offset_of!(DISK_EXTENT, ExtentLength),
        ) else {
            continue;
        };
        let mut label = [0u16; 261];
        let mut filesystem = [0u16; 64];
        // SAFETY: root is NUL-terminated and each buffer length matches its array.
        let ok = unsafe {
            GetVolumeInformationW(
                root.as_ptr(),
                label.as_mut_ptr(),
                win32_len(label.len()),
                ptr::null_mut(),
                ptr::null_mut(),
                ptr::null_mut(),
                filesystem.as_mut_ptr(),
                win32_len(filesystem.len()),
            )
        };
        // Locked/inaccessible volumes must not be offered as usable sources.
        let encrypted = ok == 0;
        let sector_size = u32_at(&extents, extent_offset)
            .and_then(|number| disk_rows.get(&number))
            .map_or(4096, |&index| rows[index].sector_size);
        if !encrypted && let Some(number) = u32_at(&extents, extent_offset) {
            represented_disks.insert(number);
        }
        rows.push(Drive {
            path: path.into(),
            name: if label[0] == 0 {
                format!("{letter}:")
            } else {
                text(&label)
            },
            name_is_message: false,
            size,
            kind: "Volume".into(),
            location: format!("Mounted at {letter}:\\"),
            mounted: true,
            encrypted,
            sector_size,
            phone: false,
        });
    }
    if unknown_backing_disk {
        // A volume we could not inspect may belong to any physical disk.
        // Keep the in-use warning rather than falsely promising an idle source.
        for &index in disk_rows.values() {
            rows[index].mounted = true;
            rows[index].location = "Contains mounted volumes".into();
        }
    }
    let hidden: HashSet<_> = represented_disks
        .iter()
        .filter_map(|number| disk_rows.get(number).map(|&index| rows[index].path.clone()))
        .collect();
    rows.retain(|row| !hidden.contains(&row.path));
    Ok(rows)
}
/// Physical disk numbers behind a volume device such as `\\.\C:`.
fn volume_disks(path: &str) -> Option<HashSet<u32>> {
    extent_disks(&query(
        &open(path)?,
        IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
        &[],
    )?)
}

fn extent_disks(extents: &[u8]) -> Option<HashSet<u32>> {
    let first = std::mem::offset_of!(VOLUME_DISK_EXTENTS, Extents);
    let count = u32_at(&extents, 0)? as usize;
    // A partial response must never prove that two sets of disks are separate.
    let end = count
        .checked_mul(size_of::<DISK_EXTENT>())?
        .checked_add(first)?;
    if count == 0 || end > extents.len() {
        return None;
    }
    let disks: HashSet<_> = (0..count)
        .map(|i| u32_at(&extents, first + i * size_of::<DISK_EXTENT>()))
        .collect::<Option<_>>()?;
    Some(disks)
}

fn handle_disks(file: &File, physical: bool) -> Option<HashSet<u32>> {
    if !physical {
        return extent_disks(&query(file, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, &[])?);
    }
    // Physical disk handles don't expose volume extents. Query their device
    // number on the actual authorized handle, not the current drive-letter path.
    let number = query(file, IOCTL_STORAGE_GET_DEVICE_NUMBER, &[])?;
    if u32_at(&number, 0)? != 7 {
        return None;
    } // FILE_DEVICE_DISK
    Some(HashSet::from([u32_at(&number, 4)?]))
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct DeviceInfo {
    pub size: u64,
    pub alignment: usize,
    pub disks: HashSet<u32>,
    pub physical: bool,
}

/// Raw device handles do not have ordinary file metadata. Keep their size,
/// sector geometry and backing disks pinned to the same handle used for reads.
pub fn device_info(file: &File, physical: bool) -> Option<DeviceInfo> {
    let size = positive_i64(&query(file, IOCTL_DISK_GET_LENGTH_INFO, &[])?, 0)?;
    let alignment = query(file, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, &[])
        .and_then(|geometry| {
            u32_at(
                &geometry,
                std::mem::offset_of!(DISK_GEOMETRY, BytesPerSector),
            )
        })
        .or_else(|| {
            // StorageAccessAlignmentProperty, PropertyStandardQuery. Volumes
            // whose driver doesn't expose disk geometry can expose this descriptor.
            let descriptor = query(
                file,
                IOCTL_STORAGE_QUERY_PROPERTY,
                &[6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            )?;
            u32_at(&descriptor, 16) // STORAGE_ACCESS_ALIGNMENT_DESCRIPTOR.BytesPerLogicalSector
        })?;
    if !alignment.is_power_of_two() || !(512..=65536).contains(&alignment) {
        return None;
    }
    Some(DeviceInfo {
        size,
        alignment: alignment as usize,
        disks: handle_disks(file, physical)?,
        physical,
    })
}

pub fn destination_for_device(source: &DeviceInfo, folder: &Path) -> Destination {
    match folder_disks(folder) {
        Some(disks) if source.disks.is_disjoint(&disks) => Destination::Separate,
        Some(_) => Destination::SameDrive,
        None => Destination::Unknown,
    }
}
/// Physical disk numbers behind the volume a folder is stored on, including
/// volumes mounted into folders rather than drive letters.
fn folder_disks(folder: &Path) -> Option<HashSet<u32>> {
    let name: Vec<u16> = folder.as_os_str().encode_wide().chain([0]).collect();
    let mut mount = vec![0u16; 32768];
    let mut volume = vec![0u16; 64];
    // SAFETY: name is NUL-terminated; the mount buffer matches the given length.
    let ok = unsafe {
        GetVolumePathNameW(name.as_ptr(), mount.as_mut_ptr(), win32_len(mount.len())) != 0
    }
        // SAFETY: mount was NUL-terminated by the call above; the volume buffer
        // matches the given length.
        && unsafe {
            GetVolumeNameForVolumeMountPointW(
                mount.as_ptr(),
                volume.as_mut_ptr(),
                win32_len(volume.len()),
            ) != 0
        };
    if !ok {
        return None;
    }
    // The volume GUID path names the volume device once its slash is removed.
    volume_disks(text(&volume).trim_end_matches('\\'))
}
#[cfg(test)]
pub fn destination(source: &Path, folder: &Path) -> Destination {
    let source = source.to_string_lossy();
    // Writing next to a disk image never changes the image's own bytes.
    let Some(device) = source.strip_prefix(r"\\.\") else {
        return Destination::Separate;
    };
    let source = match device
        .strip_prefix("PhysicalDrive")
        .and_then(|n| n.parse().ok())
    {
        Some(number) => Some(HashSet::from([number])),
        None => volume_disks(&source),
    };
    match (source, folder_disks(folder)) {
        (Some(source), Some(folder)) if source.is_disjoint(&folder) => Destination::Separate,
        (Some(_), Some(_)) => Destination::SameDrive,
        _ => Destination::Unknown,
    }
}

/// A buffer length for a Win32 `u32` length parameter. Every buffer here is far
/// below 4 GiB; if one ever were not, the API is told less than the real size,
/// which is always safe, instead of a wrapped size.
fn win32_len(len: usize) -> u32 {
    u32::try_from(len).unwrap_or(u32::MAX)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn truncated_disk_extent_lists_never_prove_separation() {
        let first = std::mem::offset_of!(VOLUME_DISK_EXTENTS, Extents);
        let mut bytes = vec![0u8; first + size_of::<DISK_EXTENT>()];
        bytes[..4].copy_from_slice(&2u32.to_le_bytes());
        bytes[first..first + 4].copy_from_slice(&3u32.to_le_bytes());
        assert!(extent_disks(&bytes).is_none());
        bytes[..4].copy_from_slice(&1u32.to_le_bytes());
        assert_eq!(extent_disks(&bytes), Some(HashSet::from([3])));
        bytes[..4].copy_from_slice(&u32::MAX.to_le_bytes());
        assert!(extent_disks(&bytes).is_none());
        assert!(extent_disks(&[]).is_none());
    }

    #[test]
    fn destination_checks_use_pinned_source_disks() {
        let folder = tempfile::tempdir().unwrap();
        let source = DeviceInfo {
            size: 4096,
            alignment: 512,
            disks: folder_disks(folder.path()).unwrap(),
            physical: false,
        };
        assert_eq!(
            destination_for_device(&source, folder.path()),
            Destination::SameDrive
        );
        assert_eq!(
            destination_for_device(&source, &folder.path().join("missing")),
            Destination::SameDrive
        );
        let other = DeviceInfo {
            disks: HashSet::from([u32::MAX]),
            ..source
        };
        assert_eq!(
            destination_for_device(&other, folder.path()),
            Destination::Separate
        );
        assert_eq!(
            destination_for_device(
                &other,
                Path::new(r"\\?\Volume{00000000-0000-0000-0000-000000000000}\folder")
            ),
            Destination::Unknown
        );
    }

    #[test]
    fn recovery_to_another_folder_on_the_source_volume_is_blocked() {
        let folder = tempfile::tempdir().unwrap();
        let absolute = std::fs::canonicalize(folder.path()).unwrap();
        let path = absolute.to_string_lossy();
        let letter = path.trim_start_matches(r"\\?\").chars().next().unwrap();
        let source = format!(r"\\.\{letter}:");
        assert_eq!(
            destination(Path::new(&source), folder.path()),
            Destination::SameDrive
        );
        assert_eq!(
            destination(&folder.path().join("disk.img"), folder.path()),
            Destination::Separate
        );
    }

    #[test]
    fn extended_raw_reads_apply_only_to_volume_device_handles() {
        for path in [r"\\.\C:", r"\\.\d:"] {
            assert!(is_volume_device(Path::new(path)));
        }
        for path in [
            r"C:\",
            r"C:\disk.img",
            r"\\server\disk.img",
            r"\\.\PhysicalDrive0",
            r"\\.\C:\file",
            r"\\.\1:",
        ] {
            assert!(!is_volume_device(Path::new(path)), "{path}");
        }
    }

    #[test]
    fn disk_image_sources_are_readable_without_device_controls() {
        let folder = tempfile::tempdir().unwrap();
        let path = folder.path().join("source.img");
        std::fs::write(&path, b"read-only source").unwrap();
        let content = std::io::read_to_string(open_source(&path).unwrap()).unwrap();
        assert_eq!(content, "read-only source");
    }
}
