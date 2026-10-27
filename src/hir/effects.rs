//! Logical resource effects, deliberately independent of filesystem observation.
//!
//! Different sibling paths may commute because managed writes publish by path,
//! rather than modifying an existing inode through another hard-link name.
//! Native/WSL projections and unresolved contextual endpoints remain aliases.

use crate::compiler::{Backend, Location};
use crate::hir::{Input, Instruction, InstructionKind, Output, ProcessId, ReadPort};
use crate::language::DestinationMode;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Scope {
    Exact,
    Tree,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct FileResource {
    pub location: Location,
    pub scope: Scope,
    path: LogicalPath,
}

impl FileResource {
    pub fn new(location: Location, scope: Scope) -> Self {
        let path = LogicalPath::new(&location);
        Self {
            location,
            scope,
            path,
        }
    }

    /// A negative answer is a proof in the logical managed-path model, not a
    /// claim about arbitrary aliases in an unmediated external application.
    pub fn may_overlap(&self, other: &Self) -> bool {
        if unresolved(&self.location.backend) || unresolved(&other.location.backend) {
            return true;
        }
        if native_wsl_pair(&self.location.backend, &other.location.backend) {
            return true;
        }
        if self.location.backend != other.location.backend {
            return false;
        }
        if self.path.root != other.path.root || self.path.uncertain || other.path.uncertain {
            return true;
        }
        // An exact parent write can change whether a descendant exists, even
        // when neither caller requested recursive enumeration.
        self.path.parts.starts_with(&other.path.parts)
            || other.path.parts.starts_with(&self.path.parts)
    }
}

fn unresolved(backend: &Backend) -> bool {
    matches!(backend, Backend::Path | Backend::Env)
}

fn native_wsl_pair(left: &Backend, right: &Backend) -> bool {
    matches!(
        (left, right),
        (Backend::Local, Backend::Wsl) | (Backend::Wsl, Backend::Local)
    )
}

#[derive(Clone, Debug, PartialEq, Eq)]
struct LogicalPath {
    root: String,
    parts: Vec<String>,
    uncertain: bool,
}

impl LogicalPath {
    fn new(location: &Location) -> Self {
        let local = matches!(
            location.backend,
            Backend::Local | Backend::Path | Backend::Env
        );
        let path = if local {
            location.path.replace('\\', "/")
        } else {
            location.path.clone()
        };
        let drive = local && path.as_bytes().get(1) == Some(&b':');
        let unc = local && path.starts_with("//");
        let windows = drive || unc;
        let (root, tail) = if drive {
            (path[..2].to_ascii_lowercase(), &path[2..])
        } else if unc {
            ("//".to_owned(), path.trim_start_matches('/'))
        } else if path.starts_with('/') {
            ("/".to_owned(), path.trim_start_matches('/'))
        } else if let Some(tail) = path.strip_prefix("~/") {
            ("~".to_owned(), tail)
        } else {
            (String::new(), path.as_str())
        };
        let mut parts = Vec::new();
        let mut uncertain = drive && !tail.starts_with('/');
        for part in tail.split('/') {
            match part {
                "" | "." => {}
                ".." => {
                    if parts.pop().is_none() {
                        uncertain = true;
                    }
                }
                part => {
                    if windows && (!part.is_ascii() || part.contains(':')) {
                        // Unicode case folding and NTFS alternate streams need
                        // native identity evidence, not a portable string guess.
                        uncertain = true;
                    }
                    parts.push(if windows {
                        part.trim_end_matches(['.', ' ']).to_ascii_lowercase()
                    } else {
                        part.to_owned()
                    });
                }
            }
        }
        Self {
            root,
            parts,
            uncertain,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Port {
    Stdin,
    Stdout,
    Stderr,
    ExitCode,
    Pid,
    Meta,
}

impl From<ReadPort> for Port {
    fn from(port: ReadPort) -> Self {
        match port {
            ReadPort::Stdout => Self::Stdout,
            ReadPort::Stderr => Self::Stderr,
            ReadPort::ExitCode => Self::ExitCode,
            ReadPort::Pid => Self::Pid,
            ReadPort::Meta => Self::Meta,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Resource {
    File(FileResource),
    Port { process: ProcessId, port: Port },
}

impl Resource {
    pub fn may_overlap(&self, other: &Self) -> bool {
        match (self, other) {
            (Self::File(left), Self::File(right)) => left.may_overlap(right),
            (
                Self::Port {
                    process: lp,
                    port: lc,
                },
                Self::Port {
                    process: rp,
                    port: rc,
                },
            ) => lp == rp && lc == rc,
            _ => false,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AccessKind {
    Read,
    Write,
    Consume,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Access {
    pub resource: Resource,
    pub kind: AccessKind,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct Effects {
    pub accesses: Vec<Access>,
    /// External effects are not disproved by seeing only literal argv values.
    pub unknown_external: bool,
}

impl Effects {
    pub fn append(&mut self, other: Self) {
        self.accesses.extend(other.accesses);
        self.unknown_external |= other.unknown_external;
    }

    pub fn conflicts(&self, later: &Self) -> Vec<Conflict> {
        let mut conflicts = Vec::new();
        for before in &self.accesses {
            for after in &later.accesses {
                if !before.resource.may_overlap(&after.resource) {
                    continue;
                }
                let kind = match (before.kind, after.kind) {
                    (AccessKind::Read, AccessKind::Read) => continue,
                    (AccessKind::Consume, _) | (_, AccessKind::Consume) => Hazard::ConsumingRead,
                    (AccessKind::Write, AccessKind::Read) => Hazard::ReadAfterWrite,
                    (AccessKind::Read, AccessKind::Write) => Hazard::WriteAfterRead,
                    (AccessKind::Write, AccessKind::Write) => Hazard::WriteAfterWrite,
                };
                conflicts.push(Conflict {
                    kind,
                    before: before.clone(),
                    after: after.clone(),
                });
            }
        }
        conflicts
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Hazard {
    ReadAfterWrite,
    WriteAfterRead,
    WriteAfterWrite,
    ConsumingRead,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Conflict {
    pub kind: Hazard,
    pub before: Access,
    pub after: Access,
}

pub fn summarize(instruction: &Instruction) -> Effects {
    match &instruction.kind {
        InstructionKind::Transfer {
            source,
            destination,
        } => transfer(source, destination),
        InstructionKind::Spawn { .. } => Effects {
            unknown_external: true,
            ..Effects::default()
        },
        InstructionKind::Transaction { body, .. } => {
            let mut effects = Effects::default();
            for instruction in &body.instructions {
                effects.append(summarize(instruction));
            }
            effects
        }
        InstructionKind::Commit { .. } | InstructionKind::Rollback { .. } => Effects::default(),
    }
}

fn transfer(source: &Input, destination: &Output) -> Effects {
    let mut effects = Effects::default();
    match source {
        Input::File { location, .. } => effects.accesses.push(Access {
            resource: Resource::File(FileResource::new(location.clone(), Scope::Tree)),
            kind: AccessKind::Read,
        }),
        Input::Literal(_) => {}
        Input::Port { process, port } => effects.accesses.push(Access {
            resource: Resource::Port {
                process: *process,
                port: (*port).into(),
            },
            kind: if matches!(port, ReadPort::Stdout | ReadPort::Stderr) {
                AccessKind::Consume
            } else {
                AccessKind::Read
            },
        }),
    }
    match destination {
        Output::File { location, mode } => effects.accesses.push(Access {
            resource: Resource::File(FileResource::new(
                location.clone(),
                if matches!(source, Input::Literal(_)) && *mode == DestinationMode::Exact {
                    Scope::Exact
                } else {
                    Scope::Tree
                },
            )),
            kind: AccessKind::Write,
        }),
        Output::Stdin(process) => effects.accesses.push(Access {
            resource: Resource::Port {
                process: *process,
                port: Port::Stdin,
            },
            kind: AccessKind::Write,
        }),
    }
    effects
}
