//! Observe foreign host publications without discarding managed version history.

use std::collections::BTreeMap;
use std::fs;
use std::io;
use std::path::Path;

use super::{
    Coverage, ManagedEntry, Manager, ManagerError, Result, WriteSet, invalid, join, normalize,
    validate_names, validate_snapshot_case_policy,
};
use crate::transaction::filesystem::{Entry, NodeMetadata};
use crate::transaction::{Epoch, ResourceKey};

impl Manager {
    /// Observe externally changed host state at a quiescent session boundary.
    /// All captures and byte comparisons finish before an atomic logical swap.
    /// Changed heads have no managed author, so an older named origin cannot
    /// silently undo an authorized shell/formatter/legacy write.
    ///
    /// Complete coverage observes new/deleted children; sparse namespaces observe
    /// only their directory node and explicitly enrolled complete subtrees. This
    /// never opens an unknown FIFO/socket or upgrades sparse coverage implicitly.
    pub fn refresh_host(&mut self) -> Result<Epoch> {
        self.ready()?;
        if self.namespaces.has_pending() {
            return Err(invalid(
                self.snapshots.root(),
                "finish or cancel live namespace publications before observing host directory state",
            ));
        }
        if !self.active.is_empty() {
            return Err(ManagerError::ActiveTransactions(
                self.active.iter().copied().collect(),
            ));
        }
        if !self.cleanup.is_empty() {
            return Err(ManagerError::Cleanup {
                failures: Vec::new(),
                journals: self.retained_journals(),
            });
        }
        let mut scopes = WriteSet::default();
        for root in &self.domain.roots {
            match &root.coverage {
                Coverage::Complete => {
                    scopes.data.insert(root.key.clone());
                }
                Coverage::Namespace(known) => scopes.data.extend(known.iter().cloned()),
            }
        }
        let scopes = scopes.minimal_data();
        let mut domain = self.domain.clone();
        let mut observed = BTreeMap::<ResourceKey, Option<ManagedEntry>>::new();
        for scope in &scopes {
            let path = self.key_path(&self.store, scope)?;
            let snapshot = self.capture_host(&path)?;
            validate_snapshot_case_policy(&path, &snapshot)?;
            validate_names(snapshot.entries())?;
            for key in self
                .domain
                .keys
                .iter()
                .filter(|key| scope.tree().contains(key))
            {
                observed.insert(key.clone(), None);
            }
            observed.insert(scope.clone(), None);
            for (suffix, entry) in snapshot.entries() {
                let path = join(&path, suffix);
                let key = domain.physical_key(&path)?;
                observed.insert(
                    key,
                    Some(ManagedEntry {
                        relative: path,
                        entry: entry.clone(),
                    }),
                );
            }
            // Physical capture prunes invocation-only shells. A directory the
            // user explicitly published there is nevertheless logically present.
            // Only the retained identity-proved overlay can restore that fact.
            for (key, value) in self.observed_scaffolds(scope)? {
                if observed.get(&key).is_none_or(Option::is_none) {
                    observed.insert(key, Some(value));
                }
            }
        }
        for root in &self.domain.roots {
            if scopes.iter().any(|scope| scope.tree().contains(&root.key)) {
                continue;
            }
            observed.insert(root.key.clone(), self.observe_namespace_node(&root.path)?);
        }
        let mut changed = Vec::new();
        for (key, new) in observed {
            let old = self.store.committed(&key);
            let path = new
                .as_ref()
                .or(old.as_ref())
                .map(|value| value.relative.clone())
                .map(Ok)
                .unwrap_or_else(|| self.key_path(&self.store, &key))?;
            if !same_observation(old.as_ref(), new.as_ref()).map_err(|cause| ManagerError::Io {
                action: "compare foreign filesystem version",
                path,
                cause,
            })? {
                changed.push((key, new));
            }
        }
        let mut candidate = self.store.clone();
        for (key, value) in changed {
            candidate.seed(key.clone(), value)?;
            domain.keys.insert(key);
        }
        self.store = candidate;
        self.domain = domain;
        Ok(self.store.epoch())
    }

    fn observe_namespace_node(&self, path: &Path) -> Result<Option<ManagedEntry>> {
        let path = normalize(path)?;
        let metadata = match fs::symlink_metadata(&path) {
            Ok(metadata) => metadata,
            Err(cause) if cause.kind() == io::ErrorKind::NotFound => return Ok(None),
            Err(cause) => {
                return Err(ManagerError::Io {
                    action: "observe sparse namespace",
                    path,
                    cause,
                });
            }
        };
        if !metadata.is_dir() {
            return Err(invalid(
                &path,
                "an enrolled sparse namespace is no longer a directory; host refresh cannot assume its payload is snapshot data",
            ));
        }
        let metadata = NodeMetadata::capture(&metadata).map_err(|cause| ManagerError::Io {
            action: "observe sparse namespace metadata",
            path: path.clone(),
            cause,
        })?;
        Ok(Some(ManagedEntry {
            relative: path,
            entry: Entry::Directory { metadata },
        }))
    }
}

fn same_observation(old: Option<&ManagedEntry>, new: Option<&ManagedEntry>) -> io::Result<bool> {
    match (old, new) {
        (None, None) => Ok(true),
        (Some(old), Some(new)) if old.relative == new.relative => {
            super::delta::same_entry(&old.entry, &new.entry)
        }
        _ => Ok(false),
    }
}
