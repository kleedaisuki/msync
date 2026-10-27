//! A framed logical tree is independent of Sandboxie's physical drive mappings.

use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::os::windows::fs::MetadataExt;
use std::path::{Path, PathBuf};

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWrite};

use super::wire::{self, Decode, Encode};
use crate::transaction::filesystem::{Entry, NodeMetadata, Snapshot};

pub(super) async fn send_snapshot(
    sink: &mut (impl AsyncWrite + Unpin),
    logical: &Path,
    snapshot: &Snapshot,
    buffer: usize,
) -> io::Result<()> {
    let mut root = Encode::default();
    root.text(logical.as_os_str())?;
    wire::send(sink, wire::ROOT, &root.0).await?;
    for (relative, entry) in snapshot.entries() {
        let mut body = Encode::default();
        body.text(relative.as_os_str())?;
        match entry {
            Entry::Directory { metadata } => {
                body.integer(u64::from(metadata.permissions.readonly()));
                body.modified(metadata.modified);
                wire::send(sink, wire::DIRECTORY, &body.0).await?;
            }
            Entry::File { artifact, metadata } => {
                body.integer(u64::from(metadata.permissions.readonly()));
                body.modified(metadata.modified);
                body.integer(artifact.len());
                wire::send(sink, wire::FILE, &body.0).await?;
                let mut file = tokio::fs::File::open(artifact.path()).await?;
                let mut bytes = vec![0; buffer];
                loop {
                    let count = file.read(&mut bytes).await?;
                    if count == 0 {
                        break;
                    }
                    wire::send(sink, wire::DATA, &bytes[..count]).await?;
                }
            }
        }
    }
    wire::send(sink, wire::END_ROOT, &[]).await
}

/// The caller fixes the target root after authenticating its logical ROOT frame.
pub(super) async fn receive_tree(
    source: &mut (impl AsyncRead + Unpin),
    target: &Path,
) -> io::Result<()> {
    let mut directories: BTreeMap<PathBuf, (bool, std::time::SystemTime)> = BTreeMap::new();
    let mut seen = BTreeMap::new();
    for _ in 0..1_000_000 {
        let (kind, bytes) = wire::receive(source).await?;
        if kind == wire::END_ROOT && bytes.is_empty() {
            for (path, (readonly, modified)) in directories.into_iter().rev() {
                set_metadata(&path, readonly, modified)?;
            }
            return Ok(());
        }
        let mut body = Decode::new(&bytes);
        let relative = body.path()?;
        wire::relative(&relative)?;
        if seen.insert(relative.clone(), ()).is_some() {
            return Err(wire::invalid("duplicate tree entry"));
        }
        let readonly = match body.integer()? {
            0 => false,
            1 => true,
            _ => return Err(wire::invalid("invalid file attribute")),
        };
        let modified = body.modified()?;
        let path = wire::joined(target, &relative);
        if !relative.as_os_str().is_empty() {
            let parent = relative.parent().unwrap_or_else(|| Path::new(""));
            if !directories.contains_key(&wire::joined(target, parent)) {
                return Err(wire::invalid("tree entry has no declared directory parent"));
            }
        }
        match kind {
            wire::DIRECTORY => {
                body.finish()?;
                fs::create_dir(&path)?;
                directories.insert(path, (readonly, modified));
            }
            wire::FILE => {
                let length = body.integer()?;
                body.finish()?;
                let mut file = tokio::fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&path)
                    .await?;
                let mut remaining = length;
                while remaining > 0 {
                    let (kind, bytes) = wire::receive(source).await?;
                    if kind != wire::DATA || bytes.is_empty() || bytes.len() as u64 > remaining {
                        return Err(wire::invalid("file data does not match declared length"));
                    }
                    use tokio::io::AsyncWriteExt;
                    file.write_all(&bytes).await?;
                    remaining -= bytes.len() as u64;
                }
                use tokio::io::AsyncWriteExt;
                file.flush().await?;
                drop(file);
                set_metadata(&path, readonly, modified)?;
            }
            _ => return Err(wire::invalid("unexpected tree frame")),
        }
    }
    Err(wire::invalid("tree exceeds entry limit"))
}

pub(super) fn set_metadata(
    path: &Path,
    readonly: bool,
    modified: std::time::SystemTime,
) -> io::Result<()> {
    let mut permissions = fs::metadata(path)?.permissions();
    #[allow(clippy::permissions_set_readonly_false)]
    permissions.set_readonly(readonly);
    NodeMetadata::new(permissions, modified).apply(path)
}

pub(super) fn safe_path(path: &Path) -> io::Result<PathBuf> {
    if !path.is_absolute() || path.file_name().is_none() {
        return Err(wire::invalid(
            "managed root must be absolute and cannot be a volume root",
        ));
    }
    // A slash or final dot can make the first metadata query follow a junction.
    // Inspect the component-trimmed leaf too before any canonicalization.
    for ancestor in path
        .ancestors()
        .chain(path.components().as_path().ancestors())
    {
        match fs::symlink_metadata(ancestor) {
            Ok(metadata) if metadata.file_attributes() & 0x400 != 0 => {
                return Err(wire::invalid("reparse points are not managed roots"));
            }
            Ok(_) => {}
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error),
        }
    }
    fs::canonicalize(path)
}

/// Canonical spelling is not a containment proof by itself: compare directory
/// identities along the candidate's ancestry after rejecting reparse traversal.
pub(super) fn contains_directory(root: &Path, candidate: &Path) -> io::Result<bool> {
    let root = safe_path(root)?;
    let candidate = safe_path(candidate)?;
    if !root.is_dir() || !candidate.is_dir() {
        return Err(wire::invalid(
            "program cwd and managed roots must be ordinary directories",
        ));
    }
    let root = same_file::Handle::from_path(root)?;
    for ancestor in candidate.ancestors() {
        if same_file::Handle::from_path(ancestor)? == root {
            return Ok(true);
        }
    }
    Ok(false)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::transaction::filesystem::SnapshotStore;
    use std::time::{Duration, UNIX_EPOCH};

    fn fixture() -> tempfile::TempDir {
        let project = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&project).unwrap();
        tempfile::Builder::new()
            .prefix("view-cwd-")
            .tempdir_in(project)
            .unwrap()
    }

    #[test]
    fn cwd_containment_accepts_dos_verbatim_and_case_aliases_but_not_prefix_siblings() {
        let fixture = fixture();
        let root = fixture.path().join("managed");
        let child = root.join("child");
        let outside = fixture.path().join("managed-other");
        fs::create_dir_all(&child).unwrap();
        fs::create_dir(&outside).unwrap();
        fs::write(root.join("not-a-directory"), "data").unwrap();
        let canonical = fs::canonicalize(&root).unwrap();
        let ordinary = PathBuf::from(super::super::native::configuration_path(&canonical).unwrap());
        assert_ne!(
            canonical, ordinary,
            "exercise the cloud's ordinary/verbatim mismatch"
        );
        assert!(contains_directory(&canonical, &ordinary).unwrap());
        assert!(contains_directory(&ordinary, &canonical).unwrap());
        assert!(contains_directory(&canonical, &ordinary.join("child")).unwrap());
        assert!(contains_directory(&canonical, &ordinary.join("CHILD")).unwrap());
        assert!(!contains_directory(&canonical, &outside).unwrap());
        assert!(
            !contains_directory(&canonical, &ordinary.join("..").join("managed-other")).unwrap()
        );
        assert!(contains_directory(&canonical, Path::new("managed")).is_err());
        assert!(contains_directory(&canonical, &root.join("not-a-directory")).is_err());
    }

    #[test]
    fn cwd_containment_rejects_junction_escapes_including_slash_and_dot_spellings() {
        let fixture = fixture();
        let root = fixture.path().join("managed");
        let outside = fixture.path().join("outside");
        fs::create_dir(&root).unwrap();
        fs::create_dir_all(outside.join("child")).unwrap();
        let junction = root.join("escape");
        create_junction(&junction, &outside).unwrap();
        for path in [
            junction.clone(),
            junction.join("child"),
            junction.join(""),
            junction.join("."),
        ] {
            assert!(
                contains_directory(&root, &path).is_err(),
                "{}",
                path.display()
            );
        }
        fs::remove_dir(&junction).unwrap();
        assert!(outside.join("child").is_dir());
    }

    fn create_junction(link: &Path, target: &Path) -> io::Result<()> {
        use std::os::windows::ffi::OsStrExt;
        use std::os::windows::fs::OpenOptionsExt;
        use std::os::windows::io::AsRawHandle;
        use windows_sys::Wdk::Storage::FileSystem::REPARSE_DATA_BUFFER;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT,
        };
        use windows_sys::Win32::System::IO::DeviceIoControl;

        // Ordinary NTFS junctions do not require symlink privilege or a helper
        // shell. The SDK structure supplies the variable-sized mount-point ABI.
        fs::create_dir(link)?;
        let target = super::super::native::configuration_path(&fs::canonicalize(target)?)?;
        let substitute = std::ffi::OsString::from(format!(r"\??\{}", target.to_string_lossy()));
        let substitute = substitute.encode_wide().collect::<Vec<_>>();
        let print = target.encode_wide().collect::<Vec<_>>();
        let mut names = substitute.clone();
        names.push(0);
        names.extend_from_slice(&print);
        names.push(0);
        let bytes = std::mem::offset_of!(
            REPARSE_DATA_BUFFER,
            Anonymous.MountPointReparseBuffer.PathBuffer
        ) + names.len() * 2;
        let mut storage = vec![0usize; bytes.div_ceil(std::mem::size_of::<usize>())];
        let data = storage.as_mut_ptr().cast::<REPARSE_DATA_BUFFER>();
        let handle = fs::OpenOptions::new()
            .write(true)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
            .open(link)?;
        let mut returned = 0;
        // SAFETY: Aligned SDK-typed storage contains the complete mount-point
        // header and its two NUL-terminated UTF-16 path strings.
        let result = unsafe {
            (*data).ReparseTag = 0xa0000003; // IO_REPARSE_TAG_MOUNT_POINT
            (*data).ReparseDataLength = (bytes - 8) as u16;
            let mount = &raw mut (*data).Anonymous.MountPointReparseBuffer;
            (*mount).SubstituteNameOffset = 0;
            (*mount).SubstituteNameLength = (substitute.len() * 2) as u16;
            (*mount).PrintNameOffset = ((substitute.len() + 1) * 2) as u16;
            (*mount).PrintNameLength = (print.len() * 2) as u16;
            names
                .as_ptr()
                .copy_to_nonoverlapping((&raw mut (*mount).PathBuffer).cast::<u16>(), names.len());
            DeviceIoControl(
                handle.as_raw_handle(),
                0x900a4,
                data.cast(),
                bytes as u32,
                std::ptr::null_mut(),
                0,
                &mut returned,
                std::ptr::null_mut(),
            ) // FSCTL_SET_REPARSE_POINT
        };
        if result == 0 {
            return Err(io::Error::last_os_error());
        }
        Ok(())
    }

    #[tokio::test]
    async fn logical_tree_transport_preserves_file_root_and_empty_directory_times() {
        let project = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&project).unwrap();
        let fixture = tempfile::tempdir_in(project).unwrap();
        let input = fixture.path().join("input");
        let output = fixture.path().join("output");
        fs::create_dir_all(input.join("empty")).unwrap();
        fs::write(input.join("file"), "UTF-8 可莉🌟").unwrap();
        let paths = ["file", "empty", ""];
        for (index, relative) in paths.iter().enumerate() {
            let path = wire::joined(&input, Path::new(relative));
            NodeMetadata::new(
                fs::metadata(&path).unwrap().permissions(),
                UNIX_EPOCH + Duration::from_secs(978_307_210 + index as u64),
            )
            .apply(&path)
            .unwrap();
        }
        let before = paths.map(|relative| {
            fs::metadata(wire::joined(&input, Path::new(relative)))
                .unwrap()
                .modified()
                .unwrap()
        });
        let mut store = SnapshotStore::new(&fixture.path().join("scratch")).unwrap();
        let snapshot = store.capture(&input).unwrap();
        let (mut sink, mut source) = tokio::io::duplex(128);
        let send =
            tokio::spawn(async move { send_snapshot(&mut sink, &input, &snapshot, 64).await });
        let (kind, _) = wire::receive(&mut source).await.unwrap();
        assert_eq!(kind, wire::ROOT);
        receive_tree(&mut source, &output).await.unwrap();
        send.await.unwrap().unwrap();
        let after = paths.map(|relative| {
            fs::metadata(wire::joined(&output, Path::new(relative)))
                .unwrap()
                .modified()
                .unwrap()
        });
        assert_eq!(after, before);
        assert_eq!(
            fs::read_to_string(output.join("file")).unwrap(),
            "UTF-8 可莉🌟"
        );
        store.cleanup().unwrap();
    }
}
