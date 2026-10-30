//! Metadata evidence for object-domain separation, never a pathname heuristic.

use crate::endpoint::{Backend, Location};
use crate::plan::FileClass;

/// A native Linux file cannot be the NTFS/ReFS object replaced by a managed
/// Windows assignment. Unknown, projected and layered filesystems prove nothing.
pub(crate) fn wsl_native_file(location: &Location) -> Option<(Location, FileClass)> {
    if location.backend != Backend::Wsl
        || location.path.contains(['\0', '\\'])
        || location.path.split('/').any(|part| part == "..")
    {
        return None;
    }
    let output = super::remote_command(&Backend::Wsl, WSL_NATIVE_FILE, &[&location.path]).ok()?;
    let fields = output.stdout.split(|byte| *byte == 0).collect::<Vec<_>>();
    if fields.len() != 4 || fields[1] != b"ef53" || !fields[3].is_empty() {
        return None;
    }
    let path = std::str::from_utf8(fields[0]).ok()?;
    if !path.starts_with('/') || path.split('/').any(|part| matches!(part, "." | "..")) {
        return None;
    }
    let kind = match fields[2] {
        b"regular" => FileClass::Regular,
        b"directory" => FileClass::Directory,
        _ => return None,
    };
    Some((
        Location {
            backend: Backend::Wsl,
            path: path.into(),
        },
        kind,
    ))
}

#[cfg(windows)]
pub(crate) fn native_windows_filesystem(location: &Location) -> bool {
    use std::fs;
    use std::os::windows::{
        fs::{MetadataExt, OpenOptionsExt},
        io::AsRawHandle,
    };
    use std::path::{Component, Path, Prefix};
    use std::ptr::null_mut;
    use windows_sys::Win32::Storage::FileSystem::{
        FILE_ATTRIBUTE_REPARSE_POINT, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
        FILE_READ_ATTRIBUTES, GetDriveTypeW, GetFinalPathNameByHandleW,
        GetVolumeInformationByHandleW, VOLUME_NAME_NT,
    };
    if location.backend != Backend::Local {
        return false;
    }
    let path = Path::new(&location.path);
    let drive = match path.components().next() {
        Some(Component::Prefix(prefix)) => match prefix.kind() {
            Prefix::Disk(drive) | Prefix::VerbatimDisk(drive) => drive,
            _ => return false,
        },
        _ => return false,
    };
    if path.components().any(|part| part == Component::ParentDir) {
        return false;
    }
    let root = [u16::from(drive), b':' as u16, b'\\' as u16, 0];
    // A drive letter may be a network mapping even if its server reports NTFS.
    if !matches!(
        unsafe { GetDriveTypeW(root.as_ptr()) },
        2 | 3 | 6 // DRIVE_REMOVABLE, DRIVE_FIXED, DRIVE_RAMDISK from GetDriveTypeW.
    ) {
        return false;
    }
    let mut nearest = None;
    for ancestor in path.ancestors() {
        match fs::symlink_metadata(ancestor) {
            Ok(metadata) if metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 => {
                return false;
            }
            Ok(_) if nearest.is_none() => nearest = Some(ancestor),
            Ok(_) => {}
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {}
            Err(_) => return false,
        }
    }
    let Some(ancestor) = nearest else {
        return false;
    };
    let Ok(file) = fs::OpenOptions::new()
        .access_mode(FILE_READ_ATTRIBUTES)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(ancestor)
    else {
        return false;
    };
    let Ok(metadata) = file.metadata() else {
        return false;
    };
    if metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0 {
        return false;
    }
    let mut native_path = vec![0u16; 32768];
    // SAFETY: The live metadata handle and bounded output buffer are valid.
    let length = unsafe {
        GetFinalPathNameByHandleW(
            file.as_raw_handle(),
            native_path.as_mut_ptr(),
            native_path.len() as u32,
            VOLUME_NAME_NT,
        )
    } as usize;
    if length == 0 || length >= native_path.len() {
        return false;
    }
    let Ok(native_path) = String::from_utf16(&native_path[..length]) else {
        return false;
    };
    if !local_volume_device(&native_path) {
        return false;
    }
    let mut filesystem = [0u16; 32];
    // SAFETY: The metadata handle and bounded UTF-16 output buffer stay live;
    // all unused optional outputs are null as required by the platform API.
    if unsafe {
        GetVolumeInformationByHandleW(
            file.as_raw_handle(),
            null_mut(),
            0,
            null_mut(),
            null_mut(),
            null_mut(),
            filesystem.as_mut_ptr(),
            filesystem.len() as u32,
        )
    } == 0
    {
        return false;
    }
    let end = filesystem
        .iter()
        .position(|unit| *unit == 0)
        .unwrap_or(filesystem.len());
    matches!(
        String::from_utf16_lossy(&filesystem[..end]).as_str(),
        "NTFS" | "ReFS"
    )
}

#[cfg(not(windows))]
pub(crate) fn native_windows_filesystem(_: &Location) -> bool {
    false
}

#[cfg(any(windows, test))]
fn local_volume_device(path: &str) -> bool {
    let lower = path.to_ascii_lowercase();
    let Some(tail) = lower.strip_prefix(r"\device\harddiskvolume") else {
        return false;
    };
    let digits = tail.bytes().take_while(u8::is_ascii_digit).count();
    digits > 0 && tail.as_bytes().get(digits) == Some(&b'\\')
}

const WSL_NATIVE_FILE: &str = r#"
set -eu
p=$1
case $p in '~') p=$HOME;; '~/'*) p=$HOME/${p#\~/};; esac
case $p in /*) ;; *) p=$PWD/$p;; esac
a=$p
while [ "$a" != / ]; do
    [ ! -L "$a" ] || exit 1
    a=${a%/*}; [ -n "$a" ] || a=/
done
p=$(realpath -e -- "$p")
f=$(stat -f -c %t -- "$p")
if [ -f "$p" ]; then k=regular
elif [ -d "$p" ]; then k=directory
else exit 1
fi
printf '%s\0%s\0%s\0' "$p" "$f" "$k"
"#;

#[cfg(test)]
mod tests {
    use super::local_volume_device;

    #[test]
    fn local_volume_identity_rejects_redirectors_and_lookalikes() {
        assert!(local_volume_device(r"\Device\HarddiskVolume42\work\target"));
        assert!(!local_volume_device(r"\Device\Mup\server\share\target"));
        assert!(!local_volume_device(
            r"\Device\LanmanRedirector\server\share"
        ));
        assert!(!local_volume_device(
            r"\Device\HarddiskVolumeShadowCopy1\target"
        ));
        assert!(!local_volume_device(r"\Device\HarddiskVolume1fake\target"));
    }
}
