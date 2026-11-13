// Windows subset of Reclaim's MIT-licensed source discovery.
use std::{
    fs::File,
    path::{Path, PathBuf},
};
#[derive(Clone, Debug)]
pub struct Drive {
    pub path: PathBuf,
    pub name: String,
    pub name_is_message: bool,
    pub size: u64,
    pub kind: String,
    pub location: String,
    pub mounted: bool,
    pub encrypted: bool,
    pub sector_size: usize,
    pub phone: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Destination {
    SameDrive,
    Separate,
    Unknown,
}
mod windows;
pub use windows::{DeviceInfo, destination_for_device, device_info, discover};
pub fn open_source(path: &Path) -> std::io::Result<File> {
    windows::open_source(path)
}
