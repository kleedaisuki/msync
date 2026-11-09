use std::collections::{BTreeSet, HashMap, HashSet};
use std::sync::Arc;

use crate::endpoint::{Backend, Location};
use crate::hir::effects::{AccessKind, Effects, FileResource, Files, Resource, Resources, Scope};
use crate::hir::graph::PlanningContext;
use crate::plan::FileClass;

#[derive(Clone, Default)]
pub(super) struct State {
    events: Vec<Event>,
    event_groups: HashMap<Location, Vec<usize>>,
    event_index: Arc<Files>,
    sequence: usize,
    unknown: bool,
    pending_writes: Vec<PendingWrite>,
    pending_index: Arc<Resources>,
    managed: HashSet<Location>,
    independent: Arc<HashSet<(Location, Location)>>,
}

#[derive(Clone)]
struct PendingWrite {
    resource: Resource,
    managed: bool,
}

#[derive(Clone)]
struct Event {
    resource: Arc<FileResource>,
    tree: bool,
    kind: FileClass,
    sequence: usize,
}

impl State {
    pub(super) fn record_metrics(&self, span: &mut crate::telemetry::SpanGuard) {
        span.metric("postcondition_updates", self.sequence as u64);
        span.metric("postcondition_groups", self.events.len() as u64);
        span.metric("pending_writes", self.pending_writes.len() as u64);
    }

    pub(super) fn initial(context: &PlanningContext) -> Self {
        let mut state = Self::default();
        // Process IDs establish port availability, not filesystem snapshots.
        // Current-block observations remain useful under explicitly async work;
        // dispatch and publication still validate unmanaged filesystem races.
        for effects in &context.pending_effects {
            state.invalidate_writes(effects);
        }
        state
    }

    pub(super) fn invalidate_writes(&mut self, effects: &Effects) {
        self.unknown |= effects.unknown_external;
        for access in &effects.accesses {
            if access.kind == AccessKind::Write
                && matches!(
                    access.resource,
                    Resource::File(_)
                        | Resource::DirectoryEntry(_)
                        | Resource::DirectoryEntries(_)
                        | Resource::DirectoryMetadata(_)
                )
            {
                self.insert_pending(access.resource.clone(), false);
            }
        }
    }

    fn insert_pending(&mut self, resource: Resource, managed: bool) {
        Arc::make_mut(&mut self.pending_index).insert(&resource, self.pending_writes.len());
        self.pending_writes.push(PendingWrite { resource, managed });
    }

    pub(super) fn defer_write(&mut self, location: Location) {
        if self.managed.insert(location.clone()) {
            self.insert_pending(
                Resource::File(FileResource::new(location, Scope::Tree)),
                true,
            );
        }
    }

    pub(super) fn extend_independent(&mut self, pairs: &HashSet<(Location, Location)>) {
        Arc::make_mut(&mut self.independent).extend(pairs.iter().cloned());
    }

    pub(super) fn separation_candidates(&self, location: &Location) -> Option<Vec<Location>> {
        if self.unknown || location.backend != Backend::Wsl {
            return None;
        }
        let reader = Resource::File(FileResource::new(location.clone(), Scope::Exact));
        let mut candidates = Vec::new();
        let mut indices = BTreeSet::new();
        self.pending_index.candidates(&reader, &mut indices);
        for index in indices {
            let write = &self.pending_writes[index];
            if !write.resource.may_overlap(&reader) {
                continue;
            }
            let Resource::File(file) = &write.resource else {
                return None;
            };
            if !write.managed || file.location.backend != Backend::Local {
                return None;
            }
            candidates.push(file.location.clone());
        }
        (!candidates.is_empty()).then_some(candidates)
    }

    fn independent(&self, left: &Location, right: &Location) -> bool {
        if self.independent.is_empty() || left.backend == right.backend {
            return false;
        }
        self.independent.contains(&(left.clone(), right.clone()))
            || self.independent.contains(&(right.clone(), left.clone()))
    }

    pub(super) fn host_is_stable(&self, location: &Location) -> bool {
        if self.unknown {
            return false;
        }
        if !self.pending_writes.is_empty()
            && location
                .path
                .replace('\\', "/")
                .split('/')
                .any(|part| part == "..")
        {
            return false;
        }
        let resource = Resource::File(FileResource::new(location.clone(), Scope::Exact));
        let mut indices = BTreeSet::new();
        self.pending_index.candidates(&resource, &mut indices);
        !indices.into_iter().any(|index| {
            let write = &self.pending_writes[index];
            !(write.managed && matches!(&write.resource, Resource::File(file) if self.independent(&file.location, location)))
                && write.resource.may_overlap(&resource)
        })
    }

    pub(super) fn unknown(&mut self) {
        self.events.clear();
        self.event_groups.clear();
        self.event_index = Arc::default();
        self.sequence = 0;
        self.independent = Arc::default();
        self.unknown = true;
    }

    pub(super) fn write(&mut self, location: Location, tree: bool, kind: FileClass) {
        let groups = self.event_groups.entry(location.clone()).or_default();
        if let Some(&index) = groups
            .iter()
            .find(|&&index| self.events[index].tree == tree && self.events[index].kind == kind)
        {
            // Identical events either return the same answer or both skip for
            // every query. Only their most recent position can influence class.
            self.events[index].sequence = self.sequence;
            self.sequence += 1;
            return;
        }
        let resource = Arc::new(FileResource::new(location, Scope::Exact));
        let index = self.events.len();
        Arc::make_mut(&mut self.event_index).insert(&resource, index);
        groups.push(index);
        self.events.push(Event {
            resource,
            tree,
            kind,
            sequence: self.sequence,
        });
        self.sequence += 1;
    }

    pub(super) fn class(
        &self,
        location: &Location,
        initial: &HashMap<Location, FileClass>,
    ) -> FileClass {
        let resource = FileResource::new(location.clone(), Scope::Exact);
        let mut indices = BTreeSet::new();
        if location.path.split(['/', '\\']).any(|part| part == "..") {
            // Classification preserves raw traversal postconditions even when
            // normalized alias keys cancel a component (a/../b still crosses a).
            indices.extend(0..self.events.len());
        } else {
            self.event_index.candidates(&resource, &mut indices);
        }
        let mut events = indices
            .into_iter()
            .map(|index| &self.events[index])
            .collect::<Vec<_>>();
        events.sort_unstable_by_key(|event| std::cmp::Reverse(event.sequence));
        for event in events {
            let event_location = &event.resource.location;
            if self.independent(location, event_location) {
                continue;
            }
            if event_location == location {
                return event.kind;
            }
            if descendant(location, event_location)
                && (event.tree || matches!(event.kind, FileClass::Missing | FileClass::Regular))
            {
                return match event.kind {
                    FileClass::Missing | FileClass::Regular => FileClass::Missing,
                    _ => FileClass::Unknown,
                };
            }
            // A successful child assignment does not turn its ancestors into a
            // different kind. Explicit parent postconditions are recorded first.
            if descendant(event_location, location) {
                continue;
            }
            if descendant(location, event_location)
                && !event.tree
                && matches!(event.kind, FileClass::Directory)
            {
                continue;
            }
            if resource.may_overlap(&event.resource) {
                return FileClass::Unknown;
            }
        }
        // Host observations precede pending native imports. Own ordered writes
        // above are postconditions after their gates, not those stale snapshots.
        if !self.host_is_stable(location) {
            FileClass::Unknown
        } else {
            initial.get(location).copied().unwrap_or(FileClass::Unknown)
        }
    }
}

fn descendant(child: &Location, parent: &Location) -> bool {
    if child.backend != parent.backend {
        return false;
    }
    if child.backend == Backend::Local {
        return std::path::Path::new(&child.path)
            .strip_prefix(std::path::Path::new(&parent.path))
            .is_ok_and(|suffix| !suffix.as_os_str().is_empty());
    }
    child
        .path
        .strip_prefix(parent.path.trim_end_matches('/'))
        .is_some_and(|suffix| suffix.starts_with('/'))
}

#[cfg(test)]
mod namespace_tests {
    use super::*;
    use crate::hir::effects::Access;

    #[test]
    fn cloned_events_share_exact_resources_and_preserve_postconditions() {
        let root = Location {
            backend: Backend::Local,
            path: "/state-cache".into(),
        };
        let child = Location {
            backend: Backend::Local,
            path: "/state-cache/child".into(),
        };
        let initial = HashMap::from([
            (root.clone(), FileClass::Directory),
            (child.clone(), FileClass::Regular),
        ]);

        let mut state = State::default();
        state.write(root.clone(), false, FileClass::Regular);
        assert_eq!(state.class(&child, &initial), FileClass::Missing);
        state.write(root.clone(), true, FileClass::Directory);
        let snapshot = state.clone();
        for (event, cloned) in state.events.iter().zip(&snapshot.events) {
            assert_eq!(event.resource.scope, Scope::Exact);
            assert!(Arc::ptr_eq(&event.resource, &cloned.resource));
        }
        assert_eq!(snapshot.class(&root, &initial), FileClass::Directory);
        assert_eq!(snapshot.class(&child, &initial), FileClass::Unknown);
    }

    #[test]
    fn managed_namespace_separation_does_not_waive_native_or_opaque_writers() {
        let native = Location {
            backend: Backend::Local,
            path: "C:/target".into(),
        };
        let wsl = Location {
            backend: Backend::Wsl,
            path: "/home/test/source".into(),
        };
        let pairs = HashSet::from([(native.clone(), wsl.clone())]);
        let mut state = State::default();
        state.defer_write(native.clone());
        assert!(!state.host_is_stable(&wsl));
        state.extend_independent(&pairs);
        assert!(state.host_is_stable(&wsl));
        state.write(native.clone(), false, FileClass::Regular);
        assert_eq!(
            state.class(&wsl, &HashMap::from([(wsl.clone(), FileClass::Regular)])),
            FileClass::Regular
        );
        state.invalidate_writes(&Effects {
            accesses: vec![Access {
                resource: Resource::File(FileResource::new(native, Scope::Tree)),
                kind: AccessKind::Write,
            }],
            unknown_external: false,
        });
        assert!(!state.host_is_stable(&wsl));
        state.unknown();
        assert!(!state.host_is_stable(&wsl));
    }
}

#[cfg(test)]
mod tests;
