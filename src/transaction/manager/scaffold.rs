//! Publish logical user entries without exchanging an owned invocation ancestor.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

use super::{Domain, Manager, Result, WriteSet, invalid, join, relative_to};
use crate::transaction::filesystem::{Entry, NodeMetadata, Snapshot};
use crate::transaction::{ResourceKey, Store};

pub(super) struct Layout {
    pub roots: Vec<ResourceKey>,
    pub views: Vec<(PathBuf, Snapshot)>,
    pub metadata: BTreeMap<ResourceKey, NodeMetadata>,
    pub baselines: BTreeMap<ResourceKey, NodeMetadata>,
}

impl Manager {
    pub(super) fn observed_scaffolds(
        &self,
        scope: &ResourceKey,
    ) -> Result<Vec<(ResourceKey, super::ManagedEntry)>> {
        let mut nodes = Vec::new();
        for key in self
            .scaffold_baselines
            .keys()
            .filter(|key| scope.tree().contains(key))
        {
            let Some(mut value) = self.store.committed(key) else {
                continue;
            };
            if !matches!(value.entry, Entry::Directory { .. }) {
                continue;
            }
            if let Some(metadata) = self.owned_scaffold(&value.relative)? {
                value.entry = Entry::Directory { metadata };
                nodes.push((key.clone(), value));
            }
        }
        Ok(nodes)
    }

    pub(super) fn scaffold_publication(
        &self,
        candidate: &Store<super::ManagedEntry>,
        writes: &WriteSet,
    ) -> Result<Layout> {
        let mut layout = Layout {
            roots: Vec::new(),
            views: Vec::new(),
            metadata: BTreeMap::new(),
            baselines: self.scaffold_baselines.clone(),
        };
        let mut domain = self.domain.clone();
        for key in writes.minimal_data() {
            let path = self.key_path(candidate, &key)?;
            let after = self.committed_snapshot(candidate, &key)?;
            if self.owned_scaffold(&path)?.is_none() {
                layout.roots.push(key);
                layout.views.push((path, after));
                continue;
            }
            let before = self.committed_snapshot(&self.store, &key)?;
            self.expand_scaffold(key, path, &before, &after, &mut domain, &mut layout)?;
        }
        Ok(layout)
    }

    fn expand_scaffold(
        &self,
        key: ResourceKey,
        path: PathBuf,
        before: &Snapshot,
        after: &Snapshot,
        domain: &mut Domain,
        layout: &mut Layout,
    ) -> Result<()> {
        let owned = self.owned_scaffold(&path)?;
        let Some(native) = owned else {
            layout.roots.push(key);
            layout.views.push((path, after.clone()));
            return Ok(());
        };
        if (!before.is_missing() && !before.is_directory())
            || (!after.is_missing() && !after.is_directory())
        {
            return Err(invalid(
                &path,
                "an owned invocation scaffold supports logical directory entries, not replacement by a file",
            ));
        }
        let baseline = layout
            .baselines
            .entry(key.clone())
            .or_insert(native)
            .clone();
        let desired = after
            .entries()
            .get(Path::new(""))
            .map(Entry::metadata)
            .cloned()
            .unwrap_or(baseline);
        layout.metadata.insert(key, desired);
        let children = scaffold_children(domain, &path, before, after)?;
        for (key, (old_name, new_name)) in children {
            let name = new_name
                .as_ref()
                .or(old_name.as_ref())
                .expect("union of visible children");
            let target = join(&path, name);
            let old = self.scaffold_child(before, old_name.as_deref(), &target)?;
            let new = self.scaffold_child(after, new_name.as_deref(), &target)?;
            self.expand_scaffold(key, target, &old, &new, domain, layout)?;
        }
        Ok(())
    }

    fn owned_scaffold(&self, path: &Path) -> Result<Option<NodeMetadata>> {
        let Some(invocation) = &self.invocation else {
            return Ok(None);
        };
        // This is only a cheap negative filter. Permission to decompose still
        // comes exclusively from the invocation's created-directory identity.
        if relative_to(invocation.path(), path).is_none() {
            return Ok(None);
        }
        Ok(invocation.scaffold_directory(path)?)
    }

    fn scaffold_child(
        &self,
        snapshot: &Snapshot,
        prefix: Option<&Path>,
        logical: &Path,
    ) -> Result<Snapshot> {
        let mut entries = BTreeMap::new();
        if let Some(prefix) = prefix {
            for (path, entry) in snapshot.entries() {
                if let Ok(suffix) = path.strip_prefix(prefix) {
                    entries.insert(suffix.to_owned(), entry.clone());
                }
            }
        }
        Ok(match &self.invocation {
            Some(invocation) => self
                .snapshots
                .from_entries_in(entries, logical, invocation)?,
            None => self.snapshots.from_entries(entries)?,
        })
    }
}

type Children = BTreeMap<ResourceKey, (Option<PathBuf>, Option<PathBuf>)>;

fn scaffold_children(
    domain: &mut Domain,
    base: &Path,
    before: &Snapshot,
    after: &Snapshot,
) -> Result<Children> {
    let mut children = Children::new();
    for path in before
        .entries()
        .keys()
        .filter(|path| path.components().count() == 1)
    {
        children
            .entry(domain.physical_key(&join(base, path))?)
            .or_default()
            .0 = Some(path.clone());
    }
    for path in after
        .entries()
        .keys()
        .filter(|path| path.components().count() == 1)
    {
        children
            .entry(domain.physical_key(&join(base, path))?)
            .or_default()
            .1 = Some(path.clone());
    }
    Ok(children)
}
