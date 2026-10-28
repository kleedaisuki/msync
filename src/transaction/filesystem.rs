//! Immutable disk snapshots and retained whole-root publication journals.
//!
//! A publication has one managed destination root. Its rename steps are not an
//! OS-wide multi-path transaction: callers must hold the manager's resource guard
//! throughout publication/recovery and a read guard throughout capture.
//! Uncooperative concurrent mutation and crash
//! recovery are outside this adapter's contract. In-process failures retain an
//! owned journal and attempt compensation before returning.

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
    owner: Arc<ArtifactRoot>,
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

/// Values suitable for `transaction::Store<Entry>` without retaining file bytes.
#[derive(Clone, Debug)]
pub enum Entry {
    Directory {
        metadata: NodeMetadata,
    },
    File {
        artifact: Artifact,
        metadata: NodeMetadata,
    },
}

impl Entry {
    pub fn metadata(&self) -> &NodeMetadata {
        match self {
            Self::Directory { metadata } | Self::File { metadata, .. } => metadata,
        }
    }
}

/// Flattened relative paths; the empty path is the root, and an empty map is absence.
#[derive(Clone, Debug)]
pub struct Snapshot {
    owner: Arc<ArtifactRoot>,
    entries: BTreeMap<PathBuf, Entry>,
    exclusions: Vec<InvocationRoot>,
}

impl Snapshot {
    /// The manager checks canonical logical names, not the possibly older host
    /// tree, before retaining the exact invocation excluded by its enrolled view.
    pub(crate) fn retain_invocation_exclusion(
        &mut self,
        invocation: &InvocationRoot,
    ) -> Result<()> {
        invocation.verify()?;
        self.exclusions.push(invocation.clone());
        Ok(())
    }

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
    closed: bool,
}

impl SnapshotStore {
    pub fn new(scratch: &Path) -> Result<Self> {
        let scratch = absolute(scratch)?;
        check_path(&scratch)?;
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
            closed: false,
        })
    }

    pub fn root(&self) -> &Path {
        &self.owner.path
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
        let source = absolute(source)?;
        check_path(&source)?;
        if overlaps_store(&source, &self.owner)? {
            return Err(invalid(
                "capture snapshot",
                &source,
                "snapshot source and artifact store must be disjoint",
            ));
        }
        let mut snapshot = Snapshot {
            owner: self.owner.clone(),
            entries: BTreeMap::new(),
            exclusions: Vec::new(),
        };
        self.capture_entry(&source, Path::new(""), &mut snapshot.entries, None)?;
        Ok(snapshot)
    }

    /// Observe an ordinary host file without allocating a duplicate artifact
    /// when its complete bytes still match an immutable managed version.
    pub(crate) fn reuse_file(
        &self,
        source: &Path,
        previous: &Entry,
        invocation: Option<&InvocationRoot>,
    ) -> Result<Option<Snapshot>> {
        self.ensure_open()?;
        let Entry::File { artifact, .. } = previous else {
            return Ok(None);
        };
        let source = absolute(source)?;
        if let Some(invocation) = invocation {
            invocation.verify()?;
            if invocation.contains(&source)? {
                return Err(invalid(
                    "observe logical file",
                    &source,
                    "invocation-private data is not a logical source",
                ));
            }
        }
        if overlaps_store(&source, &self.owner)? {
            return Err(invalid(
                "observe logical file",
                &source,
                "snapshot source and artifact store must be disjoint",
            ));
        }
        let Some(metadata) = safe_metadata(&source)? else {
            return Ok(None);
        };
        if !metadata.is_file() || metadata.len() != artifact.len() {
            return Ok(None);
        }
        let mut options = fs::OpenOptions::new();
        options.read(true);
        #[cfg(unix)]
        {
            use std::os::unix::fs::OpenOptionsExt;
            options.custom_flags(libc::O_NOFOLLOW | libc::O_NONBLOCK);
        }
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            options.custom_flags(
                windows_sys::Win32::Storage::FileSystem::FILE_FLAG_OPEN_REPARSE_POINT,
            );
        }
        let mut file = options
            .open(&source)
            .map_err(|cause| error("open observed host file", &source, cause))?;
        let metadata = file
            .metadata()
            .map_err(|cause| error("inspect observed host file", &source, cause))?;
        require_file(&metadata, &source)?;
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            if metadata.file_attributes()
                & windows_sys::Win32::Storage::FileSystem::FILE_ATTRIBUTE_REPARSE_POINT
                != 0
            {
                return Err(invalid(
                    "observe logical file",
                    &source,
                    "reparse files cannot be reused as managed versions",
                ));
            }
        }
        let mut old = fs::File::open(artifact.path())
            .map_err(|cause| error("open immutable comparison file", artifact.path(), cause))?;
        if !equal_file_bytes(&mut file, &mut old)
            .map_err(|cause| error("compare observed host bytes", &source, cause))?
        {
            return Ok(None);
        }
        let metadata = NodeMetadata::capture(&metadata)
            .map_err(|cause| error("observe file metadata", &source, cause))?;
        self.from_entries(BTreeMap::from([(
            PathBuf::new(),
            Entry::File {
                artifact: artifact.clone(),
                metadata,
            },
        )]))
        .map(Some)
    }

    /// Capture a logical source while omitting only this exclusively owned
    /// invocation. User `.temp` data and other runs remain ordinary entries.
    pub fn capture_in(&self, source: &Path, invocation: &InvocationRoot) -> Result<Snapshot> {
        self.ensure_open()?;
        invocation.verify()?;
        let source = absolute(source)?;
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
            owner: self.owner.clone(),
            entries: BTreeMap::new(),
            exclusions: Vec::new(),
        };
        self.capture_entry(
            &source,
            Path::new(""),
            &mut snapshot.entries,
            Some(invocation),
        )?;
        if excluded {
            snapshot.exclusions.push(invocation.clone());
        }
        Ok(snapshot)
    }

    /// Assemble a version-store view without copying its already immutable files.
    /// Every child must have a directory parent in the manifest; paths cannot
    /// escape the destination root or depend on platform drive-relative behavior.
    pub fn from_entries(&self, entries: BTreeMap<PathBuf, Entry>) -> Result<Snapshot> {
        self.ensure_open()?;
        for (path, entry) in &entries {
            if path
                .components()
                .any(|part| !matches!(part, Component::Normal(_)))
            {
                return Err(invalid(
                    "assemble snapshot manifest",
                    path,
                    "manifest keys must be relative normal path components",
                ));
            }
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
            if let Entry::File { artifact, .. } = entry {
                ensure_identity(&artifact.owner.path, Some(&artifact.owner.identity))?;
                let metadata = safe_metadata(&artifact.path)?.ok_or_else(|| {
                    invalid(
                        "assemble snapshot manifest",
                        &artifact.path,
                        "immutable file artifact is missing",
                    )
                })?;
                require_file(&metadata, &artifact.path)?;
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
            owner: self.owner.clone(),
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
        if metadata.is_dir() {
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
                    owner: self.owner.clone(),
                    path,
                    length,
                },
                metadata: version_metadata,
            },
        );
        Ok(())
    }

    /// Build the complete replacement privately. The destination is unchanged.
    /// Existing parents must be directories; parent creation belongs to the manager.
    pub fn prepare(&self, snapshot: &Snapshot, target: &Path) -> Result<Publication> {
        self.ensure_open()?;
        let target = absolute(target)?;
        check_path(&target)?;
        if target.file_name().is_none() {
            return Err(invalid(
                "prepare publication",
                &target,
                "a filesystem root cannot be replaced",
            ));
        }
        if overlaps_store(&target, &self.owner)? || overlaps_store(&target, &snapshot.owner)? {
            return Err(invalid(
                "prepare publication",
                &target,
                "publication target and artifact store must be disjoint",
            ));
        }
        for entry in snapshot.entries.values() {
            if let Entry::File { artifact, .. } = entry
                && overlaps_store(&target, &artifact.owner)?
            {
                return Err(invalid(
                    "prepare publication",
                    &target,
                    "publication cannot replace an artifact owner retained by its manifest",
                ));
            }
        }
        validate_tree(&target)?;
        let parent = target
            .parent()
            .expect("non-root absolute target has a parent");
        let workspace = create_owned_directory(parent, ".msync-transaction")?;
        let identity = Handle::from_path(&workspace)
            .map_err(|cause| error("identify publication journal", &workspace, cause))?;
        let mut publication = Publication {
            target,
            workspace,
            identity,
            original: None,
            installed: None,
            absent: snapshot.is_missing(),
            phase: Phase::Prepared,
        };
        publication.original = identity_at(&publication.target)?;
        if let Err(mut failure) = materialize(snapshot, &publication.new_path()) {
            failure.journal = Some(publication.workspace.clone());
            failure.recovery = Recovery::JournalRetained;
            return Err(failure);
        }
        Ok(publication)
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
        ensure_identity(&self.owner.path, Some(&self.owner.identity))
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Phase {
    Prepared,
    BackedUp,
    Installed,
    Published,
    RecoveryRequired,
    Finalizing,
    Finalized,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PublishEvent {
    AfterBackup,
    AfterInstall,
}

/// Retains the destination before-image until explicit `finalize`.
/// Dropping this object deliberately leaves its owned journal available for diagnosis.
#[must_use = "publish or recover, then explicitly finalize the retained journal"]
#[derive(Debug)]
pub struct Publication {
    target: PathBuf,
    workspace: PathBuf,
    identity: Handle,
    original: Option<Handle>,
    installed: Option<Handle>,
    absent: bool,
    phase: Phase,
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

    /// Observers may reject a step, for example when a surrounding write-set
    /// coordinator detects cancellation. Rejection follows the same recovery path
    /// as native I/O failure, and is useful for deterministic failure injection.
    pub fn publish_with(
        &mut self,
        mut observe: impl FnMut(PublishEvent) -> io::Result<()>,
    ) -> Result<()> {
        self.require_phase(Phase::Prepared)?;
        self.check_workspace()?;
        ensure_identity(&self.target, self.original.as_ref())
            .map_err(|failure| self.annotate(failure))?;
        if self.original.is_some()
            && let Err(cause) = move_new(&self.target, &self.old_path())
        {
            return Err(self.compensate("retain destination before-image", cause));
        }
        self.phase = Phase::BackedUp;
        if let Err(cause) = observe(PublishEvent::AfterBackup) {
            return Err(self.compensate("publication cancelled after backup", cause));
        }
        if !self.absent
            && let Err(cause) = move_new(&self.new_path(), &self.target)
        {
            return Err(self.compensate("install replacement", cause));
        }
        self.phase = Phase::Installed;
        if let Err(cause) = observe(PublishEvent::AfterInstall) {
            return Err(self.compensate("publication cancelled after install", cause));
        }
        match identity_at(&self.target) {
            Ok(identity) => self.installed = identity,
            Err(failure) => {
                return Err(self.compensate("identify installed replacement", failure.cause));
            }
        }
        self.phase = Phase::Published;
        Ok(())
    }

    /// Restore the exact previous root, including an absent root and removed children.
    /// Another manager's replacement is not overwritten if it changed the root identity.
    pub fn rollback(&mut self) -> Result<()> {
        self.require_phase(Phase::Published)?;
        self.check_workspace()?;
        ensure_identity(&self.target, self.installed.as_ref())
            .map_err(|failure| self.annotate(failure))?;
        self.phase = Phase::Installed;
        self.restore().map_err(|cause| {
            self.phase = Phase::RecoveryRequired;
            self.failure("restore destination before-image", cause)
        })?;
        self.installed = None;
        self.phase = Phase::Prepared;
        Ok(())
    }

    /// Retry a failed compensation while the caller still owns the resource guard.
    pub fn recover(&mut self) -> Result<()> {
        self.require_phase(Phase::RecoveryRequired)?;
        self.check_workspace()?;
        self.restore()
            .map_err(|cause| self.failure("recover destination before-image", cause))?;
        self.installed = None;
        self.phase = Phase::Prepared;
        Ok(())
    }

    /// Idempotently restore an unpublished or published before-image. A manager
    /// retains this receipt after a failed compensation and retries this method
    /// without guessing whether rollback failed before or after a rename.
    pub fn restore_before_image(&mut self) -> Result<()> {
        match self.phase {
            Phase::Prepared => Ok(()),
            Phase::Published => self.rollback(),
            Phase::RecoveryRequired => self.recover(),
            _ => Err(self.failure(
                "restore publication before-image",
                io::Error::other("publication is not recoverable after finalization starts"),
            )),
        }
    }

    /// Irreversibly release the journal after publication or successful recovery.
    /// A cleanup error may be retried, but rollback is no longer available once
    /// finalization starts. A failed publication must be recovered first.
    pub fn finalize(&mut self) -> Result<()> {
        if !matches!(
            self.phase,
            Phase::Prepared | Phase::Published | Phase::Finalizing
        ) {
            return Err(self.failure(
                "finalize publication",
                io::Error::other("recover the journal before finalizing"),
            ));
        }
        self.phase = Phase::Finalizing;
        remove_owned(&self.workspace, &self.identity).map_err(|failure| self.annotate(failure))?;
        self.phase = Phase::Finalized;
        Ok(())
    }

    fn compensate(&mut self, action: &'static str, cause: io::Error) -> FsError {
        let mut failure = self.failure(action, cause);
        match self.restore() {
            Ok(()) => {
                self.phase = Phase::Prepared;
                failure.recovery = Recovery::Restored;
            }
            Err(error) => {
                self.phase = Phase::RecoveryRequired;
                failure.recovery_error = Some(error);
            }
        }
        failure
    }

    fn restore(&mut self) -> io::Result<()> {
        // Path presence records completed rename steps even if the immediately
        // following I/O failed. Never erase a target in order to force recovery.
        let old = self.old_path();
        let new = self.new_path();
        let target_exists = safe_metadata(&self.target)
            .map_err(|error| error.cause)?
            .is_some();
        let new_exists = safe_metadata(&new).map_err(|error| error.cause)?.is_some();
        if target_exists && !new_exists && !self.absent {
            move_new(&self.target, &new)?;
        }
        if self.original.is_some() && safe_metadata(&old).map_err(|error| error.cause)?.is_some() {
            move_new(&old, &self.target)?;
        }
        ensure_identity(&self.target, self.original.as_ref()).map_err(|failure| failure.cause)?;
        Ok(())
    }

    fn new_path(&self) -> PathBuf {
        self.workspace.join("new")
    }
    fn old_path(&self) -> PathBuf {
        self.workspace.join("old")
    }

    fn check_workspace(&self) -> Result<()> {
        ensure_identity(&self.workspace, Some(&self.identity))
            .map_err(|failure| self.annotate(failure))?;
        check_path(&self.target).map_err(|failure| self.annotate(failure))
    }

    fn require_phase(&self, phase: Phase) -> Result<()> {
        if self.phase != phase {
            return Err(self.failure(
                "use publication journal",
                io::Error::other(format!("expected {phase:?}, found {:?}", self.phase)),
            ));
        }
        Ok(())
    }

    fn failure(&self, action: &'static str, cause: io::Error) -> FsError {
        self.annotate(error(action, &self.target, cause))
    }

    fn annotate(&self, mut failure: FsError) -> FsError {
        failure.journal = Some(self.workspace.clone());
        failure.recovery = Recovery::JournalRetained;
        failure
    }
}

fn materialize(snapshot: &Snapshot, root: &Path) -> Result<()> {
    ensure_identity(&snapshot.owner.path, Some(&snapshot.owner.identity))?;
    for (relative, entry) in &snapshot.entries {
        let target = entry_path(root, relative);
        match entry {
            Entry::Directory { .. } => fs::create_dir(&target)
                .map_err(|cause| error("create private directory", &target, cause))?,
            Entry::File { artifact, metadata } => {
                ensure_identity(&artifact.owner.path, Some(&artifact.owner.identity))?;
                copy_new(&artifact.path, &target)?;
                metadata
                    .apply(&target)
                    .map_err(|cause| error("restore file metadata", &target, cause))?;
            }
        }
    }
    // Directory permissions are applied after children, so a read-only snapshot
    // directory does not prevent materialization of its own contents.
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

fn next_id() -> u64 {
    NEXT_ID.fetch_add(1, Ordering::Relaxed)
}

fn entry_path(root: &Path, relative: &Path) -> PathBuf {
    if relative.as_os_str().is_empty() {
        root.to_owned()
    } else {
        root.join(relative)
    }
}

fn create_owned_directory(parent: &Path, prefix: &str) -> Result<PathBuf> {
    for _ in 0..128 {
        let path = parent.join(format!("{prefix}-{}-{}", std::process::id(), next_id()));
        match fs::create_dir(&path) {
            Ok(()) => return Ok(path),
            Err(cause) if cause.kind() == io::ErrorKind::AlreadyExists => continue,
            Err(cause) => return Err(error("create owned directory", &path, cause)),
        }
    }
    Err(error(
        "create owned directory",
        parent,
        io::Error::new(
            io::ErrorKind::AlreadyExists,
            "unique journal allocation exhausted",
        ),
    ))
}

fn equal_file_bytes(left: &mut fs::File, right: &mut fs::File) -> io::Result<bool> {
    use std::io::Read;
    let mut a = [0; 64 * 1024];
    let mut b = [0; 64 * 1024];
    loop {
        let count = left.read(&mut a)?;
        if count == 0 {
            return Ok(right.read(&mut b)? == 0);
        }
        if let Err(cause) = right.read_exact(&mut b[..count]) {
            return if cause.kind() == io::ErrorKind::UnexpectedEof {
                Ok(false)
            } else {
                Err(cause)
            };
        }
        if a[..count] != b[..count] {
            return Ok(false);
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

fn move_new(source: &Path, target: &Path) -> io::Result<()> {
    #[cfg(unix)]
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
    let result = crate::io::publish_file(source, target, true);
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

pub(super) fn identity_at(path: &Path) -> Result<Option<Handle>> {
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

fn check_path(path: &Path) -> Result<()> {
    safe_metadata(path)?;
    // lstat on `link/` or `link/.` follows the link. Inspect the trimmed leaf too,
    // and preserve `..` prefixes until every original ancestor has been checked.
    let trimmed = path.components().as_path();
    let skip_leaf = usize::from(trimmed.as_os_str() == path.as_os_str());
    for ancestor in trimmed.ancestors().skip(skip_leaf) {
        safe_metadata(ancestor)?;
    }
    Ok(())
}

fn absolute(path: &Path) -> Result<PathBuf> {
    let raw = if path.is_absolute() {
        path.to_owned()
    } else {
        std::env::current_dir()
            .map_err(|cause| error("capture working directory", path, cause))?
            .join(path)
    };
    check_path(&raw)?;
    let raw =
        std::path::absolute(&raw).map_err(|cause| error("resolve absolute path", path, cause))?;
    let mut result = PathBuf::new();
    for component in raw.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                result.pop();
            }
            component => result.push(component.as_os_str()),
        }
    }
    Ok(result)
}

fn safe_metadata(path: &Path) -> Result<Option<Metadata>> {
    match fs::symlink_metadata(path) {
        Ok(metadata) => {
            if metadata.file_type().is_symlink() {
                return Err(invalid(
                    "inspect snapshot entry",
                    path,
                    "symbolic links are not supported in managed snapshots",
                ));
            }
            #[cfg(windows)]
            {
                use std::os::windows::fs::MetadataExt;
                if metadata.file_attributes() & 0x400 != 0 {
                    return Err(invalid(
                        "inspect snapshot entry",
                        path,
                        "reparse points are not supported in managed snapshots",
                    ));
                }
            }
            Ok(Some(metadata))
        }
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

fn validate_tree(path: &Path) -> Result<()> {
    let Some(metadata) = safe_metadata(path)? else {
        return Ok(());
    };
    if !metadata.is_dir() {
        return require_file(&metadata, path);
    }
    for entry in children(path)? {
        validate_tree(&entry.path())?;
    }
    Ok(())
}

fn remove_owned(path: &Path, identity: &Handle) -> Result<()> {
    if !path.is_absolute() || path.file_name().is_none() {
        return Err(invalid(
            "cleanup owned directory",
            path,
            "cleanup requires a checked absolute non-root owned directory",
        ));
    }
    check_path(path)?;
    ensure_identity(path, Some(identity))?;
    remove_owned_entry(path)
}

fn remove_owned_entry(path: &Path) -> Result<()> {
    let Some(metadata) = safe_metadata(path)? else {
        return Ok(());
    };
    if !metadata.is_dir() {
        require_file(&metadata, path)?;
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
    fs::set_permissions(path, permissions)
        .map_err(|cause| error("prepare owned cleanup permissions", path, cause))?;
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
