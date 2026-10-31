//! Conservative candidates only: exact effect predicates remain the proof owner.
use std::collections::{BTreeSet, HashMap};

use super::{AccessKind, Effects, FileResource, Resource};
use crate::endpoint::Backend;
use crate::hir::ProcessId;
use crate::plan::index::PathIndex;

#[derive(Default)]
pub(crate) struct CandidateIndex {
    readers: Resources,
    modifiers: Resources,
}

impl CandidateIndex {
    pub(crate) fn insert(&mut self, effects: &Effects, instruction: usize) {
        for access in &effects.accesses {
            let resources = if access.kind == AccessKind::Read {
                &mut self.readers
            } else {
                &mut self.modifiers
            };
            resources.insert(&access.resource, instruction);
        }
    }

    pub(crate) fn candidates(&self, effects: &Effects, result: &mut BTreeSet<usize>) {
        for access in &effects.accesses {
            self.modifiers.candidates(&access.resource, result);
            if access.kind != AccessKind::Read {
                self.readers.candidates(&access.resource, result);
            }
        }
    }
}

type Members = BTreeSet<usize>;
#[derive(Clone, Default)]
pub(crate) struct Resources {
    files: Files,
    streams: HashMap<(String, String), Members>,
    snapshots: HashMap<(String, String), Members>,
    ports: HashMap<(ProcessId, u8), Members>,
    http: HashMap<Option<String>, Members>,
    all_http: Members,
}

impl Resources {
    pub(crate) fn insert(&mut self, resource: &Resource, instruction: usize) {
        match resource {
            Resource::File(file)
            | Resource::StreamPath(file)
            | Resource::DirectoryEntry(file)
            | Resource::DirectoryEntries(file)
            | Resource::DirectoryMetadata(file) => self.files.insert(file, instruction),
            Resource::StreamIdentity { provider, identity } => {
                self.streams
                    .entry((provider.clone(), identity.clone()))
                    .or_default()
                    .insert(instruction);
            }
            Resource::SnapshotFile(proof) => {
                self.snapshots
                    .entry((proof.provider.clone(), proof.version_id.clone()))
                    .or_default()
                    .insert(instruction);
            }
            Resource::Port { process, port } => {
                self.ports
                    .entry((*process, *port as u8))
                    .or_default()
                    .insert(instruction);
            }
            Resource::HttpRequests(origin) => {
                self.http
                    .entry(origin.clone())
                    .or_default()
                    .insert(instruction);
                self.all_http.insert(instruction);
            }
        }
    }

    pub(crate) fn candidates(&self, resource: &Resource, result: &mut Members) {
        match resource {
            Resource::File(file)
            | Resource::StreamPath(file)
            | Resource::DirectoryEntry(file)
            | Resource::DirectoryEntries(file)
            | Resource::DirectoryMetadata(file) => self.files.candidates(file, result),
            Resource::StreamIdentity { provider, identity } => extend(
                self.streams.get(&(provider.clone(), identity.clone())),
                result,
            ),
            Resource::SnapshotFile(proof) => extend(
                self.snapshots
                    .get(&(proof.provider.clone(), proof.version_id.clone())),
                result,
            ),
            Resource::Port { process, port } => {
                extend(self.ports.get(&(*process, *port as u8)), result)
            }
            Resource::HttpRequests(None) => result.extend(&self.all_http),
            Resource::HttpRequests(Some(origin)) => {
                extend(self.http.get(&Some(origin.clone())), result);
                extend(self.http.get(&None), result);
            }
        }
    }
}

fn extend(entries: Option<&Members>, result: &mut Members) {
    if let Some(entries) = entries {
        result.extend(entries);
    }
}

#[derive(Clone, Default)]
pub(crate) struct Files {
    endpoints: HashMap<Backend, Endpoint>,
    unresolved: Members,
    all: Members,
}

impl Files {
    pub(crate) fn insert(&mut self, file: &FileResource, instruction: usize) {
        self.all.insert(instruction);
        if super::unresolved(&file.location.backend) {
            self.unresolved.insert(instruction);
            return;
        }
        self.endpoints
            .entry(file.location.backend.clone())
            .or_default()
            .insert(file, instruction);
    }

    pub(crate) fn candidates(&self, file: &FileResource, result: &mut Members) {
        if super::unresolved(&file.location.backend) {
            result.extend(&self.all);
            return;
        }
        result.extend(&self.unresolved);
        if let Some(endpoint) = self.endpoints.get(&file.location.backend) {
            endpoint.candidates(file, result);
        }
        let counterpart = match file.location.backend {
            Backend::Local => Some(Backend::Wsl),
            Backend::Wsl => Some(Backend::Local),
            _ => None,
        };
        if let Some(counterpart) = counterpart.and_then(|backend| self.endpoints.get(&backend)) {
            result.extend(&counterpart.all);
        }
    }
}

#[derive(Clone, Default)]
struct Endpoint {
    roots: HashMap<String, Root>,
    uncertain: Members,
    all: Members,
}

impl Endpoint {
    fn insert(&mut self, file: &FileResource, instruction: usize) {
        self.all.insert(instruction);
        if file.path.uncertain {
            self.uncertain.insert(instruction);
            return;
        }
        let root = self.roots.entry(file.path.root.clone()).or_default();
        root.members.insert(instruction);
        root.paths.insert(&file.path.parts, instruction);
    }

    fn candidates(&self, file: &FileResource, result: &mut Members) {
        if file.path.uncertain {
            result.extend(&self.all);
            return;
        }
        result.extend(&self.uncertain);
        for (name, root) in &self.roots {
            if *name == file.path.root {
                root.paths.candidates(&file.path.parts, result);
            } else {
                result.extend(&root.members);
            }
        }
    }
}

#[derive(Clone, Default)]
struct Root {
    members: Members,
    paths: PathIndex,
}

#[cfg(test)]
mod tests {
    use super::super::{Access, Port, Scope, VersionReadProof};
    use super::*;
    use crate::endpoint::Location;
    use proptest::prelude::*;

    fn backend(kind: u8) -> Backend {
        match kind % 8 {
            0 => Backend::Local,
            1 => Backend::Wsl,
            2 => Backend::Env,
            3 => Backend::Path,
            4 => Backend::Ssh {
                host: "a".into(),
                user: None,
                port: None,
            },
            5 => Backend::Ssh {
                host: "b".into(),
                user: Some("u".into()),
                port: Some(22),
            },
            6 => Backend::Http {
                base: "https://a/".into(),
            },
            _ => Backend::Tcp {
                host: "a".into(),
                port: 1,
            },
        }
    }

    fn resource(kind: u8, endpoint: u8, name: u8, tail: u8) -> Resource {
        let paths = [
            "root/a",
            "/root/a",
            "root",
            "/root",
            "~",
            "C:\\root\\A",
            "C:\\root\\a.",
            "../a",
            "a/../../b",
            "//./pipe/p",
            "//./pipe/é",
            "//host/share/É",
            "./root/a/../b",
            "root/a/b",
            "root/b",
            ".",
        ];
        let file = FileResource::new(
            Location {
                backend: backend(endpoint),
                path: paths[name as usize % paths.len()].into(),
            },
            if tail.is_multiple_of(2) {
                Scope::Exact
            } else {
                Scope::Tree
            },
        );
        match kind % 9 {
            0 => Resource::File(file),
            1 => Resource::StreamPath(file),
            2 => Resource::DirectoryEntry(file),
            3 => Resource::DirectoryEntries(file),
            4 => Resource::DirectoryMetadata(file),
            5 => Resource::StreamIdentity {
                provider: (endpoint % 2).to_string(),
                identity: (name % 3).to_string(),
            },
            6 => Resource::Port {
                process: ProcessId((name % 3) as usize),
                port: match tail % 6 {
                    0 => Port::Stdin,
                    1 => Port::Stdout,
                    2 => Port::Stderr,
                    3 => Port::Pid,
                    4 => Port::ExitCode,
                    _ => Port::Meta,
                },
            },
            7 => Resource::HttpRequests(
                (!endpoint.is_multiple_of(3)).then(|| (name % 2).to_string()),
            ),
            _ => Resource::SnapshotFile(
                VersionReadProof::pinned_file(
                    Location {
                        backend: Backend::Local,
                        path: file.location.path,
                    },
                    (endpoint % 2).to_string(),
                    (tail % 2).to_string(),
                )
                .unwrap(),
            ),
        }
    }

    proptest! {
        #![proptest_config(ProptestConfig::with_cases(512))]
        #[test]
        fn candidates_never_exclude_a_real_conflict(
            rows in prop::collection::vec(prop::collection::vec((0u8..9, 0u8..8, any::<u8>(), any::<u8>(), 0u8..3), 0..8), 0..20)
        ) {
            let effects = rows.into_iter().map(|accesses| Effects { accesses: accesses.into_iter().map(|(kind, endpoint, name, tail, access)| Access { resource: resource(kind, endpoint, name, tail), kind: match access {0 => AccessKind::Read, 1 => AccessKind::Write, _ => AccessKind::Consume} }).collect(), unknown_external: false }).collect::<Vec<_>>();
            let mut index = CandidateIndex::default();
            for (right, effect) in effects.iter().enumerate() {
                let mut candidates = Members::new();
                index.candidates(effect, &mut candidates);
                for (left, previous) in effects[..right].iter().enumerate() {
                    if !previous.conflicts(effect).is_empty() { prop_assert!(candidates.contains(&left), "left={left} right={right} previous={previous:?} next={effect:?}"); }
                }
                index.insert(effect, right);
            }
        }
    }

    #[test]
    fn shared_readers_and_sibling_writes_need_no_conflict_queries() {
        let mut index = CandidateIndex::default();
        for instruction in 0..128 {
            let effect = Effects {
                accesses: vec![
                    Access {
                        kind: AccessKind::Read,
                        resource: Resource::File(FileResource::new(
                            Location {
                                backend: Backend::Local,
                                path: "/shared/input".into(),
                            },
                            Scope::Exact,
                        )),
                    },
                    Access {
                        kind: AccessKind::Write,
                        resource: Resource::DirectoryEntry(FileResource::new(
                            Location {
                                backend: Backend::Local,
                                path: format!("/outputs/{instruction}"),
                            },
                            Scope::Exact,
                        )),
                    },
                ],
                unknown_external: false,
            };
            let mut candidates = Members::new();
            index.candidates(&effect, &mut candidates);
            assert!(candidates.is_empty());
            index.insert(&effect, instruction);
        }
    }
}
