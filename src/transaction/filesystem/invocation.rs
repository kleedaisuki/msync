use super::*;

#[derive(Debug)]
struct CreatedDirectory {
    path: PathBuf,
    identity: Handle,
}

#[derive(Debug)]
struct Invocation {
    root: CreatedDirectory,
    created_parents: Vec<CreatedDirectory>,
    parent: CreatedDirectory,
    parent_before: NodeMetadata,
    parent_after_creation: SystemTime,
    cleanup: std::sync::Mutex<Cleanup>,
}

#[derive(Debug)]
enum Cleanup {
    Open,
    Removed(SystemTime),
    Closed,
}

/// Proof of a directory created exclusively for this invocation. Captures may
/// omit this exact identity, never a caller-supplied pattern or every `.temp`.
/// Cleanup is explicit; clones retain identity and do not recursively delete.
#[derive(Clone, Debug)]
pub struct InvocationRoot(Arc<Invocation>);

impl InvocationRoot {
    pub fn create(scratch: &Path) -> Result<Self> {
        let scratch = absolute(scratch)?;
        check_path(&scratch)?;
        let mut missing = Vec::new();
        let mut existing = scratch.as_path();
        while safe_metadata(existing)?.is_none() {
            missing.push(existing.to_owned());
            existing = existing.parent().ok_or_else(|| {
                invalid(
                    "create invocation",
                    &scratch,
                    "scratch has no existing directory ancestor",
                )
            })?;
        }
        let metadata = safe_metadata(existing)?.expect("existing ancestor");
        if !metadata.is_dir() {
            return Err(invalid(
                "create invocation",
                existing,
                "scratch ancestor must be a directory",
            ));
        }
        let parent_before = NodeMetadata::capture(&metadata)
            .map_err(|cause| error("capture scratch parent metadata", existing, cause))?;
        let parent = directory(existing)?;
        let mut created_parents = Vec::new();
        for path in missing.into_iter().rev() {
            fs::create_dir(&path)
                .map_err(|cause| error("create invocation scratch", &path, cause))?;
            created_parents.push(directory(&path)?);
        }
        let path = create_owned_directory(&scratch, "execute")?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            fs::set_permissions(&path, Permissions::from_mode(0o700))
                .map_err(|cause| error("protect invocation", &path, cause))?;
        }
        let root = directory(&path)?;
        let parent_after_creation = fs::metadata(&parent.path)
            .and_then(|metadata| metadata.modified())
            .map_err(|cause| error("inspect scratch parent metadata", &parent.path, cause))?;
        Ok(Self(Arc::new(Invocation {
            root,
            created_parents,
            parent,
            parent_before,
            parent_after_creation,
            cleanup: std::sync::Mutex::new(Cleanup::Open),
        })))
    }

    pub fn path(&self) -> &Path {
        &self.0.root.path
    }

    pub fn verify(&self) -> Result<()> {
        check_path(self.path())?;
        ensure_identity(self.path(), Some(&self.0.root.identity))
    }

    pub fn contains_private(&self, path: &Path) -> Result<bool> {
        self.verify()?;
        self.contains(&absolute(path)?)
    }

    /// Remove only the owned invocation. Configured scratch directories remain
    /// available to callers; no pre-existing/user directory is removed.
    pub fn cleanup(&self) -> Result<()> {
        if Arc::strong_count(&self.0) != 1 {
            return Err(error(
                "cleanup invocation",
                self.path(),
                io::Error::new(
                    io::ErrorKind::ResourceBusy,
                    "live stores, snapshots or resource views retain this invocation",
                ),
            ));
        }
        let mut cleanup = self
            .0
            .cleanup
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if matches!(*cleanup, Cleanup::Closed) {
            return Ok(());
        }
        if matches!(*cleanup, Cleanup::Open) {
            self.verify()?;
            let stamp = self.parent_stamp()?;
            remove_owned(self.path(), &self.0.root.identity)?;
            *cleanup = Cleanup::Removed(stamp);
        }
        let Cleanup::Removed(stamp) = *cleanup else {
            unreachable!()
        };
        ensure_identity(self.path(), None)?;
        ensure_identity(&self.0.parent.path, Some(&self.0.parent.identity))?;
        set_modified(&self.0.parent.path, stamp).map_err(|cause| {
            error(
                "restore scratch parent modification time",
                &self.0.parent.path,
                cause,
            )
        })?;
        *cleanup = Cleanup::Closed;
        Ok(())
    }

    pub(super) fn excludes(&self, source: &Path) -> Result<bool> {
        self.verify()?;
        Ok(identity_at(source)?.as_ref() == Some(&self.0.root.identity))
    }

    pub(super) fn contains(&self, source: &Path) -> Result<bool> {
        for ancestor in source.ancestors() {
            if identity_at(ancestor)?.as_ref() == Some(&self.0.root.identity) {
                return Ok(true);
            }
        }
        Ok(false)
    }

    pub(super) fn contains_store(&self, store: &ArtifactRoot) -> Result<bool> {
        self.contains(&store.path)
    }

    pub(super) fn same(&self, other: &Self) -> bool {
        self.0.root.identity == other.0.root.identity
    }

    pub(super) fn relative_to(&self, source: &Path) -> Result<Option<PathBuf>> {
        self.verify()?;
        let Some(identity) = identity_at(source)? else {
            return Ok(None);
        };
        for ancestor in self.path().ancestors() {
            if identity_at(ancestor)?.as_ref() == Some(&identity) {
                return Ok(self.path().strip_prefix(ancestor).ok().map(Path::to_owned));
            }
        }
        Ok(None)
    }

    pub(super) fn created_empty_parent(&self, source: &Path) -> Result<bool> {
        let Some(identity) = identity_at(source)? else {
            return Ok(false);
        };
        Ok(self
            .0
            .created_parents
            .iter()
            .any(|directory| directory.identity == identity))
    }

    pub(super) fn logical_metadata(
        &self,
        source: &Path,
        metadata: &mut NodeMetadata,
    ) -> Result<()> {
        if identity_at(source)?.as_ref() == Some(&self.0.parent.identity)
            && metadata.modified == self.0.parent_after_creation
        {
            metadata.modified = self.0.parent_before.modified;
        }
        Ok(())
    }

    fn parent_stamp(&self) -> Result<SystemTime> {
        ensure_identity(&self.0.parent.path, Some(&self.0.parent.identity))?;
        let current = fs::metadata(&self.0.parent.path)
            .and_then(|metadata| metadata.modified())
            .map_err(|cause| {
                error(
                    "inspect scratch parent modification time",
                    &self.0.parent.path,
                    cause,
                )
            })?;
        Ok(if current == self.0.parent_after_creation {
            self.0.parent_before.modified
        } else {
            current
        })
    }
}

fn directory(path: &Path) -> Result<CreatedDirectory> {
    let metadata = safe_metadata(path)?.ok_or_else(|| {
        invalid(
            "identify invocation directory",
            path,
            "directory is missing",
        )
    })?;
    if !metadata.is_dir() {
        return Err(invalid(
            "identify invocation directory",
            path,
            "ordinary directory required",
        ));
    }
    Ok(CreatedDirectory {
        path: path.to_owned(),
        identity: Handle::from_path(path)
            .map_err(|cause| error("identify invocation directory", path, cause))?,
    })
}

fn set_modified(path: &Path, modified: SystemTime) -> io::Result<()> {
    let mut options = fs::OpenOptions::new();
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, FILE_WRITE_ATTRIBUTES,
        };
        options
            .access_mode(FILE_WRITE_ATTRIBUTES)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS);
    }
    #[cfg(not(windows))]
    options.read(true);
    options
        .open(path)?
        .set_times(fs::FileTimes::new().set_modified(modified))
}
