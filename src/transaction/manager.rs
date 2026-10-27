//! Filesystem-backed transactions over a finite, enrolled local resource domain.
//!
//! Every managed read comes from an immutable version, never from the live host
//! filesystem. Enrollment finishes before the first transaction begins. A future
//! process-view provider must enroll or lock dynamically discovered resources;
//! this manager does not pretend that unknown child IO has been isolated.
//!
//! Exclusive mutable ownership is the publication guard. Physical roots are
//! replaced separately, with retained compensating receipts; only after every
//! replacement succeeds is the logical version store swapped. This is atomic to
//! manager readers, not to unmediated host observers, and is not crash recovery.

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::{OsStr, OsString};
use std::fmt;
use std::fs;
use std::io;
use std::path::{Component, Path, PathBuf};

use super::filesystem::{Entry, FsError, Publication, PublishEvent, Snapshot, SnapshotStore};
use super::{Epoch, ResourceKey, Store, StoreError};
use crate::hir::TransactionId;
use crate::language::Isolation;

type Result<T> = std::result::Result<T, ManagerError>;

#[derive(Debug)]
pub enum ManagerError {
    InvalidPath {
        path: PathBuf,
        reason: &'static str,
    },
    EnrollmentClosed,
    Lifecycle(Lifecycle),
    ActiveTransactions(Vec<TransactionId>),
    Cleanup {
        failures: Vec<FsError>,
        journals: Vec<PathBuf>,
    },
    RecoveryRequired {
        journals: Vec<PathBuf>,
    },
    Store(StoreError),
    Filesystem(Box<FsError>),
    Io {
        action: &'static str,
        path: PathBuf,
        cause: io::Error,
    },
    Publication {
        transaction: TransactionId,
        cause: Box<FsError>,
        compensation: Vec<FsError>,
        journals: Vec<PathBuf>,
    },
}

impl fmt::Display for ManagerError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidPath { path, reason } => write!(f, "managed path {}: {reason}", path.display()),
            Self::EnrollmentClosed => f.write_str("resource enrollment is closed; enroll every known root before beginning a transaction"),
            Self::Lifecycle(state) => write!(f, "transaction manager is {state:?}; no new resource operations are allowed"),
            Self::ActiveTransactions(ids) => write!(f, "finish or roll back active transactions before shutdown: {ids:?}"),
            Self::Cleanup { failures, journals } => {
                f.write_str("shutdown has not started because publication cleanup is incomplete")?;
                for failure in failures { write!(f, "; {failure}")?; }
                write!(f, "; retain journals {journals:?}")
            }
            Self::RecoveryRequired { journals } => write!(f, "publication recovery is required before further changes; retain journals {journals:?}"),
            Self::Store(error) => error.fmt(f),
            Self::Filesystem(error) => error.fmt(f),
            Self::Io { action, path, cause } => write!(f, "{action} {}: {cause}", path.display()),
            Self::Publication { transaction, cause, compensation, journals } => {
                write!(f, "transaction {} publication was not committed: {cause}", transaction.0)?;
                for failure in compensation { write!(f, "; compensation: {failure}")?; }
                if !journals.is_empty() { write!(f, "; retained journals {journals:?}")?; }
                Ok(())
            }
        }
    }
}

impl std::error::Error for ManagerError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Store(error) => Some(error),
            Self::Filesystem(error) | Self::Publication { cause: error, .. } => {
                Some(error.as_ref())
            }
            Self::Io { cause, .. } => Some(cause),
            _ => None,
        }
    }
}

impl From<StoreError> for ManagerError {
    fn from(value: StoreError) -> Self {
        Self::Store(value)
    }
}

impl From<FsError> for ManagerError {
    fn from(value: FsError) -> Self {
        Self::Filesystem(Box::new(value))
    }
}

#[derive(Clone, Debug)]
struct ManagedEntry {
    /// Native spelling is a value, not a case-folded resource identity.
    relative: PathBuf,
    entry: Entry,
}

#[derive(Clone, Debug)]
struct Root {
    path: PathBuf,
    namespace: String,
}

#[derive(Clone, Debug, Default)]
struct Domain {
    roots: Vec<Root>,
    keys: BTreeSet<ResourceKey>,
    names: NameIndex,
}

/// Each parent's names use native comparison while retaining a stable key.
type NameIndex = BTreeMap<(usize, Vec<String>), Vec<(OsString, String)>>;

/// Successful logical publication. Cleanup failure is not a failed commit.
#[derive(Debug)]
pub struct CommitReport {
    pub epoch: Epoch,
    pub published_roots: usize,
    pub cleanup: Vec<FsError>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Lifecycle {
    Open,
    Closing { epoch: Epoch },
    Closed { epoch: Epoch },
}

impl CommitReport {
    pub fn cleanup_pending(&self) -> bool {
        !self.cleanup.is_empty()
    }
}

#[derive(Debug)]
pub struct Manager {
    snapshots: SnapshotStore,
    store: Store<ManagedEntry>,
    domain: Domain,
    pending_roots: BTreeMap<TransactionId, BTreeSet<usize>>,
    origin_roots: BTreeMap<TransactionId, BTreeSet<usize>>,
    sealed: bool,
    recovery: Vec<Publication>,
    cleanup: Vec<Publication>,
    active: BTreeSet<TransactionId>,
    lifecycle: Lifecycle,
}

impl Manager {
    pub fn new(scratch: &Path) -> Result<Self> {
        Ok(Self {
            snapshots: SnapshotStore::new(scratch)?,
            store: Store::new(),
            domain: Domain::default(),
            pending_roots: BTreeMap::new(),
            origin_roots: BTreeMap::new(),
            sealed: false,
            recovery: Vec::new(),
            cleanup: Vec::new(),
            active: BTreeSet::new(),
            lifecycle: Lifecycle::Open,
        })
    }

    pub fn epoch(&self) -> Epoch {
        match self.lifecycle {
            Lifecycle::Open => self.store.epoch(),
            Lifecycle::Closing { epoch } | Lifecycle::Closed { epoch } => epoch,
        }
    }

    pub fn lifecycle(&self) -> Lifecycle {
        self.lifecycle
    }

    pub fn artifact_root(&self) -> &Path {
        self.snapshots.root()
    }

    /// Captures a disjoint known root, including an explicitly missing root.
    /// Parent directories must already exist for eventual physical publication.
    pub fn enroll(&mut self, path: &Path) -> Result<()> {
        self.ready()?;
        if self.sealed {
            return Err(ManagerError::EnrollmentClosed);
        }
        let path = normalize(path)?;
        if path.file_name().is_none() {
            return Err(invalid(
                &path,
                "a filesystem root cannot be a publication root",
            ));
        }
        let parent = path
            .parent()
            .expect("non-root absolute resource has a parent");
        if !parent.is_dir() {
            return Err(invalid(
                parent,
                "enroll an existing parent domain before creating a missing directory tree",
            ));
        }
        for root in &self.domain.roots {
            if roots_overlap(&path, &root.path)? {
                return Err(invalid(
                    &path,
                    "enrolled roots must be disjoint; enroll their common managed ancestor once",
                ));
            }
        }
        let snapshot = self.snapshots.capture(&path)?;
        validate_snapshot_case_policy(&path, &snapshot)?;
        validate_names(snapshot.entries())?;
        let mut domain = self.domain.clone();
        let index = domain.roots.len();
        let namespace = format!("local-root-{index}");
        domain.roots.push(Root { path, namespace });
        let root_key = domain.key(index, Path::new(""))?;
        let mut candidate = self.store.clone();
        if snapshot.is_missing() {
            candidate.seed(root_key.clone(), None)?;
        }
        domain.keys.insert(root_key);
        for (relative, entry) in snapshot.entries() {
            let key = domain.key(index, relative)?;
            candidate.seed(
                key.clone(),
                Some(ManagedEntry {
                    relative: relative.clone(),
                    entry: entry.clone(),
                }),
            )?;
            domain.keys.insert(key);
        }
        self.store = candidate;
        self.domain = domain;
        Ok(())
    }

    /// Copy caller-owned bytes/files into immutable artifacts; no live read is
    /// substituted for a managed transaction read by this explicit import API.
    pub fn capture(&self, path: &Path) -> Result<Snapshot> {
        self.open()?;
        let path = normalize(path)?;
        for root in &self.domain.roots {
            if roots_overlap(&path, &root.path)? {
                return Err(invalid(
                    &path,
                    "read enrolled resources through read_snapshot, not a fresh host capture",
                ));
            }
        }
        let snapshot = self.snapshots.capture(&path)?;
        validate_snapshot_case_policy(&path, &snapshot)?;
        validate_names(snapshot.entries())?;
        Ok(snapshot)
    }

    pub fn begin(&mut self, id: TransactionId, isolation: Isolation) -> Result<()> {
        self.ready()?;
        self.store.begin(id, isolation)?;
        self.active.insert(id);
        self.sealed = true;
        Ok(())
    }

    pub fn start_statement(&mut self, id: TransactionId) -> Result<Epoch> {
        self.ready()?;
        Ok(self.store.start_statement(id)?)
    }

    pub fn read_snapshot(&mut self, id: TransactionId, path: &Path) -> Result<Snapshot> {
        self.open()?;
        // Immutable views remain readable while physical recovery is blocked.
        // Failed reads do not install name-index entries or read observations.
        let (root, relative) = self.locate(path)?;
        let mut domain = self.domain.clone();
        let key = domain.key(root, &relative)?;
        let mut candidate = self.store.clone();
        let mut prefix = PathBuf::new();
        let mut reachable = true;
        for part in relative.components() {
            let parent = domain.key(root, &prefix)?;
            match candidate.read(id, &parent)? {
                Some(ManagedEntry {
                    entry: Entry::Directory { .. },
                    ..
                }) => prefix.push(part),
                None => {
                    reachable = false;
                    break;
                }
                Some(_) => {
                    return Err(invalid(
                        path,
                        "an ancestor in this transaction view is not a directory",
                    ));
                }
            }
        }
        let values = if reachable {
            candidate.enumerate(id, &key.tree())?
        } else {
            Vec::new()
        };
        let snapshot = self.assemble(values, relative.components().count())?;
        self.domain = domain;
        self.store = candidate;
        Ok(snapshot)
    }

    /// Stage a complete subtree replacement. A missing snapshot stages deletion.
    /// The transaction view's parent must be a directory; callers create missing
    /// parent trees explicitly rather than mutating the host during staging.
    pub fn replace_snapshot(
        &mut self,
        id: TransactionId,
        path: &Path,
        snapshot: &Snapshot,
    ) -> Result<()> {
        self.ready()?;
        validate_names(snapshot.entries())?;
        let (root, relative) = self.locate(path)?;
        let mut domain = self.domain.clone();
        let key = domain.key(root, &relative)?;
        let mut candidate = self.store.clone();
        let previous_kind = candidate
            .read(id, &key)?
            .map(|value| matches!(value.entry, Entry::Directory { .. }));
        let replacement_kind = snapshot
            .entries()
            .get(Path::new(""))
            .map(|entry| matches!(entry, Entry::Directory { .. }));
        if !relative.as_os_str().is_empty() {
            let parent = domain.key(root, relative.parent().unwrap_or_else(|| Path::new("")))?;
            let Some(mut parent_value) = candidate.read(id, &parent)? else {
                return Err(invalid(
                    path,
                    "a subtree replacement requires a directory parent in the transaction view",
                ));
            };
            let Entry::Directory { metadata } = &mut parent_value.entry else {
                return Err(invalid(
                    path,
                    "a subtree replacement requires a directory parent in the transaction view",
                ));
            };
            if previous_kind != replacement_kind {
                metadata.modified = std::time::SystemTime::now();
                candidate.write(id, parent, parent_value)?;
            }
        }
        for (old, _) in candidate.enumerate(id, &key.tree())? {
            candidate.delete(id, old)?;
        }
        if snapshot.is_missing() {
            candidate.delete(id, key.clone())?;
        }
        let mut added = Vec::new();
        for (suffix, entry) in snapshot.entries() {
            let relative = join(&relative, suffix);
            let key = domain.key(root, &relative)?;
            candidate.write(
                id,
                key.clone(),
                ManagedEntry {
                    relative,
                    entry: entry.clone(),
                },
            )?;
            added.push(key);
        }
        domain.keys.insert(key);
        domain.keys.extend(added);
        self.pending_roots.entry(id).or_default().insert(root);
        self.store = candidate;
        self.domain = domain;
        Ok(())
    }

    pub fn checkpoint(&mut self, id: TransactionId) -> Result<CommitReport> {
        self.checkpoint_with(id, |_, _| Ok(()))
    }

    /// The observer runs at physical rename boundaries while the manager owns
    /// the publication guard. It can inject cancellation without bypassing undo.
    pub fn checkpoint_with(
        &mut self,
        id: TransactionId,
        observe: impl FnMut(&Path, PublishEvent) -> io::Result<()>,
    ) -> Result<CommitReport> {
        self.ready()?;
        let mut candidate = self.store.clone();
        candidate.checkpoint(id)?;
        let roots = self.pending_roots.get(&id).cloned().unwrap_or_default();
        let report = self.publish(id, candidate, &roots, observe)?;
        self.origin_roots.entry(id).or_default().extend(roots);
        self.pending_roots.remove(&id);
        Ok(report)
    }

    pub fn rollback_pending(&mut self, id: TransactionId) -> Result<()> {
        self.ready()?;
        self.store.rollback_pending(id)?;
        self.pending_roots.remove(&id);
        Ok(())
    }

    pub fn finish(&mut self, id: TransactionId) -> Result<CommitReport> {
        self.ready()?;
        let mut candidate = self.store.clone();
        candidate.finish(id)?;
        let roots = self.pending_roots.get(&id).cloned().unwrap_or_default();
        let report = self.publish(id, candidate, &roots, |_, _| Ok(()))?;
        self.origin_roots.entry(id).or_default().extend(roots);
        self.pending_roots.remove(&id);
        self.active.remove(&id);
        Ok(report)
    }

    pub fn rollback_origin(&mut self, id: TransactionId) -> Result<CommitReport> {
        self.ready()?;
        let mut candidate = self.store.clone();
        candidate.rollback_origin(id)?;
        let roots = self.origin_roots.get(&id).cloned().unwrap_or_default();
        let report = self.publish(id, candidate, &roots, |_, _| Ok(()))?;
        self.origin_roots.remove(&id);
        self.pending_roots.remove(&id);
        self.active.remove(&id);
        Ok(report)
    }

    /// End a session explicitly. Until cleanup can begin, errors preserve every
    /// logical version and undo capability. Once Closing, normal operations are
    /// refused and only shutdown may retry after external snapshots are dropped.
    /// No Drop implementation silently removes data or hides cleanup failures.
    pub fn shutdown(&mut self) -> Result<()> {
        match self.lifecycle {
            Lifecycle::Closed { .. } => return Ok(()),
            Lifecycle::Closing { .. } => {}
            Lifecycle::Open => {
                self.ready()?;
                if !self.active.is_empty() {
                    return Err(ManagerError::ActiveTransactions(
                        self.active.iter().copied().collect(),
                    ));
                }
                let failures = self.retry_cleanup();
                if !failures.is_empty() {
                    return Err(ManagerError::Cleanup {
                        failures,
                        journals: self.retained_journals(),
                    });
                }
                let epoch = self.store.epoch();
                self.store = Store::new();
                self.domain = Domain::default();
                self.pending_roots.clear();
                self.origin_roots.clear();
                self.lifecycle = Lifecycle::Closing { epoch };
            }
        }
        self.snapshots.cleanup()?;
        self.lifecycle = Lifecycle::Closed {
            epoch: self.epoch(),
        };
        Ok(())
    }

    pub fn recovery_journals(&self) -> Vec<PathBuf> {
        self.recovery
            .iter()
            .map(|receipt| receipt.journal().to_owned())
            .collect()
    }

    pub fn retained_journals(&self) -> Vec<PathBuf> {
        self.recovery
            .iter()
            .chain(&self.cleanup)
            .map(|receipt| receipt.journal().to_owned())
            .collect()
    }

    /// Retry all failed before-image restorations. Logical committed state and
    /// transaction pending writes were never advanced by the failed checkpoint.
    pub fn recover(&mut self) -> Vec<FsError> {
        let mut failures = Vec::new();
        let mut retained = Vec::new();
        for mut receipt in self.recovery.drain(..).rev() {
            match receipt.restore_before_image() {
                Ok(()) => self.cleanup.push(receipt),
                Err(error) => {
                    failures.push(error);
                    retained.push(receipt);
                }
            }
        }
        retained.reverse();
        self.recovery = retained;
        failures.extend(self.retry_cleanup());
        failures
    }

    /// Retry post-commit/compensation cleanup without changing commit status.
    pub fn retry_cleanup(&mut self) -> Vec<FsError> {
        let mut failures = Vec::new();
        let mut retained = Vec::new();
        for mut receipt in self.cleanup.drain(..) {
            if let Err(error) = receipt.finalize() {
                failures.push(error);
                retained.push(receipt);
            }
        }
        self.cleanup = retained;
        failures
    }

    fn ready(&self) -> Result<()> {
        self.open()?;
        if self.recovery.is_empty() {
            return Ok(());
        }
        Err(ManagerError::RecoveryRequired {
            journals: self.recovery_journals(),
        })
    }

    fn open(&self) -> Result<()> {
        if self.lifecycle == Lifecycle::Open {
            return Ok(());
        }
        Err(ManagerError::Lifecycle(self.lifecycle))
    }

    fn locate(&self, path: &Path) -> Result<(usize, PathBuf)> {
        let path = normalize(path)?;
        self.domain
            .roots
            .iter()
            .enumerate()
            .find_map(|(index, root)| {
                relative_to(&path, &root.path).map(|relative| (index, relative))
            })
            .ok_or_else(|| {
                invalid(
                    &path,
                    "resource is outside the enrolled domain; enroll its root before begin",
                )
            })
    }

    fn assemble(&self, values: Vec<(ResourceKey, ManagedEntry)>, skip: usize) -> Result<Snapshot> {
        let mut entries = BTreeMap::new();
        let mut parents = BTreeMap::<Vec<String>, PathBuf>::new();
        for (key, value) in values {
            let parts = &key.components()[skip..];
            let relative = if parts.is_empty() {
                PathBuf::new()
            } else {
                // A dirty view can observe a child from another transaction
                // beneath a deleted/file parent. Namespace lookup hides it.
                let Some(parent) = parents.get(&parts[..parts.len() - 1]) else {
                    continue;
                };
                let name = value.relative.file_name().ok_or_else(|| {
                    invalid(&value.relative, "a descendant version requires a basename")
                })?;
                join(parent, Path::new(name))
            };
            if matches!(value.entry, Entry::Directory { .. }) {
                parents.insert(parts.to_vec(), relative.clone());
            }
            entries.insert(relative, value.entry);
        }
        Ok(self.snapshots.from_entries(entries)?)
    }

    fn committed_snapshot(&self, candidate: &Store<ManagedEntry>, root: usize) -> Result<Snapshot> {
        let namespace = &self.domain.roots[root].namespace;
        let values = self
            .domain
            .keys
            .iter()
            .filter(|key| key.namespace() == namespace)
            .filter_map(|key| candidate.committed(key).map(|value| (key.clone(), value)))
            .collect();
        self.assemble(values, 0)
    }

    fn publish(
        &mut self,
        transaction: TransactionId,
        candidate: Store<ManagedEntry>,
        roots: &BTreeSet<usize>,
        mut observe: impl FnMut(&Path, PublishEvent) -> io::Result<()>,
    ) -> Result<CommitReport> {
        let views = roots
            .iter()
            .map(|root| {
                Ok((
                    self.domain.roots[*root].path.clone(),
                    self.committed_snapshot(&candidate, *root)?,
                ))
            })
            .collect::<Result<Vec<_>>>()?;
        let mut receipts = Vec::new();
        for (path, view) in views {
            match self.snapshots.prepare(&view, &path) {
                Ok(receipt) => receipts.push(receipt),
                Err(error) => {
                    self.cleanup.extend(receipts);
                    let compensation = self.retry_cleanup();
                    let mut journals = self.retained_journals();
                    journals.extend(error.journal.iter().cloned());
                    return Err(ManagerError::Publication {
                        transaction,
                        cause: Box::new(error),
                        compensation,
                        journals,
                    });
                }
            }
        }
        for index in 0..receipts.len() {
            let target = receipts[index].target().to_owned();
            if let Err(cause) = receipts[index].publish_with(|event| observe(&target, event)) {
                return Err(self.compensate(transaction, cause, receipts));
            }
        }
        let epoch = candidate.epoch();
        self.store = candidate;
        let published_roots = receipts.len();
        self.cleanup.extend(receipts);
        Ok(CommitReport {
            epoch,
            published_roots,
            cleanup: self.retry_cleanup(),
        })
    }

    fn compensate(
        &mut self,
        transaction: TransactionId,
        cause: FsError,
        receipts: Vec<Publication>,
    ) -> ManagerError {
        let mut compensation = Vec::new();
        for mut receipt in receipts.into_iter().rev() {
            match receipt.restore_before_image() {
                Ok(()) => self.cleanup.push(receipt),
                Err(error) => {
                    compensation.push(error);
                    self.recovery.push(receipt);
                }
            }
        }
        compensation.extend(self.retry_cleanup());
        ManagerError::Publication {
            transaction,
            cause: Box::new(cause),
            compensation,
            journals: self.retained_journals(),
        }
    }
}

impl Domain {
    fn key(&mut self, root: usize, relative: &Path) -> Result<ResourceKey> {
        let mut parts = Vec::new();
        for part in relative.components() {
            let Component::Normal(name) = part else {
                return Err(invalid(
                    relative,
                    "relative resources require normal path components",
                ));
            };
            let spelling = name
                .to_str()
                .ok_or_else(|| invalid(relative, "managed resource names must be UTF-8"))?;
            let names = self.names.entry((root, parts.clone())).or_default();
            let canonical = names
                .iter()
                .find(|(existing, _)| native_eq(existing, name))
                .map(|(_, canonical)| canonical.clone());
            let canonical = canonical.unwrap_or_else(|| {
                names.push((name.to_owned(), spelling.to_owned()));
                spelling.to_owned()
            });
            parts.push(canonical);
        }
        Ok(ResourceKey::new(
            self.roots[root].namespace.clone(),
            &format!("/{}", parts.join("/")),
        )?)
    }
}

fn relative_to(path: &Path, root: &Path) -> Option<PathBuf> {
    let mut parts = path.components();
    for prefix in root.components() {
        if !native_eq(parts.next()?.as_os_str(), prefix.as_os_str()) {
            return None;
        }
    }
    Some(parts.collect())
}

fn roots_overlap(left: &Path, right: &Path) -> Result<bool> {
    if relative_to(left, right).is_some() || relative_to(right, left).is_some() {
        return Ok(true);
    }
    for (root, candidate) in [(left, right), (right, left)] {
        let Some(identity) = identity(root)? else {
            continue;
        };
        for ancestor in candidate.ancestors() {
            if self::identity(ancestor)?.as_ref() == Some(&identity) {
                return Ok(true);
            }
        }
    }
    Ok(false)
}

fn identity(path: &Path) -> Result<Option<same_file::Handle>> {
    if !path.try_exists().map_err(|cause| ManagerError::Io {
        action: "inspect enrolled root",
        path: path.to_owned(),
        cause,
    })? {
        return Ok(None);
    }
    same_file::Handle::from_path(path)
        .map(Some)
        .map_err(|cause| ManagerError::Io {
            action: "identify enrolled root",
            path: path.to_owned(),
            cause,
        })
}

fn invalid(path: &Path, reason: &'static str) -> ManagerError {
    ManagerError::InvalidPath {
        path: path.to_owned(),
        reason,
    }
}

fn join(root: &Path, relative: &Path) -> PathBuf {
    if relative.as_os_str().is_empty() {
        root.to_owned()
    } else {
        root.join(relative)
    }
}

fn normalize(path: &Path) -> Result<PathBuf> {
    let raw = if path.is_absolute() {
        path.to_owned()
    } else {
        std::env::current_dir()
            .map_err(|cause| ManagerError::Io {
                action: "capture working directory",
                path: path.to_owned(),
                cause,
            })?
            .join(path)
    };
    // Trailing '/' or '/.' can make lstat follow a directory symlink. Inspect
    // the normalized component leaf as well, before lexical '..' elimination.
    for ancestor in std::iter::once(raw.as_path()).chain(raw.components().as_path().ancestors()) {
        match fs::symlink_metadata(ancestor) {
            Ok(metadata) => {
                if metadata.file_type().is_symlink() {
                    return Err(invalid(ancestor, "symbolic link ancestors are not managed"));
                }
                #[cfg(windows)]
                {
                    use std::os::windows::fs::MetadataExt;
                    if metadata.file_attributes() & 0x400 != 0 {
                        return Err(invalid(ancestor, "reparse point ancestors are not managed"));
                    }
                }
            }
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(cause) => {
                return Err(ManagerError::Io {
                    action: "inspect resource ancestor",
                    path: ancestor.to_owned(),
                    cause,
                });
            }
        }
    }
    validate_case_policy(&raw)?;
    let mut clean = PathBuf::new();
    for part in raw.components() {
        match part {
            Component::CurDir => {}
            Component::ParentDir => {
                clean.pop();
            }
            part => clean.push(part.as_os_str()),
        }
    }
    let mut missing = Vec::new();
    let mut existing = clean.as_path();
    while !existing.try_exists().map_err(|cause| ManagerError::Io {
        action: "inspect resource location",
        path: existing.to_owned(),
        cause,
    })? {
        missing.push(
            existing
                .file_name()
                .ok_or_else(|| invalid(path, "cannot find an existing resource ancestor"))?
                .to_owned(),
        );
        existing = existing
            .parent()
            .ok_or_else(|| invalid(path, "resource has no existing root"))?;
    }
    let mut result = fs::canonicalize(existing).map_err(|cause| ManagerError::Io {
        action: "resolve resource identity",
        path: existing.to_owned(),
        cause,
    })?;
    for name in missing.into_iter().rev() {
        result.push(name);
    }
    Ok(result)
}

fn validate_case_policy(path: &Path) -> Result<()> {
    #[cfg(windows)]
    crate::io::windows::require_case_insensitive_ancestors(path).map_err(|cause| {
        ManagerError::Io {
            action: "inspect native directory case policy",
            path: path.to_owned(),
            cause,
        }
    })?;
    #[cfg(not(windows))]
    let _ = path;
    Ok(())
}

#[cfg(windows)]
fn validate_snapshot_case_policy(source: &Path, snapshot: &Snapshot) -> Result<()> {
    // Whole-root publication recreates every directory, including unmodified
    // children. Reject policy metadata this adapter cannot faithfully restore.
    for (relative, entry) in snapshot.entries() {
        if matches!(entry, Entry::Directory { .. }) {
            validate_case_policy(&join(source, relative))?;
        }
    }
    Ok(())
}

#[cfg(not(windows))]
fn validate_snapshot_case_policy(_: &Path, _: &Snapshot) -> Result<()> {
    Ok(())
}

fn validate_names(entries: &BTreeMap<PathBuf, Entry>) -> Result<()> {
    let mut names = BTreeMap::<PathBuf, Vec<OsString>>::new();
    for relative in entries.keys().filter(|path| !path.as_os_str().is_empty()) {
        let parent = relative.parent().unwrap_or_else(|| Path::new(""));
        let name = relative
            .file_name()
            .ok_or_else(|| invalid(relative, "manifest entry requires a basename"))?;
        let text = name
            .to_str()
            .ok_or_else(|| invalid(relative, "managed resource names must be UTF-8"))?;
        if text.contains('\0') {
            return Err(invalid(
                relative,
                "managed resource names cannot contain NUL",
            ));
        }
        let siblings = names.entry(parent.to_owned()).or_default();
        if siblings.iter().any(|old| native_eq(old, name)) {
            return Err(invalid(
                relative,
                "case-colliding entries require a case-sensitive resource adapter; this manager refuses to merge them",
            ));
        }
        siblings.push(name.to_owned());
    }
    Ok(())
}

#[cfg(not(windows))]
fn native_eq(left: &OsStr, right: &OsStr) -> bool {
    left == right
}

#[cfg(windows)]
fn native_eq(left: &OsStr, right: &OsStr) -> bool {
    use std::os::windows::ffi::OsStrExt;
    use windows_sys::Win32::Globalization::{CSTR_EQUAL, CompareStringOrdinal};
    let left = left.encode_wide().collect::<Vec<_>>();
    let right = right.encode_wide().collect::<Vec<_>>();
    let Ok(left_len) = i32::try_from(left.len()) else {
        return false;
    };
    let Ok(right_len) = i32::try_from(right.len()) else {
        return false;
    };
    // SAFETY: Both UTF-16 slices remain live and their explicit lengths fit i32.
    unsafe {
        CompareStringOrdinal(left.as_ptr(), left_len, right.as_ptr(), right_len, 1) == CSTR_EQUAL
    }
}
