//! Logical resource effects, deliberately independent of filesystem observation.
//!
//! Different sibling paths may commute because managed writes publish by path,
//! rather than modifying an existing inode through another hard-link name.
//! Native/WSL projections and unresolved contextual endpoints remain aliases.

use std::borrow::Cow;
use std::sync::Arc;

use crate::compiler::{Backend, Location};
use crate::hir::{Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, ReadPort};
use crate::language::DestinationMode;
use crate::plan::{FileClass, OperationKinds};

mod index;
mod rendezvous;
pub(crate) use index::{CandidateIndex, Files, Resources};
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
    path: Arc<LogicalPath>,
}

impl FileResource {
    pub fn new(location: Location, scope: Scope) -> Self {
        let path = Arc::new(LogicalPath::new(&location));
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
        self.path
            .comparison_parts()
            .starts_with(other.path.comparison_parts())
            || other
                .path
                .comparison_parts()
                .starts_with(self.path.comparison_parts())
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
        if let Some(answer) = self.pipe_overlap(other) {
            return Some(answer);
        }
        if self.path.root != other.path.root || self.path.uncertain || other.path.uncertain {
            return Some(true);
        }
        None
    }

    fn pipe_overlap(&self, other: &Self) -> Option<bool> {
        let left = self.local_pipe_name();
        let right = other.local_pipe_name();
        match (left, right) {
            (Some(left), Some(right)) => {
                Some(!left.is_ascii() || !right.is_ascii() || left.eq_ignore_ascii_case(&right))
            }
            (Some(_), None) if other.path.root.ends_with(':') => Some(false),
            (None, Some(_)) if self.path.root.ends_with(':') => Some(false),
            _ => None,
        }
    }

    fn local_pipe_name(&self) -> Option<String> {
        if !cfg!(windows) || self.location.backend != Backend::Local {
            return None;
        }
        let path = self.location.path.replace('\\', "/");
        let prefix = path.get(..9)?;
        if !prefix.eq_ignore_ascii_case("//./pipe/") {
            return None;
        }
        let name = &path[9..];
        (!name.is_empty() && !name.contains('\0')).then(|| name.to_owned())
    }

    fn same_path(&self, other: &Self) -> bool {
        self.comparable(other).unwrap_or_else(|| {
            let left = self.path.comparison_parts();
            let right = other.path.comparison_parts();
            if self.path.comparison_is_prefix() || other.path.comparison_is_prefix() {
                left.starts_with(right) || right.starts_with(left)
            } else {
                left == right
            }
        })
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
        self.comparable(other).unwrap_or_else(|| {
            let left = self.path.comparison_parts();
            let right = other.path.comparison_parts();
            right.starts_with(left)
                || (other.path.comparison_is_prefix() && left.starts_with(right))
        })
    }

    fn parent(&self) -> Option<Self> {
        let minimum = if self.path.root == "//" { 2 } else { 0 };
        if self.path.parts.len() <= minimum {
            return None;
        }
        let mut parent = self.clone();
        let path = Arc::make_mut(&mut parent.path);
        path.parts.pop();
        if let Some(comparison) = path.comparison.as_mut() {
            comparison.truncate(path.parts.len());
        }
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
    comparison: Option<Vec<String>>,
    uncertain: bool,
}

impl LogicalPath {
    fn comparison_parts(&self) -> &[String] {
        self.comparison.as_deref().unwrap_or(&self.parts)
    }

    fn comparison_is_prefix(&self) -> bool {
        self.comparison_parts().len() < self.parts.len()
    }

    fn new(location: &Location) -> Self {
        let local = matches!(
            location.backend,
            Backend::Local | Backend::Path | Backend::Env
        );
        let bytes = location.path.as_bytes();
        let windows_spelling = local
            && (cfg!(windows)
                || location.path.starts_with("\\\\")
                || location.path.starts_with("//")
                || (bytes.first().is_some_and(u8::is_ascii_alphabetic)
                    && bytes.get(1) == Some(&b':')
                    && bytes
                        .get(2)
                        .is_some_and(|byte| matches!(*byte, b'/' | b'\\'))));
        let path = if windows_spelling {
            location.path.replace('\\', "/")
        } else {
            location.path.clone()
        };
        let verbatim = windows_spelling && path.starts_with("//?/");
        let (path, device) = if windows_spelling {
            windows_comparison_path(&path)
        } else {
            (Cow::Borrowed(path.as_str()), false)
        };
        let drive = windows_spelling && path.as_bytes().get(1) == Some(&b':');
        let unc = windows_spelling && path.starts_with("//");
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
            (String::new(), path.as_ref())
        };
        let mut parts = Vec::new();
        let mut uncertain = device
            || (drive && !tail.starts_with('/'))
            || (verbatim && location.path.contains('/'));
        for part in tail.split('/') {
            if verbatim && matches!(part, "." | "..") {
                uncertain = true;
            }
            match part {
                "" | "." => {}
                ".." => {
                    // A selected directory may be a link. Only native lookup
                    // can determine its parent; lexical cancellation is not identity.
                    parts.push(part.to_owned());
                    uncertain = true;
                }
                part => {
                    if windows && (part.contains(':') || (verbatim && part.ends_with(['.', ' ']))) {
                        // Device/alternate-stream spellings need native evidence;
                        // they are not ordinary directory components.
                        uncertain = true;
                    }
                    parts.push(part.to_owned());
                }
            }
        }
        uncertain |= unc && parts.len() < 2;
        if unc {
            for component in parts.iter_mut().take(2) {
                component.make_ascii_lowercase();
            }
        }
        let comparison = windows.then(|| {
            parts
                .iter()
                .map_while(|part| {
                    let component = if verbatim {
                        part.as_str()
                    } else {
                        part.trim_end_matches(['.', ' '])
                    };
                    crate::plan::windows_comparison_component(component)
                })
                .collect()
        });
        Self {
            root,
            parts,
            comparison,
            uncertain,
        }
    }
}

fn windows_comparison_path(path: &str) -> (Cow<'_, str>, bool) {
    let Some(tail) = path.strip_prefix("//?/") else {
        return (Cow::Borrowed(path), path.starts_with("//./"));
    };
    // Canonical Windows paths use these roots too; this key never changes an IO operand.
    if tail
        .get(..4)
        .is_some_and(|prefix| prefix.eq_ignore_ascii_case("UNC/"))
    {
        return (Cow::Owned(format!("//{}", &tail[4..])), false);
    }
    if tail.as_bytes().first().is_some_and(u8::is_ascii_alphabetic)
        && tail.as_bytes().get(1) == Some(&b':')
        && tail.as_bytes().get(2) == Some(&b'/')
    {
        return (Cow::Borrowed(tail), false);
    }
    (Cow::Borrowed(path), true)
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
    /// Bytes already selected by a retained immutable-version reader. Private
    /// writes to the original pathname do not mutate this captured object.
    SnapshotFile(VersionReadProof),
    /// One replaceable pathname binding, not the containing directory's state.
    DirectoryEntry(FileResource),
    /// The complete immediate child-name set used by enumeration or insertion.
    DirectoryEntries(FileResource),
    /// Explicitly observed directory attributes, not incidental sibling mtime.
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
        if let Self::DirectoryEntry(entry) = self {
            return entry_overlap(entry, other);
        }
        if let Self::DirectoryEntry(entry) = other {
            return entry_overlap(entry, self);
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
            _ => None,
        }
    }

    fn metadata(&self) -> Option<&FileResource> {
        match self {
            Self::DirectoryEntries(file) | Self::DirectoryMetadata(file) => Some(file),
            _ => None,
        }
    }
}

fn entry_overlap(entry: &FileResource, other: &Resource) -> bool {
    match other {
        Resource::DirectoryEntry(other) | Resource::File(other) | Resource::StreamPath(other) => {
            entry.may_overlap(other)
        }
        Resource::DirectoryEntries(directory) | Resource::DirectoryMetadata(directory) => {
            entry.contains_path(directory)
                || entry
                    .parent()
                    .is_some_and(|parent| parent.same_path(directory))
        }
        _ => false,
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
    summarize_with_facts(instruction, OperationFacts::default())
}

pub fn summarize_with_facts(instruction: &Instruction, facts: OperationFacts) -> Effects {
    summarize_prepared(instruction, facts, None, None)
}

pub fn summarize_prepared(
    instruction: &Instruction,
    facts: OperationFacts,
    existing_parent: Option<&Location>,
    pinned_input: Option<&VersionReadProof>,
) -> Effects {
    match &instruction.kind {
        InstructionKind::Transfer {
            source,
            destination,
        } => transfer(source, destination, facts, existing_parent, pinned_input),
        InstructionKind::Spawn { .. } | InstructionKind::Control(_) => Effects {
            unknown_external: true,
            ..Effects::default()
        },
        InstructionKind::Barrier { .. } | InstructionKind::Await { .. } => Effects::default(),
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
                    resource: Resource::DirectoryEntries(FileResource::new(
                        location.clone(),
                        Scope::Exact,
                    )),
                    kind: AccessKind::Read,
                });
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
    destination: FileClass,
    existing_parent: Option<&Location>,
) {
    if destination == FileClass::Stream {
        return;
    }
    if mode == DestinationMode::Container {
        effects.accesses.push(Access {
            resource: Resource::DirectoryEntries(FileResource::new(
                resource.location.clone(),
                Scope::Exact,
            )),
            kind: AccessKind::Write,
        });
        if destination == FileClass::Directory {
            return;
        }
    }
    let frontier = existing_parent
        .map(|location| FileResource::new(location.clone(), Scope::Exact))
        .filter(|frontier| {
            frontier.is_definite_ancestor_of(&resource) && !frontier.definitely_same_path(&resource)
        });
    let mut binding = Some(resource);
    while let Some(entry) = binding {
        let at_frontier = frontier
            .as_ref()
            .is_some_and(|frontier| entry.definitely_same_path(frontier));
        let parent = entry.parent();
        if at_frontier || parent.is_none() {
            break;
        }
        binding = if matches!(destination, FileClass::Missing | FileClass::Unknown) {
            parent
        } else {
            None
        };
        effects.accesses.push(Access {
            resource: Resource::DirectoryEntry(entry),
            kind: AccessKind::Write,
        });
    }
}

#[cfg(test)]
mod windows_path_tests {
    use super::*;
    use proptest::prelude::*;

    fn file(path: impl Into<String>) -> FileResource {
        FileResource::new(
            Location {
                backend: Backend::Local,
                path: path.into(),
            },
            Scope::Exact,
        )
    }

    #[test]
    fn extended_windows_roots_share_normal_drive_and_unc_identities() {
        for (normal, extended, sibling) in [
            (
                r"D:\workspace\stream",
                r"\\?\d:\workspace\stream",
                r"\\?\D:\workspace\gate",
            ),
            (
                r"\\server\share\workspace\stream",
                r"\\?\UNC\SERVER\share\workspace\stream",
                r"\\?\UNC\server\share\workspace\gate",
            ),
        ] {
            let normal = file(normal);
            let extended = file(extended);
            assert_eq!(normal.path, extended.path);
            assert!(normal.definitely_same_path(&extended));
            assert!(normal.may_overlap(&extended));
            assert!(!normal.may_overlap(&file(sibling)));
            assert!(!extended.may_overlap(&file(sibling)));
        }
    }

    #[test]
    fn verbatim_special_components_and_unknown_devices_stay_conservative() {
        for path in [
            r"\\?\Volume{unknown}\leaf",
            r"\\.\PhysicalDrive0",
            r"\\?\GLOBALROOT\Device\volume\leaf",
            r"\\?\relative\leaf",
            r"\\?\D:\parent\..\leaf",
            r"\\?\D:\parent\.\leaf",
            r"\\?\D:\parent.\leaf",
            r"\\?\D:\parent \leaf",
            r"\\?\D:\parent/leaf",
        ] {
            let resource = file(path);
            assert!(resource.path.uncertain, "{path}");
            assert!(resource.may_overlap(&file(r"D:\unrelated")), "{path}");
            assert!(
                resource.may_overlap(&file(r"\\server\share\unrelated")),
                "{path}"
            );
        }
        assert!(file(r"D:\parent\leaf.").may_overlap(&file(r"d:\PARENT\leaf")));
        assert!(file(r"\\?\D:\parent\leaf:stream").path.uncertain);
    }

    #[test]
    fn native_component_case_is_not_a_positive_identity_proof() {
        let lower = file(r"D:\workspace\a");
        let upper = file(r"D:\workspace\A");
        assert_ne!(lower.path.parts, upper.path.parts);
        assert!(!lower.definitely_same_path(&upper));
        assert!(lower.may_overlap(&upper));
        let street = file(r"D:\workspace\straße");
        let expanded = file(r"D:\workspace\STRASSE");
        assert!(!street.definitely_same_path(&expanded));
        assert!(street.may_overlap(&expanded));
        assert!(!street.may_overlap(&file(r"D:\unrelated\file")));
        let source = VersionReadProof::pinned_file(lower.location.clone(), "capture", "1").unwrap();
        assert!(!source.covers(&upper.location));
    }

    #[test]
    fn uncased_unicode_workspaces_keep_independent_siblings() {
        for root in [r"D:\用户\项目", r"D:\日本語\🚀", r"\\server\共享\项目"] {
            let left = file(format!(r"{root}\a"));
            let right = file(format!(r"{root}\b"));
            let case_alias = file(format!(r"{root}\A"));
            assert!(!left.may_overlap(&right), "{root}");
            assert!(!left.definitely_same_path(&case_alias), "{root}");
            assert!(left.may_overlap(&case_alias), "{root}");
        }
    }

    #[test]
    fn parent_traversal_is_not_a_positive_lexical_identity() {
        let indirect = file("alias/../target");
        let lexical = file("target");
        assert!(!indirect.definitely_same_path(&lexical));
        assert!(indirect.may_overlap(&lexical));
    }

    #[cfg(unix)]
    #[test]
    fn native_posix_backslashes_remain_filename_characters() {
        let literal = file("/workspace/line\nslash\\ 可莉");
        let split = file("/workspace/line\nslash/ 可莉");
        assert_eq!(literal.path.parts, ["workspace", "line\nslash\\ 可莉"]);
        assert!(!literal.definitely_same_path(&split));
        assert!(!literal.may_overlap(&split));
    }

    #[test]
    fn remote_posix_names_do_not_gain_native_windows_aliases() {
        let remote = |path: &str| {
            FileResource::new(
                Location {
                    backend: Backend::Ssh {
                        host: "example.invalid".into(),
                        user: None,
                        port: None,
                    },
                    path: path.into(),
                },
                Scope::Exact,
            )
        };
        assert!(!remote(r"D:\workspace\file").may_overlap(&remote(r"\\?\D:\workspace\file")));
    }

    proptest! {
        #![proptest_config(ProptestConfig::with_cases(64))]

        #[test]
        fn drive_and_unc_prefix_spellings_preserve_aliases_and_sibling_separation(
            left in "[a-z]{1,10}",
            right in "[a-z]{1,10}",
        ) {
            prop_assume!(left != right);
            for (normal, extended) in [
                (r"D:\root\", r"\\?\d:\root\"),
                (r"\\server\share\root\", r"\\?\UNC\SERVER\SHARE\root\"),
            ] {
                let aliases = [file(format!("{normal}leaf-{left}")), file(format!("{extended}leaf-{left}"))];
                let siblings = [file(format!("{normal}leaf-{right}")), file(format!("{extended}leaf-{right}"))];
                prop_assert!(aliases[0].definitely_same_path(&aliases[1]));
                for alias in &aliases {
                    for sibling in &siblings {
                        prop_assert!(!alias.may_overlap(sibling));
                        prop_assert!(!sibling.may_overlap(alias));
                    }
                }
            }
        }
    }
}

#[cfg(test)]
mod sharing_tests {
    use super::*;

    #[test]
    fn cloned_resources_share_normalized_paths_but_parent_is_independent() {
        let child = FileResource::new(
            Location {
                backend: Backend::Local,
                path: "/root/child/leaf".into(),
            },
            Scope::Tree,
        );
        let cloned = child.clone();
        assert!(Arc::ptr_eq(&child.path, &cloned.path));
        assert_eq!(child, cloned);
        assert_eq!(format!("{child:?}"), format!("{cloned:?}"));
        let parent = child.parent().expect("child has a parent");
        assert!(!Arc::ptr_eq(&child.path, &parent.path));
        assert_eq!(parent.location.path, "/root/child");
        assert_eq!(parent.path.parts, ["root", "child"]);
        assert_eq!(child.path.parts, ["root", "child", "leaf"]);
        assert_eq!(cloned.path.parts, child.path.parts);
        assert_eq!(child.scope, Scope::Tree);
        assert_eq!(parent.scope, Scope::Exact);
    }
}
