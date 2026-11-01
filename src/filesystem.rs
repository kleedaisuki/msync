//! Owned snapshots and ordinary same-directory replacement.
//!
//! A file is staged, flushed once and atomically renamed. Directory and multi-root
//! operations are retryable copies, not durable transactions or recovery journals.

use std::collections::BTreeMap;
use std::fmt;
use std::fs::{self, Metadata, Permissions};
use std::io;
use std::path::{Component, Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::SystemTime;

use same_file::Handle;

mod invocation;
pub use invocation::InvocationRoot;
pub mod durable;
pub mod recovery;

static NEXT_ID: AtomicU64 = AtomicU64::new(0);

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Recovery {
    Unchanged,
    Restored,
    JournalRetained,
}

#[derive(Debug)]
pub struct FsError {
    pub action: &'static str,
    pub path: PathBuf,
    pub cause: io::Error,
    pub journal: Option<PathBuf>,
    pub recovery: Recovery,
    pub recovery_error: Option<io::Error>,
    pub help: Option<&'static str>,
}

impl fmt::Display for FsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "{} {}: {}", self.action, self.path.display(), self.cause)?;
        if let Some(journal) = &self.journal {
            write!(f, "; journal {} ({:?})", journal.display(), self.recovery)?;
        }
        if let Some(error) = &self.recovery_error {
            write!(
                f,
                "; recovery failed: {error}; retain the journal and retry recovery"
            )?;
        }
        if let Some(help) = self.help {
            write!(f, "; help: {help}")?;
        }
        Ok(())
    }
}

impl std::error::Error for FsError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(&self.cause)
    }
}

type Result<T> = std::result::Result<T, FsError>;

fn error(action: &'static str, path: &Path, cause: io::Error) -> FsError {
    FsError {
        action,
        path: path.to_owned(),
        cause,
        journal: None,
        recovery: Recovery::Unchanged,
        recovery_error: None,
        help: None,
    }
}

fn invalid(action: &'static str, path: &Path, message: &'static str) -> FsError {
    error(
        action,
        path,
        io::Error::new(io::ErrorKind::InvalidInput, message),
    )
}

#[derive(Debug)]
struct ArtifactRoot {
    path: PathBuf,
    identity: Handle,
    invocation: Option<InvocationRoot>,
}

/// A copied ordinary file. Cloning retains ownership of its artifact directory.
#[derive(Clone, Debug)]
pub struct Artifact {
    _owner: Arc<ArtifactRoot>,
    path: PathBuf,
    length: u64,
}

impl Artifact {
    pub fn path(&self) -> &Path {
        &self.path
    }

    pub fn len(&self) -> u64 {
        self.length
    }

    pub fn is_empty(&self) -> bool {
        self.length == 0
    }
}

/// Portable version metadata. Identity, atime and platform ACLs are not copied.
#[derive(Clone, Debug)]
pub struct NodeMetadata {
    pub permissions: Permissions,
    pub modified: SystemTime,
}

impl NodeMetadata {
    pub fn new(permissions: Permissions, modified: SystemTime) -> Self {
        Self {
            permissions,
            modified,
        }
    }

    pub fn capture(metadata: &Metadata) -> io::Result<Self> {
        Ok(Self::new(metadata.permissions(), metadata.modified()?))
    }

    pub(crate) fn apply_link(&self, path: &Path) -> io::Result<()> {
        let modified = filetime::FileTime::from_system_time(self.modified);
        filetime::set_symlink_file_times(path, modified, modified)
    }

    /// Only call this on a privately materialized node, never a shared source.
    pub fn apply(&self, path: &Path) -> io::Result<()> {
        let mut options = fs::OpenOptions::new();
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            use windows_sys::Win32::Storage::FileSystem::{
                FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_WRITE_ATTRIBUTES,
            };
            options
                .access_mode(FILE_WRITE_ATTRIBUTES)
                .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT);
        }
        #[cfg(not(windows))]
        options.read(true);
        options
            .open(path)?
            .set_times(fs::FileTimes::new().set_modified(self.modified))?;
        fs::set_permissions(path, self.permissions.clone())
    }
}

/// Snapshot manifest entries retain metadata and owned artifacts, not file bytes.
#[derive(Clone, Debug)]
pub enum Entry {
    Directory {
        metadata: NodeMetadata,
    },
    File {
        artifact: Artifact,
        metadata: NodeMetadata,
    },
    Symlink {
        target: PathBuf,
        metadata: NodeMetadata,
        directory: bool,
    },
}

impl Entry {
    pub fn metadata(&self) -> &NodeMetadata {
        match self {
            Self::Directory { metadata }
            | Self::File { metadata, .. }
            | Self::Symlink { metadata, .. } => metadata,
        }
    }
}

/// Flattened relative paths; the empty path is the root, and an empty map is absence.
#[derive(Clone, Debug)]
pub struct Snapshot {
    _owner: Arc<ArtifactRoot>,
    entries: BTreeMap<PathBuf, Entry>,
    exclusions: Vec<InvocationRoot>,
}

impl Snapshot {
    pub fn entries(&self) -> &BTreeMap<PathBuf, Entry> {
        &self.entries
    }

    pub fn is_missing(&self) -> bool {
        self.entries.is_empty()
    }

    pub fn is_directory(&self) -> bool {
        matches!(
            self.entries.get(Path::new("")),
            Some(Entry::Directory { .. })
        )
    }

    /// True only for the exact still-owned invocation omitted from this view.
    pub fn excludes_invocation(&self, invocation: &InvocationRoot) -> Result<bool> {
        invocation.verify()?;
        Ok(self
            .exclusions
            .iter()
            .any(|excluded| excluded.same(invocation)))
    }

    /// Verify a backing path belongs to the exact excluded owned invocation.
    /// Missing children are checked through existing ancestors, not just text.
    pub fn excludes_private_path(&self, path: &Path) -> Result<bool> {
        Ok(self.invocation_for_path(path)?.is_some())
    }

    /// Retain the backing invocation while a prepared/running private view uses it.
    pub fn invocation_for_path(&self, path: &Path) -> Result<Option<InvocationRoot>> {
        for invocation in &self.exclusions {
            if invocation.contains_private(path)? {
                return Ok(Some(invocation.clone()));
            }
        }
        Ok(None)
    }
}

/// Owns exclusively created storage under the caller's project-local scratch root.
/// Cleanup is explicit so permission failures cannot silently discard undo data.
#[derive(Debug)]
pub struct SnapshotStore {
    owner: Arc<ArtifactRoot>,
    private_directories: Vec<(PathBuf, Handle)>,
    closed: bool,
}

impl SnapshotStore {
    pub fn new(scratch: &Path) -> Result<Self> {
        let scratch = absolute(scratch)?;
        fs::create_dir_all(&scratch)
            .map_err(|cause| error("create snapshot scratch", &scratch, cause))?;
        let path = create_owned_directory(&scratch, ".msync-versions")?;
        let identity = Handle::from_path(&path)
            .map_err(|cause| error("identify snapshot store", &path, cause))?;
        Ok(Self {
            owner: Arc::new(ArtifactRoot {
                path,
                identity,
                invocation: None,
            }),
            private_directories: Vec::new(),
            closed: false,
        })
    }

    pub fn root(&self) -> &Path {
        &self.owner.path
    }

    /// Compatibility with the retired persistent registry API.
    pub fn exclude_registry(&mut self, _registry: &recovery::Registry) -> Result<()> {
        Ok(())
    }

    pub(crate) fn exclude_directory_identity(
        &mut self,
        path: &Path,
        expected: &Handle,
    ) -> Result<()> {
        self.exclude_private_directory(path, Some(expected))
    }

    fn exclude_private_directory(&mut self, path: &Path, expected: Option<&Handle>) -> Result<()> {
        self.ensure_open()?;
        let path = absolute(path)?;
        if !safe_metadata(&path)?.is_some_and(|metadata| metadata.is_dir()) {
            return Err(invalid(
                "exclude private directory",
                &path,
                "owned control directory is absent or not a directory",
            ));
        }
        let identity = Handle::from_path(&path)
            .map_err(|cause| error("identify private directory exclusion", &path, cause))?;
        if expected.is_some_and(|expected| *expected != identity) {
            return Err(invalid(
                "exclude private directory",
                &path,
                "owned control directory identity changed",
            ));
        }
        if !self
            .private_directories
            .iter()
            .any(|(_, known)| *known == identity)
        {
            self.private_directories.push((path, identity));
        }
        Ok(())
    }

    /// Register this store's lifetime with an exclusively owned invocation.
    pub fn new_in(scratch: &Path, invocation: &InvocationRoot) -> Result<Self> {
        if !invocation.contains_private(scratch)? {
            return Err(invalid(
                "create invocation store",
                scratch,
                "backing store must be inside the owned invocation",
            ));
        }
        let mut store = Self::new(scratch)?;
        Arc::get_mut(&mut store.owner)
            .expect("new unshared store")
            .invocation = Some(invocation.clone());
        Ok(store)
    }

    pub fn capture(&self, source: &Path) -> Result<Snapshot> {
        self.ensure_open()?;
        let source = source_path(source)?;
        if overlaps_store(&source, &self.owner)? {
            return Err(invalid(
                "capture snapshot",
                &source,
                "snapshot source and artifact store must be disjoint",
            ));
        }
        let mut snapshot = Snapshot {
            _owner: self.owner.clone(),
            entries: BTreeMap::new(),
            exclusions: Vec::new(),
        };
        self.capture_entry(&source, Path::new(""), &mut snapshot.entries, None)?;
        Ok(snapshot)
    }

    /// Capture a logical source while omitting only this exclusively owned
    /// invocation. User `.temp` data and other runs remain ordinary entries.
    pub fn capture_in(&self, source: &Path, invocation: &InvocationRoot) -> Result<Snapshot> {
        self.capture_logical(source, invocation, None)
    }

    /// Select direct child names before observing or copying any child payload.
    /// The caller supplies the complete anchored selector; matched directories
    /// retain their complete subtrees, not a recursive filename filter.
    pub fn capture_selected_in(
        &self,
        source: &Path,
        invocation: &InvocationRoot,
        regex: &regex::Regex,
    ) -> Result<Snapshot> {
        self.capture_logical(source, invocation, Some(regex))
    }

    fn capture_logical(
        &self,
        source: &Path,
        invocation: &InvocationRoot,
        selector: Option<&regex::Regex>,
    ) -> Result<Snapshot> {
        self.ensure_open()?;
        invocation.verify()?;
        let source = source_path(source)?;
        if invocation.contains(&source)? {
            return Err(invalid(
                "capture logical source",
                &source,
                "invocation-private data is not a logical source",
            ));
        }
        if overlaps_store(&source, &self.owner)?
            && (!invocation.contains_store(&self.owner)?
                || !self
                    .owner
                    .invocation
                    .as_ref()
                    .is_some_and(|owner| owner.same(invocation)))
        {
            return Err(invalid(
                "capture logical source",
                &source,
                "an overlapping artifact store is not owned by this invocation",
            ));
        }
        let excluded = invocation.relative_to(&source)?.is_some();
        let mut snapshot = Snapshot {
            _owner: self.owner.clone(),
            entries: BTreeMap::new(),
            exclusions: Vec::new(),
        };
        match selector {
            Some(regex) => {
                self.capture_selected_root(&source, invocation, regex, &mut snapshot.entries)?
            }
            None => self.capture_entry(
                &source,
                Path::new(""),
                &mut snapshot.entries,
                Some(invocation),
            )?,
        }
        if excluded {
            snapshot.exclusions.push(invocation.clone());
        }
        Ok(snapshot)
    }

    fn capture_selected_root(
        &self,
        source: &Path,
        invocation: &InvocationRoot,
        regex: &regex::Regex,
        entries: &mut BTreeMap<PathBuf, Entry>,
    ) -> Result<()> {
        let Some(metadata) = (match fs::metadata(source) {
            Ok(metadata) => Some(metadata),
            Err(cause) if cause.kind() == io::ErrorKind::NotFound => None,
            Err(cause) => return Err(error("inspect selector source", source, cause)),
        }) else {
            return Ok(());
        };
        if !metadata.is_dir() {
            return Err(invalid(
                "capture selector source",
                source,
                "a selector source must be a directory",
            ));
        }
        let mut metadata = NodeMetadata::capture(&metadata)
            .map_err(|cause| error("capture selector root metadata", source, cause))?;
        invocation.logical_metadata(source, &mut metadata)?;
        entries.insert(PathBuf::new(), Entry::Directory { metadata });
        for child in children(source)? {
            let name = child.file_name();
            if name.to_str().is_some_and(|name| regex.is_match(name)) {
                self.capture_entry(&child.path(), Path::new(&name), entries, Some(invocation))?;
            }
        }
        Ok(())
    }

    /// Assemble a version-store view without copying its already immutable files.
    /// Every child must have a directory parent in the manifest; paths cannot
    /// escape the destination root or depend on platform drive-relative behavior.
    /// Stored keys use only canonical normal components, with no trailing separator.
    pub fn from_entries(&self, entries: BTreeMap<PathBuf, Entry>) -> Result<Snapshot> {
        self.ensure_open()?;
        let mut normalized = BTreeMap::new();
        for (path, entry) in entries {
            if path
                .components()
                .any(|part| !matches!(part, Component::Normal(_)))
            {
                return Err(invalid(
                    "assemble snapshot manifest",
                    &path,
                    "manifest keys must be relative normal path components",
                ));
            }
            // Path equality ignores trailing separators, but OS file opens do
            // not. Store one canonical spelling for every relative tree node.
            normalized.insert(path.components().collect::<PathBuf>(), entry);
        }
        let entries = normalized;
        for path in entries.keys() {
            if !path.as_os_str().is_empty() {
                let parent = path.parent().unwrap_or_else(|| Path::new(""));
                if !matches!(entries.get(parent), Some(Entry::Directory { .. })) {
                    return Err(invalid(
                        "assemble snapshot manifest",
                        path,
                        "each child requires a directory parent in the manifest",
                    ));
                }
            }
        }
        if !entries.is_empty() && !entries.contains_key(Path::new("")) {
            return Err(invalid(
                "assemble snapshot manifest",
                Path::new(""),
                "a nonempty manifest requires a root entry",
            ));
        }
        Ok(Snapshot {
            _owner: self.owner.clone(),
            entries,
            exclusions: Vec::new(),
        })
    }

    /// Reassemble a versioned view while retaining its invocation exclusion.
    pub fn from_entries_in(
        &self,
        entries: BTreeMap<PathBuf, Entry>,
        logical_root: &Path,
        invocation: &InvocationRoot,
    ) -> Result<Snapshot> {
        let mut snapshot = self.from_entries(entries)?;
        let logical_root = absolute(logical_root)?;
        if invocation.relative_to(&logical_root)?.is_some() {
            for relative in snapshot.entries.keys() {
                if invocation.contains_private(&entry_path(&logical_root, relative))? {
                    return Err(invalid(
                        "assemble logical source",
                        relative,
                        "logical view contains invocation-private data",
                    ));
                }
            }
            snapshot.exclusions.push(invocation.clone());
        }
        Ok(snapshot)
    }

    fn capture_entry(
        &self,
        source: &Path,
        relative: &Path,
        entries: &mut BTreeMap<PathBuf, Entry>,
        invocation: Option<&InvocationRoot>,
    ) -> Result<()> {
        let Some(metadata) = safe_metadata(source)? else {
            return Ok(());
        };
        let mut version_metadata = NodeMetadata::capture(&metadata)
            .map_err(|cause| error("capture modification time", source, cause))?;
        if metadata.file_type().is_symlink() {
            let target = fs::read_link(source)
                .map_err(|cause| error("read symbolic link", source, cause))?;
            #[cfg(windows)]
            let directory = {
                use std::os::windows::fs::FileTypeExt;
                metadata.file_type().is_symlink_dir()
            };
            #[cfg(not(windows))]
            let directory = false;
            entries.insert(
                relative.to_owned(),
                Entry::Symlink {
                    target,
                    metadata: version_metadata,
                    directory,
                },
            );
            return Ok(());
        }
        if metadata.is_dir() {
            let identity = Handle::from_path(source)
                .map_err(|cause| error("identify source directory", source, cause))?;
            if self
                .private_directories
                .iter()
                .any(|(_, excluded)| *excluded == identity)
            {
                return Ok(());
            }
            if let Some(invocation) = invocation {
                if invocation.excludes(source)? {
                    return Ok(());
                }
                invocation.logical_metadata(source, &mut version_metadata)?;
            }
            entries.insert(
                relative.to_owned(),
                Entry::Directory {
                    metadata: version_metadata,
                },
            );
            for child in children(source)? {
                self.capture_entry(
                    &child.path(),
                    &relative.join(child.file_name()),
                    entries,
                    invocation,
                )?;
            }
            if let Some(invocation) = invocation
                && invocation.owns_scaffold(source)?
                && !entries
                    .keys()
                    .any(|entry| entry != relative && entry.starts_with(relative))
            {
                entries.remove(relative);
            }
            return Ok(());
        }
        require_file(&metadata, source)?;
        let path = self.owner.path.join(format!("file-{}", next_id()));
        copy_new(source, &path)?;
        let length = fs::metadata(&path)
            .map_err(|cause| error("inspect copied artifact", &path, cause))?
            .len();
        entries.insert(
            relative.to_owned(),
            Entry::File {
                artifact: Artifact {
                    _owner: self.owner.clone(),
                    path,
                    length,
                },
                metadata: version_metadata,
            },
        );
        Ok(())
    }

    /// Materialize a replacement beside its target, without changing the target.
    pub fn prepare(&self, snapshot: &Snapshot, target: &Path) -> Result<Publication> {
        self.ensure_open()?;
        let target = absolute(target)?;
        let parent = target.parent().ok_or_else(|| {
            invalid(
                "prepare replacement",
                &target,
                "a filesystem root cannot be replaced",
            )
        })?;
        fs::create_dir_all(parent)
            .map_err(|cause| error("create destination parent", parent, cause))?;
        let workspace = create_owned_directory(parent, ".msync-stage")?;
        let identity = Handle::from_path(&workspace)
            .map_err(|cause| error("identify replacement stage", &workspace, cause))?;
        let mut publication = Publication {
            target,
            workspace,
            identity,
            absent: snapshot.is_missing(),
            published: false,
            finalized: false,
        };
        if let Err(failure) = materialize(snapshot, &publication.new_path()) {
            let _ = publication.finalize();
            return Err(failure);
        }
        Ok(publication)
    }

    /// Compatibility adapter; staging no longer creates a persistent record.
    pub fn prepare_registered(
        &self,
        snapshot: &Snapshot,
        target: &Path,
        lease: &recovery::RegistryLease,
    ) -> Result<(Publication, recovery::Registration)> {
        let publication = self.prepare(snapshot, target)?;
        let registration = lease
            .register_stage(
                publication
                    .target
                    .parent()
                    .expect("staged target has a parent"),
                &publication.workspace,
            )
            .map_err(|cause| error("create compatibility receipt", target, cause))?;
        Ok((publication, registration))
    }

    pub fn cleanup(&mut self) -> Result<()> {
        self.ensure_open()?;
        if Arc::strong_count(&self.owner) != 1 {
            return Err(error(
                "cleanup snapshot store",
                &self.owner.path,
                io::Error::new(
                    io::ErrorKind::ResourceBusy,
                    "live snapshots or artifacts still retain this store",
                ),
            ));
        }
        remove_owned(&self.owner.path, &self.owner.identity)?;
        Arc::get_mut(&mut self.owner)
            .expect("cleanup requires an unshared store")
            .invocation = None;
        self.closed = true;
        Ok(())
    }

    fn ensure_open(&self) -> Result<()> {
        if self.closed {
            return Err(invalid(
                "use snapshot store",
                &self.owner.path,
                "snapshot store has already been cleaned",
            ));
        }
        Ok(())
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PublishEvent {
    /// Legacy name for the pre-install observation point; no backup is created.
    AfterBackup,
    AfterInstall,
}

/// One transient same-filesystem replacement stage. No before-image is retained.
#[must_use = "publish the replacement and release its transient stage"]
#[derive(Debug)]
pub struct Publication {
    target: PathBuf,
    workspace: PathBuf,
    identity: Handle,
    absent: bool,
    published: bool,
    finalized: bool,
}

impl Publication {
    pub fn target(&self) -> &Path {
        &self.target
    }
    pub fn journal(&self) -> &Path {
        &self.workspace
    }

    pub fn publish(&mut self) -> Result<()> {
        self.publish_with(|_| Ok(()))
    }

    pub fn publish_with(
        &mut self,
        mut observe: impl FnMut(PublishEvent) -> io::Result<()>,
    ) -> Result<()> {
        observe(PublishEvent::AfterBackup)
            .map_err(|cause| error("replacement cancelled before install", &self.target, cause))?;
        let staged = self.new_path();
        if self.absent {
            remove_owned_entry(&self.target)?;
        } else {
            let metadata = fs::symlink_metadata(&staged)
                .map_err(|cause| error("inspect replacement", &staged, cause))?;
            if metadata.is_file() || metadata.file_type().is_symlink() {
                if metadata.is_file() {
                    sync_file(&staged)?;
                }
                crate::io::publish_file(&staged, &self.target, false)
                    .map_err(|cause| error("replace file", &self.target, cause))?;
            } else {
                if fs::symlink_metadata(&self.target).is_ok() {
                    remove_owned_entry(&self.target)?;
                }
                move_new(&staged, &self.target)
                    .map_err(|cause| error("install replacement tree", &self.target, cause))?;
            }
        }
        self.published = true;
        observe(PublishEvent::AfterInstall)
            .map_err(|cause| error("replacement interrupted after install", &self.target, cause))
    }

    /// Legacy compensation APIs cannot undo a completed ordinary replacement.
    pub fn rollback(&mut self) -> Result<()> {
        self.restore_before_image()
    }
    pub fn recover(&mut self) -> Result<()> {
        self.restore_before_image()
    }
    pub fn restore_before_image(&mut self) -> Result<()> {
        if self.published {
            return Err(error(
                "undo replacement",
                &self.target,
                io::Error::new(
                    io::ErrorKind::Unsupported,
                    "ordinary replacements retain no before-image; retry the transfer",
                ),
            ));
        }
        Ok(())
    }

    pub fn finalize(&mut self) -> Result<()> {
        if !self.finalized {
            remove_owned(&self.workspace, &self.identity)?;
            self.finalized = true;
        }
        Ok(())
    }

    fn new_path(&self) -> PathBuf {
        self.workspace.join("new")
    }
}

impl Drop for Publication {
    fn drop(&mut self) {
        if !self.finalized
            && let Err(failure) = self.finalize()
        {
            crate::telemetry::io_error("file.stage.cleanup", &failure.cause);
        }
    }
}

pub(crate) fn sync_file(path: &Path) -> Result<()> {
    let mut options = fs::OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        options.write(true);
        let original = fs::metadata(path)
            .map_err(|cause| error("inspect staged file", path, cause))?
            .permissions();
        if original.readonly() {
            let mut writable = original.clone();
            #[allow(clippy::permissions_set_readonly_false)]
            writable.set_readonly(false);
            fs::set_permissions(path, writable)
                .map_err(|cause| error("open staged file for flush", path, cause))?;
        }
        let file = options.open(path);
        fs::set_permissions(path, original)
            .map_err(|cause| error("restore staged file attributes", path, cause))?;
        file.and_then(|file| file.sync_all())
            .map_err(|cause| error("flush staged file", path, cause))
    }
    #[cfg(not(windows))]
    options
        .open(path)
        .and_then(|file| file.sync_all())
        .map_err(|cause| error("flush staged file", path, cause))
}

fn materialize(snapshot: &Snapshot, root: &Path) -> Result<()> {
    for (relative, entry) in &snapshot.entries {
        let target = entry_path(root, relative);
        match entry {
            Entry::Directory { .. } => fs::create_dir(&target)
                .map_err(|cause| error("create staged directory", &target, cause))?,
            Entry::File { artifact, metadata } => {
                copy_new(&artifact.path, &target)?;
                metadata
                    .apply(&target)
                    .map_err(|cause| error("restore file metadata", &target, cause))?;
            }
            Entry::Symlink {
                target: link,
                directory,
                metadata,
            } => {
                create_symlink(link, &target, *directory)
                    .map_err(|cause| error("create symbolic link", &target, cause))?;
                metadata
                    .apply_link(&target)
                    .map_err(|cause| error("restore symbolic link metadata", &target, cause))?;
            }
        }
    }
    for (relative, entry) in snapshot.entries.iter().rev() {
        if let Entry::Directory { metadata } = entry {
            let target = entry_path(root, relative);
            metadata
                .apply(&target)
                .map_err(|cause| error("restore directory metadata", &target, cause))?;
        }
    }
    Ok(())
}

pub(crate) fn create_symlink(link: &Path, path: &Path, directory: bool) -> io::Result<()> {
    #[cfg(unix)]
    {
        let _ = directory;
        std::os::unix::fs::symlink(link, path)
    }
    #[cfg(windows)]
    {
        if directory {
            std::os::windows::fs::symlink_dir(link, path)
        } else {
            std::os::windows::fs::symlink_file(link, path)
        }
    }
    #[cfg(not(any(unix, windows)))]
    {
        let _ = (link, path, directory);
        Err(io::Error::new(
            io::ErrorKind::Unsupported,
            "native symbolic links are unavailable",
        ))
    }
}

fn next_id() -> u64 {
    NEXT_ID.fetch_add(1, Ordering::Relaxed)
}

pub(crate) fn entry_path(root: &Path, relative: &Path) -> PathBuf {
    if relative.as_os_str().is_empty() {
        root.to_owned()
    } else {
        root.join(relative)
    }
}

fn create_owned_directory(parent: &Path, prefix: &str) -> Result<PathBuf> {
    loop {
        let path = parent.join(format!("{prefix}-{}-{}", std::process::id(), next_id()));
        match fs::create_dir(&path) {
            Ok(()) => return Ok(path),
            Err(cause) if cause.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(cause) => return Err(error("create owned directory", &path, cause)),
        }
    }
}

fn copy_new(source: &Path, target: &Path) -> Result<()> {
    fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(target)
        .map_err(|cause| error("create immutable file", target, cause))?;
    fs::copy(source, target).map_err(|cause| error("copy immutable file", source, cause))?;
    Ok(())
}

pub(crate) fn move_new(source: &Path, target: &Path) -> io::Result<()> {
    // Groups and ACLs can admit the rename even when owner-write is absent.
    let result = crate::io::publish_file(source, target, true);
    #[cfg(unix)]
    if result
        .as_ref()
        .is_err_and(|cause| cause.raw_os_error() == Some(libc::EACCES))
    {
        use std::os::unix::fs::{OpenOptionsExt, PermissionsExt};
        let metadata = fs::symlink_metadata(source)?;
        if metadata.is_dir() && metadata.permissions().mode() & 0o200 == 0 {
            // A cross-parent directory rename updates `..` and may require owner
            // write permission. Both this mutation and restoration address the
            // same open directory; ordinary files are never chmod'ed.
            let directory = fs::OpenOptions::new()
                .read(true)
                .custom_flags(libc::O_DIRECTORY | libc::O_NOFOLLOW | libc::O_CLOEXEC)
                .open(source)?;
            let original = directory.metadata()?.permissions();
            let mut writable = original.clone();
            writable.set_mode(original.mode() | 0o200);
            directory.set_permissions(writable)?;
            let moved = crate::io::publish_file(source, target, true);
            if let Err(restore) = directory.set_permissions(original) {
                return Err(io::Error::new(
                    restore.kind(),
                    DirectoryModeFailure {
                        moved: moved.err(),
                        restore,
                    },
                ));
            }
            return moved;
        }
    }
    #[cfg(windows)]
    if result.as_ref().is_err_and(|error| {
        error.raw_os_error() == Some(windows_sys::Win32::Foundation::ERROR_ACCESS_DENIED as i32)
    }) {
        return move_new_posix(source, target);
    }
    result
}

#[cfg(windows)]
fn move_new_posix(source: &Path, target: &Path) -> io::Result<()> {
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Wdk::Storage::FileSystem::FILE_RENAME_POSIX_SEMANTICS;
    use windows_sys::Win32::Storage::FileSystem::{
        DELETE, FILE_FLAG_BACKUP_SEMANTICS, FILE_FLAG_OPEN_REPARSE_POINT, FILE_RENAME_INFO,
        FILE_RENAME_INFO_0, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE, FileRenameInfoEx,
        SetFileInformationByHandle,
    };

    // Match std's ACCESS_DENIED fallback without its REPLACE_IF_EXISTS flag.
    // Open shared-delete handles can outlive a name change; Win32 MoveFileEx
    // may reject a valid transition that handle-based POSIX renaming permits.
    // https://github.com/rust-lang/rust/issues/123985
    let file = fs::OpenOptions::new()
        .access_mode(DELETE)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT)
        .open(source)?;
    let target = crate::io::windows::wide_path(target)?;
    let name_bytes = (target.len() - 1)
        .checked_mul(2)
        .and_then(|bytes| u32::try_from(bytes).ok())
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "rename target exceeds Windows length limits",
            )
        })?;
    let bytes = std::mem::offset_of!(FILE_RENAME_INFO, FileName)
        .checked_add(target.len() * 2)
        .ok_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "rename buffer size overflow"))?
        .max(std::mem::size_of::<FILE_RENAME_INFO>());
    let size = u32::try_from(bytes).map_err(|_| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "rename buffer exceeds Windows length limits",
        )
    })?;
    // usize storage has the alignment of HANDLE/FILE_RENAME_INFO on each Windows
    // target. The trailing UTF-16 field is variable-sized and includes its NUL.
    let mut storage = vec![0usize; bytes.div_ceil(std::mem::size_of::<usize>())];
    let info = storage.as_mut_ptr().cast::<FILE_RENAME_INFO>();
    // SAFETY: Aligned zeroed storage includes a complete header and target name;
    // every pointer remains valid until the synchronous native call returns.
    let renamed = unsafe {
        (&raw mut (*info).Anonymous).write(FILE_RENAME_INFO_0 {
            Flags: FILE_RENAME_POSIX_SEMANTICS,
        });
        (&raw mut (*info).RootDirectory).write(std::ptr::null_mut());
        (&raw mut (*info).FileNameLength).write(name_bytes);
        target
            .as_ptr()
            .copy_to_nonoverlapping((&raw mut (*info).FileName).cast::<u16>(), target.len());
        SetFileInformationByHandle(file.as_raw_handle(), FileRenameInfoEx, info.cast(), size)
    };
    if renamed == 0 {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

#[cfg(unix)]
#[derive(Debug)]
struct DirectoryModeFailure {
    moved: Option<io::Error>,
    restore: io::Error,
}

#[cfg(unix)]
impl fmt::Display for DirectoryModeFailure {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        if let Some(moved) = &self.moved {
            write!(f, "directory rename failed: {moved}; ")?;
        }
        write!(
            f,
            "restoring original directory mode failed: {}",
            self.restore
        )
    }
}

#[cfg(unix)]
impl std::error::Error for DirectoryModeFailure {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(self.moved.as_ref().unwrap_or(&self.restore))
    }
}

pub(crate) fn identity_at(path: &Path) -> Result<Option<Handle>> {
    let Some(metadata) = safe_metadata(path)? else {
        return Ok(None);
    };
    if !metadata.is_dir() && !metadata.is_file() {
        return Err(invalid(
            "identify managed node",
            path,
            "ordinary files and directories are snapshot resources; use a stream endpoint for FIFO/socket/device data",
        ));
    }
    #[cfg(unix)]
    let handle = {
        use std::os::unix::fs::OpenOptionsExt;
        #[cfg(target_os = "linux")]
        let flags = libc::O_PATH | libc::O_NOFOLLOW;
        #[cfg(not(target_os = "linux"))]
        let flags = libc::O_NONBLOCK | libc::O_NOFOLLOW;
        // Identity inspection must never rendezvous with a FIFO peer, even if
        // an uncooperative host replaces the checked node before this open.
        fs::OpenOptions::new()
            .read(true)
            .custom_flags(flags)
            .open(path)
            .and_then(|file| {
                let actual = file.metadata()?;
                if !actual.is_file() && !actual.is_dir() {
                    return Err(io::Error::new(
                        io::ErrorKind::InvalidInput,
                        "managed identity changed to a stream, device or symbolic link",
                    ));
                }
                Handle::from_file(file)
            })
    };
    #[cfg(not(unix))]
    let handle = Handle::from_path(path);
    handle
        .map(Some)
        .map_err(|cause| error("identify path", path, cause))
}

fn overlaps_store(path: &Path, store: &ArtifactRoot) -> Result<bool> {
    if safe_metadata(path)?.is_some_and(|metadata| metadata.file_type().is_symlink()) {
        return Ok(false);
    }
    if path.starts_with(&store.path) || store.path.starts_with(path) {
        return Ok(true);
    }
    // Windows case, verbatim and short-path spellings may bypass lexical prefix
    // comparisons. Compare existing root identities against both ancestor chains.
    for ancestor in path.ancestors() {
        if identity_at(ancestor)?.as_ref() == Some(&store.identity) {
            return Ok(true);
        }
    }
    let Some(path_identity) = identity_at(path)? else {
        return Ok(false);
    };
    for ancestor in store.path.ancestors() {
        if identity_at(ancestor)?.as_ref() == Some(&path_identity) {
            return Ok(true);
        }
    }
    Ok(false)
}

fn ensure_identity(path: &Path, expected: Option<&Handle>) -> Result<()> {
    let actual = identity_at(path)?;
    if actual.as_ref() != expected {
        return Err(error(
            "validate resource ownership",
            path,
            io::Error::new(
                io::ErrorKind::AlreadyExists,
                "path identity changed; do not overwrite another owner's resource",
            ),
        ));
    }
    Ok(())
}

pub(crate) fn source_path(path: &Path) -> Result<PathBuf> {
    if fs::symlink_metadata(path).is_ok_and(|metadata| metadata.file_type().is_symlink()) {
        let absolute =
            std::path::absolute(path).map_err(|cause| error("resolve source path", path, cause))?;
        let parent = absolute.parent().unwrap_or_else(|| Path::new("."));
        return Ok(self::absolute(parent)?.join(absolute.file_name().unwrap_or_default()));
    }
    absolute(path)
}

fn absolute(path: &Path) -> Result<PathBuf> {
    let raw = std::path::absolute(path).map_err(|cause| error("resolve path", path, cause))?;
    let mut existing = raw.as_path();
    let mut suffix = Vec::new();
    loop {
        match fs::canonicalize(existing) {
            Ok(mut resolved) => {
                for component in suffix.into_iter().rev() {
                    resolved.push(component);
                }
                return Ok(resolved);
            }
            Err(cause) if cause.kind() == io::ErrorKind::NotFound => {
                if let Some(name) = existing.file_name() {
                    suffix.push(name.to_owned());
                }
                existing = existing
                    .parent()
                    .ok_or_else(|| error("resolve path", path, cause))?;
            }
            Err(cause) => return Err(error("resolve path", path, cause)),
        }
    }
}

fn safe_metadata(path: &Path) -> Result<Option<Metadata>> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => Ok(Some(metadata)),
        Err(cause) if cause.kind() == io::ErrorKind::NotFound => Ok(None),
        Err(cause) => Err(error("inspect snapshot entry", path, cause)),
    }
}

fn require_file(metadata: &Metadata, path: &Path) -> Result<()> {
    if !metadata.is_file() {
        return Err(invalid(
            "capture snapshot entry",
            path,
            "only ordinary files and directories can be snapshotted; streams require a process/flow adapter",
        ));
    }
    Ok(())
}

fn children(path: &Path) -> Result<Vec<fs::DirEntry>> {
    let mut entries = fs::read_dir(path)
        .map_err(|cause| error("enumerate snapshot directory", path, cause))?
        .collect::<std::result::Result<Vec<_>, _>>()
        .map_err(|cause| error("read snapshot directory entry", path, cause))?;
    entries.sort_by_key(fs::DirEntry::file_name);
    Ok(entries)
}

fn remove_owned(path: &Path, identity: &Handle) -> Result<()> {
    if safe_metadata(path)?.is_none() {
        return Ok(());
    }
    ensure_identity(path, Some(identity))?;
    remove_owned_entry(path)
}

pub(crate) fn remove_tree(path: &Path) -> Result<()> {
    remove_owned_entry(path)
}

fn remove_owned_entry(path: &Path) -> Result<()> {
    let Some(metadata) = safe_metadata(path)? else {
        return Ok(());
    };
    if metadata.file_type().is_symlink() {
        #[cfg(windows)]
        {
            use std::os::windows::fs::FileTypeExt;
            if metadata.file_type().is_symlink_dir() {
                return fs::remove_dir(path)
                    .map_err(|cause| error("remove directory link", path, cause));
            }
        }
        return fs::remove_file(path).map_err(|cause| error("remove symbolic link", path, cause));
    }
    if !metadata.is_dir() {
        return remove_file(path, &metadata);
    }
    // File modes/attributes belong to the inode and may be shared by hard links
    // outside this journal. Only directories need permissions normalized for
    // traversal/unlink; directory hard links and reparse points are rejected.
    let mut permissions = metadata.permissions();
    #[cfg(unix)]
    {
        use std::os::unix::fs::PermissionsExt;
        permissions.set_mode(permissions.mode() | 0o700);
    }
    #[cfg(windows)]
    {
        // Windows clears FILE_ATTRIBUTE_READONLY; Unix uses owner-only mode bits above.
        #[allow(clippy::permissions_set_readonly_false)]
        permissions.set_readonly(false);
    }
    // Group or ACL access can already admit cleanup without chmod authority.
    if let Err(cause) = fs::set_permissions(path, permissions)
        && cause.kind() != io::ErrorKind::PermissionDenied
    {
        return Err(error("prepare owned cleanup permissions", path, cause));
    }
    for entry in children(path)? {
        remove_owned_entry(&entry.path())?;
    }
    fs::remove_dir(path).map_err(|cause| error("cleanup owned directory", path, cause))
}

fn remove_file(path: &Path, metadata: &Metadata) -> Result<()> {
    #[cfg(windows)]
    if metadata.permissions().readonly() {
        return remove_readonly_link(path);
    }
    #[cfg(not(windows))]
    let _ = metadata;
    fs::remove_file(path).map_err(|cause| error("unlink owned file", path, cause))
}

#[cfg(windows)]
fn remove_readonly_link(path: &Path) -> Result<()> {
    // POSIX disposition removes one name while preserving surviving hard-link
    // attributes: https://learn.microsoft.com/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex
    use std::os::windows::fs::OpenOptionsExt;
    use std::os::windows::io::AsRawHandle;
    use windows_sys::Win32::Storage::FileSystem::{
        DELETE, FILE_DISPOSITION_FLAG_DELETE, FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
        FILE_DISPOSITION_FLAG_POSIX_SEMANTICS, FILE_DISPOSITION_INFO_EX,
        FILE_FLAG_OPEN_REPARSE_POINT, FILE_SHARE_DELETE, FILE_SHARE_READ, FILE_SHARE_WRITE,
        FileDispositionInfoEx, SetFileInformationByHandle,
    };

    let failure = |cause| {
        let mut failure = error(
            "unlink read-only owned file without changing shared attributes",
            path,
            cause,
        );
        failure.help = Some(
            "Retain the journal and retry on a filesystem supporting FileDispositionInfoEx with IGNORE_READONLY_ATTRIBUTE; cleanup never clears attributes shared with other hard links.",
        );
        failure
    };
    let file = fs::OpenOptions::new()
        .access_mode(DELETE)
        .share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE)
        .custom_flags(FILE_FLAG_OPEN_REPARSE_POINT)
        .open(path)
        .map_err(failure)?;
    let disposition = FILE_DISPOSITION_INFO_EX {
        Flags: FILE_DISPOSITION_FLAG_DELETE
            | FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
            | FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
    };
    // SAFETY: Live DELETE-capable file handle and correctly sized typed buffer.
    // This removes this link rather than changing inode-wide read-only attributes.
    if unsafe {
        SetFileInformationByHandle(
            file.as_raw_handle(),
            FileDispositionInfoEx,
            (&disposition as *const FILE_DISPOSITION_INFO_EX).cast(),
            std::mem::size_of::<FILE_DISPOSITION_INFO_EX>() as u32,
        )
    } == 0
    {
        return Err(failure(io::Error::last_os_error()));
    }
    Ok(())
}

#[cfg(all(test, windows))]
mod windows_tests {
    use super::*;

    #[test]
    fn posix_no_replace_rename_preserves_root_handles_and_refuses_collisions() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        let fixture = tempfile::Builder::new()
            .prefix("posix-rename-")
            .tempdir_in(root)
            .unwrap();
        let source = fixture.path().join("source-目录");
        let target = fixture.path().join("target-目录");
        let collision = fixture.path().join("collision");
        fs::create_dir(&source).unwrap();
        fs::create_dir(&collision).unwrap();
        fs::write(source.join("held"), "live file handle").unwrap();
        fs::write(collision.join("retained"), "collision target").unwrap();
        let held = Handle::from_path(&source).unwrap();
        let legacy = crate::io::publish_file(&source, &target, true);
        if legacy.is_ok() {
            move_new_posix(&target, &source).unwrap();
        }
        move_new_posix(&source, &target).unwrap();
        assert_eq!(held, Handle::from_path(&target).unwrap());
        assert!(!source.exists());
        assert_eq!(
            fs::read_to_string(target.join("held")).unwrap(),
            "live file handle"
        );
        let failure = move_new_posix(&target, &collision).unwrap_err();
        assert!(
            failure.raw_os_error().is_some(),
            "preserve the native collision cause"
        );
        assert_eq!(
            fs::read_to_string(collision.join("retained")).unwrap(),
            "collision target"
        );
        assert_eq!(
            fs::read_to_string(target.join("held")).unwrap(),
            "live file handle"
        );
    }

    #[test]
    fn moving_a_tree_with_a_live_child_handle_reports_the_native_denial_without_data_loss() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        let fixture = tempfile::Builder::new()
            .prefix("posix-rename-denied-")
            .tempdir_in(root)
            .unwrap();
        let source = fixture.path().join("source");
        let target = fixture.path().join("target");
        fs::create_dir(&source).unwrap();
        fs::write(source.join("held"), "requires quiescence").unwrap();
        let held = fs::File::open(source.join("held")).unwrap();
        let legacy = crate::io::publish_file(&source, &target, true).unwrap_err();
        assert_eq!(
            legacy.raw_os_error(),
            Some(windows_sys::Win32::Foundation::ERROR_ACCESS_DENIED as i32)
        );
        let failure = move_new(&source, &target).unwrap_err();
        assert_eq!(failure.raw_os_error(), legacy.raw_os_error());
        assert!(!target.exists());
        assert_eq!(
            fs::read_to_string(source.join("held")).unwrap(),
            "requires quiescence"
        );
        drop(held);
        move_new(&source, &target).unwrap();
        assert!(!source.exists());
        assert_eq!(
            fs::read_to_string(target.join("held")).unwrap(),
            "requires quiescence"
        );
    }
}

#[cfg(all(test, target_os = "linux"))]
mod linux_move_tests {
    use std::ffi::CString;
    use std::io::Read;
    use std::os::fd::{AsRawFd, FromRawFd};
    use std::os::unix::ffi::OsStrExt;
    use std::os::unix::fs::PermissionsExt;

    use super::*;

    fn workspace() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        tempfile::tempdir_in(root).unwrap()
    }

    fn watch(path: &Path) -> fs::File {
        // SAFETY: flags are valid and the returned descriptor is checked below.
        let descriptor = unsafe { libc::inotify_init1(libc::IN_NONBLOCK | libc::IN_CLOEXEC) };
        assert!(descriptor >= 0, "{}", io::Error::last_os_error());
        // SAFETY: the successful call created one descriptor owned by this test.
        let file = unsafe { fs::File::from_raw_fd(descriptor) };
        let path = CString::new(path.as_os_str().as_bytes()).unwrap();
        // SAFETY: the descriptor and NUL-terminated path remain valid for this call.
        let registered = unsafe {
            libc::inotify_add_watch(
                file.as_raw_fd(),
                path.as_ptr(),
                libc::IN_ATTRIB | libc::IN_MOVE_SELF,
            )
        };
        assert!(registered >= 0, "{}", io::Error::last_os_error());
        file
    }

    fn masks(mut watch: fs::File) -> Vec<u32> {
        let mut result = Vec::new();
        let mut bytes = [0u8; 4096];
        loop {
            let count = match watch.read(&mut bytes) {
                Ok(0) => break,
                Ok(count) => count,
                Err(cause) if cause.kind() == io::ErrorKind::WouldBlock => break,
                Err(cause) => panic!("cannot read directory observations: {cause}"),
            };
            let mut offset = 0;
            while offset < count {
                result.push(u32::from_ne_bytes(
                    bytes[offset + 4..offset + 8].try_into().unwrap(),
                ));
                let names = u32::from_ne_bytes(bytes[offset + 12..offset + 16].try_into().unwrap())
                    as usize;
                offset += std::mem::size_of::<libc::inotify_event>() + names;
            }
        }
        result
    }

    #[test]
    fn native_readonly_directory_rename_does_not_change_permissions() {
        let root = workspace();
        let source = root.path().join("source");
        let target = root.path().join("target");
        fs::create_dir(&source).unwrap();
        fs::set_permissions(&source, fs::Permissions::from_mode(0o500)).unwrap();
        let observed = watch(&source);
        move_new(&source, &target).unwrap();
        let events = masks(observed);
        assert!(events.iter().any(|mask| mask & libc::IN_MOVE_SELF != 0));
        assert!(events.iter().all(|mask| mask & libc::IN_ATTRIB == 0));
        assert_eq!(
            fs::metadata(target).unwrap().permissions().mode() & 0o777,
            0o500
        );
    }

    #[test]
    fn no_replace_collision_does_not_admit_directory_writes() {
        let root = workspace();
        let source = root.path().join("source");
        let target = root.path().join("target");
        fs::create_dir(&source).unwrap();
        fs::set_permissions(&source, fs::Permissions::from_mode(0o500)).unwrap();
        fs::write(&target, b"untouched").unwrap();
        let observed = watch(&source);
        let failure = move_new(&source, &target).unwrap_err();
        assert_eq!(failure.kind(), io::ErrorKind::AlreadyExists);
        assert!(
            masks(observed)
                .iter()
                .all(|mask| mask & libc::IN_ATTRIB == 0)
        );
        assert_eq!(
            fs::metadata(source).unwrap().permissions().mode() & 0o777,
            0o500
        );
        assert_eq!(fs::read(target).unwrap(), b"untouched");
    }
}
