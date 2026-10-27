//! Extend immutable coverage without refreshing already versioned branches.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

use super::{Domain, Manager, ManagerError, Result, invalid, join, normalize, relative_to};
use crate::transaction::filesystem::{Entry, NodeMetadata, Snapshot};

impl Manager {
    pub(super) fn capture_extension(&self, path: &Path) -> Result<Snapshot> {
        if let Some(invocation) = &self.invocation
            && invocation.contains_private(path)?
        {
            return Err(invalid(
                path,
                "owned invocation data is not a user resource",
            ));
        }
        let mut domain = self.domain.clone();
        let prefix = domain.physical_key(path)?;
        let mut entries = BTreeMap::new();
        for key in self
            .domain
            .keys
            .iter()
            .filter(|key| prefix.tree().contains(key))
        {
            if let Some(value) = self.store.committed(key) {
                let relative = relative_to(&value.relative, path).ok_or_else(|| {
                    invalid(
                        path,
                        "versioned native spelling is outside its resource prefix",
                    )
                })?;
                entries.insert(relative, value.entry);
            }
        }
        self.capture_unknown(path, path, &mut domain, &mut entries)?;
        // Historical keys hidden beneath a deleted/file parent must not become
        // reachable merely because a broader read descriptor is registered.
        let mut reachable = BTreeMap::new();
        for (relative, entry) in entries {
            if relative.as_os_str().is_empty()
                || matches!(
                    reachable.get(relative.parent().unwrap_or_else(|| Path::new(""))),
                    Some(Entry::Directory { .. })
                )
            {
                reachable.insert(relative, entry);
            }
        }
        Ok(match &self.invocation {
            Some(invocation) => self
                .snapshots
                .from_entries_in(reachable, path, invocation)?,
            None => self.snapshots.from_entries(reachable)?,
        })
    }

    fn capture_unknown(
        &self,
        base: &Path,
        path: &Path,
        domain: &mut Domain,
        entries: &mut BTreeMap<PathBuf, Entry>,
    ) -> Result<()> {
        let key = domain.physical_key(path)?;
        if self.domain.complete(&key) {
            return Ok(());
        }
        if let Some(invocation) = &self.invocation
            && invocation.contains_private(path)?
        {
            return Ok(());
        }
        let relative = relative_to(path, base).expect("recursive child of capture root");
        if !self
            .domain
            .keys
            .iter()
            .any(|known| key.tree().contains(known))
        {
            let snapshot = self.capture_host(path)?;
            for (suffix, entry) in snapshot.entries() {
                entries.insert(join(&relative, suffix), entry.clone());
            }
            return Ok(());
        }
        // Only ancestors of previously enrolled nodes need a partial walk.
        // Unknown branches still use the filesystem adapter's strict capture.
        let path = normalize(path)?;
        let metadata = fs::symlink_metadata(&path).map_err(|cause| ManagerError::Io {
            action: "inspect read-domain extension",
            path: path.clone(),
            cause,
        })?;
        if !metadata.is_dir() {
            return Err(invalid(
                &path,
                "an ancestor of existing coverage is no longer a directory",
            ));
        }
        if let std::collections::btree_map::Entry::Vacant(slot) = entries.entry(relative) {
            let metadata = NodeMetadata::capture(&metadata).map_err(|cause| ManagerError::Io {
                action: "capture read-domain extension metadata",
                path: path.clone(),
                cause,
            })?;
            slot.insert(Entry::Directory { metadata });
        }
        let children = fs::read_dir(&path).map_err(|cause| ManagerError::Io {
            action: "enumerate read-domain extension",
            path: path.clone(),
            cause,
        })?;
        for child in children {
            let child = child.map_err(|cause| ManagerError::Io {
                action: "enumerate read-domain extension child",
                path: path.clone(),
                cause,
            })?;
            self.capture_unknown(base, &child.path(), domain, entries)?;
        }
        Ok(())
    }
}
