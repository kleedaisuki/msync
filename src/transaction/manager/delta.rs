//! Import changed values from a completed program view, not its entire read domain.

use std::collections::{BTreeMap, BTreeSet};
use std::fs::File;
use std::io::{self, Read};
use std::path::{Path, PathBuf};

use super::{Domain, ManagedEntry, Manager, ManagerError, Result, invalid, join};
use crate::hir::TransactionId;
use crate::transaction::ResourceKey;
use crate::transaction::filesystem::{Entry, NodeMetadata, Snapshot};

#[derive(Debug)]
enum Change {
    Data {
        path: PathBuf,
        before: Snapshot,
        after: Snapshot,
    },
    Metadata {
        path: PathBuf,
        before: NodeMetadata,
        after: NodeMetadata,
    },
}

impl Manager {
    /// Import a private view against the exact immutable version from which it
    /// started. Disjoint concurrent results merge; overlapping newer values are
    /// rejected rather than erased by an old full-root snapshot.
    ///
    /// This validates changed values, not the program's unobserved reads. The
    /// caller must still establish read independence or coordinate isolation;
    /// disjoint output filenames alone do not prove serializable execution.
    pub fn stage_view_delta(
        &mut self,
        id: TransactionId,
        path: &Path,
        before: &Snapshot,
        after: &Snapshot,
    ) -> Result<()> {
        self.ready()?;
        let saved = (
            self.store.clone(),
            self.domain.clone(),
            self.pending_roots.clone(),
        );
        let result = self.stage_view_delta_inner(id, path, before, after);
        if result.is_err() {
            (self.store, self.domain, self.pending_roots) = saved;
        }
        result
    }

    pub fn stage_delta(&mut self, id: TransactionId, path: &Path, after: &Snapshot) -> Result<()> {
        let saved = (self.store.clone(), self.domain.clone());
        let result = self
            .read_snapshot(id, path)
            .and_then(|before| self.stage_view_delta(id, path, &before, after));
        if result.is_err() {
            (self.store, self.domain) = saved;
        }
        result
    }

    fn stage_view_delta_inner(
        &mut self,
        id: TransactionId,
        path: &Path,
        before: &Snapshot,
        after: &Snapshot,
    ) -> Result<()> {
        super::validate_names(before.entries())?;
        super::validate_names(after.entries())?;
        let (root, relative) = self.locate(path)?;
        let mut domain = self.domain.clone();
        let scope = domain.key(root, &relative)?;
        self.require_coverage(root, &scope, path)?;
        let old = index(&mut domain, root, &relative, before)?;
        let new = index(&mut domain, root, &relative, after)?;
        let keys = old
            .keys()
            .chain(new.keys())
            .cloned()
            .collect::<BTreeSet<_>>();
        let mut ordered = keys.into_iter().collect::<Vec<_>>();
        ordered.sort_by_key(|key| key.components().len());
        let mut data = Vec::<ResourceKey>::new();
        let mut changes = Vec::new();
        for key in ordered {
            if data.iter().any(|parent| parent.tree().contains(&key)) {
                continue;
            }
            let left = old.get(&key);
            let right = new.get(&key);
            if same_value(left, right).map_err(|cause| io_failure(path, cause))? {
                continue;
            }
            let name = right.or(left).expect("union of existing values");
            let target = join(path, &name.relative);
            if let (Some(a), Some(b)) = (left, right)
                && a.relative == b.relative
                && let (Entry::Directory { metadata: before }, Entry::Directory { metadata: after }) =
                    (&a.entry, &b.entry)
            {
                changes.push(Change::Metadata {
                    path: target,
                    before: before.clone(),
                    after: after.clone(),
                });
                continue;
            }
            let before = self.assemble(subtree(&old, &key), key.components().len(), &target)?;
            let after = self.assemble(subtree(&new, &key), key.components().len(), &target)?;
            changes.push(Change::Data {
                path: target,
                before,
                after,
            });
            data.push(key);
        }
        // Validate every changed value before installing any part of the delta.
        // The read set is retained on success and reverted with the candidate on
        // failure, just like ordinary subtree staging.
        for change in &changes {
            match change {
                Change::Data { path, before, .. } => {
                    let current = self.read_snapshot(id, path)?;
                    if !same_snapshot(before, &current).map_err(|cause| io_failure(path, cause))? {
                        return Err(invalid(
                            path,
                            "private program view overlaps a newer transaction value; retry from the current view",
                        ));
                    }
                }
                Change::Metadata { path, before, .. } => {
                    let current = self.read_snapshot(id, path)?;
                    if !matches!(current.entries().get(Path::new("")), Some(Entry::Directory { metadata }) if same_metadata(metadata, before))
                    {
                        return Err(invalid(
                            path,
                            "private program directory metadata overlaps a newer transaction value",
                        ));
                    }
                }
            }
        }
        self.domain = domain;
        for change in &changes {
            if let Change::Data { path, after, .. } = change {
                self.replace_snapshot(id, path, after)?;
            }
        }
        // Namespace insertion stamps staged parents. The exported program view
        // is authoritative for its final mtime, so apply metadata deltas last.
        for change in changes {
            if let Change::Metadata { path, after, .. } = change {
                self.stage_directory_metadata(id, &path, after)?;
            }
        }
        Ok(())
    }

    fn stage_directory_metadata(
        &mut self,
        id: TransactionId,
        path: &Path,
        metadata: NodeMetadata,
    ) -> Result<()> {
        let (root, relative) = self.locate(path)?;
        let key = self.domain.key(root, &relative)?;
        let Some(mut value) = self.store.read(id, &key)? else {
            return Err(invalid(
                path,
                "directory metadata target does not exist in the transaction",
            ));
        };
        let Entry::Directory { metadata: current } = &mut value.entry else {
            return Err(invalid(path, "directory metadata target changed kind"));
        };
        *current = metadata;
        self.store.write(id, key.clone(), value)?;
        self.pending_roots
            .entry(id)
            .or_default()
            .metadata
            .insert(key);
        Ok(())
    }
}

fn index(
    domain: &mut Domain,
    root: usize,
    relative: &Path,
    snapshot: &Snapshot,
) -> Result<BTreeMap<ResourceKey, ManagedEntry>> {
    snapshot
        .entries()
        .iter()
        .map(|(suffix, entry)| {
            Ok((
                domain.key(root, &join(relative, suffix))?,
                ManagedEntry {
                    relative: suffix.clone(),
                    entry: entry.clone(),
                },
            ))
        })
        .collect()
}

fn subtree(
    entries: &BTreeMap<ResourceKey, ManagedEntry>,
    root: &ResourceKey,
) -> Vec<(ResourceKey, ManagedEntry)> {
    entries
        .iter()
        .filter(|(key, _)| root.tree().contains(key))
        .map(|(key, value)| (key.clone(), value.clone()))
        .collect()
}

fn same_snapshot(left: &Snapshot, right: &Snapshot) -> io::Result<bool> {
    if left.entries().len() != right.entries().len() {
        return Ok(false);
    }
    for (name, entry) in left.entries() {
        let Some(other) = right.entries().get(name) else {
            return Ok(false);
        };
        if !same_entry(entry, other)? {
            return Ok(false);
        }
    }
    Ok(true)
}

fn same_value(left: Option<&ManagedEntry>, right: Option<&ManagedEntry>) -> io::Result<bool> {
    match (left, right) {
        (None, None) => Ok(true),
        (Some(left), Some(right)) if left.relative == right.relative => {
            same_entry(&left.entry, &right.entry)
        }
        _ => Ok(false),
    }
}

pub(super) fn same_entry(left: &Entry, right: &Entry) -> io::Result<bool> {
    if !same_metadata(left.metadata(), right.metadata()) {
        return Ok(false);
    }
    match (left, right) {
        (Entry::Directory { .. }, Entry::Directory { .. }) => Ok(true),
        (
            Entry::File { artifact: left, .. },
            Entry::File {
                artifact: right, ..
            },
        ) if left.len() == right.len() => {
            if left.path() == right.path() {
                return Ok(true);
            }
            let mut left = File::open(left.path())?;
            let mut right = File::open(right.path())?;
            let mut a = [0; 64 * 1024];
            let mut b = [0; 64 * 1024];
            loop {
                let count = left.read(&mut a)?;
                if count == 0 {
                    return Ok(right.read(&mut b)? == 0);
                }
                right.read_exact(&mut b[..count])?;
                if a[..count] != b[..count] {
                    return Ok(false);
                }
            }
        }
        _ => Ok(false),
    }
}

fn same_metadata(left: &NodeMetadata, right: &NodeMetadata) -> bool {
    left.modified == right.modified && left.permissions == right.permissions
}

fn io_failure(path: &Path, cause: io::Error) -> ManagerError {
    ManagerError::Io {
        action: "compare immutable program delta",
        path: path.to_owned(),
        cause,
    }
}
