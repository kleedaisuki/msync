use std::collections::HashMap;
use std::sync::Arc;

use crate::endpoint::{Backend, Location};
use crate::hir::effects::{FileResource, Scope};
use crate::plan::FileClass;

#[derive(Clone, Default)]
pub(super) struct State {
    events: Vec<Event>,
    unknown: bool,
}

#[derive(Clone)]
struct Event {
    location: Location,
    tree: bool,
    value: Value,
}

#[derive(Clone)]
enum Value {
    Kind(FileClass),
    Restore(Arc<State>),
}

impl State {
    pub(super) fn unknown(&mut self) {
        self.events.clear();
        self.unknown = true;
    }

    pub(super) fn write(&mut self, location: Location, tree: bool, kind: FileClass) {
        self.events.push(Event {
            location,
            tree,
            value: Value::Kind(kind),
        });
    }

    pub(super) fn restore(&mut self, location: Location, tree: bool, origin: Arc<Self>) {
        self.events.push(Event {
            location,
            tree,
            value: Value::Restore(origin),
        });
    }

    pub(super) fn class(
        &self,
        location: &Location,
        initial: &HashMap<Location, FileClass>,
    ) -> FileClass {
        for event in self.events.iter().rev() {
            if event.location == *location {
                return match &event.value {
                    Value::Kind(kind) => *kind,
                    Value::Restore(origin) => origin.class(location, initial),
                };
            }
            if descendant(location, &event.location)
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
            if descendant(&event.location, location) {
                continue;
            }
            if descendant(location, &event.location)
                && !event.tree
                && matches!(event.value, Value::Kind(FileClass::Directory))
            {
                continue;
            }
            if FileResource::new(location.clone(), Scope::Exact)
                .may_overlap(&FileResource::new(event.location.clone(), Scope::Exact))
            {
                return FileClass::Unknown;
            }
        }
        if self.unknown {
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
