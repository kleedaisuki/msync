use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use crate::endpoint::{Backend, Location};
use crate::hir::effects::{AccessKind, Effects, FileResource, Resource, Scope};
use crate::hir::graph::PlanningContext;
use crate::plan::FileClass;

#[derive(Clone, Default)]
pub(super) struct State {
    events: Vec<Event>,
    unknown: bool,
    pending_writes: Vec<PendingWrite>,
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
    value: Value,
}

#[derive(Clone)]
enum Value {
    Kind(FileClass),
    Restore(Arc<State>),
}

impl State {
    pub(super) fn initial(context: &PlanningContext) -> Self {
        let mut state = Self::default();
        for process in &context.live_processes {
            let Some(effects) = context.process_effects.get(process) else {
                state.unknown = true;
                continue;
            };
            state.invalidate_writes(effects.footprint());
        }
        for effects in &context.pending_effects {
            state.invalidate_writes(effects);
        }
        state
    }

    pub(super) fn invalidate_writes(&mut self, effects: &Effects) {
        self.unknown |= effects.unknown_external;
        self.pending_writes.extend(
            effects
                .accesses
                .iter()
                .filter(|access| {
                    matches!(access.kind, AccessKind::Write | AccessKind::NamespaceMerge)
                        && matches!(
                            access.resource,
                            Resource::File(_)
                                | Resource::DirectoryMetadata(_)
                                | Resource::NamespaceEntry(_)
                                | Resource::NamespaceChange(_)
                        )
                })
                .map(|access| PendingWrite {
                    resource: access.resource.clone(),
                    managed: false,
                }),
        );
    }

    pub(super) fn defer_write(&mut self, location: Location) {
        self.pending_writes.push(PendingWrite {
            resource: Resource::File(FileResource::new(location, Scope::Tree)),
            managed: true,
        });
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
        for write in &self.pending_writes {
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
        !self
            .pending_writes
            .iter()
            .any(|write| {
                !(write.managed && matches!(&write.resource, Resource::File(file) if self.independent(&file.location, location)))
                    && write.resource.may_overlap(&resource)
            })
    }

    pub(super) fn unknown(&mut self) {
        self.events.clear();
        self.independent = Arc::default();
        self.unknown = true;
    }

    pub(super) fn write(&mut self, location: Location, tree: bool, kind: FileClass) {
        self.events.push(Event {
            resource: Arc::new(FileResource::new(location, Scope::Exact)),
            tree,
            value: Value::Kind(kind),
        });
    }

    pub(super) fn restore(&mut self, location: Location, tree: bool, origin: Arc<Self>) {
        self.events.push(Event {
            resource: Arc::new(FileResource::new(location, Scope::Exact)),
            tree,
            value: Value::Restore(origin),
        });
    }

    pub(super) fn class(
        &self,
        location: &Location,
        initial: &HashMap<Location, FileClass>,
    ) -> FileClass {
        let mut resource = None;
        for event in self.events.iter().rev() {
            let event_location = &event.resource.location;
            if self.independent(location, event_location) {
                continue;
            }
            if event_location == location {
                return match &event.value {
                    Value::Kind(kind) => *kind,
                    Value::Restore(origin) => origin.class(location, initial),
                };
            }
            if descendant(location, event_location)
                && (event.tree
                    || matches!(
                        event.value,
                        Value::Kind(FileClass::Missing | FileClass::Regular)
                    ))
            {
                return match &event.value {
                    Value::Kind(FileClass::Missing | FileClass::Regular) => FileClass::Missing,
                    Value::Kind(_) => FileClass::Unknown,
                    Value::Restore(origin) => origin.class(location, initial),
                };
            }
            // A successful child assignment does not turn its ancestors into a
            // different kind. Explicit parent postconditions are recorded first.
            if descendant(event_location, location) {
                continue;
            }
            if descendant(location, event_location)
                && !event.tree
                && matches!(event.value, Value::Kind(FileClass::Directory))
            {
                continue;
            }
            if resource
                .get_or_insert_with(|| FileResource::new(location.clone(), Scope::Exact))
                .may_overlap(&event.resource)
            {
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
    fn cloned_events_share_exact_resources_and_preserve_restore_postconditions() {
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
        let origin = Arc::new(State::default());
        let mut state = State::default();
        state.write(root.clone(), false, FileClass::Regular);
        assert_eq!(state.class(&child, &initial), FileClass::Missing);
        state.restore(root.clone(), true, origin);
        let snapshot = state.clone();
        for (event, cloned) in state.events.iter().zip(&snapshot.events) {
            assert_eq!(event.resource.scope, Scope::Exact);
            assert!(Arc::ptr_eq(&event.resource, &cloned.resource));
        }
        assert_eq!(snapshot.class(&root, &initial), FileClass::Directory);
        assert_eq!(snapshot.class(&child, &initial), FileClass::Regular);
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
