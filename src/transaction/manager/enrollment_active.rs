//! Extend finite coverage without changing any active owner's statement snapshot.
//!
//! Lazy ordinary execution need not have registered a Manager namespace first.
//! New ancestor metadata is an exact-node observation, not a replacement of its
//! already-versioned descendants. Data imports still use subtree obligations;
//! both preserve actual write, namespace and complete owner-inventory fences.
use super::{ManagedEntry, Manager, Result, invalid, normalize};
use crate::endpoint::Backend;
use crate::hir::TransactionId;
use crate::hir::effects::{AccessKind, Effects, Resource, Scope};
use crate::transaction::ResourceKey;
use crate::transaction::filesystem::{Entry, NodeMetadata};
use std::collections::{BTreeMap, BTreeSet};
use std::path::Path;
impl Manager {
    pub(crate) fn active_transaction_ids(&self) -> BTreeSet<TransactionId> {
        self.active.clone()
    }
    /// Actual retained implicit-owner effects are supplied by the runtime. No
    /// named/native owner is eligible; public enrollment keeps its global guard.
    pub(crate) fn enroll_disjoint_entry(
        &mut self,
        path: &Path,
        owners: &BTreeMap<TransactionId, Effects>,
    ) -> Result<()> {
        self.verify_import_inventory(path, owners)?;
        let path = normalize(path)?;
        let (root, relative) = self.locate(&path)?;
        let mut domain = self.domain.clone();
        let key = domain.key(root, &relative)?;
        if self.require_coverage(root, &key, &path).is_ok() {
            return Ok(());
        }
        if relative.components().count() != 1
            || self.domain.keys.iter().any(|old| key.tree().contains(old))
        {
            return Err(invalid(
                &path,
                "active enrollment requires one previously unobserved direct child",
            ));
        }
        self.verify_import_scope(&path, &key, &mut domain, owners)?;
        self.verify_import_directory(
            path.parent().expect("direct child has a parent"),
            &domain.key(root, Path::new(""))?,
        )?;
        self.verify_finite_import(&path)?;
        self.enroll_entry_inner(&path)
    }
    /// Import complete finite coverage without refreshing already-versioned
    /// branches. A lazy runtime may have no prior namespace registration.
    pub(crate) fn enroll_disjoint_root(
        &mut self,
        path: &Path,
        owners: &BTreeMap<TransactionId, Effects>,
    ) -> Result<()> {
        self.verify_import_inventory(path, owners)?;
        let path = normalize(path)?;
        if path.file_name().is_none() {
            return Err(invalid(
                &path,
                "a filesystem root cannot be a publication root",
            ));
        }
        let mut domain = self.domain.clone();
        let key = domain.physical_key(&path)?;
        if let Ok((root, relative)) = self.locate(&path) {
            if self.require_coverage(root, &key, &path).is_ok() {
                return Ok(());
            }
            if !relative.as_os_str().is_empty() {
                return Err(invalid(
                    &path,
                    "complete active enrollment requires a namespace root or a new domain",
                ));
            }
        }
        for effects in owners.values() {
            for access in &effects.accesses {
                let namespace_observer = matches!(&access.resource,
                    Resource::File(file) | Resource::StreamPath(file) if file.scope == Scope::Tree)
                    || matches!(
                        &access.resource,
                        Resource::DirectoryMetadata(_) | Resource::NamespaceChange(_)
                    );
                if namespace_observer
                    && self.import_access_overlaps(
                        &mut domain,
                        &access.resource,
                        access.kind,
                        &key,
                        true,
                    )?
                {
                    return Err(invalid(
                        &path,
                        "active owner observes the namespace being completed",
                    ));
                }
            }
        }
        let mut frontier = Vec::new();
        self.import_frontier(&path, &mut domain, &mut frontier)?;
        for (path, key, tree) in frontier {
            if tree {
                self.verify_import_scope(&path, &key, &mut domain, owners)?;
                self.verify_finite_import(&path)?;
            } else {
                self.verify_metadata_import(&path, &key, &mut domain, owners)?;
            }
        }
        self.enroll_inner(&path)
    }

    /// Register only a previously unobserved directory node. Descendant read
    /// pins do not observe it; pending writes and explicit namespace reads do.
    pub(crate) fn enroll_disjoint_namespace(
        &mut self,
        path: &Path,
        owners: &BTreeMap<TransactionId, Effects>,
    ) -> Result<()> {
        self.verify_import_inventory(path, owners)?;
        let path = normalize(path)?;
        let mut domain = self.domain.clone();
        let key = domain.physical_key(&path)?;
        if !self.domain.known(&key) {
            self.verify_metadata_import(&path, &key, &mut domain, owners)?;
        }
        self.enroll_namespace_inner(&path)
    }

    fn import_frontier(
        &self,
        path: &Path,
        domain: &mut super::Domain,
        frontier: &mut Vec<(std::path::PathBuf, ResourceKey, bool)>,
    ) -> Result<()> {
        let key = domain.physical_key(path)?;
        if self.domain.complete(&key) {
            return Ok(());
        }
        let known = self.domain.known(&key);
        let descendants = self.domain.keys.iter().any(|old| key.tree().contains(old));
        if !known && !descendants {
            frontier.push((path.to_owned(), key, true));
            return Ok(());
        }
        if known {
            self.verify_import_directory(path, &key)?;
        } else {
            // An unobserved ancestor contributes its own directory metadata,
            // not a fictitious replacement of already complete descendants.
            frontier.push((path.to_owned(), key, false));
        }
        let mut children = std::fs::read_dir(path)
            .map_err(|cause| super::ManagerError::Io {
                action: "inspect import frontier",
                path: path.to_owned(),
                cause,
            })?
            .map(|entry| entry.map(|entry| entry.path()))
            .collect::<std::io::Result<Vec<_>>>()
            .map_err(|cause| super::ManagerError::Io {
                action: "list import frontier",
                path: path.to_owned(),
                cause,
            })?;
        children.sort();
        for child in children {
            self.import_frontier(&child, domain, frontier)?;
        }
        Ok(())
    }
    fn verify_import_inventory(
        &self,
        path: &Path,
        owners: &BTreeMap<TransactionId, Effects>,
    ) -> Result<()> {
        self.ready()?;
        if owners.keys().copied().collect::<BTreeSet<_>>() != self.active {
            return Err(invalid(
                path,
                "active enrollment requires every actual owner and no extra owner",
            ));
        }
        if owners
            .iter()
            .any(|(id, effects)| effects.unknown_external || !self.store.active_read_committed(*id))
        {
            return Err(invalid(
                path,
                "active import requires bounded read-committed implicit owners",
            ));
        }
        Ok(())
    }
    fn verify_import_scope(
        &self,
        path: &Path,
        key: &ResourceKey,
        domain: &mut super::Domain,
        owners: &BTreeMap<TransactionId, Effects>,
    ) -> Result<()> {
        self.fence_namespace(key, true)?;
        for (id, effects) in owners {
            let dirty = self.pending_roots.get(id).is_some_and(|writes| {
                writes
                    .data
                    .iter()
                    .chain(&writes.metadata)
                    .any(|old| old.tree().contains(key) || key.tree().contains(old))
            });
            let mut intersects = effects.unknown_external;
            for access in &effects.accesses {
                intersects |=
                    self.import_access_overlaps(domain, &access.resource, access.kind, key, true)?;
            }
            if intersects
                || dirty
                || self.namespaces.observes_import(*id, key)
                || !self.store.permits_disjoint_import(*id, key)
            {
                return Err(invalid(
                    path,
                    "active owner observes or may modify this coverage; no new payload was captured",
                ));
            }
        }
        Ok(())
    }

    fn verify_metadata_import(
        &self,
        path: &Path,
        key: &ResourceKey,
        domain: &mut super::Domain,
        owners: &BTreeMap<TransactionId, Effects>,
    ) -> Result<()> {
        self.fence_namespace(key, false)?;
        for (id, effects) in owners {
            let dirty = self.pending_roots.get(id).is_some_and(|writes| {
                writes
                    .data
                    .iter()
                    .chain(&writes.metadata)
                    .any(|old| old.tree().contains(key) || key.tree().contains(old))
            });
            let mut intersects = false;
            for access in &effects.accesses {
                intersects |=
                    self.import_access_overlaps(domain, &access.resource, access.kind, key, false)?;
            }
            if intersects
                || dirty
                || self.namespaces.observes_metadata_import(*id, key)
                || !self.store.permits_metadata_import(*id, key)
            {
                return Err(invalid(
                    path,
                    "active owner observes or may modify this directory metadata",
                ));
            }
        }
        Ok(())
    }
    fn verify_import_directory(&self, path: &Path, key: &ResourceKey) -> Result<()> {
        let Some(ManagedEntry {
            entry: Entry::Directory { metadata: before },
            ..
        }) = self.store.committed(key)
        else {
            return Err(invalid(
                path,
                "active enrollment requires an unchanged committed directory",
            ));
        };
        if self.pending_roots.values().any(|writes| {
            writes.data.iter().any(|write| write.tree().contains(key))
                || writes
                    .metadata
                    .iter()
                    .any(|write| write.tree().contains(key))
        }) {
            return Err(invalid(
                path,
                "active import cannot replace or observe dirty directory metadata",
            ));
        }
        let native = std::fs::symlink_metadata(path).map_err(|cause| super::ManagerError::Io {
            action: "inspect active enrollment directory",
            path: path.to_owned(),
            cause,
        })?;
        let mut metadata =
            NodeMetadata::capture(&native).map_err(|cause| super::ManagerError::Io {
                action: "observe active enrollment directory",
                path: path.to_owned(),
                cause,
            })?;
        if let Some(invocation) = &self.invocation {
            invocation.logical_metadata(path, &mut metadata)?;
        }
        if !native.is_dir()
            || metadata.permissions != before.permissions
            || metadata.modified != before.modified
        {
            return Err(invalid(
                path,
                "directory changed outside the managed view; wait for active work before refreshing it",
            ));
        }
        Ok(())
    }
    fn verify_finite_import(&self, path: &Path) -> Result<()> {
        match std::fs::symlink_metadata(path) {
            Ok(metadata) if metadata.is_file() || metadata.is_dir() => Ok(()),
            Err(cause) if cause.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Ok(_) => Err(invalid(
                path,
                "active enrollment cannot capture a stream or device",
            )),
            Err(cause) => Err(super::ManagerError::Io {
                action: "inspect active enrollment child",
                path: path.to_owned(),
                cause,
            }),
        }
    }
    fn import_access_overlaps(
        &self,
        domain: &mut super::Domain,
        resource: &Resource,
        kind: AccessKind,
        target: &ResourceKey,
        target_tree: bool,
    ) -> Result<bool> {
        let (location, parent_kind_read) = match resource {
            Resource::File(file) | Resource::StreamPath(file) => (&file.location, false),
            Resource::DirectoryMetadata(file) => (
                &file.location,
                kind == AccessKind::Read && file.scope == Scope::Exact,
            ),
            Resource::NamespaceEntry(proof) => (proof.target(), false),
            Resource::NamespaceChange(proof) => (proof.parent(), false),
            Resource::SnapshotFile(_)
            | Resource::Port { .. }
            | Resource::HttpRequests(_)
            | Resource::StreamIdentity { .. } => return Ok(false),
        };
        if matches!(location.backend, Backend::Tcp { .. } | Backend::Http { .. }) {
            return Ok(false);
        }
        if location.backend != Backend::Local || !Path::new(&location.path).is_absolute() {
            return Ok(true);
        }
        // Native name-index comparison supplies negative overlap evidence, not
        // portable guesses about Unicode case folding or verbatim spellings.
        let native = normalize(Path::new(&location.path))?;
        let other = domain.physical_key(&native)?;
        if parent_kind_read && other.tree().contains(target) && other != *target {
            return Ok(false);
        }
        if target_tree {
            return Ok(other.tree().contains(target) || target.tree().contains(&other));
        }
        let resource_tree = matches!(resource,
            Resource::File(file) | Resource::StreamPath(file) if file.scope == Scope::Tree);
        Ok(other == *target
            || resource_tree && other.tree().contains(target)
            || kind != AccessKind::Read
                && (other.tree().contains(target) || target.tree().contains(&other)))
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    use crate::endpoint::Location;
    use crate::hir::effects::Access;
    use crate::hir::effects::FileResource;
    use crate::language::Isolation;
    use crate::transaction::manager::ManagerError;
    use std::fs;

    fn fixture() -> (
        tempfile::TempDir,
        Manager,
        std::path::PathBuf,
        std::path::PathBuf,
    ) {
        fixture_with_namespace(true)
    }

    fn fixture_with_namespace(
        namespace: bool,
    ) -> (
        tempfile::TempDir,
        Manager,
        std::path::PathBuf,
        std::path::PathBuf,
    ) {
        let base = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&base).unwrap();
        let root = tempfile::tempdir_in(base).unwrap();
        let a = root.path().join("a");
        let b = root.path().join("b");
        fs::create_dir(&a).unwrap();
        fs::create_dir(&b).unwrap();
        fs::write(a.join("first"), b"first").unwrap();
        fs::write(a.join("second"), b"second").unwrap();
        fs::create_dir(a.join("tree")).unwrap();
        fs::write(a.join("tree/child"), b"do not capture this tree").unwrap();
        fs::write(b.join("target"), b"old").unwrap();
        let mut manager = Manager::new(&root.path().join("scratch")).unwrap();
        if namespace {
            manager.enroll_namespace(&a).unwrap();
            manager.enroll_entry(&a.join("first")).unwrap();
        }
        manager.enroll(&b.join("target")).unwrap();
        (root, manager, a, b)
    }

    fn file(path: &Path, scope: Scope, kind: AccessKind) -> Access {
        Access {
            resource: Resource::File(FileResource::new(
                Location {
                    backend: Backend::Local,
                    path: path.to_string_lossy().into_owned(),
                },
                scope,
            )),
            kind,
        }
    }

    #[test]
    fn fresh_payload_and_namespace_import_do_not_rebase_an_active_statement() {
        let (_root, mut manager, a, b) = fixture_with_namespace(false);
        let owner = TransactionId(9200);
        manager.begin(owner, Isolation::ReadCommitted).unwrap();
        let held = manager.read_snapshot(owner, &b.join("target")).unwrap();
        let owners = BTreeMap::from([(
            owner,
            Effects {
                accesses: vec![file(&b.join("target"), Scope::Exact, AccessKind::Write)],
                unknown_external: false,
            },
        )]);
        assert!(matches!(
            manager.enroll(&a.join("second")),
            Err(ManagerError::EnrollmentClosed)
        ));
        assert!(matches!(
            manager.enroll_namespace(&a),
            Err(ManagerError::EnrollmentClosed)
        ));
        manager
            .enroll_disjoint_root(&a.join("second"), &owners)
            .unwrap();
        manager.enroll_disjoint_namespace(&a, &owners).unwrap();
        assert!(
            manager
                .read_snapshot(owner, &a.join("second"))
                .unwrap()
                .is_missing()
        );
        assert!(manager.read_entry(owner, &a).unwrap().is_none());
        manager.start_statement(owner).unwrap();
        assert!(manager.read_entry(owner, &a).unwrap().is_some());
        let current = manager.read_snapshot(owner, &a.join("second")).unwrap();
        let Entry::File { artifact, .. } = &current.entries()[Path::new("")] else {
            panic!("not a file")
        };
        assert_eq!(fs::read(artifact.path()).unwrap(), b"second");
        assert!(held.entries().contains_key(Path::new("")));
        manager.finish(owner).unwrap();
    }

    #[test]
    fn fresh_ancestor_import_preserves_a_known_pinned_child() {
        let (_root, mut manager, a, _) = fixture_with_namespace(false);
        manager.enroll(&a.join("first")).unwrap();
        let owner = TransactionId(9201);
        manager.begin(owner, Isolation::ReadCommitted).unwrap();
        let pinned = manager.read_snapshot(owner, &a.join("first")).unwrap();
        let owners = BTreeMap::from([(
            owner,
            Effects {
                accesses: vec![file(&a.join("first"), Scope::Exact, AccessKind::Read)],
                unknown_external: false,
            },
        )]);
        fs::write(a.join("first"), b"external edit").unwrap();
        manager.enroll_disjoint_root(&a, &owners).unwrap();
        let Entry::File { artifact, .. } = &pinned.entries()[Path::new("")] else {
            panic!("not a file")
        };
        assert_eq!(fs::read(artifact.path()).unwrap(), b"first");
        manager.start_statement(owner).unwrap();
        let current = manager.read_snapshot(owner, &a).unwrap();
        let Entry::File { artifact, .. } = &current.entries()[Path::new("first")] else {
            panic!("not a file")
        };
        assert_eq!(fs::read(artifact.path()).unwrap(), b"first");
        assert!(current.entries().contains_key(Path::new("second")));
        assert!(current.entries().contains_key(Path::new("tree/child")));
        manager.finish(owner).unwrap();
    }

    #[test]
    fn fresh_metadata_import_requires_actual_read_and_owner_fences() {
        for mode in 0..8 {
            let (_root, mut manager, a, _) = fixture_with_namespace(false);
            let owner = TransactionId(9202);
            if mode == 7 {
                manager.enroll(&a.join("first")).unwrap();
            }
            manager
                .begin(
                    owner,
                    if mode == 6 {
                        Isolation::RepeatableRead
                    } else {
                        Isolation::ReadCommitted
                    },
                )
                .unwrap();
            let mut owners = BTreeMap::from([(owner, Effects::default())]);
            match mode {
                0 => {
                    owners.clear();
                }
                1 => {
                    owners.get_mut(&owner).unwrap().unknown_external = true;
                }
                2 => {
                    owners.get_mut(&owner).unwrap().accesses.push(file(
                        &a,
                        Scope::Tree,
                        AccessKind::Read,
                    ));
                }
                3 => {
                    owners.get_mut(&owner).unwrap().accesses.push(Access {
                        resource: Resource::DirectoryMetadata(FileResource::new(
                            Location {
                                backend: Backend::Local,
                                path: a.to_string_lossy().into_owned(),
                            },
                            Scope::Exact,
                        )),
                        kind: AccessKind::Read,
                    });
                }
                4 => {
                    let key = manager.domain.physical_key(&a).unwrap();
                    manager.store.read(owner, &key).unwrap();
                    // RC observations live in the Manager namespace ledger;
                    // Store's read-validation set is serializable-only.
                    manager.record_namespace_read(owner, 0, key, false).unwrap();
                }
                5 => {
                    let key = manager.domain.physical_key(&a).unwrap();
                    manager.store.delete(owner, key).unwrap();
                }
                7 => {
                    let key = manager.domain.physical_key(&a.join("first")).unwrap();
                    manager.store.delete(owner, key).unwrap();
                }
                _ => {}
            }
            let epoch = manager.epoch();
            assert!(
                manager.enroll_disjoint_namespace(&a, &owners).is_err(),
                "mode {mode}"
            );
            assert_eq!(manager.epoch(), epoch);
            manager.rollback_pending(owner).unwrap();
            manager.finish(owner).unwrap();
        }
    }

    #[test]
    fn disjoint_import_keeps_old_statement_and_parent_version_unchanged() {
        let (_root, mut manager, a, b) = fixture();
        let owner = TransactionId(9100);
        manager.begin(owner, Isolation::ReadCommitted).unwrap();
        let first = manager.read_snapshot(owner, &a.join("first")).unwrap();
        let parent = manager
            .read_entry(owner, &a)
            .unwrap()
            .unwrap()
            .metadata()
            .modified;
        let effects = Effects {
            accesses: vec![
                file(&a.join("first"), Scope::Exact, AccessKind::Read),
                file(&b.join("target"), Scope::Exact, AccessKind::Write),
                Access {
                    resource: Resource::StreamPath(FileResource::new(
                        Location {
                            backend: Backend::Tcp {
                                host: "127.0.0.1".into(),
                                port: 12345,
                            },
                            path: "/".into(),
                        },
                        Scope::Exact,
                    )),
                    kind: AccessKind::Consume,
                },
            ],
            unknown_external: false,
        };
        let owners = BTreeMap::from([(owner, effects)]);
        assert!(matches!(
            manager.enroll_entry(&a.join("second")),
            Err(ManagerError::EnrollmentClosed)
        ));
        manager
            .enroll_disjoint_entry(&a.join("second"), &owners)
            .unwrap();
        assert!(
            manager
                .read_snapshot(owner, &a.join("second"))
                .unwrap()
                .is_missing()
        );
        manager.start_statement(owner).unwrap();
        assert!(
            !manager
                .read_snapshot(owner, &a.join("second"))
                .unwrap()
                .is_missing()
        );
        assert_eq!(
            manager
                .read_entry(owner, &a)
                .unwrap()
                .unwrap()
                .metadata()
                .modified,
            parent
        );
        assert_eq!(fs::metadata(&a).unwrap().modified().unwrap(), parent);
        assert!(first.entries().contains_key(Path::new("")));
        manager.finish(owner).unwrap();
    }

    #[test]
    fn incomplete_unknown_overlapping_and_repeatable_owners_are_rejected() {
        for mode in 0..6 {
            let (_root, mut manager, a, _) = fixture();
            let owner = TransactionId(9101);
            manager
                .begin(
                    owner,
                    match mode {
                        3 => Isolation::RepeatableRead,
                        5 => Isolation::Serializable,
                        _ => Isolation::ReadCommitted,
                    },
                )
                .unwrap();
            let mut owners = BTreeMap::new();
            if mode != 0 {
                owners.insert(
                    owner,
                    Effects {
                        unknown_external: mode == 1,
                        accesses: if mode == 2 {
                            vec![file(&a.join("second"), Scope::Exact, AccessKind::Read)]
                        } else if mode == 4 {
                            vec![file(&a, Scope::Tree, AccessKind::Read)]
                        } else {
                            Vec::new()
                        },
                    },
                );
            }
            let epoch = manager.epoch();
            assert!(
                manager
                    .enroll_disjoint_entry(&a.join("second"), &owners)
                    .is_err(),
                "mode {mode}"
            );
            assert_eq!(manager.epoch(), epoch);
            manager.finish(owner).unwrap();
        }
    }

    #[test]
    fn finite_unobserved_directory_import_uses_current_statement_visibility() {
        let (_root, mut manager, a, _) = fixture();
        let owner = TransactionId(9103);
        manager.begin(owner, Isolation::ReadCommitted).unwrap();
        let before = fs::read_dir(manager.artifact_root()).unwrap().count();
        let owners = BTreeMap::from([(owner, Effects::default())]);
        manager
            .enroll_disjoint_entry(&a.join("tree"), &owners)
            .unwrap();
        assert_eq!(
            fs::read_dir(manager.artifact_root()).unwrap().count(),
            before + 1
        );
        assert!(
            manager
                .read_snapshot(owner, &a.join("tree"))
                .unwrap()
                .is_missing()
        );
        manager.start_statement(owner).unwrap();
        assert!(
            manager
                .read_snapshot(owner, &a.join("tree"))
                .unwrap()
                .entries()
                .contains_key(Path::new("child"))
        );
        manager.finish(owner).unwrap();
    }

    #[test]
    fn complete_namespace_upgrade_preserves_known_values_and_old_views() {
        let (_root, mut manager, a, b) = fixture();
        let owner = TransactionId(9104);
        manager.begin(owner, Isolation::ReadCommitted).unwrap();
        let first_pin = manager.read_snapshot(owner, &a.join("first")).unwrap();
        let owners = BTreeMap::from([(
            owner,
            Effects {
                accesses: vec![
                    file(&a.join("first"), Scope::Exact, AccessKind::Read),
                    file(&b.join("target"), Scope::Exact, AccessKind::Write),
                ],
                unknown_external: false,
            },
        )]);
        fs::write(a.join("first"), b"edits").unwrap();
        let epoch = manager.epoch();
        manager.enroll_disjoint_root(&a, &owners).unwrap();
        assert!(manager.epoch() > epoch);
        let old = manager.read_snapshot(owner, &a).unwrap();
        assert!(old.entries().contains_key(Path::new("first")));
        assert!(!old.entries().contains_key(Path::new("second")));
        let Entry::File { artifact, .. } = &old.entries()[Path::new("first")] else {
            panic!("not a file")
        };
        assert_eq!(fs::read(artifact.path()).unwrap(), b"first");
        assert!(first_pin.entries().contains_key(Path::new("")));
        manager.start_statement(owner).unwrap();
        let current = manager.read_snapshot(owner, &a).unwrap();
        assert!(current.entries().contains_key(Path::new("second")));
        assert!(current.entries().contains_key(Path::new("tree/child")));
        manager.finish(owner).unwrap();
    }

    #[test]
    fn complete_import_does_not_waive_a_future_tree_reader() {
        let (_root, mut manager, a, _) = fixture();
        let owner = TransactionId(9105);
        manager.begin(owner, Isolation::ReadCommitted).unwrap();
        let owners = BTreeMap::from([(
            owner,
            Effects {
                accesses: vec![file(&a, Scope::Tree, AccessKind::Read)],
                unknown_external: false,
            },
        )]);
        let epoch = manager.epoch();
        assert!(manager.enroll_disjoint_root(&a, &owners).is_err());
        assert_eq!(manager.epoch(), epoch);
        manager.finish(owner).unwrap();
    }

    #[test]
    fn complete_import_without_new_keys_still_fences_namespace_observers() {
        let (_root, mut manager, a, _) = fixture();
        manager.enroll_entry(&a.join("second")).unwrap();
        manager.enroll_entry(&a.join("tree")).unwrap();
        let owner = TransactionId(9106);
        manager.begin(owner, Isolation::ReadCommitted).unwrap();
        let owners = BTreeMap::from([(
            owner,
            Effects {
                accesses: vec![file(&a, Scope::Tree, AccessKind::Read)],
                unknown_external: false,
            },
        )]);
        assert!(manager.enroll_disjoint_root(&a, &owners).is_err());
        manager.finish(owner).unwrap();
    }

    #[test]
    fn dirty_parent_and_changed_native_parent_cannot_be_hidden_by_effects() {
        for dirty in [true, false] {
            let (root, mut manager, a, _) = fixture();
            let owner = TransactionId(9102);
            manager.begin(owner, Isolation::ReadCommitted).unwrap();
            if dirty {
                let first = manager.read_snapshot(owner, &a.join("first")).unwrap();
                manager
                    .replace_snapshot(owner, &a.join("first"), &first)
                    .unwrap();
            } else {
                fs::write(a.join("foreign"), b"external namespace change").unwrap();
            }
            let owners = BTreeMap::from([(owner, Effects::default())]);
            let epoch = manager.epoch();
            assert!(
                manager
                    .enroll_disjoint_entry(&a.join("second"), &owners)
                    .is_err()
            );
            assert_eq!(manager.epoch(), epoch);
            manager.rollback_pending(owner).unwrap();
            manager.finish(owner).unwrap();
            drop(root);
        }
    }
}
