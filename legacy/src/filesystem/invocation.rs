use super::*;

#[derive(Debug)]
struct CreatedDirectory {
    path: PathBuf,
    identity: Handle,
}

#[derive(Debug)]
struct Invocation {
    root: CreatedDirectory,
    scaffolds: Vec<CreatedDirectory>,
    closed: std::sync::Mutex<bool>,
}

/// Shared ownership of one ephemeral scratch directory. Its exact identity lets
/// source capture omit the running invocation without hiding user directories.
#[derive(Clone, Debug)]
pub struct InvocationRoot(Arc<Invocation>);

impl InvocationRoot {
    pub fn create(scratch: &Path) -> Result<Self> {
        let scratch = absolute(scratch)?;
        let mut missing = Vec::new();
        let mut existing = scratch.as_path();
        loop {
            match fs::metadata(existing) {
                Ok(_) => break,
                Err(cause) if cause.kind() == io::ErrorKind::NotFound => {
                    missing.push(existing.to_owned());
                    existing = existing
                        .parent()
                        .ok_or_else(|| error("create invocation scratch", &scratch, cause))?;
                }
                Err(cause) => return Err(error("inspect invocation scratch", existing, cause)),
            }
        }
        fs::create_dir_all(&scratch)
            .map_err(|cause| error("create invocation scratch", &scratch, cause))?;
        let scaffolds = missing
            .into_iter()
            .map(|path| directory(&path))
            .collect::<Result<Vec<_>>>()?;
        let path = create_owned_directory(&scratch, "execute")?;
        let root = directory(&path)?;
        Ok(Self(Arc::new(Invocation {
            root,
            scaffolds,
            closed: std::sync::Mutex::new(false),
        })))
    }

    pub fn path(&self) -> &Path {
        &self.0.root.path
    }

    pub fn verify(&self) -> Result<()> {
        ensure_identity(self.path(), Some(&self.0.root.identity))
    }

    pub fn contains_private(&self, path: &Path) -> Result<bool> {
        self.contains(&absolute(path)?)
    }

    /// Scratch ancestors created by this invocation are omitted only when they
    /// contain no user data; they are not additional cleanup authorities.
    pub fn scaffold_directory(&self, path: &Path) -> Result<Option<NodeMetadata>> {
        if !self.owns_scaffold(path)? {
            return Ok(None);
        }
        fs::metadata(path)
            .and_then(|metadata| NodeMetadata::capture(&metadata))
            .map(Some)
            .map_err(|cause| error("inspect scratch directory", path, cause))
    }

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
        let mut closed = self
            .0
            .closed
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        if !*closed {
            remove_owned(self.path(), &self.0.root.identity)?;
            *closed = true;
        }
        Ok(())
    }

    pub(crate) fn excludes(&self, source: &Path) -> Result<bool> {
        Ok(identity(source)?.as_ref() == Some(&self.0.root.identity))
    }

    pub(super) fn contains(&self, source: &Path) -> Result<bool> {
        for ancestor in source.ancestors() {
            if self.excludes(ancestor)? {
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
        let Some(identity) = identity(source)? else {
            return Ok(None);
        };
        for ancestor in self.path().ancestors() {
            if self::identity(ancestor)?.as_ref() == Some(&identity) {
                return Ok(self.path().strip_prefix(ancestor).ok().map(Path::to_owned));
            }
        }
        Ok(None)
    }

    pub(super) fn owns_scaffold(&self, source: &Path) -> Result<bool> {
        let Some(identity) = identity(source)? else {
            return Ok(false);
        };
        Ok(self
            .0
            .scaffolds
            .iter()
            .any(|directory| directory.identity == identity))
    }

    // Namespace changes keep their native timestamps. The signature remains for
    // callers that previously projected a pre-scratch parent modification time.
    pub(crate) fn logical_metadata(
        &self,
        _source: &Path,
        _metadata: &mut NodeMetadata,
    ) -> Result<()> {
        Ok(())
    }
}

fn identity(path: &Path) -> Result<Option<Handle>> {
    match Handle::from_path(path) {
        Ok(identity) => Ok(Some(identity)),
        Err(cause) if cause.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(cause) => Err(error("identify scratch directory", path, cause)),
    }
}

fn directory(path: &Path) -> Result<CreatedDirectory> {
    Ok(CreatedDirectory {
        path: path.to_owned(),
        identity: Handle::from_path(path)
            .map_err(|cause| error("identify scratch directory", path, cause))?,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn cleanup_keeps_the_native_parent_namespace_timestamp() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        let work = tempfile::tempdir_in(root).unwrap();
        let scratch = work.path().join("scratch");
        let invocation = InvocationRoot::create(&scratch).unwrap();
        let stamp = std::time::UNIX_EPOCH + std::time::Duration::from_secs(12_345);
        NodeMetadata::new(fs::metadata(&scratch).unwrap().permissions(), stamp)
            .apply(&scratch)
            .unwrap();
        invocation.cleanup().unwrap();
        assert_ne!(fs::metadata(&scratch).unwrap().modified().unwrap(), stamp);
        assert_eq!(fs::read_dir(scratch).unwrap().count(), 0);
    }
}
