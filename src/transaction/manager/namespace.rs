//! Native-name reservations and logical-order directory mutation receipts.

use std::collections::{BTreeMap, BTreeSet};
use std::fs::{self, Permissions};
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::SystemTime;

use same_file::Handle;

use super::receipt::DirectoryReceipt;
use super::{
    CommitReport, ManagedEntry, Manager, ManagerError, Result, invalid, normalize, relative_to,
};
use crate::hir::TransactionId;
use crate::language::Isolation;
use crate::transaction::filesystem::{Entry, FsError, PublishEvent, Recovery, Snapshot};
use crate::transaction::{ResourceKey, Store};

/// Source-order ticket, independent of HIR identity and native completion time.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct NamespacePublicationOrder(pub u64);

/// Invocation-owned native-name capability. Completion or explicit cancellation
/// consumes each reserved child; cloning or dropping this value does not do so.
#[derive(Clone, Debug)]
pub struct NamespaceMergePermit {
    provider: String,
    epoch: u64,
    parent: ResourceKey,
    path: PathBuf,
    children: BTreeMap<ResourceKey, PathBuf>,
}

impl NamespaceMergePermit {
    pub fn provider(&self) -> &str {
        &self.provider
    }
    pub fn epoch_id(&self) -> u64 {
        self.epoch
    }
    pub fn parent_key(&self) -> &ResourceKey {
        &self.parent
    }
    pub fn parent_path(&self) -> &Path {
        &self.path
    }
    pub fn child_key(&self, path: &Path) -> Option<&ResourceKey> {
        self.children
            .iter()
            .find(|(_, native)| {
                relative_to(path, native).is_some_and(|suffix| suffix.as_os_str().is_empty())
            })
            .map(|(key, _)| key)
    }
}

/// A successful native publication and its logical-order metadata decision.
#[derive(Debug)]
pub struct NamespacePublicationReceipt {
    pub report: CommitReport,
    pub order: NamespacePublicationOrder,
    pub mutation_modified: SystemTime,
    pub selected_order: NamespacePublicationOrder,
    pub selected_modified: SystemTime,
}

#[derive(Clone, Debug)]
pub(super) struct Head {
    pub order: NamespacePublicationOrder,
    pub modified: SystemTime,
}

#[derive(Clone, Debug)]
struct ParentEpoch {
    id: u64,
    path: PathBuf,
    identity: Arc<Handle>,
    permissions: Permissions,
    pending: BTreeSet<ResourceKey>,
    head: Option<Head>,
    orders: BTreeSet<NamespacePublicationOrder>,
}

#[derive(Clone, Debug)]
pub(super) struct Request {
    pub transaction: TransactionId,
    parent: ResourceKey,
    order: NamespacePublicationOrder,
    receipt: Option<(SystemTime, Head)>,
}

#[derive(Clone, Debug)]
struct Access {
    key: ResourceKey,
    tree: bool,
}

#[derive(Clone, Debug, Default)]
pub(super) struct Namespaces {
    epochs: BTreeMap<ResourceKey, ParentEpoch>,
    next_epoch: u64,
    accesses: BTreeMap<TransactionId, Vec<Access>>,
    pub current: Option<Request>,
}

impl Namespaces {
    pub(super) fn observes_import(&self, id: TransactionId, key: &ResourceKey) -> bool {
        self.accesses.get(&id).is_some_and(|accesses| {
            accesses.iter().any(|access| {
                key.tree().contains(&access.key) || access.tree && access.key.tree().contains(key)
            })
        })
    }

    pub(super) fn has_pending(&self) -> bool {
        self.epochs.values().any(|epoch| !epoch.pending.is_empty())
    }
    fn conflicts(&self, key: &ResourceKey, tree: bool) -> bool {
        self.epochs
            .iter()
            .any(|(parent, epoch)| !epoch.pending.is_empty() && intersects(key, tree, parent))
    }
}

fn intersects(key: &ResourceKey, tree: bool, parent: &ResourceKey) -> bool {
    parent.tree().contains(key) || (tree && key.tree().contains(parent))
}

impl Manager {
    /// Reserve missing ordinary direct children under one existing directory.
    /// Canonical native keys, not lexical basenames, prove disjoint bindings.
    /// Overlapping certificates share a parent epoch until its last reservation
    /// completes or is explicitly cancelled. No directory predicate is waived.
    pub fn certify_namespace_children(
        &mut self,
        targets: &[PathBuf],
    ) -> Result<NamespaceMergePermit> {
        self.ready()?;
        let first = targets.first().ok_or_else(|| {
            invalid(
                Path::new(""),
                "namespace certification requires at least one child",
            )
        })?;
        let first = normalize(first)?;
        let parent_path = first
            .parent()
            .ok_or_else(|| invalid(&first, "namespace child requires a parent"))?
            .to_owned();
        let (root, relative) = self.locate(&parent_path)?;
        let mut domain = self.domain.clone();
        let parent = domain.key(root, &relative)?;
        self.ensure_committed_ancestors(root, &relative, &mut domain)?;
        let Some(ManagedEntry {
            entry: Entry::Directory { metadata: logical },
            ..
        }) = self.store.committed(&parent)
        else {
            return Err(invalid(
                &parent_path,
                "namespace merge requires an enrolled existing directory node",
            ));
        };
        self.certify_no_observer(&parent)?;
        let native = fs::metadata(&parent_path)
            .map_err(|cause| io_error("inspect merge parent", &parent_path, cause))?;
        if !native.is_dir()
            || native.permissions() != logical.permissions
            || native.permissions().readonly()
        {
            return Err(invalid(
                &parent_path,
                "namespace merge requires unchanged writable directory permissions",
            ));
        }
        let identity = Handle::from_path(&parent_path)
            .map_err(|cause| io_error("identify merge parent", &parent_path, cause))?;
        let mut children = BTreeMap::new();
        for target in targets {
            let path = normalize(target)?;
            if !relative_to(path.parent().unwrap_or_else(|| Path::new("")), &parent_path)
                .is_some_and(|suffix| suffix.as_os_str().is_empty())
            {
                return Err(invalid(
                    &path,
                    "namespace merge children must have the same existing parent",
                ));
            }
            let (root, relative) = self.locate(&path)?;
            let key = domain.key(root, &relative)?;
            self.require_coverage(root, &key, &path)?;
            if self.store.committed(&key).is_some() || !missing(&path)? {
                return Err(invalid(
                    &path,
                    "namespace insertion proof requires an actually missing target",
                ));
            }
            if children.insert(key.clone(), path.clone()).is_some()
                || self
                    .namespaces
                    .epochs
                    .values()
                    .any(|epoch| epoch.pending.contains(&key))
            {
                return Err(invalid(
                    &path,
                    "namespace insertion aliases an already reserved native child name",
                ));
            }
        }
        let reuse = self
            .namespaces
            .epochs
            .get(&parent)
            .is_some_and(|epoch| !epoch.pending.is_empty());
        if reuse {
            let epoch = &self.namespaces.epochs[&parent];
            if *epoch.identity != identity || epoch.permissions != native.permissions() {
                return Err(invalid(
                    &parent_path,
                    "live namespace epoch identity or permissions changed",
                ));
            }
        } else {
            self.namespaces.next_epoch = self
                .namespaces
                .next_epoch
                .checked_add(1)
                .ok_or_else(|| invalid(&parent_path, "namespace epoch counter exhausted"))?;
            self.namespaces.epochs.insert(
                parent.clone(),
                ParentEpoch {
                    id: self.namespaces.next_epoch,
                    path: parent_path.clone(),
                    identity: Arc::new(identity),
                    permissions: native.permissions(),
                    pending: BTreeSet::new(),
                    head: None,
                    orders: BTreeSet::new(),
                },
            );
        }
        let provider = self.namespace_provider();
        let epoch = self
            .namespaces
            .epochs
            .get_mut(&parent)
            .expect("inserted namespace epoch");
        epoch.pending.extend(children.keys().cloned());
        let permit = NamespaceMergePermit {
            provider,
            epoch: epoch.id,
            parent,
            path: parent_path,
            children,
        };
        self.domain = domain;
        Ok(permit)
    }

    /// An implicit exact regular-file read pinned at the current committed head,
    /// without directory enumeration or a parent-metadata predicate. A pending
    /// output binding is never served as an older snapshot through this API.
    pub fn read_committed_file(&self, path: &Path) -> Result<Snapshot> {
        self.open()?;
        let (root, relative) = self.locate(path)?;
        let mut domain = self.domain.clone();
        let key = domain.key(root, &relative)?;
        self.require_coverage(root, &key, path)?;
        self.ensure_committed_ancestors(root, &relative, &mut domain)?;
        if self.namespaces.epochs.values().any(|epoch| {
            epoch
                .pending
                .iter()
                .any(|pending| pending.tree().contains(&key) || key.tree().contains(pending))
        }) {
            return Err(invalid(
                path,
                "file binding has an unpublished namespace producer; wait for its logical completion",
            ));
        }
        if !matches!(
            self.store.committed(&key),
            Some(ManagedEntry {
                entry: Entry::File { .. },
                ..
            })
        ) {
            return Err(invalid(
                path,
                "a committed file lease requires one existing ordinary file",
            ));
        }
        self.committed_snapshot(&self.store, &key)
    }

    pub fn cancel_namespace_child(
        &mut self,
        permit: &NamespaceMergePermit,
        path: &Path,
    ) -> Result<()> {
        let key = permit.child_key(path).cloned().ok_or_else(|| {
            invalid(
                path,
                "cancellation is not for a child owned by this capability",
            )
        })?;
        let valid = permit.provider == self.namespace_provider()
            && self
                .namespaces
                .epochs
                .get(&permit.parent)
                .is_some_and(|epoch| epoch.id == permit.epoch && epoch.pending.contains(&key));
        if !valid {
            return Err(invalid(
                path,
                "namespace cancellation capability is stale or belongs to another manager",
            ));
        }
        self.release_namespace_child(&permit.parent, &key);
        Ok(())
    }

    pub fn publish_merged_file(
        &mut self,
        id: TransactionId,
        permit: &NamespaceMergePermit,
        path: &Path,
        snapshot: &Snapshot,
        order: NamespacePublicationOrder,
    ) -> Result<NamespacePublicationReceipt> {
        self.publish_merged_file_with(id, permit, path, snapshot, order, |_, _| Ok(()))
    }

    /// Fault observer has the same tentative install semantics as checkpoint_with.
    pub fn publish_merged_file_with(
        &mut self,
        id: TransactionId,
        permit: &NamespaceMergePermit,
        path: &Path,
        snapshot: &Snapshot,
        order: NamespacePublicationOrder,
        observe: impl FnMut(&Path, PublishEvent) -> io::Result<()>,
    ) -> Result<NamespacePublicationReceipt> {
        self.ready()?;
        let key = self.validate_permit(permit, path)?;
        self.revalidate_parent(permit)?;
        if self.namespaces.epochs[&permit.parent]
            .orders
            .contains(&order)
        {
            return Err(invalid(
                path,
                "logical publication order is already owned by another successful namespace receipt",
            ));
        }
        if snapshot.entries().len() != 1
            || !matches!(
                snapshot.entries().get(Path::new("")),
                Some(Entry::File { .. })
            )
        {
            return Err(invalid(
                path,
                "namespace merge can publish exactly one ordinary file, not a tree or deletion",
            ));
        }
        if self.store.committed(&key).is_some() || !missing(path)? {
            return Err(invalid(
                path,
                "reserved child is no longer missing; publication must not overwrite a foreign binding",
            ));
        }
        let saved = (
            self.store.clone(),
            self.domain.clone(),
            self.pending_roots.clone(),
            self.origin_roots.clone(),
            self.active.clone(),
        );
        self.namespaces.current = Some(Request {
            transaction: id,
            parent: permit.parent.clone(),
            order,
            receipt: None,
        });
        let result = (|| {
            self.store.begin(id, Isolation::ReadCommitted)?;
            self.active.insert(id);
            self.replace_snapshot(id, path, snapshot)?;
            let mut candidate = self.store.clone();
            candidate.finish(id)?;
            let writes = self.pending_roots.get(&id).cloned().unwrap_or_default();
            let report = self.publish(id, candidate, &writes, observe)?;
            self.origin_roots.entry(id).or_default().extend(writes);
            self.pending_roots.remove(&id);
            self.active.remove(&id);
            Ok(report)
        })();
        let request = self
            .namespaces
            .current
            .take()
            .expect("owned namespace publication");
        match result {
            Ok(report) => {
                let (mutation_modified, selected) = request
                    .receipt
                    .expect("successful native namespace receipt");
                self.release_namespace_child(&permit.parent, &key);
                Ok(NamespacePublicationReceipt {
                    report,
                    order,
                    mutation_modified,
                    selected_order: selected.order,
                    selected_modified: selected.modified,
                })
            }
            Err(failure) => {
                (
                    self.store,
                    self.domain,
                    self.pending_roots,
                    self.origin_roots,
                    self.active,
                ) = saved;
                Err(failure)
            }
        }
    }

    fn namespace_provider(&self) -> String {
        format!("manager:{}", self.snapshots.root().display())
    }

    fn ensure_committed_ancestors(
        &self,
        root: usize,
        relative: &Path,
        domain: &mut super::Domain,
    ) -> Result<()> {
        let mut prefix = PathBuf::new();
        for part in relative.components() {
            let key = domain.key(root, &prefix)?;
            if !matches!(
                self.store.committed(&key),
                Some(ManagedEntry {
                    entry: Entry::Directory { .. },
                    ..
                })
            ) {
                return Err(invalid(
                    &self.key_path(&self.store, &key)?,
                    "a committed file capability requires reachable directory ancestors",
                ));
            }
            prefix.push(part);
        }
        Ok(())
    }

    fn release_namespace_child(&mut self, parent: &ResourceKey, key: &ResourceKey) {
        let epoch = self
            .namespaces
            .epochs
            .get_mut(parent)
            .expect("owned namespace reservation");
        epoch.pending.remove(key);
        if epoch.pending.is_empty() {
            self.namespaces.epochs.remove(parent);
        }
    }

    pub(super) fn clear_namespace_reads(&mut self, id: TransactionId) {
        self.namespaces.accesses.remove(&id);
    }

    fn validate_permit(&self, permit: &NamespaceMergePermit, path: &Path) -> Result<ResourceKey> {
        if permit.provider != self.namespace_provider() {
            return Err(invalid(
                path,
                "namespace capability belongs to another manager invocation",
            ));
        }
        let path = normalize(path)?;
        let (root, relative) = self.locate(&path)?;
        let mut domain = self.domain.clone();
        let key = domain.key(root, &relative)?;
        let valid = self
            .namespaces
            .epochs
            .get(&permit.parent)
            .is_some_and(|epoch| epoch.id == permit.epoch && epoch.pending.contains(&key));
        if !permit.children.contains_key(&key) || !valid {
            return Err(invalid(
                &path,
                "namespace child capability is stale, cancelled, completed, or for another native name",
            ));
        }
        Ok(key)
    }

    fn revalidate_parent(&self, permit: &NamespaceMergePermit) -> Result<()> {
        let epoch = &self.namespaces.epochs[&permit.parent];
        let identity = Handle::from_path(&epoch.path)
            .map_err(|cause| io_error("revalidate merge parent identity", &epoch.path, cause))?;
        let metadata = fs::symlink_metadata(&epoch.path)
            .map_err(|cause| io_error("revalidate merge parent permissions", &epoch.path, cause))?;
        if identity != *epoch.identity
            || !metadata.is_dir()
            || metadata.permissions() != epoch.permissions
        {
            return Err(invalid(
                &epoch.path,
                "namespace epoch parent identity or permissions changed; no child was published",
            ));
        }
        Ok(())
    }

    pub(super) fn fence_namespace(&self, key: &ResourceKey, tree: bool) -> Result<()> {
        if self.namespaces.conflicts(key, tree) {
            return Err(invalid(
                &self.key_path(&self.store, key)?,
                "operation observes or replaces a live namespace epoch; wait for or cancel its reserved children",
            ));
        }
        Ok(())
    }

    pub(super) fn record_namespace_read(
        &mut self,
        id: TransactionId,
        root: usize,
        key: ResourceKey,
        tree: bool,
    ) -> Result<()> {
        let mut accesses = vec![Access {
            key: key.clone(),
            tree,
        }];
        for length in self.domain.roots[root].key.components().len()..key.components().len() {
            accesses.push(Access {
                key: ResourceKey::new(
                    key.namespace(),
                    &format!("/{}", key.components()[..length].join("/")),
                )?,
                tree: false,
            });
        }
        self.namespaces
            .accesses
            .entry(id)
            .or_default()
            .extend(accesses);
        Ok(())
    }

    fn certify_no_observer(&self, parent: &ResourceKey) -> Result<()> {
        for id in &self.active {
            let reads = self.namespaces.accesses.get(id).is_some_and(|reads| {
                reads
                    .iter()
                    .any(|read| intersects(&read.key, read.tree, parent))
            });
            let writes = self.pending_roots.get(id).is_some_and(|writes| {
                writes.data.iter().any(|key| intersects(key, true, parent))
                    || writes
                        .metadata
                        .iter()
                        .any(|key| intersects(key, false, parent))
            });
            if reads || writes {
                return Err(invalid(
                    &self.key_path(&self.store, parent)?,
                    "an active strict transaction already observes this namespace; finish it before certifying merges",
                ));
            }
        }
        Ok(())
    }

    pub(super) fn namespace_import_allowed(
        &self,
        key: &ResourceKey,
        snapshot: &Snapshot,
    ) -> Result<()> {
        if !self.namespaces.conflicts(key, true) {
            return Ok(());
        }
        if self
            .namespaces
            .epochs
            .values()
            .any(|epoch| epoch.pending.contains(key))
            || (!snapshot.is_missing()
                && (snapshot.entries().len() != 1
                    || !matches!(
                        snapshot.entries().get(Path::new("")),
                        Some(Entry::File { .. })
                    )))
        {
            return Err(invalid(
                &self.key_path(&self.store, key)?,
                "live namespace permits only disjoint ordinary-file enrollment, not directory/tree observation",
            ));
        }
        Ok(())
    }

    pub(super) fn bind_namespace_metadata(
        &self,
        candidate: &mut Store<ManagedEntry>,
        metadata: &mut [DirectoryReceipt],
        transaction: TransactionId,
    ) -> std::result::Result<Option<(SystemTime, Head)>, FsError> {
        let Some(request) = &self.namespaces.current else {
            return Ok(None);
        };
        let epoch = &self.namespaces.epochs[&request.parent];
        let native =
            fs::symlink_metadata(&epoch.path).map_err(|cause| native_error(&epoch.path, cause))?;
        let identity =
            Handle::from_path(&epoch.path).map_err(|cause| native_error(&epoch.path, cause))?;
        if request.transaction != transaction
            || identity != *epoch.identity
            || !native.is_dir()
            || native.permissions() != epoch.permissions
        {
            return Err(native_error(
                &epoch.path,
                io::Error::other(
                    "namespace parent identity or permissions changed before receipt binding",
                ),
            ));
        }
        let observed = native
            .modified()
            .map_err(|cause| native_error(&epoch.path, cause))?;
        let incoming = Head {
            order: request.order,
            modified: observed,
        };
        let selected = epoch
            .head
            .as_ref()
            .filter(|head| head.order > incoming.order)
            .cloned()
            .unwrap_or(incoming);
        let mut value = candidate.committed(&request.parent).ok_or_else(|| {
            native_error(
                &epoch.path,
                io::Error::other("unpublished namespace parent head is missing"),
            )
        })?;
        let Entry::Directory {
            metadata: attributes,
        } = &mut value.entry
        else {
            return Err(native_error(
                &epoch.path,
                io::Error::other("unpublished namespace head is not a directory"),
            ));
        };
        attributes.modified = selected.modified;
        candidate
            .replace_unpublished_head(&request.parent, self.store.epoch(), transaction, value)
            .map_err(|cause| native_error(&epoch.path, io::Error::other(cause)))?;
        let receipt = metadata
            .iter_mut()
            .find(|receipt| {
                relative_to(receipt.path(), &epoch.path)
                    .is_some_and(|suffix| suffix.as_os_str().is_empty())
            })
            .ok_or_else(|| {
                native_error(
                    &epoch.path,
                    io::Error::other("namespace directory receipt is missing"),
                )
            })?;
        receipt.bind_modified(selected.modified)?;
        Ok(Some((observed, selected)))
    }

    pub(super) fn commit_namespace_receipt(&mut self, receipt: Option<(SystemTime, Head)>) {
        if let (Some(request), Some((observed, selected))) = (&mut self.namespaces.current, receipt)
        {
            self.namespaces
                .epochs
                .get_mut(&request.parent)
                .expect("live namespace")
                .head = Some(selected.clone());
            self.namespaces
                .epochs
                .get_mut(&request.parent)
                .expect("live namespace")
                .orders
                .insert(request.order);
            request.receipt = Some((observed, selected));
        }
    }
}

fn missing(path: &Path) -> Result<bool> {
    match fs::symlink_metadata(path) {
        Ok(_) => Ok(false),
        Err(cause) if cause.kind() == io::ErrorKind::NotFound => Ok(true),
        Err(cause) => Err(io_error("inspect reserved namespace child", path, cause)),
    }
}
fn io_error(action: &'static str, path: &Path, cause: io::Error) -> ManagerError {
    ManagerError::Io {
        action,
        path: path.to_owned(),
        cause,
    }
}
fn native_error(path: &Path, cause: io::Error) -> FsError {
    FsError {
        action: "bind native namespace receipt",
        path: path.to_owned(),
        cause,
        journal: None,
        recovery: Recovery::Unchanged,
        recovery_error: None,
        help: Some("The candidate was not committed; retain any reported compensation journals."),
    }
}
