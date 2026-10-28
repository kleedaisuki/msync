//! Logical resource effects, deliberately independent of filesystem observation.
//!
//! Different sibling paths may commute because managed writes publish by path,
//! rather than modifying an existing inode through another hard-link name.
//! Native/WSL projections and unresolved contextual endpoints remain aliases.

use crate::compiler::{Backend, Location};
use crate::hir::{Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, ReadPort};
use crate::language::DestinationMode;
use crate::plan::{FileClass, OperationKinds};

mod namespace;
pub use namespace::{NamespaceMergeEpoch, NamespaceMergeProof};
mod rendezvous;
pub use rendezvous::{FifoEndpointProof, FifoRendezvousProof};

/// Physical endpoint stability is independent of its logical file class.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum EndpointRole {
    Source,
    Destination,
    Executable,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct EndpointKey {
    pub instruction: InstructionId,
    pub role: EndpointRole,
}

/// Observations/snapshot premises supplied by preparation, never discovered by
/// the pure graph builder. A missing source is not a finite-file proof.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct OperationFacts {
    pub source: FileClass,
    pub destination: FileClass,
}

impl From<OperationKinds> for OperationFacts {
    fn from(kinds: OperationKinds) -> Self {
        Self {
            source: kinds.source,
            destination: kinds.destination,
        }
    }
}

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
        if let Some(answer) = self.comparable(other) {
            return answer;
        }
        // An exact parent write can change whether a descendant exists, even
        // when neither caller requested recursive enumeration.
        self.path.parts.starts_with(&other.path.parts)
            || other.path.parts.starts_with(&self.path.parts)
    }

    fn comparable(&self, other: &Self) -> Option<bool> {
        if unresolved(&self.location.backend) || unresolved(&other.location.backend) {
            return Some(true);
        }
        if native_wsl_pair(&self.location.backend, &other.location.backend) {
            return Some(true);
        }
        if self.location.backend != other.location.backend {
            return Some(false);
        }
        if self.path.root != other.path.root || self.path.uncertain || other.path.uncertain {
            return Some(true);
        }
        None
    }

    fn same_path(&self, other: &Self) -> bool {
        self.comparable(other)
            .unwrap_or_else(|| self.path.parts == other.path.parts)
    }

    fn definitely_same_path(&self, other: &Self) -> bool {
        self.location == other.location
            || (self.location.backend == other.location.backend
                && self.path.root == other.path.root
                && !self.path.uncertain
                && !other.path.uncertain
                && self.path.parts == other.path.parts)
    }

    /// A negative overlap guess cannot establish a preparation premise.
    pub fn is_definite_ancestor_of(&self, other: &Self) -> bool {
        self.location.backend == other.location.backend
            && self.path.root == other.path.root
            && !self.path.uncertain
            && !other.path.uncertain
            && other.path.parts.starts_with(&self.path.parts)
    }

    fn contains_path(&self, other: &Self) -> bool {
        self.comparable(other)
            .unwrap_or_else(|| other.path.parts.starts_with(&self.path.parts))
    }

    fn parent(&self) -> Option<Self> {
        let minimum = if self.path.root == "//" { 2 } else { 0 };
        if self.path.parts.len() <= minimum {
            return None;
        }
        let mut parent = self.clone();
        parent.path.parts.pop();
        parent.scope = Scope::Exact;
        let tail = parent.path.parts.join("/");
        parent.location.path = match parent.path.root.as_str() {
            "" if tail.is_empty() => ".".into(),
            "" => tail,
            "/" => format!("/{tail}"),
            "//" => format!("//{tail}"),
            "~" if tail.is_empty() => "~".into(),
            root => format!("{root}/{tail}"),
        };
        Some(parent)
    }
}

/// A retained, immutable regular-file version selected at this instruction's
/// logical read epoch. The reader adapter must serve these captured bytes and
/// retain their lease until the flow closes, not reopen the mutable path.
///
/// Construction validates shape only. It cannot prove OS capture, source-order
/// visibility, or lifetime. A file type, cwd, pathname or HEAD response alone is
/// not evidence. Raw HTTP/TCP inputs must first be captured/lowered to a local
/// artifact; this token does not erase their request/consumption events.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct VersionReadProof {
    source: FileResource,
    provider: String,
    version_id: String,
}

impl VersionReadProof {
    pub fn pinned_file(
        location: Location,
        provider: impl Into<String>,
        version_id: impl Into<String>,
    ) -> Result<Self, String> {
        let provider = provider.into();
        let version_id = version_id.into();
        if provider.is_empty()
            || version_id.is_empty()
            || location.path.is_empty()
            || location.path.contains('\0')
            || !matches!(
                location.backend,
                Backend::Local | Backend::Wsl | Backend::Ssh { .. }
            )
        {
            return Err("a pinned-file premise needs a retained immutable filesystem version, its source location, and nonempty provider/version identities".into());
        }
        Ok(Self {
            source: FileResource::new(location, Scope::Exact),
            provider,
            version_id,
        })
    }

    pub fn location(&self) -> &Location {
        &self.source.location
    }
    pub fn provider(&self) -> &str {
        &self.provider
    }
    pub fn version_id(&self) -> &str {
        &self.version_id
    }

    pub fn covers(&self, location: &Location) -> bool {
        self.source
            .definitely_same_path(&FileResource::new(location.clone(), Scope::Exact))
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
        } else if path == "~" {
            ("~".to_owned(), "")
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
    /// Bytes addressed by a mutable stream pathname, not a filesystem mutation.
    StreamPath(FileResource),
    /// A verified mutable byte channel, separate from its replaceable pathnames.
    StreamIdentity {
        provider: String,
        identity: String,
    },
    /// Canonical native child identity for one certified file insertion.
    NamespaceEntry(NamespaceMergeProof),
    /// Parent membership/mtime delta committed by the Manager receipt algebra.
    NamespaceChange(NamespaceMergeProof),
    /// Bytes already selected by a retained immutable-version reader. Private
    /// writes to the original pathname do not mutate this captured object.
    SnapshotFile(VersionReadProof),
    /// Directory entries and directory metadata, not sibling regular-file data.
    DirectoryMetadata(FileResource),
    /// Finite responses are not immutable versions. Requests at one origin
    /// remain ordered even when they use different URL paths.
    HttpRequests(Option<String>),
    Port {
        process: ProcessId,
        port: Port,
    },
}

impl Resource {
    pub fn may_overlap(&self, other: &Self) -> bool {
        if let (Self::NamespaceEntry(left), Self::NamespaceEntry(right)) = (self, other) {
            return left.entry_overlaps(right);
        }
        if let (Self::NamespaceEntry(entry), Self::NamespaceChange(change))
        | (Self::NamespaceChange(change), Self::NamespaceEntry(entry)) = (self, other)
            && entry.same_native_parent(change)
        {
            return false;
        }
        if let (Some(left), Some(right)) = (self.file(), other.file()) {
            return left.may_overlap(right);
        }
        if let (Some(left), Some(right)) = (self.metadata(), other.metadata()) {
            return left.same_path(right);
        }
        if let (Some(file), Some(metadata)) = (self.file(), other.metadata()) {
            return file_metadata_overlap(file, metadata);
        }
        if let (Some(metadata), Some(file)) = (self.metadata(), other.file()) {
            return file_metadata_overlap(file, metadata);
        }
        match (self, other) {
            (
                Self::StreamIdentity {
                    provider: lp,
                    identity: li,
                },
                Self::StreamIdentity {
                    provider: rp,
                    identity: ri,
                },
            ) => lp == rp && li == ri,
            (Self::SnapshotFile(left), Self::SnapshotFile(right)) => {
                left.provider == right.provider
                    && left.version_id == right.version_id
                    && left.source.definitely_same_path(&right.source)
            }
            (Self::HttpRequests(left), Self::HttpRequests(right)) => {
                left.is_none() || right.is_none() || left == right
            }
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

    fn file(&self) -> Option<&FileResource> {
        match self {
            Self::File(file) | Self::StreamPath(file) => Some(file),
            Self::NamespaceEntry(proof) => Some(proof.target_resource()),
            _ => None,
        }
    }

    fn metadata(&self) -> Option<&FileResource> {
        match self {
            Self::DirectoryMetadata(file) => Some(file),
            Self::NamespaceChange(proof) => Some(proof.parent_resource()),
            _ => None,
        }
    }
}

fn file_metadata_overlap(file: &FileResource, metadata: &FileResource) -> bool {
    if file.scope == Scope::Tree {
        file.contains_path(metadata)
    } else {
        file.same_path(metadata)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AccessKind {
    Read,
    Write,
    Consume,
    NamespaceMerge,
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
                if before.kind == AccessKind::NamespaceMerge
                    && after.kind == AccessKind::NamespaceMerge
                    && let (Resource::NamespaceChange(left), Resource::NamespaceChange(right)) =
                        (&before.resource, &after.resource)
                    && left.commutes_with(right)
                {
                    continue;
                }
                let kind = match (before.kind, after.kind) {
                    (AccessKind::Read, AccessKind::Read) => continue,
                    (AccessKind::Consume, _) | (_, AccessKind::Consume) => Hazard::ConsumingRead,
                    (AccessKind::Write | AccessKind::NamespaceMerge, AccessKind::Read) => {
                        Hazard::ReadAfterWrite
                    }
                    (AccessKind::Read, AccessKind::Write | AccessKind::NamespaceMerge) => {
                        Hazard::WriteAfterRead
                    }
                    (
                        AccessKind::Write | AccessKind::NamespaceMerge,
                        AccessKind::Write | AccessKind::NamespaceMerge,
                    ) => Hazard::WriteAfterWrite,
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
    summarize_with_facts(instruction, OperationFacts::default())
}

pub fn summarize_with_facts(instruction: &Instruction, facts: OperationFacts) -> Effects {
    summarize_prepared(instruction, facts, None, None, None)
}

pub fn summarize_prepared(
    instruction: &Instruction,
    facts: OperationFacts,
    existing_parent: Option<&Location>,
    pinned_input: Option<&VersionReadProof>,
    namespace_merge: Option<&NamespaceMergeProof>,
) -> Effects {
    let namespace_merge = namespace_merge.filter(|proof| proof.supports(instruction, facts));
    match &instruction.kind {
        InstructionKind::Transfer {
            source,
            destination,
        } => transfer(
            source,
            destination,
            facts,
            existing_parent,
            pinned_input,
            namespace_merge,
        ),
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

/// TCP is a consuming byte stream regardless of a caller's stale file premise.
pub fn file_class(location: &Location, observed: FileClass) -> FileClass {
    if matches!(location.backend, Backend::Tcp { .. }) {
        FileClass::Stream
    } else {
        observed
    }
}

fn transfer(
    source: &Input,
    destination: &Output,
    facts: OperationFacts,
    existing_parent: Option<&Location>,
    pinned_input: Option<&VersionReadProof>,
    namespace_merge: Option<&NamespaceMergeProof>,
) -> Effects {
    let mut effects = Effects::default();
    let pinned = match source {
        Input::File {
            location,
            pattern: None,
        } => pinned_input.filter(|proof| proof.covers(location)),
        _ => None,
    };
    match (source, pinned) {
        (Input::File { .. }, Some(proof)) => effects.accesses.push(Access {
            resource: Resource::SnapshotFile(proof.clone()),
            kind: AccessKind::Read,
        }),
        (Input::File { location, pattern }, None) => {
            let class = file_class(location, facts.source);
            let http = matches!(location.backend, Backend::Http { .. });
            let scope =
                if matches!(class, FileClass::Regular | FileClass::Stream) && pattern.is_none() {
                    Scope::Exact
                } else {
                    Scope::Tree
                };
            effects.accesses.push(Access {
                resource: if class == FileClass::Stream {
                    Resource::StreamPath(FileResource::new(location.clone(), scope))
                } else {
                    Resource::File(FileResource::new(location.clone(), scope))
                },
                kind: if !http && matches!(class, FileClass::Regular | FileClass::Directory) {
                    AccessKind::Read
                } else {
                    AccessKind::Consume
                },
            });
            if let Backend::Http { base } = &location.backend {
                let origin = url::Url::parse(base)
                    .ok()
                    .filter(|url| {
                        matches!(url.scheme(), "http" | "https") && url.host_str().is_some()
                    })
                    .map(|url| url.origin().ascii_serialization());
                effects.accesses.push(Access {
                    resource: Resource::HttpRequests(origin),
                    kind: AccessKind::Consume,
                });
            }
            if class == FileClass::Directory || pattern.is_some() {
                effects.accesses.push(Access {
                    resource: Resource::DirectoryMetadata(FileResource::new(
                        location.clone(),
                        Scope::Exact,
                    )),
                    kind: AccessKind::Read,
                });
            }
        }
        (Input::Literal(_), _) => {}
        (Input::Port { process, port }, _) => effects.accesses.push(Access {
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
        Output::File { location, mode } => {
            let destination_class = file_class(location, facts.destination);
            let source_class = if pinned.is_some() {
                FileClass::Regular
            } else {
                match source {
                    Input::File {
                        location,
                        pattern: None,
                    } => file_class(location, facts.source),
                    Input::File { .. } => FileClass::Unknown,
                    Input::Literal(_) | Input::Port { .. } => FileClass::Regular,
                }
            };
            let scope = if *mode == DestinationMode::Exact
                && matches!(source_class, FileClass::Regular | FileClass::Stream)
                && destination_class != FileClass::Directory
            {
                Scope::Exact
            } else {
                Scope::Tree
            };
            let resource = FileResource::new(location.clone(), scope);
            if let Some(proof) = namespace_merge {
                effects.accesses.push(Access {
                    resource: Resource::NamespaceEntry(proof.clone()),
                    kind: AccessKind::Write,
                });
                effects.accesses.push(Access {
                    resource: Resource::NamespaceChange(proof.clone()),
                    kind: AccessKind::NamespaceMerge,
                });
                return effects;
            }
            effects.accesses.push(Access {
                resource: if destination_class == FileClass::Stream {
                    Resource::StreamPath(resource.clone())
                } else {
                    Resource::File(resource.clone())
                },
                kind: AccessKind::Write,
            });
            add_namespace_writes(
                &mut effects,
                resource,
                *mode,
                source_class,
                destination_class,
                existing_parent,
            );
        }
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

fn add_namespace_writes(
    effects: &mut Effects,
    resource: FileResource,
    mode: DestinationMode,
    source: FileClass,
    destination: FileClass,
    existing_parent: Option<&Location>,
) {
    if destination == FileClass::Stream
        || (destination == FileClass::Regular && mode == DestinationMode::Exact)
        || (destination == FileClass::Directory
            && source == FileClass::Directory
            && mode == DestinationMode::ReplaceTree)
    {
        return;
    }
    if mode == DestinationMode::Container {
        effects.accesses.push(Access {
            resource: Resource::DirectoryMetadata(FileResource::new(
                resource.location.clone(),
                Scope::Exact,
            )),
            kind: AccessKind::Write,
        });
        if destination == FileClass::Directory {
            return;
        }
    }
    let mut parent = resource.parent();
    while let Some(directory) = parent {
        let at_frontier = existing_parent.is_some_and(|frontier| {
            directory.definitely_same_path(&FileResource::new(frontier.clone(), Scope::Exact))
        });
        parent = if !at_frontier && matches!(destination, FileClass::Missing | FileClass::Unknown) {
            directory.parent()
        } else {
            None
        };
        effects.accesses.push(Access {
            resource: Resource::DirectoryMetadata(directory),
            kind: AccessKind::Write,
        });
    }
}
