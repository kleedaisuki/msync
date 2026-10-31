//! Frozen v0.6 scan model; test-only oracle for indexed state inference.
use std::collections::{HashMap, HashSet};
use std::sync::Arc;

use crate::endpoint::{Backend, Location};
use crate::hir::effects::{AccessKind, Effects, FileResource, Resource, Scope};
use crate::hir::graph::PlanningContext;
use crate::plan::FileClass;

#[derive(Clone, Default)]
pub(super) struct ReferenceState {
    events: Vec<ReferenceEvent>,
    unknown: bool,
    pending_writes: Vec<ReferencePendingWrite>,
    independent: Arc<HashSet<(Location, Location)>>,
}

#[derive(Clone)]
struct ReferencePendingWrite {
    resource: Resource,
    managed: bool,
}

#[derive(Clone)]
struct ReferenceEvent {
    resource: Arc<FileResource>,
    tree: bool,
    kind: FileClass,
}

impl ReferenceState {
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
        self.pending_writes.extend(
            effects
                .accesses
                .iter()
                .filter(|access| {
                    access.kind == AccessKind::Write
                        && matches!(
                            access.resource,
                            Resource::File(_)
                                | Resource::DirectoryEntry(_)
                                | Resource::DirectoryEntries(_)
                                | Resource::DirectoryMetadata(_)
                        )
                })
                .map(|access| ReferencePendingWrite {
                    resource: access.resource.clone(),
                    managed: false,
                }),
        );
    }

    pub(super) fn defer_write(&mut self, location: Location) {
        self.pending_writes.push(ReferencePendingWrite {
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
        self.events.push(ReferenceEvent {
            resource: Arc::new(FileResource::new(location, Scope::Exact)),
            tree,
            kind,
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

use super::State;
use crate::hir::ProcessId;
use crate::hir::effects::{Access, Port};
use proptest::prelude::*;

fn locations() -> Vec<Location> {
    let native = [
        "/state",
        "/state/a",
        "/state/a/leaf",
        "/state/b",
        "/state/a/../b",
        "/other",
        "C:\\ROOT",
        "c:\\root\\child",
        "//./pipe/p",
        "//./pipe/p/child",
        "//host/share/root",
        "//HOST/share/root/leaf",
    ];
    let mut locations = native
        .into_iter()
        .map(|path| Location {
            backend: Backend::Local,
            path: path.into(),
        })
        .collect::<Vec<_>>();
    for backend in [
        Backend::Wsl,
        Backend::Ssh {
            host: "host".into(),
            user: None,
            port: None,
        },
        Backend::Env,
    ] {
        locations.extend(
            [
                "/state",
                "/state/a",
                "/state/a/leaf",
                "state/b",
                "~/home",
                "../root",
            ]
            .map(|path| Location {
                backend: backend.clone(),
                path: path.into(),
            }),
        );
    }
    locations
}

fn kind(value: u8) -> FileClass {
    match value % 5 {
        0 => FileClass::Regular,
        1 => FileClass::Directory,
        2 => FileClass::Missing,
        3 => FileClass::Stream,
        _ => FileClass::Unknown,
    }
}

fn footprint(location: Location, value: u8, opaque: bool) -> Effects {
    let file = FileResource::new(
        location,
        if value.is_multiple_of(2) {
            Scope::Tree
        } else {
            Scope::Exact
        },
    );
    let resource = match value % 7 {
        0 => Resource::File(file),
        1 => Resource::DirectoryEntry(file),
        2 => Resource::DirectoryEntries(file),
        3 => Resource::DirectoryMetadata(file),
        4 => Resource::StreamPath(file),
        5 => Resource::Port {
            process: ProcessId(0),
            port: Port::Stdin,
        },
        _ => Resource::StreamIdentity {
            provider: "fifo".into(),
            identity: "pipe".into(),
        },
    };
    Effects {
        accesses: vec![Access {
            resource,
            kind: if value.is_multiple_of(3) {
                AccessKind::Read
            } else {
                AccessKind::Write
            },
        }],
        unknown_external: opaque,
    }
}

fn separation_set(candidates: Option<Vec<Location>>) -> Option<HashSet<Location>> {
    candidates.map(|locations| locations.into_iter().collect())
}

fn check(
    indexed: &State,
    reference: &ReferenceState,
    queries: &[Location],
    initial: &HashMap<Location, FileClass>,
) {
    for location in queries {
        assert_eq!(
            indexed.host_is_stable(location),
            reference.host_is_stable(location),
            "host stability for {location}"
        );
        assert_eq!(
            indexed.class(location, initial),
            reference.class(location, initial),
            "class for {location}"
        );
        assert_eq!(
            separation_set(indexed.separation_candidates(location)),
            separation_set(reference.separation_candidates(location)),
            "WSL separation for {location}"
        );
    }
}

proptest! {
    #![proptest_config(ProptestConfig::with_cases(128))]
    #[test]
    fn indexed_state_matches_scan_across_aliases_and_namespace_effects(rows in prop::collection::vec((0u8..8, 0usize..30, any::<bool>(), any::<u8>()), 0..50)) {
        let locations = locations();
        let initial = locations.iter().enumerate().map(|(index, location)| (location.clone(), kind(index as u8))).collect::<HashMap<_, _>>();
        let context = PlanningContext { pending_effects: vec![footprint(locations[1].clone(), 2, false), footprint(locations[3].clone(), 10, false)], ..PlanningContext::default() };
        let mut indexed = State::initial(&context);
        let mut reference = ReferenceState::initial(&context);
        let mut snapshot = (indexed.clone(), reference.clone());
        for (step, (action, location, tree, value)) in rows.into_iter().enumerate() {
            let location = locations[location].clone();
            match action {
                0 | 1 => { indexed.write(location.clone(), tree, kind(value)); reference.write(location, tree, kind(value)); }
                2 => { indexed.defer_write(location.clone()); reference.defer_write(location); }
                3 => { let effects = footprint(location, value, tree && value.is_multiple_of(11)); indexed.invalidate_writes(&effects); reference.invalidate_writes(&effects); }
                4 => { let pairs = HashSet::from([(locations[1].clone(), locations[12].clone()), (location, locations[13].clone())]); indexed.extend_independent(&pairs); reference.extend_independent(&pairs); }
                5 => { indexed.unknown(); reference.unknown(); }
                6 => { indexed = State::default(); reference = ReferenceState::default(); }
                _ => { indexed.write(location.clone(), false, FileClass::Directory); reference.write(location, false, FileClass::Directory); }
            }
            check(&indexed, &reference, &locations, &initial);
            check(&snapshot.0, &snapshot.1, &locations, &initial);
            if step.is_multiple_of(10) { snapshot = (indexed.clone(), reference.clone()); }
        }
    }
}

#[test]
fn repeated_directory_postconditions_share_one_index_group_without_erasing_old_file_state() {
    let parent = Location {
        backend: Backend::Local,
        path: "/postconditions".into(),
    };
    let child = Location {
        backend: Backend::Local,
        path: "/postconditions/child".into(),
    };
    let mut indexed = State::default();
    let mut reference = ReferenceState::default();
    indexed.write(parent.clone(), false, FileClass::Regular);
    reference.write(parent.clone(), false, FileClass::Regular);
    for _ in 0..1000 {
        indexed.write(parent.clone(), false, FileClass::Directory);
        reference.write(parent.clone(), false, FileClass::Directory);
    }
    assert_eq!(indexed.events.len(), 2);
    assert_eq!(indexed.sequence, 1001);
    let initial = HashMap::from([(child.clone(), FileClass::Regular)]);
    assert_eq!(indexed.class(&child, &initial), FileClass::Missing);
    check(&indexed, &reference, &[parent, child], &initial);
}

#[test]
fn raw_parent_traversal_retains_preceding_missing_ancestor_postcondition() {
    let parent = Location {
        backend: Backend::Local,
        path: "/state/a".into(),
    };
    let traversed = Location {
        backend: Backend::Local,
        path: "/state/a/../b".into(),
    };
    let mut indexed = State::default();
    let mut reference = ReferenceState::default();
    indexed.write(parent.clone(), false, FileClass::Missing);
    reference.write(parent, false, FileClass::Missing);
    assert_eq!(
        indexed.class(&traversed, &HashMap::new()),
        FileClass::Missing
    );
    check(&indexed, &reference, &[traversed], &HashMap::new());
}
