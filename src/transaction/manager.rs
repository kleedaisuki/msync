//! Filesystem-backed transactions over a finite, enrolled local resource domain.
//!
//! Every managed read comes from an immutable version, never from the live host
//! filesystem. Read coverage can expand between transactions while
//! retaining ended transactions' named origins. A future
//! process-view provider must enroll or lock dynamically discovered resources;
//! this manager does not pretend that unknown child IO has been isolated.
//!
//! Exclusive mutable ownership is the publication guard. Read enrollment is not
//! a write footprint: only dirty subtrees and directory metadata are published.
//! Every physical change has a retained compensating receipt; only after every
//! replacement succeeds is the logical version store swapped. This is atomic to
//! manager readers, not to unmediated host observers, and is not crash recovery.

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::{OsStr, OsString};
use std::fmt;
use std::fs;
use std::io;
use std::path::{Component, Path, PathBuf};

use super::filesystem::{
    Entry, FsError, InvocationRoot, NodeMetadata, PublishEvent, Snapshot, SnapshotStore,
};
use super::{Epoch, ResourceKey, Store, StoreError};
use crate::hir::TransactionId;
use crate::language::Isolation;

type Result<T> = std::result::Result<T, ManagerError>;

mod delta;
mod enrollment;
mod receipt;
mod refresh;
use receipt::{Batch, DirectoryReceipt};

#[derive(Clone, Debug, Default)]
struct WriteSet {
    data: BTreeSet<ResourceKey>,
    metadata: BTreeSet<ResourceKey>,
}

impl WriteSet {
    fn extend(&mut self, other: Self) {
        self.data.extend(other.data);
        self.metadata.extend(other.metadata);
    }

    fn minimal_data(&self) -> Vec<ResourceKey> {
        let mut keys = self.data.iter().collect::<Vec<_>>();
        keys.sort_by_key(|key| key.components().len());
        let mut roots = Vec::<ResourceKey>::new();
        for key in keys {
            if !roots.iter().any(|root| root.tree().contains(key)) {
                roots.push(key.clone());
            }
        }
        roots
    }
}

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
            Self::EnrollmentClosed => f.write_str("resource enrollment is closed while transactions are active; finish them before extending read coverage"),
            Self::Lifecycle(state) => write!(f, "transaction manager is {state:?}; no new resource operations are allowed"),
            Self::ActiveTransactions(ids) => write!(f, "finish or roll back active transactions before shutdown: {ids:?}"),
            Self::Cleanup { failures, journals } => {
                f.write_str("publication cleanup is incomplete; repair retained journals before another publication or shutdown")?;
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
    /// Absolute native spelling is a value, independent of read descriptors.
    relative: PathBuf,
    entry: Entry,
}

#[derive(Clone, Debug)]
struct Root {
    path: PathBuf,
    key: ResourceKey,
    coverage: Coverage,
}

#[derive(Clone, Debug)]
enum Coverage {
    Complete,
    /// Only these complete child trees are known. The root value is metadata,
    /// not evidence that its unobserved children are absent.
    Namespace(BTreeSet<ResourceKey>),
}

#[derive(Clone, Debug, Default)]
struct Domain {
    roots: Vec<Root>,
    volumes: Vec<PathBuf>,
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
    pending_roots: BTreeMap<TransactionId, WriteSet>,
    origin_roots: BTreeMap<TransactionId, WriteSet>,
    invocation: Option<InvocationRoot>,
    recovery: Vec<Batch>,
    cleanup: Vec<Batch>,
    active: BTreeSet<TransactionId>,
    lifecycle: Lifecycle,
}

impl Manager {
    pub fn new(scratch: &Path) -> Result<Self> {
        Ok(Self::with_store(SnapshotStore::new(scratch)?))
    }

    fn with_store(snapshots: SnapshotStore) -> Self {
        Self {
            snapshots,
            store: Store::new(),
            domain: Domain::default(),
            pending_roots: BTreeMap::new(),
            origin_roots: BTreeMap::new(),
            invocation: None,
            recovery: Vec::new(),
            cleanup: Vec::new(),
            active: BTreeSet::new(),
            lifecycle: Lifecycle::Open,
        }
    }

    pub fn new_in(scratch: &Path, invocation: &InvocationRoot) -> Result<Self> {
        invocation.verify()?;
        if relative_to(&normalize(scratch)?, &normalize(invocation.path())?).is_none() {
            return Err(invalid(
                scratch,
                "the snapshot store must belong to the owned invocation",
            ));
        }
        let mut manager = Self::with_store(SnapshotStore::new_in(scratch, invocation)?);
        manager.invocation = Some(invocation.clone());
        Ok(manager)
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

    pub fn has_active_transactions(&self) -> bool {
        !self.active.is_empty()
    }

    pub fn artifact_root(&self) -> &Path {
        self.snapshots.root()
    }

    /// Extends complete read coverage, including an explicitly missing root.
    /// Existing versions and named origins retain their physical-path identity;
    /// enrolling an ancestor imports only previously unknown nodes.
    /// Parent directories must already exist for eventual physical publication.
    pub fn enroll(&mut self, path: &Path) -> Result<()> {
        self.ready()?;
        if !self.active.is_empty() {
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
                if relative_to(&root.path, &path).is_some() {
                    continue;
                }
                return Err(invalid(
                    &path,
                    "the resource is already covered or aliases an enrolled root",
                ));
            }
        }
        let snapshot = self.capture_extension(&path)?;
        validate_snapshot_case_policy(&path, &snapshot)?;
        validate_names(snapshot.entries())?;
        let mut domain = self.domain.clone();
        let index = domain.add_root(path.clone(), Coverage::Complete)?;
        let root_key = domain.key(index, Path::new(""))?;
        let mut candidate = self.store.clone();
        if snapshot.is_missing() && !self.domain.known(&root_key) {
            candidate.seed(root_key.clone(), None)?;
        }
        domain.keys.insert(root_key);
        for (relative, entry) in snapshot.entries() {
            let key = domain.key(index, relative)?;
            if self.domain.known(&key) {
                continue;
            }
            candidate.seed(
                key.clone(),
                Some(ManagedEntry {
                    relative: join(&path, relative),
                    entry: entry.clone(),
                }),
            )?;
            domain.keys.insert(key);
        }
        self.store = candidate;
        self.domain = domain;
        Ok(())
    }

    /// Enroll directory identity/kind/metadata without observing its children.
    /// Live FIFO/socket siblings are not snapshot data. Explicitly enroll each
    /// ordinary direct child with `enroll_entry` before starting transactions.
    /// A namespace is not a complete directory snapshot and cannot be read or
    /// replaced as one. Registration never refreshes already versioned values.
    pub fn enroll_namespace(&mut self, path: &Path) -> Result<()> {
        self.ready()?;
        if !self.active.is_empty() {
            return Err(ManagerError::EnrollmentClosed);
        }
        let path = normalize(path)?;
        if let Ok((root, relative)) = self.locate(&path) {
            if relative.as_os_str().is_empty() {
                return Ok(());
            }
            if matches!(self.domain.roots[root].coverage, Coverage::Complete) {
                return Ok(());
            }
            return Err(invalid(
                &path,
                "nested sparse namespaces require explicit complete child enrollment",
            ));
        }
        if path.file_name().is_none() {
            return Err(invalid(
                &path,
                "a filesystem root cannot be a publication root",
            ));
        }
        for root in &self.domain.roots {
            if roots_overlap(&path, &root.path)? && relative_to(&root.path, &path).is_none() {
                return Err(invalid(
                    &path,
                    "a namespace cannot alias an already enrolled root",
                ));
            }
        }
        if let Some(invocation) = &self.invocation
            && invocation.contains_private(&path)?
        {
            return Err(invalid(
                &path,
                "owned invocation data is not a user resource",
            ));
        }
        let metadata = fs::symlink_metadata(&path).map_err(|cause| ManagerError::Io {
            action: "inspect sparse namespace",
            path: path.clone(),
            cause,
        })?;
        if !metadata.is_dir() {
            return Err(invalid(
                &path,
                "a sparse namespace must be an existing directory",
            ));
        }
        let metadata = NodeMetadata::capture(&metadata).map_err(|cause| ManagerError::Io {
            action: "capture sparse namespace metadata",
            path: path.clone(),
            cause,
        })?;
        let mut domain = self.domain.clone();
        let root = domain.add_root(path.clone(), Coverage::Namespace(BTreeSet::new()))?;
        let key = domain.key(root, Path::new(""))?;
        let mut candidate = self.store.clone();
        candidate.seed(
            key.clone(),
            Some(ManagedEntry {
                relative: path,
                entry: Entry::Directory { metadata },
            }),
        )?;
        domain.keys.insert(key);
        self.store = candidate;
        self.domain = domain;
        Ok(())
    }

    /// Capture a complete direct-child subtree, or its explicit absence, in a
    /// sparse namespace. Unknown siblings remain unknown, not missing. Repeated
    /// registration is a no-op, preserving versions and named rollback origins.
    pub fn enroll_entry(&mut self, path: &Path) -> Result<()> {
        self.ready()?;
        if !self.active.is_empty() {
            return Err(ManagerError::EnrollmentClosed);
        }
        let (root, relative) = self.locate(path)?;
        let mut domain = self.domain.clone();
        let key = domain.key(root, &relative)?;
        if self.require_coverage(root, &key, path).is_ok() {
            return Ok(());
        }
        if relative.components().count() != 1 {
            return Err(invalid(
                path,
                "enroll a complete direct child of the sparse namespace",
            ));
        }
        let path = normalize(path)?;
        let snapshot = self.capture_host(&path)?;
        validate_snapshot_case_policy(&path, &snapshot)?;
        validate_names(snapshot.entries())?;
        let mut candidate = self.store.clone();
        if snapshot.is_missing() && !self.domain.known(&key) {
            candidate.seed(key.clone(), None)?;
        }
        domain.keys.insert(key.clone());
        for (suffix, entry) in snapshot.entries() {
            let relative = join(&relative, suffix);
            let child = domain.key(root, &relative)?;
            if self.domain.known(&child) {
                continue;
            }
            candidate.seed(
                child.clone(),
                Some(ManagedEntry {
                    relative: join(&domain.roots[root].path, &relative),
                    entry: entry.clone(),
                }),
            )?;
            domain.keys.insert(child);
        }
        let Coverage::Namespace(known) = &mut domain.roots[root].coverage else {
            unreachable!("complete roots already have coverage");
        };
        known.insert(key);
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
        let snapshot = self.capture_host(&path)?;
        validate_snapshot_case_policy(&path, &snapshot)?;
        validate_names(snapshot.entries())?;
        Ok(snapshot)
    }

    pub fn begin(&mut self, id: TransactionId, isolation: Isolation) -> Result<()> {
        self.ready()?;
        self.store.begin(id, isolation)?;
        self.active.insert(id);
        Ok(())
    }

    pub fn start_statement(&mut self, id: TransactionId) -> Result<Epoch> {
        self.ready()?;
        Ok(self.store.start_statement(id)?)
    }

    /// Read one immutable node, without claiming knowledge of its children.
    /// This is the directory-kind/metadata capability of a sparse namespace;
    /// unlike `read_snapshot`, reading that namespace itself is permitted.
    pub fn read_entry(&mut self, id: TransactionId, path: &Path) -> Result<Option<Entry>> {
        self.open()?;
        let (root, relative) = self.locate(path)?;
        let mut domain = self.domain.clone();
        let key = domain.key(root, &relative)?;
        if !relative.as_os_str().is_empty() {
            self.require_coverage(root, &key, path)?;
        }
        let mut candidate = self.store.clone();
        let mut prefix = PathBuf::new();
        for part in relative.components() {
            let parent = domain.key(root, &prefix)?;
            match candidate.read(id, &parent)? {
                Some(ManagedEntry {
                    entry: Entry::Directory { .. },
                    ..
                }) => prefix.push(part),
                None => {
                    self.domain = domain;
                    self.store = candidate;
                    return Ok(None);
                }
                Some(_) => {
                    return Err(invalid(
                        path,
                        "an ancestor in this transaction view is not a directory",
                    ));
                }
            }
        }
        let value = candidate.read(id, &key)?.map(|value| value.entry);
        self.domain = domain;
        self.store = candidate;
        Ok(value)
    }

    pub fn read_snapshot(&mut self, id: TransactionId, path: &Path) -> Result<Snapshot> {
        self.open()?;
        // Immutable views remain readable while physical recovery is blocked.
        // Failed reads do not install name-index entries or read observations.
        let (root, relative) = self.locate(path)?;
        let mut domain = self.domain.clone();
        let key = domain.key(root, &relative)?;
        self.require_coverage(root, &key, path)?;
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
        let snapshot = self.assemble(values, key.components().len(), path)?;
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
        self.require_coverage(root, &key, path)?;
        let mut candidate = self.store.clone();
        let mut writes = self.pending_roots.get(&id).cloned().unwrap_or_default();
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
                candidate.write(id, parent.clone(), parent_value)?;
            }
            writes.metadata.insert(parent);
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
                    relative: join(&domain.roots[root].path, &relative),
                    entry: entry.clone(),
                },
            )?;
            added.push(key);
        }
        domain.keys.insert(key.clone());
        domain.keys.extend(added);
        writes.data.insert(key);
        self.pending_roots.insert(id, writes);
        self.store = candidate;
        self.domain = domain;
        Ok(())
    }

    pub fn checkpoint(&mut self, id: TransactionId) -> Result<CommitReport> {
        self.checkpoint_with(id, |_, _| Ok(()))
    }

    /// The observer runs at data/metadata install boundaries while the manager owns
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
        self.invocation = None;
        self.lifecycle = Lifecycle::Closed {
            epoch: self.epoch(),
        };
        Ok(())
    }

    pub fn recovery_journals(&self) -> Vec<PathBuf> {
        self.recovery
            .iter()
            .flat_map(Batch::journals)
            .map(Path::to_owned)
            .collect()
    }

    pub fn retained_journals(&self) -> Vec<PathBuf> {
        self.recovery
            .iter()
            .chain(&self.cleanup)
            .flat_map(Batch::journals)
            .map(Path::to_owned)
            .collect()
    }

    /// Retry all failed before-image restorations. Logical committed state and
    /// transaction pending writes were never advanced by the failed checkpoint.
    pub fn recover(&mut self) -> Vec<FsError> {
        let mut failures = Vec::new();
        let mut retained = Vec::new();
        for mut batch in self.recovery.drain(..).rev() {
            let mut errors = batch.restore();
            if errors.is_empty() {
                self.cleanup.push(batch);
            } else {
                failures.append(&mut errors);
                retained.push(batch);
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
        for mut batch in self.cleanup.drain(..) {
            failures.extend(batch.cleanup());
            if !batch.is_empty() {
                retained.push(batch);
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
        if let Some(invocation) = &self.invocation
            && invocation.contains_private(&path)?
        {
            return Err(invalid(
                &path,
                "owned invocation data is not a user resource",
            ));
        }
        self.domain
            .roots
            .iter()
            .enumerate()
            .filter_map(|(index, root)| {
                relative_to(&path, &root.path).map(|relative| (index, relative))
            })
            .min_by_key(|(index, _)| self.domain.roots[*index].path.components().count())
            .ok_or_else(|| {
                invalid(
                    &path,
                    "resource is outside the enrolled domain; enroll its root before begin",
                )
            })
    }

    fn require_coverage(&self, root: usize, key: &ResourceKey, path: &Path) -> Result<()> {
        let _ = root;
        if self.domain.complete(key) {
            return Ok(());
        }
        Err(invalid(
            path,
            "resource has unknown sparse coverage; enroll its complete child subtree before begin (namespace metadata is not a directory snapshot)",
        ))
    }

    fn capture_host(&self, path: &Path) -> Result<Snapshot> {
        Ok(match &self.invocation {
            Some(invocation) => self.snapshots.capture_in(path, invocation)?,
            None => self.snapshots.capture(path)?,
        })
    }

    fn assemble(
        &self,
        values: Vec<(ResourceKey, ManagedEntry)>,
        skip: usize,
        logical: &Path,
    ) -> Result<Snapshot> {
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
        Ok(match &self.invocation {
            Some(invocation) => self
                .snapshots
                .from_entries_in(entries, logical, invocation)?,
            None => self.snapshots.from_entries(entries)?,
        })
    }

    fn committed_snapshot(
        &self,
        candidate: &Store<ManagedEntry>,
        root: &ResourceKey,
    ) -> Result<Snapshot> {
        let values = self
            .domain
            .keys
            .iter()
            .filter(|key| root.tree().contains(key))
            .filter_map(|key| candidate.committed(key).map(|value| (key.clone(), value)))
            .collect();
        self.assemble(
            values,
            root.components().len(),
            &self.key_path(candidate, root)?,
        )
    }

    fn key_path(&self, candidate: &Store<ManagedEntry>, key: &ResourceKey) -> Result<PathBuf> {
        if let Some(value) = candidate
            .committed(key)
            .or_else(|| self.store.committed(key))
        {
            return Ok(value.relative);
        }
        let volume = self
            .domain
            .volumes
            .iter()
            .enumerate()
            .find(|(index, _)| key.namespace() == format!("local-volume-{index}"))
            .map(|(_, base)| base)
            .ok_or_else(|| invalid(Path::new(""), "write resource has no enrolled volume"))?;
        Ok(key
            .components()
            .iter()
            .fold(volume.clone(), |path, part| path.join(part)))
    }

    fn publish(
        &mut self,
        transaction: TransactionId,
        candidate: Store<ManagedEntry>,
        writes: &WriteSet,
        mut observe: impl FnMut(&Path, PublishEvent) -> io::Result<()>,
    ) -> Result<CommitReport> {
        let mut trace = crate::telemetry::span("transaction.publish");
        trace.expect_completion();
        trace.attribute("phase", "prepare");
        trace.attribute("destination.backend", "local");
        trace.attribute("publication.atomicity", "managed-readers");
        trace.metric("transaction.id", transaction.0 as u64);
        trace.attribute_lazy("manager.root", || {
            self.snapshots.root().display().to_string()
        });
        trace.metric("resources.data", writes.data.len() as u64);
        trace.metric("resources.metadata", writes.metadata.len() as u64);
        let published = if trace.is_recording() {
            writes
                .minimal_data()
                .iter()
                .filter_map(|root| {
                    let path = self.key_path(&candidate, root).ok()?;
                    let (files, bytes) = self
                        .domain
                        .keys
                        .iter()
                        .filter(|key| root.tree().contains(key))
                        .filter_map(|key| candidate.committed(key))
                        .fold((0u64, 0u64), |(files, bytes), value| match value.entry {
                            Entry::File { artifact, .. } => {
                                (files + 1, bytes.saturating_add(artifact.len()))
                            }
                            _ => (files, bytes),
                        });
                    Some((path, files, bytes))
                })
                .collect::<Vec<_>>()
        } else {
            Vec::new()
        };
        let result = self.publish_inner(transaction, candidate, writes, |path, event| {
            if trace.is_recording() {
                let target = path.display().to_string();
                let step = match event {
                    PublishEvent::AfterBackup => "after-backup",
                    PublishEvent::AfterInstall => "after-install",
                };
                trace.event(
                    "file.publish.step",
                    &[
                        ("phase", step),
                        ("destination.path", &target),
                        ("publication.state", "tentative"),
                    ],
                    &[],
                );
            }
            observe(path, event)
        });
        trace.finish_result(&result);
        match &result {
            Ok(report) => {
                trace.attribute("phase", "committed");
                trace.metric("epoch", report.epoch.0);
                trace.metric("roots.published", report.published_roots as u64);
                trace.metric("cleanup.pending", report.cleanup.len() as u64);
                let state = if report.cleanup.is_empty() {
                    "committed"
                } else {
                    "committed-cleanup-pending"
                };
                trace.attribute("publication.state", state);
                for (path, files, bytes) in published {
                    trace.event(
                        "file.publish",
                        &[
                            ("phase", "commit"),
                            ("destination.path", &path.display().to_string()),
                            ("publication.state", state),
                        ],
                        &[("files.committed", files), ("bytes.committed", bytes)],
                    );
                }
                trace.event(
                    "transaction.commit",
                    &[("phase", "commit"), ("publication.state", state)],
                    &[],
                );
                for failure in &report.cleanup {
                    record_publication_failure(&trace, "publication.cleanup", failure);
                }
            }
            Err(failure) => {
                trace.attribute("phase", "failed");
                trace.attribute(
                    "publication.state",
                    if self.recovery.is_empty() {
                        "not-committed"
                    } else {
                        "not-committed-recovery-pending"
                    },
                );
                trace.record_error("publication", failure);
                match failure {
                    ManagerError::Publication {
                        cause,
                        compensation,
                        ..
                    } => {
                        record_publication_failure(&trace, "publication.failure", cause);
                        for failure in compensation {
                            record_publication_failure(&trace, "publication.compensate", failure);
                        }
                        if cause.cause.kind() == io::ErrorKind::Interrupted {
                            trace.attribute("phase", "cancelled");
                            trace.cancelled();
                        }
                    }
                    ManagerError::Cleanup { failures, .. } => {
                        for failure in failures {
                            record_publication_failure(&trace, "publication.cleanup", failure);
                        }
                    }
                    _ => {}
                }
            }
        }
        result
    }

    fn publish_inner(
        &mut self,
        transaction: TransactionId,
        candidate: Store<ManagedEntry>,
        writes: &WriteSet,
        mut observe: impl FnMut(&Path, PublishEvent) -> io::Result<()>,
    ) -> Result<CommitReport> {
        if !writes.data.is_empty() || !writes.metadata.is_empty() {
            let failures = self.retry_cleanup();
            if !self.cleanup.is_empty() {
                return Err(ManagerError::Cleanup {
                    failures,
                    journals: self.retained_journals(),
                });
            }
        }
        let roots = writes.minimal_data();
        let views = roots
            .iter()
            .map(|key| {
                Ok((
                    self.key_path(&candidate, key)?,
                    self.committed_snapshot(&candidate, key)?,
                ))
            })
            .collect::<Result<Vec<_>>>()?;
        let mut batch = Batch::default();
        let mut metadata_keys = writes.metadata.clone();
        for key in &roots {
            let parts = key.components();
            if !parts.is_empty() {
                metadata_keys.insert(ResourceKey::new(
                    key.namespace(),
                    &format!("/{}", parts[..parts.len() - 1].join("/")),
                )?);
            }
        }
        // Capture all namespace before-images before sibling data journals are
        // allocated: those allocations themselves change directory mtimes.
        // An origin may predate enrollment of its parent. Its original write
        // set remains unchanged, but a now-managed parent's metadata must still
        // survive the physical journals used to publish that origin's undo.
        for key in &metadata_keys {
            if roots.iter().any(|root| root.tree().contains(key)) {
                continue;
            }
            let Some(ManagedEntry {
                entry: Entry::Directory { metadata },
                ..
            }) = candidate.committed(key)
            else {
                continue;
            };
            let path = self.key_path(&candidate, key)?;
            match DirectoryReceipt::prepare(&path, metadata, self.snapshots.root()) {
                Ok(receipt) => batch.metadata.push(receipt),
                Err(cause) => return Err(self.compensate(transaction, cause, batch)),
            }
        }
        for (path, view) in views {
            match self.snapshots.prepare(&view, &path) {
                Ok(receipt) => {
                    if let Some(context) = crate::telemetry::context() {
                        context.event(
                            "file.stage",
                            &[
                                ("phase", "prepare"),
                                ("destination.path", &path.display().to_string()),
                                ("stage.path", &receipt.journal().display().to_string()),
                            ],
                            &[("transaction.id", transaction.0 as u64)],
                        );
                    }
                    batch.data.push(receipt);
                }
                Err(error) => {
                    let orphan = error.journal.clone();
                    let mut failure = self.compensate(transaction, error, batch);
                    if let ManagerError::Publication { journals, .. } = &mut failure {
                        journals.extend(orphan);
                    }
                    return Err(failure);
                }
            }
        }
        if let Err(cause) = batch.publish(&mut observe) {
            return Err(self.compensate(transaction, cause, batch));
        }
        let epoch = candidate.epoch();
        self.store = candidate;
        let published_roots = batch.data.len();
        batch.committed = true;
        self.cleanup.push(batch);
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
        mut batch: Batch,
    ) -> ManagerError {
        crate::telemetry::event(
            "transaction.compensate",
            &[("phase", "restore-before-images")],
            &[("transaction.id", transaction.0 as u64)],
        );
        let mut compensation = batch.restore();
        crate::telemetry::event(
            "transaction.compensate.result",
            &[("phase", "restore-before-images")],
            &[
                ("transaction.id", transaction.0 as u64),
                ("recovery.failures", compensation.len() as u64),
            ],
        );
        if compensation.is_empty() {
            self.cleanup.push(batch);
        } else {
            self.recovery.push(batch);
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

fn record_publication_failure(
    trace: &crate::telemetry::SpanGuard,
    phase: &'static str,
    failure: &FsError,
) {
    trace.record_error(phase, failure);
    if let Some(cause) = &failure.recovery_error {
        trace.io_error("publication.recovery", cause);
    }
}

impl Domain {
    fn key(&mut self, root: usize, relative: &Path) -> Result<ResourceKey> {
        self.physical_key(&join(&self.roots[root].path, relative))
    }

    fn physical_key(&mut self, path: &Path) -> Result<ResourceKey> {
        let base = volume_root(path);
        let volume = match self
            .volumes
            .iter()
            .position(|existing| native_eq(existing.as_os_str(), base.as_os_str()))
        {
            Some(index) => index,
            None => {
                self.volumes.push(base);
                self.volumes.len() - 1
            }
        };
        let mut parts = Vec::new();
        for part in path.components() {
            let Component::Normal(name) = part else {
                continue;
            };
            let spelling = name
                .to_str()
                .ok_or_else(|| invalid(path, "managed resource names must be UTF-8"))?;
            let names = self.names.entry((volume, parts.clone())).or_default();
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
            format!("local-volume-{volume}"),
            &format!("/{}", parts.join("/")),
        )?)
    }

    fn add_root(&mut self, path: PathBuf, coverage: Coverage) -> Result<usize> {
        let key = self.physical_key(&path)?;
        let index = self.roots.len();
        self.roots.push(Root {
            path,
            key,
            coverage,
        });
        Ok(index)
    }

    fn complete(&self, key: &ResourceKey) -> bool {
        self.roots.iter().any(|root| match &root.coverage {
            Coverage::Complete => root.key.tree().contains(key),
            Coverage::Namespace(known) => known.iter().any(|entry| entry.tree().contains(key)),
        })
    }

    fn known(&self, key: &ResourceKey) -> bool {
        self.keys.contains(key) || self.complete(key)
    }
}

fn volume_root(path: &Path) -> PathBuf {
    #[cfg(windows)]
    if let Some(Component::Prefix(prefix)) = path.components().next() {
        use std::path::Prefix;
        match prefix.kind() {
            Prefix::Disk(letter) | Prefix::VerbatimDisk(letter) => {
                return PathBuf::from(format!("{}:\\", char::from(letter)));
            }
            Prefix::UNC(server, share) | Prefix::VerbatimUNC(server, share) => {
                return PathBuf::from(format!(
                    "\\\\{}\\{}\\",
                    server.to_string_lossy(),
                    share.to_string_lossy()
                ));
            }
            _ => {}
        }
    }
    path.ancestors()
        .last()
        .expect("absolute managed path has a volume root")
        .to_owned()
}

fn relative_to(path: &Path, root: &Path) -> Option<PathBuf> {
    let mut parts = path.components();
    for prefix in root.components() {
        let part = parts.next()?;
        let equal = match (part, prefix) {
            (Component::Prefix(_), Component::Prefix(_)) => {
                native_eq(volume_root(path).as_os_str(), volume_root(root).as_os_str())
            }
            _ => native_eq(part.as_os_str(), prefix.as_os_str()),
        };
        if !equal {
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
    // Managed views may rematerialize any captured directory. Reject policy
    // metadata this adapter cannot faithfully restore, even on read-only nodes.
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
