//! Path traversal in the selected immutable version, not the live host tree.

use std::path::{Component, Path, PathBuf};

use super::ManagerError;
#[cfg(windows)]
use super::validate_case_policy;
use super::{Domain, ManagedEntry, Manager, Result, invalid, join, relative_to};
use crate::hir::TransactionId;
use crate::transaction::Store;
use crate::transaction::filesystem::Entry;

impl Manager {
    /// Resolve a name after its producer gate. Missing ordinary components may
    /// name future destination parents; traversing a file or missing `..` is
    /// invalid. This does not assert that the final source exists.
    pub fn resolve_path(&mut self, id: TransactionId, raw: &Path) -> Result<PathBuf> {
        self.open()?;
        if !raw.is_absolute() {
            return Err(invalid(raw, "selected-view paths must be absolute"));
        }
        let (root, relative) = self
            .enrolled_location(raw)
            .ok_or_else(|| invalid(raw, "selected-view path is outside the enrolled domain"))?;
        let base = self.domain.roots[root].path.clone();
        let mut domain = self.domain.clone();
        let mut candidate = self.store.clone();
        let mut prefix = PathBuf::new();
        self.check_managed_path(&base)?;
        // Even resolving the root requires a real active transaction.
        candidate.read(id, &domain.key(root, &prefix)?)?;
        for part in relative.components() {
            let key = domain.key(root, &prefix)?;
            let value = candidate.read(id, &key)?;
            let directory = matches!(
                value,
                Some(ManagedEntry {
                    entry: Entry::Directory { .. },
                    ..
                })
            );
            if let Some(value) = &value {
                prefix = relative_to(&value.relative, &base).ok_or_else(|| {
                    invalid(
                        &value.relative,
                        "selected node spelling is outside its enrolled root",
                    )
                })?;
            }
            match part {
                Component::Normal(name) if directory || value.is_none() => prefix.push(name),
                Component::ParentDir if directory && prefix.pop() => {}
                Component::CurDir if directory => {}
                _ => {
                    return Err(invalid(
                        &join(&base, &prefix),
                        "path traversal requires a directory in the selected transaction view and cannot leave its enrolled root",
                    ));
                }
            }
            let path = join(&base, &prefix);
            self.check_managed_path(&path)?;
            let key = domain.key(root, &prefix)?;
            if !prefix.as_os_str().is_empty() {
                self.require_coverage(root, &key, &path)?;
            }
        }
        let prefix = selected_relative(&mut domain, &mut candidate, id, root, &prefix)?;
        let path = join(&base, &prefix);
        validate_case_ancestors(&path)?;
        self.domain = domain;
        self.store = candidate;
        Ok(path)
    }

    pub(super) fn check_managed_path(&self, path: &Path) -> Result<()> {
        if let Some(invocation) = &self.invocation {
            invocation.verify()?;
            if relative_to(path, invocation.path()).is_some() {
                return Err(invalid(
                    path,
                    "owned invocation data is not a user resource",
                ));
            }
            // Preserve native alias protection without making a future child
            // depend on the old host ancestor being a directory.
            if invocation.contains_private(reachable_host_ancestor(path)?)? {
                return Err(invalid(
                    path,
                    "owned invocation data is not a user resource",
                ));
            }
        }
        Ok(())
    }
}

pub(super) fn selected_relative(
    domain: &mut Domain,
    store: &mut Store<ManagedEntry>,
    id: TransactionId,
    root: usize,
    relative: &Path,
) -> Result<PathBuf> {
    let key = domain.key(root, relative)?;
    if let Some(value) = store.read(id, &key)? {
        return relative_to(&value.relative, &domain.roots[root].path).ok_or_else(|| {
            invalid(
                &value.relative,
                "selected node spelling is outside its enrolled root",
            )
        });
    }
    let Some(parent) = relative.parent() else {
        return Ok(relative.to_owned());
    };
    let parent = domain.key(root, parent)?;
    let Some(parent) = store.read(id, &parent)? else {
        return Ok(relative.to_owned());
    };
    let parent = relative_to(&parent.relative, &domain.roots[root].path).ok_or_else(|| {
        invalid(
            &parent.relative,
            "selected parent spelling is outside its enrolled root",
        )
    })?;
    Ok(join(
        &parent,
        Path::new(
            relative
                .file_name()
                .ok_or_else(|| invalid(relative, "selected child requires a basename"))?,
        ),
    ))
}

#[cfg(windows)]
pub(super) fn validate_case_ancestors(path: &Path) -> Result<()> {
    // A selected-view directory may still be a file in the older host version.
    // Check host policy on its nearest reachable ancestor, not its logical kind.
    validate_case_policy(reachable_host_ancestor(path)?)
}

fn reachable_host_ancestor(path: &Path) -> Result<&Path> {
    for ancestor in path.ancestors() {
        match std::fs::symlink_metadata(ancestor) {
            Ok(_) => return Ok(ancestor),
            Err(cause)
                if matches!(
                    cause.kind(),
                    std::io::ErrorKind::NotFound | std::io::ErrorKind::NotADirectory
                ) => {}
            Err(cause) => {
                return Err(ManagerError::Io {
                    action: "inspect managed native guard ancestor",
                    path: ancestor.to_owned(),
                    cause,
                });
            }
        }
    }
    Err(invalid(
        path,
        "managed path has no reachable native guard ancestor",
    ))
}

#[cfg(not(windows))]
pub(super) fn validate_case_ancestors(_: &Path) -> Result<()> {
    Ok(())
}
