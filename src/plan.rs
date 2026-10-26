//! Effect-aware copy planning. Only proven regular-file assignments are rewritten.

use std::collections::{BTreeMap, BTreeSet};
use std::fmt;
use std::hash::{DefaultHasher, Hash, Hasher};
use std::path::{Component, Path, PathBuf};

use crate::compiler::{Backend, CopyOp, Location, Program};

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum FileClass {
    Missing,
    Regular,
    Directory,
    Stream,
    #[default]
    Unknown,
}

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct OperationKinds {
    pub source: FileClass,
    pub destination: FileClass,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum AccessScope {
    Exact,
    Tree,
}

#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
struct ResourceKey {
    endpoint: String,
    components: Vec<String>,
}

#[derive(Clone, Debug)]
pub struct Resource {
    pub location: Location,
    pub scope: AccessScope,
    key: ResourceKey,
    comparison: Vec<String>,
    possible_windows_path: Option<Vec<String>>,
    // A collision only adds a conservative dependency; it never justifies DSE.
    identity_hint: Option<u64>,
}

impl Resource {
    pub fn overlaps(&self, other: &Self) -> bool {
        if self.key.endpoint == "*" || other.key.endpoint == "*" {
            return true;
        }
        if let (Some(left), Some(right)) =
            (&self.possible_windows_path, &other.possible_windows_path)
            && (left.starts_with(right) || right.starts_with(left))
        {
            return true;
        }
        if self.key.endpoint != other.key.endpoint {
            return false;
        }
        if self.identity_hint.is_some() && self.identity_hint == other.identity_hint {
            return true;
        }
        let left = &self.comparison;
        let right = &other.comparison;
        // Even an exact file write changes whether its path can be a parent of
        // another access. Sibling files remain independent; ancestor paths do not.
        left.starts_with(right) || right.starts_with(left)
    }

    fn same_path(&self, other: &Self) -> bool {
        self.key == other.key
    }
}

#[derive(Clone, Debug, Default)]
pub struct Effects {
    pub reads: Vec<Resource>,
    pub writes: Vec<Resource>,
    /// Reads of streams consume bytes; they are not ordinary repeatable reads.
    pub consumes: Vec<Resource>,
    pub observable: bool,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Hazard {
    ReadAfterWrite,
    WriteAfterRead,
    WriteAfterWrite,
    ConsumingRead,
}

#[derive(Clone, Debug)]
pub struct Dependency {
    pub before: usize,
    pub after: usize,
    pub hazards: Vec<Hazard>,
}

#[derive(Clone, Debug)]
pub struct PlanNode {
    pub id: usize,
    pub original_index: usize,
    pub operation: CopyOp,
    pub kinds: OperationKinds,
    pub dependencies: Vec<usize>,
    pub effects: Effects,
}

#[derive(Clone, Debug)]
pub struct RemovedOperation {
    pub original_index: usize,
    pub overwritten_by: usize,
}

#[derive(Clone, Debug)]
pub struct ForwardedSource {
    pub original_index: usize,
    pub from: Location,
    pub to: Location,
}

#[derive(Clone, Debug)]
pub struct StreamConnection {
    pub producer: usize,
    pub consumer: usize,
    pub resource: Resource,
}

#[derive(Clone, Debug)]
pub struct PlanIssue {
    pub nodes: Vec<usize>,
    pub message: String,
}

#[derive(Clone, Debug, Default)]
pub struct Plan {
    pub nodes: Vec<PlanNode>,
    pub edges: Vec<Dependency>,
    pub removed: Vec<RemovedOperation>,
    pub forwarded: Vec<ForwardedSource>,
    pub optimization_rounds: usize,
    pub stream_groups: Vec<Vec<usize>>,
    pub stream_connections: Vec<StreamConnection>,
    pub issues: Vec<PlanIssue>,
}

impl Plan {
    /// Missing inspection rows are unknown, never assumed to be ordinary files.
    pub fn build(program: &Program, kinds: &[OperationKinds]) -> Self {
        let mut trace = crate::telemetry::span("plan");
        let mut cache = ResourceCache::default();
        let mut work = classify(program, kinds, &mut cache);
        let mut plan = Self::default();
        loop {
            plan.optimization_rounds += 1;
            let forwarded = forward_sources(&mut work, &mut cache, &mut plan.forwarded);
            let removed = remove_dead_writes(&mut work, &mut plan.removed);
            if !forwarded && !removed {
                break;
            }
        }
        for working in work.into_iter().filter(|node| node.alive) {
            plan.nodes.push(PlanNode {
                id: plan.nodes.len(),
                original_index: working.original_index,
                operation: working.operation,
                kinds: working.kinds,
                dependencies: Vec::new(),
                effects: working.effects,
            });
        }
        plan.connect_streams();
        plan.build_dependencies();
        plan.validate_stream_groups();
        plan.removed.sort_by_key(|removed| removed.original_index);
        trace.metric("input_operations", program.operations.len() as u64);
        trace.metric("operations", plan.nodes.len() as u64);
        trace.metric("eliminated", plan.removed.len() as u64);
        trace.metric("dependencies", plan.edges.len() as u64);
        trace.metric("stream_groups", plan.stream_groups.len() as u64);
        plan
    }

    pub fn offline(program: &Program) -> Self {
        Self::build(program, &[])
    }

    pub fn program(&self) -> Program {
        Program {
            operations: self
                .nodes
                .iter()
                .map(|node| node.operation.clone())
                .collect(),
        }
    }

    fn build_dependencies(&mut self) {
        let words = self.nodes.len().div_ceil(64);
        let mut ancestors = vec![vec![0_u64; words]; self.nodes.len()];
        for after in 0..self.nodes.len() {
            let mut candidates = Vec::new();
            let mut inherited = vec![0_u64; words];
            for (before, previous_ancestors) in ancestors.iter().enumerate().take(after) {
                let shared: Vec<&Resource> = self
                    .stream_connections
                    .iter()
                    .filter(|connection| {
                        (connection.producer == before && connection.consumer == after)
                            || (connection.consumer == before && connection.producer == after)
                    })
                    .map(|connection| &connection.resource)
                    .collect();
                let hazards = hazards(
                    &self.nodes[before].effects,
                    &self.nodes[after].effects,
                    &shared,
                );
                if hazards.is_empty() {
                    continue;
                }
                for (word, ancestor) in inherited.iter_mut().zip(previous_ancestors) {
                    *word |= ancestor;
                }
                candidates.push((before, hazards));
            }
            ancestors[after] = inherited.clone();
            for (before, hazards) in candidates {
                ancestors[after][before / 64] |= 1_u64 << (before % 64);
                if inherited[before / 64] & (1_u64 << (before % 64)) != 0 {
                    continue;
                }
                self.nodes[after].dependencies.push(before);
                self.edges.push(Dependency {
                    before,
                    after,
                    hazards,
                });
            }
        }
    }

    fn connect_streams(&mut self) {
        let mut channels: BTreeMap<ResourceKey, ChannelRoles> = BTreeMap::new();
        for node in &self.nodes {
            if node.kinds.source == FileClass::Stream {
                let resource = &node.effects.reads[0];
                channels
                    .entry(resource.key.clone())
                    .or_insert_with(|| ChannelRoles::new(resource))
                    .readers
                    .push(node.id);
            }
            if node.kinds.destination == FileClass::Stream {
                let resource = &node.effects.writes[0];
                channels
                    .entry(resource.key.clone())
                    .or_insert_with(|| ChannelRoles::new(resource))
                    .writers
                    .push(node.id);
            }
        }
        let mut owners: Vec<usize> = (0..self.nodes.len()).collect();
        for channel in channels.into_values() {
            if channel.writers.is_empty() || channel.readers.is_empty() {
                continue;
            }
            if channel.writers.len() != channel.readers.len() {
                self.issues.push(PlanIssue {
                    nodes: channel.writers.iter().chain(&channel.readers).copied().collect(),
                    message: format!("stream channel {} has {} producer(s) and {} consumer(s); use one producer and one consumer per channel or separate the channels explicitly", channel.resource.location, channel.writers.len(), channel.readers.len()),
                });
                continue;
            }
            for (producer, consumer) in channel.writers.into_iter().zip(channel.readers) {
                if producer == consumer {
                    self.issues.push(PlanIssue {
                        nodes: vec![producer],
                        message:
                            "a stream cannot feed itself; choose distinct input and output channels"
                                .into(),
                    });
                    continue;
                }
                let left = owner(&owners, producer);
                let right = owner(&owners, consumer);
                owners[left.max(right)] = left.min(right);
                self.stream_connections.push(StreamConnection {
                    producer,
                    consumer,
                    resource: channel.resource.clone(),
                });
            }
        }
        let mut groups: BTreeMap<usize, Vec<usize>> = BTreeMap::new();
        for node in 0..self.nodes.len() {
            groups.entry(owner(&owners, node)).or_default().push(node);
        }
        self.stream_groups = groups
            .into_values()
            .filter(|nodes| nodes.len() > 1)
            .collect();
    }

    fn validate_stream_groups(&mut self) {
        for group in &self.stream_groups {
            if self
                .edges
                .iter()
                .any(|edge| group.contains(&edge.before) && group.contains(&edge.after))
            {
                self.issues.push(PlanIssue { nodes: group.clone(), message: "a stream co-start group also requires one member to finish before another can start; split the channels or remove the conflicting file dependency".into() });
            }
            let mut incoming = vec![0_usize; self.nodes.len()];
            let connections: Vec<_> = self
                .stream_connections
                .iter()
                .filter(|connection| group.contains(&connection.producer))
                .collect();
            for connection in &connections {
                incoming[connection.consumer] += 1;
            }
            let mut ready: Vec<usize> = group
                .iter()
                .copied()
                .filter(|&node| incoming[node] == 0)
                .collect();
            let mut completed = 0;
            while let Some(node) = ready.pop() {
                completed += 1;
                for connection in connections
                    .iter()
                    .filter(|connection| connection.producer == node)
                {
                    incoming[connection.consumer] -= 1;
                    if incoming[connection.consumer] == 0 {
                        ready.push(connection.consumer);
                    }
                }
            }
            if completed != group.len() {
                self.issues.push(PlanIssue { nodes: group.clone(), message: "a closed stream cycle has no independent source/sink; break the cycle or use distinct channels".into() });
            }
        }
        self.validate_group_dependencies();
    }

    fn validate_group_dependencies(&mut self) {
        let mut owners: Vec<usize> = (0..self.nodes.len()).collect();
        for group in &self.stream_groups {
            for &node in group {
                owners[node] = group[0];
            }
        }
        let groups: BTreeSet<usize> = owners.iter().copied().collect();
        let edges: BTreeSet<(usize, usize)> = self
            .edges
            .iter()
            .map(|edge| (owners[edge.before], owners[edge.after]))
            .filter(|(before, after)| before != after)
            .collect();
        let mut incoming = vec![0_usize; self.nodes.len()];
        let mut followers = vec![Vec::new(); self.nodes.len()];
        for (before, after) in edges {
            incoming[after] += 1;
            followers[before].push(after);
        }
        let mut ready: Vec<usize> = groups
            .iter()
            .copied()
            .filter(|&group| incoming[group] == 0)
            .collect();
        let mut completed = 0;
        while let Some(group) = ready.pop() {
            completed += 1;
            for &after in &followers[group] {
                incoming[after] -= 1;
                if incoming[after] == 0 {
                    ready.push(after);
                }
            }
        }
        if completed != groups.len() {
            self.issues.push(PlanIssue {
                nodes: owners.iter().enumerate().filter(|(_, group)| incoming[**group] > 0).map(|(node, _)| node).collect(),
                message: "stream co-start groups form a completion-dependency cycle; separate the channels or remove the conflicting dependency".into(),
            });
        }
    }
}

struct ChannelRoles {
    resource: Resource,
    writers: Vec<usize>,
    readers: Vec<usize>,
}

impl ChannelRoles {
    fn new(resource: &Resource) -> Self {
        Self {
            resource: resource.clone(),
            writers: Vec::new(),
            readers: Vec::new(),
        }
    }
}

fn owner(owners: &[usize], mut node: usize) -> usize {
    while owners[node] != node {
        node = owners[node];
    }
    node
}

struct WorkingNode {
    original_index: usize,
    operation: CopyOp,
    kinds: OperationKinds,
    effects: Effects,
    alive: bool,
}

impl WorkingNode {
    fn assignment(&self) -> bool {
        self.kinds.source == FileClass::Regular
            && matches!(
                self.kinds.destination,
                FileClass::Regular | FileClass::Missing
            )
            && self.operation.pattern.is_none()
            && !self.operation.destination.path.ends_with('/')
            && !self.effects.reads[0].overlaps(&self.effects.writes[0])
            && self.effects.reads[0].key.endpoint != "*"
            && self.effects.writes[0].key.endpoint != "*"
            && !self.effects.observable
    }
}

#[derive(Default)]
struct ResourceCache(BTreeMap<(String, String), Resource>);

impl ResourceCache {
    fn get(&mut self, location: &Location, scope: AccessScope) -> Resource {
        let key = (location.backend.to_string(), location.path.clone());
        let resource = self.0.entry(key).or_insert_with(|| resource(location));
        let mut result = resource.clone();
        result.scope = scope;
        result
    }
}

fn classify(
    program: &Program,
    kinds: &[OperationKinds],
    cache: &mut ResourceCache,
) -> Vec<WorkingNode> {
    let mut states: Vec<(Resource, FileClass)> = Vec::new();
    let mut nodes = Vec::with_capacity(program.operations.len());
    for (index, operation) in program.operations.iter().enumerate() {
        let mut inspected = kinds.get(index).copied().unwrap_or_default();
        if matches!(operation.source.backend, Backend::Tcp { .. }) {
            inspected.source = FileClass::Stream;
        }
        if matches!(operation.destination.backend, Backend::Tcp { .. }) {
            inspected.destination = FileClass::Stream;
        }
        let source = cache.get(&operation.source, AccessScope::Tree);
        let destination = cache.get(&operation.destination, AccessScope::Tree);
        let kinds = OperationKinds {
            source: inferred_kind(&states, &source, inspected.source),
            destination: inferred_kind(&states, &destination, inspected.destination),
        };
        let effects = effects(operation, kinds, cache);
        let resulting = match (kinds.source, kinds.destination) {
            (_, FileClass::Stream) => FileClass::Stream,
            (FileClass::Regular | FileClass::Stream, _) => FileClass::Regular,
            (FileClass::Directory, _) => FileClass::Directory,
            _ => FileClass::Unknown,
        };
        if operation.destination.path.ends_with('/') && kinds.destination != FileClass::Stream {
            states.push((destination, FileClass::Directory));
        }
        for write in &effects.writes {
            states.push((write.clone(), resulting));
        }
        nodes.push(WorkingNode {
            original_index: index,
            operation: operation.clone(),
            kinds,
            effects,
            alive: true,
        });
    }
    nodes
}

fn inferred_kind(
    states: &[(Resource, FileClass)],
    resource: &Resource,
    inspected: FileClass,
) -> FileClass {
    if inspected == FileClass::Stream {
        return inspected;
    }
    for (previous, kind) in states.iter().rev() {
        if previous.same_path(resource) {
            return *kind;
        }
        if previous.key.endpoint == resource.key.endpoint
            && previous.key.components.len() > resource.key.components.len()
            && previous
                .key
                .components
                .starts_with(&resource.key.components)
        {
            return FileClass::Directory;
        }
        if previous.overlaps(resource) {
            return if inspected == FileClass::Directory {
                inspected
            } else {
                FileClass::Unknown
            };
        }
    }
    inspected
}

fn effects(operation: &CopyOp, kinds: OperationKinds, cache: &mut ResourceCache) -> Effects {
    let source_tree = matches!(
        kinds.source,
        FileClass::Directory | FileClass::Missing | FileClass::Unknown
    ) || operation.pattern.is_some();
    let container =
        operation.destination.path.ends_with('/') && kinds.destination != FileClass::Stream;
    let preserved_name = container.then(|| basename(&operation.source)).flatten();
    let destination_tree = source_tree
        || kinds.destination == FileClass::Directory
        || (container && preserved_name.is_none());
    let source = cache.get(
        &operation.source,
        if source_tree {
            AccessScope::Tree
        } else {
            AccessScope::Exact
        },
    );
    let mut target = operation.destination.clone();
    if operation.pattern.is_none()
        && let Some(name) = preserved_name
    {
        target.path = format!("{}{name}", operation.destination.path);
    }
    let target = cache.get(
        &target,
        if destination_tree {
            AccessScope::Tree
        } else {
            AccessScope::Exact
        },
    );
    let consuming = matches!(kinds.source, FileClass::Stream | FileClass::Unknown);
    Effects {
        reads: vec![source.clone()],
        writes: vec![target],
        consumes: if consuming { vec![source] } else { Vec::new() },
        observable: matches!(
            kinds.source,
            FileClass::Stream | FileClass::Unknown | FileClass::Missing
        ) || matches!(kinds.destination, FileClass::Stream | FileClass::Unknown),
    }
}

fn basename(location: &Location) -> Option<String> {
    if matches!(location.backend, Backend::Local) {
        return local_absolute(&location.path)
            .file_name()
            .and_then(|name| name.to_str())
            .map(str::to_owned);
    }
    let path = location.path.trim_end_matches('/');
    let name = if cfg!(windows) && matches!(location.backend, Backend::Local) {
        path.rsplit(['/', '\\']).next()?
    } else {
        path.rsplit('/').next()?
    };
    (!matches!(name, "" | "." | ".." | "~" | "$pwd")).then(|| name.to_owned())
}

fn forward_sources(
    nodes: &mut [WorkingNode],
    cache: &mut ResourceCache,
    records: &mut Vec<ForwardedSource>,
) -> bool {
    let mut changed = false;
    for index in 0..nodes.len() {
        if !nodes[index].alive || !nodes[index].assignment() {
            continue;
        }
        let read = &nodes[index].effects.reads[0];
        let producer = (0..index).rev().find(|&previous| {
            nodes[previous].alive
                && nodes[previous]
                    .effects
                    .writes
                    .iter()
                    .any(|write| write.overlaps(read))
        });
        let Some(producer) = producer else {
            continue;
        };
        let origin = &nodes[producer].effects.reads[0];
        let origin_backend = &nodes[producer].operation.source.backend;
        let crosses_remote_snapshot = !matches!(origin_backend, Backend::Local)
            && origin_backend != &nodes[index].operation.source.backend;
        if !nodes[producer].assignment()
            || !nodes[producer].effects.writes[0].same_path(read)
            || origin.overlaps(&nodes[index].effects.writes[0])
            || nodes[index].operation.source == nodes[producer].operation.source
            || matches!(origin_backend, Backend::Http { .. })
            || crosses_remote_snapshot
        {
            continue;
        }
        let modified = nodes[producer + 1..index].iter().any(|node| {
            node.alive
                && node
                    .effects
                    .writes
                    .iter()
                    .chain(&node.effects.consumes)
                    .any(|write| write.overlaps(origin))
        });
        if modified {
            continue;
        }
        let from = nodes[index].operation.source.clone();
        let to = nodes[producer].operation.source.clone();
        nodes[index].operation.source = to.clone();
        nodes[index].effects = effects(&nodes[index].operation, nodes[index].kinds, cache);
        records.push(ForwardedSource {
            original_index: nodes[index].original_index,
            from,
            to,
        });
        changed = true;
    }
    changed
}

fn remove_dead_writes(nodes: &mut [WorkingNode], records: &mut Vec<RemovedOperation>) -> bool {
    let mut changed = false;
    for index in (0..nodes.len()).rev() {
        if !nodes[index].alive || !nodes[index].assignment() {
            continue;
        }
        let written = &nodes[index].effects.writes[0];
        let mut overwriter = None;
        for (later, next) in nodes.iter().enumerate().skip(index + 1) {
            if !next.alive {
                continue;
            }
            if next
                .effects
                .reads
                .iter()
                .chain(&next.effects.consumes)
                .any(|read| read.overlaps(written))
            {
                break;
            }
            if !next
                .effects
                .writes
                .iter()
                .any(|write| write.overlaps(written))
            {
                continue;
            }
            if next.assignment() && next.effects.writes[0].same_path(written) {
                overwriter = Some(later);
            }
            break;
        }
        if let Some(overwriter) = overwriter {
            records.push(RemovedOperation {
                original_index: nodes[index].original_index,
                overwritten_by: nodes[overwriter].original_index,
            });
            nodes[index].alive = false;
            changed = true;
        }
    }
    changed
}

fn hazards(before: &Effects, after: &Effects, shared: &[&Resource]) -> Vec<Hazard> {
    let mut result = Vec::new();
    if intersects(&before.writes, &after.reads, shared) {
        result.push(Hazard::ReadAfterWrite);
    }
    if intersects(&before.reads, &after.writes, shared) {
        result.push(Hazard::WriteAfterRead);
    }
    if intersects(&before.writes, &after.writes, shared) {
        result.push(Hazard::WriteAfterWrite);
    }
    if intersects(&before.consumes, &after.reads, shared)
        || intersects(&before.reads, &after.consumes, shared)
        || intersects(&before.consumes, &after.writes, shared)
        || intersects(&before.writes, &after.consumes, shared)
    {
        result.push(Hazard::ConsumingRead);
    }
    result
}

fn intersects(left: &[Resource], right: &[Resource], shared: &[&Resource]) -> bool {
    left.iter().any(|left| {
        right.iter().any(|right| {
            left.overlaps(right)
                && !shared
                    .iter()
                    .any(|channel| left.same_path(channel) && right.same_path(channel))
        })
    })
}

fn resource(location: &Location) -> Resource {
    let mut path = location.path.clone();
    if matches!(location.backend, Backend::Tcp { .. }) {
        path = "/".into();
    }
    let mut identity_hint = None;
    if matches!(location.backend, Backend::Local) {
        let expanded = local_absolute(&path);
        let named_pipe = cfg!(windows)
            && path
                .replace('/', "\\")
                .to_ascii_lowercase()
                .starts_with(r"\\.\pipe\");
        if named_pipe {
            path = expanded.to_string_lossy().into_owned();
        } else {
            // Opening a FIFO/socket can block or consume a connection. Metadata
            // inspection is sufficient to restrict identity handles to files/dirs.
            let ordinary = std::fs::symlink_metadata(&expanded)
                .is_ok_and(|metadata| metadata.is_file() || metadata.is_dir());
            if ordinary && let Ok(handle) = same_file::Handle::from_path(&expanded) {
                let mut hash = DefaultHasher::new();
                handle.hash(&mut hash);
                identity_hint = Some(hash.finish());
            }
            path = canonical_existing_prefix(&expanded)
                .to_string_lossy()
                .into_owned();
        }
    }
    if cfg!(windows) && matches!(location.backend, Backend::Local) {
        path = path.replace('\\', "/");
    }
    let mut components: Vec<String> = Vec::new();
    for part in path.split('/') {
        match part {
            "" | "." => {}
            ".." if components.last().is_some_and(|last| last != "..") => {
                components.pop();
            }
            part => components.push(part.to_string()),
        }
    }
    let endpoint = match location.backend {
        Backend::Env | Backend::Path => "*".into(),
        _ => location.backend.to_string(),
    };
    let comparison = if cfg!(windows) && matches!(location.backend, Backend::Local) {
        components.iter().map(|part| part.to_uppercase()).collect()
    } else {
        components.clone()
    };
    let possible_windows_path = possible_windows_path(location, &components);
    Resource {
        location: location.clone(),
        scope: AccessScope::Exact,
        key: ResourceKey {
            endpoint,
            components,
        },
        comparison,
        possible_windows_path,
        identity_hint,
    }
}

fn possible_windows_path(location: &Location, components: &[String]) -> Option<Vec<String>> {
    #[cfg(not(windows))]
    {
        let _ = (location, components);
        None
    }
    #[cfg(windows)]
    {
        match location.backend {
            Backend::Local => drive_components(components),
            Backend::Wsl if location.path.starts_with('/') => {
                let [mount, drive, rest @ ..] = components else {
                    return None;
                };
                if mount != "mnt" || drive.len() != 1 || !drive.as_bytes()[0].is_ascii_alphabetic()
                {
                    return None;
                }
                let native =
                    PathBuf::from(format!("{}:\\", drive.to_uppercase())).join(rest.join("\\"));
                let canonical = canonical_existing_prefix(&native)
                    .to_string_lossy()
                    .replace('\\', "/");
                drive_components(&canonical.split('/').map(str::to_owned).collect::<Vec<_>>())
            }
            Backend::Wsl if !location.path.starts_with('~') && !location.path.contains('\\') => {
                let canonical = canonical_existing_prefix(&local_absolute(&location.path))
                    .to_string_lossy()
                    .replace('\\', "/");
                drive_components(&canonical.split('/').map(str::to_owned).collect::<Vec<_>>())
            }
            _ => None,
        }
    }
}

#[cfg(windows)]
fn drive_components(components: &[String]) -> Option<Vec<String>> {
    let start = components.iter().position(|part| {
        part.len() == 2 && part.as_bytes()[0].is_ascii_alphabetic() && part.ends_with(':')
    })?;
    Some(
        components[start..]
            .iter()
            .map(|part| part.to_uppercase())
            .collect(),
    )
}

fn local_absolute(path: &str) -> PathBuf {
    let path = if path == "$pwd" {
        ".".into()
    } else if let Some(suffix) = path.strip_prefix("$pwd/") {
        format!("./{suffix}")
    } else if path == "~" || path.starts_with("~/") || path.starts_with("~\\") {
        let home = std::env::var_os("HOME").or_else(|| std::env::var_os("USERPROFILE"));
        home.map(|home| {
            PathBuf::from(home)
                .join(path.get(2..).unwrap_or(""))
                .to_string_lossy()
                .into_owned()
        })
        .unwrap_or_else(|| path.to_string())
    } else {
        path.to_string()
    };
    let path = Path::new(&path);
    let absolute = if path.is_absolute() {
        path.to_owned()
    } else {
        std::env::current_dir().unwrap_or_default().join(path)
    };
    let mut normalized = PathBuf::new();
    for component in absolute.components() {
        match component {
            Component::CurDir => {}
            Component::ParentDir => {
                normalized.pop();
            }
            part => normalized.push(part.as_os_str()),
        }
    }
    normalized
}

fn canonical_existing_prefix(path: &Path) -> PathBuf {
    for ancestor in path.ancestors() {
        if let Ok(canonical) = ancestor.canonicalize() {
            return path
                .strip_prefix(ancestor)
                .map(|suffix| canonical.join(suffix))
                .unwrap_or(canonical);
        }
    }
    path.to_owned()
}

impl fmt::Display for Plan {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        writeln!(
            f,
            "plan: {} operation(s), {} eliminated, {} forwarded, {} dependency edge(s), {} co-start group(s)",
            self.nodes.len(),
            self.removed.len(),
            self.forwarded.len(),
            self.edges.len(),
            self.stream_groups.len()
        )?;
        for node in &self.nodes {
            let kind = if node.kinds.source == FileClass::Stream
                || node.kinds.destination == FileClass::Stream
            {
                "relay"
            } else {
                "copy"
            };
            write!(f, "{:04} {kind} {}", node.id, node.operation.source)?;
            if let Some(pattern) = &node.operation.pattern {
                write!(f, " match {pattern:?}")?;
            }
            writeln!(
                f,
                " -> {}  depends={:?}  source={:?} destination={:?}",
                node.operation.destination,
                node.dependencies,
                node.kinds.source,
                node.kinds.destination
            )?;
        }
        for removed in &self.removed {
            writeln!(
                f,
                "removed #{}: overwritten by #{} before any read",
                removed.original_index, removed.overwritten_by
            )?;
        }
        for forwarded in &self.forwarded {
            writeln!(
                f,
                "forwarded #{}: {} => {}",
                forwarded.original_index, forwarded.from, forwarded.to
            )?;
        }
        for (index, group) in self.stream_groups.iter().enumerate() {
            writeln!(f, "co-start {index}: {group:?}")?;
        }
        for connection in &self.stream_connections {
            writeln!(
                f,
                "channel {}: #{} => #{} (co-start, not completion)",
                connection.resource.location, connection.producer, connection.consumer
            )?;
        }
        for issue in &self.issues {
            writeln!(f, "invalid plan {:?}: {}", issue.nodes, issue.message)?;
        }
        Ok(())
    }
}
