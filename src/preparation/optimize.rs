//! Epoch-local typed counterpart of the mature regular-file fixed point.
//! Literal values stay literals; they are never encoded as synthetic endpoints.

use std::collections::{BTreeMap, BTreeSet, HashMap};
use std::sync::Arc;

use crate::compiler::{CopyOp, Program as Copies};
use crate::endpoint::{Backend, Location};
use crate::hir::effects::{EndpointKey, EndpointRole, OperationFacts};
use crate::hir::{
    self, Input, Instruction, InstructionId, InstructionKind, Output, ReadPort, Region,
};
use crate::language::DestinationMode;
use crate::plan::{FileClass, OperationKinds, Plan, Resource};

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Value {
    File {
        location: Location,
        pattern: Option<String>,
    },
    Literal(Arc<str>),
    Port {
        process: hir::ProcessId,
        port: ReadPort,
    },
}

pub(super) fn value(input: &Input) -> Value {
    match input {
        Input::File { location, pattern } => Value::File {
            location: location.clone(),
            pattern: pattern.clone(),
        },
        Input::Literal(text) => Value::Literal(text.clone()),
        Input::Port { process, port } => Value::Port {
            process: *process,
            port: *port,
        },
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum RewriteKind {
    Forward {
        producer: InstructionId,
        from: Box<Value>,
        to: Box<Value>,
    },
    DeadWrite {
        overwritten_by: InstructionId,
    },
    RedundantWrite {
        same_as: InstructionId,
    },
    CoalesceCreation {
        overwritten_by: InstructionId,
    },
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Rewrite {
    pub instruction: InstructionId,
    /// The full observation-free epoch, shared immutably by its rewrite records.
    pub epoch: Arc<[InstructionId]>,
    pub kind: RewriteKind,
}

pub(super) fn run(
    original: &hir::Program,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    resources: &HashMap<Location, Resource>,
    unstable: &BTreeSet<EndpointKey>,
    use_legacy: bool,
) -> (hir::Program, Vec<Rewrite>) {
    let mut output = original.clone();
    let mut rewrites = Vec::new();
    region(
        &mut output.body,
        facts,
        resources,
        unstable,
        use_legacy,
        &mut rewrites,
    );
    (output, rewrites)
}

fn region(
    region: &mut Region,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    resources: &HashMap<Location, Resource>,
    unstable: &BTreeSet<EndpointKey>,
    use_legacy: bool,
    records: &mut Vec<Rewrite>,
) {
    let mut output = Vec::new();
    let mut epoch = Vec::new();
    for node in std::mem::take(&mut region.instructions) {
        if eligible(&node, facts, resources) {
            epoch.push(node);
            continue;
        }
        flush(
            &mut epoch,
            &mut output,
            resources,
            facts,
            unstable,
            use_legacy,
            records,
        );
        output.push(node);
    }
    flush(
        &mut epoch,
        &mut output,
        resources,
        facts,
        unstable,
        use_legacy,
        records,
    );
    region.instructions = output;
}

fn eligible(
    node: &Instruction,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    resources: &HashMap<Location, Resource>,
) -> bool {
    let Some(facts) = facts.get(&node.id) else {
        return false;
    };
    if facts.source != FileClass::Regular
        || !matches!(facts.destination, FileClass::Regular | FileClass::Missing)
    {
        return false;
    }
    let InstructionKind::Transfer {
        source,
        destination:
            Output::File {
                location,
                mode: DestinationMode::Exact,
            },
    } = &node.kind
    else {
        return false;
    };
    match source {
        Input::Literal(_) => true,
        Input::File {
            location: source,
            pattern: None,
        } => {
            !matches!(
                source.backend,
                Backend::Http { .. } | Backend::Tcp { .. } | Backend::Path | Backend::Env
            ) && !resources[source].overlaps(&resources[location])
        }
        _ => false,
    }
}

fn flush(
    epoch: &mut Vec<Instruction>,
    output: &mut Vec<Instruction>,
    resources: &HashMap<Location, Resource>,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    unstable: &BTreeSet<EndpointKey>,
    use_legacy: bool,
    records: &mut Vec<Rewrite>,
) {
    if epoch.is_empty() {
        return;
    }
    let original = std::mem::take(epoch);
    let ids = original.iter().map(|node| node.id).collect::<Arc<[_]>>();
    let permissions = permissions(&original, facts, resources, unstable);
    let mut current_facts = original
        .iter()
        .map(|node| (node.id, facts[&node.id]))
        .collect();
    let mut nodes = original.iter().cloned().map(Some).collect::<Vec<_>>();
    let mut trace = Vec::new();
    loop {
        let forwarded = forward(&mut nodes, resources, &ids, &mut trace);
        let redundant = remove_redundant(&mut nodes, resources, &permissions, &ids, &mut trace);
        let coalesced = coalesce_creation(
            &mut nodes,
            resources,
            &mut current_facts,
            &permissions,
            &ids,
            &mut trace,
        );
        let removed = eliminate(
            &mut nodes,
            resources,
            &current_facts,
            &permissions,
            &ids,
            &mut trace,
        );
        if !forwarded && !redundant && !coalesced && !removed {
            break;
        }
    }
    let candidate = nodes.into_iter().flatten().collect::<Vec<_>>();
    if use_legacy
        && original.iter().all(|node| {
            matches!(
                &node.kind,
                InstructionKind::Transfer {
                    source: Input::File { .. },
                    ..
                }
            )
        })
    {
        let legacy = legacy(&original, facts);
        // Existing file rules remain the primary implementation. The typed
        // replay cross-check keeps literal-aware certificates self-contained.
        if same_assignments(&legacy, &candidate) {
            output.extend(legacy);
            records.extend(trace);
            return;
        }
    }
    output.extend(candidate);
    records.extend(trace);
}

/// Query one retained prefix/suffix without rescanning unrelated assignments.
struct History<'a> {
    index: crate::plan::index::ResourceIndex,
    entries: Vec<Option<&'a Resource>>,
    native: Vec<usize>,
    wsl: Vec<usize>,
}

impl<'a> History<'a> {
    fn new(size: usize) -> Self {
        Self {
            index: Default::default(),
            entries: vec![None; size],
            native: Vec::new(),
            wsl: Vec::new(),
        }
    }

    fn insert(&mut self, resource: &'a Resource, id: usize) {
        self.index.insert(resource, id);
        self.entries[id] = Some(resource);
        match resource.location.backend {
            Backend::Local => self.native.push(id),
            Backend::Wsl => self.wsl.push(id),
            _ => {}
        }
    }

    fn latest(&self, resource: &Resource) -> Option<usize> {
        self.extreme(resource, true)
    }
    fn earliest(&self, resource: &Resource) -> Option<usize> {
        self.extreme(resource, false)
    }

    fn extreme(&self, resource: &Resource, latest: bool) -> Option<usize> {
        let cross = match resource.location.backend {
            Backend::Local => &self.wsl,
            Backend::Wsl => &self.native,
            _ => {
                return if latest {
                    self.index.latest(resource)
                } else {
                    self.index.earliest(resource)
                };
            }
        };
        if cross.is_empty() {
            return if latest {
                self.index.latest(resource)
            } else {
                self.index.earliest(resource)
            };
        }
        // Preparation requires an explicit native/WSL separation proof; the
        // physical planner's ordinary path/projection index does not encode it.
        let mut candidates = BTreeSet::new();
        self.index.candidates(resource, &mut candidates);
        candidates.extend(cross);
        let retained = |id: &usize| self.entries[*id].is_some_and(|entry| entry.overlaps(resource));
        if latest {
            candidates.into_iter().rev().find(retained)
        } else {
            candidates.into_iter().find(retained)
        }
    }
}

fn forward(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    let mut writes = History::new(nodes.len());
    for index in 0..nodes.len() {
        if let Some((producer, replacement)) = forwarding(nodes, index, resources, &writes) {
            let node = nodes[index].as_ref().expect("retained consumer");
            records.push(Rewrite {
                instruction: node.id,
                epoch: epoch.clone(),
                kind: RewriteKind::Forward {
                    producer: nodes[producer].as_ref().expect("retained producer").id,
                    from: Box::new(value(source(node))),
                    to: Box::new(value(&replacement)),
                },
            });
            let InstructionKind::Transfer { source, .. } =
                &mut nodes[index].as_mut().expect("retained consumer").kind
            else {
                unreachable!()
            };
            *source = replacement;
            changed = true;
        }
        if let Some(node) = &nodes[index] {
            writes.insert(&resources[destination(node)], index);
        }
    }
    changed
}

fn forwarding(
    nodes: &[Option<Instruction>],
    index: usize,
    resources: &HashMap<Location, Resource>,
    writes: &History<'_>,
) -> Option<(usize, Input)> {
    let node = nodes[index].as_ref()?;
    let Input::File { location: read, .. } = source(node) else {
        return None;
    };
    let read_resource = &resources[read];
    let producer = writes.latest(read_resource)?;
    let producer_node = nodes[producer].as_ref().expect("retained producer");
    if !resources[destination(producer_node)].same_path(read_resource) {
        return None;
    }
    let replacement = source(producer_node);
    // A fresh literal cannot preserve the creation mtime of a materialized file.
    if matches!(replacement, Input::Literal(_)) || value(replacement) == value(source(node)) {
        return None;
    }
    if let Input::File {
        location: origin, ..
    } = replacement
    {
        let crosses_snapshot =
            !matches!(origin.backend, Backend::Local) && origin.backend != read.backend;
        if crosses_snapshot
            || resources[origin].overlaps(&resources[destination(node)])
            || writes
                .latest(&resources[origin])
                .is_some_and(|latest| latest > producer)
        {
            return None;
        }
    }
    Some((producer, replacement.clone()))
}

fn remove_redundant(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    permissions: &BTreeMap<InstructionId, Permission>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    let mut writes = History::new(nodes.len());
    let mut reads = History::new(nodes.len());
    for index in 0..nodes.len() {
        if let Some(producer) = redundant(nodes, index, resources, permissions, &reads, &writes) {
            records.push(Rewrite {
                instruction: nodes[index].as_ref().expect("retained write").id,
                epoch: epoch.clone(),
                kind: RewriteKind::RedundantWrite {
                    same_as: nodes[producer].as_ref().expect("retained prior write").id,
                },
            });
            nodes[index] = None;
            changed = true;
            continue;
        }
        let Some(node) = &nodes[index] else {
            continue;
        };
        if let Input::File { location, .. } = source(node) {
            reads.insert(&resources[location], index);
        }
        writes.insert(&resources[destination(node)], index);
    }
    changed
}

fn redundant(
    nodes: &[Option<Instruction>],
    index: usize,
    resources: &HashMap<Location, Resource>,
    permissions: &BTreeMap<InstructionId, Permission>,
    reads: &History<'_>,
    writes: &History<'_>,
) -> Option<usize> {
    let node = nodes[index].as_ref()?;
    let Input::File {
        location: origin, ..
    } = source(node)
    else {
        return None;
    };
    let target = &resources[destination(node)];
    let producer = writes.latest(target)?;
    let previous = nodes[producer].as_ref().expect("retained prior write");
    if !permissions[&node.id].overwrite || permissions[&previous.id].result != Some(true) {
        return None;
    }
    let Input::File {
        location: prior, ..
    } = source(previous)
    else {
        return None;
    };
    if !target.same_path(&resources[destination(previous)])
        || !resources[origin].same_path(&resources[prior])
    {
        return None;
    }
    if writes
        .latest(&resources[origin])
        .is_some_and(|latest| latest > producer)
        || reads.latest(target).is_some_and(|latest| latest > producer)
        || reads
            .latest(&resources[origin])
            .is_some_and(|latest| latest > producer)
    {
        return None;
    }
    Some(producer)
}

fn eliminate(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    permissions: &BTreeMap<InstructionId, Permission>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    let mut reads = History::new(nodes.len());
    let mut writes = History::new(nodes.len());
    for index in (0..nodes.len()).rev() {
        if let Some(overwriter) =
            dead_write(nodes, index, resources, facts, permissions, &reads, &writes)
        {
            records.push(Rewrite {
                instruction: nodes[index].as_ref().expect("retained write").id,
                epoch: epoch.clone(),
                kind: RewriteKind::DeadWrite {
                    overwritten_by: nodes[overwriter].as_ref().expect("retained overwriter").id,
                },
            });
            nodes[index] = None;
            changed = true;
            continue;
        }
        let Some(node) = &nodes[index] else {
            continue;
        };
        if let Input::File { location, .. } = source(node) {
            reads.insert(&resources[location], index);
        }
        writes.insert(&resources[destination(node)], index);
    }
    changed
}

fn dead_write(
    nodes: &[Option<Instruction>],
    index: usize,
    resources: &HashMap<Location, Resource>,
    facts: &BTreeMap<InstructionId, OperationFacts>,
    permissions: &BTreeMap<InstructionId, Permission>,
    reads: &History<'_>,
    writes: &History<'_>,
) -> Option<usize> {
    let node = nodes[index].as_ref()?;
    // Creation owns a namespace timestamp until metadata provenance is explicit.
    if facts[&node.id].destination == FileClass::Missing
        || !permissions[&node.id].overwrite
        || permissions[&node.id].result != Some(true)
    {
        return None;
    }
    let target = &resources[destination(node)];
    let overwriter = writes.earliest(target)?;
    if reads
        .earliest(target)
        .is_some_and(|read| read <= overwriter)
    {
        return None;
    }
    let later = nodes[overwriter].as_ref().expect("retained overwriter");
    // A literal overwrite still observes inherited destination permissions.
    (resources[destination(later)].same_path(target) && matches!(source(later), Input::File { .. }))
        .then_some(overwriter)
}

#[derive(Clone, Copy)]
struct Permission {
    overwrite: bool,
    result: Option<bool>,
}

fn permissions(
    nodes: &[Instruction],
    facts: &BTreeMap<InstructionId, OperationFacts>,
    resources: &HashMap<Location, Resource>,
    unstable: &BTreeSet<EndpointKey>,
) -> BTreeMap<InstructionId, Permission> {
    let mut values = BTreeMap::new();
    let mut output = BTreeMap::new();
    for node in nodes {
        let destination = &resources[destination(node)];
        let observed = |role, resource: &Resource| {
            (!unstable.contains(&EndpointKey {
                instruction: node.id,
                role,
            }))
            .then(|| resource.writable_file())
            .flatten()
        };
        let previous = values
            .get(destination.key())
            .copied()
            .unwrap_or_else(|| observed(EndpointRole::Destination, destination));
        let missing = facts[&node.id].destination == FileClass::Missing;
        let result = match source(node) {
            Input::File { location, .. } => {
                let source = &resources[location];
                values
                    .get(source.key())
                    .copied()
                    .unwrap_or_else(|| observed(EndpointRole::Source, source))
            }
            Input::Literal(_) => {
                if missing {
                    None
                } else {
                    previous
                }
            }
            Input::Port { .. } => unreachable!("file/literal epoch"),
        };
        output.insert(
            node.id,
            Permission {
                overwrite: missing || previous == Some(true),
                result,
            },
        );
        values.insert(destination.key().clone(), result);
    }
    output
}

fn coalesce_creation(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    facts: &mut BTreeMap<InstructionId, OperationFacts>,
    permissions: &BTreeMap<InstructionId, Permission>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    for index in 0..nodes.len().saturating_sub(1) {
        let (Some(first), Some(last)) = (&nodes[index], &nodes[index + 1]) else {
            continue;
        };
        if facts[&first.id].destination != FileClass::Missing
            || !matches!(source(first), Input::File { .. })
            || !matches!(source(last), Input::File { .. })
            || permissions[&first.id].result != Some(true)
            || !permissions[&last.id].overwrite
            || !resources[destination(first)].same_path(&resources[destination(last)])
        {
            continue;
        }
        // Adjacent complete file values have no intervening observer or
        // namespace ticket. Keep the final source site and the insertion effect.
        let final_id = last.id;
        records.push(Rewrite {
            instruction: first.id,
            epoch: epoch.clone(),
            kind: RewriteKind::CoalesceCreation {
                overwritten_by: final_id,
            },
        });
        facts
            .get_mut(&final_id)
            .expect("eligible final assignment")
            .destination = FileClass::Missing;
        nodes[index] = None;
        changed = true;
    }
    changed
}

pub(super) fn refined_context(
    original: &crate::hir::graph::PlanningContext,
    rewrites: &[Rewrite],
) -> crate::hir::graph::PlanningContext {
    let mut context = original.clone();
    for rewrite in rewrites {
        let RewriteKind::CoalesceCreation { overwritten_by } = rewrite.kind else {
            continue;
        };
        context
            .operation_facts
            .get_mut(&overwritten_by)
            .expect("replayed creation has a typed final assignment")
            .destination = FileClass::Missing;
        if let Some(frontier) = context
            .existing_parent_frontiers
            .get(&rewrite.instruction)
            .cloned()
        {
            context
                .existing_parent_frontiers
                .insert(overwritten_by, frontier);
        }
        let first = EndpointKey {
            instruction: rewrite.instruction,
            role: EndpointRole::Destination,
        };
        if !context.host_unstable.contains(&first) {
            context.host_unstable.remove(&EndpointKey {
                instruction: overwritten_by,
                role: EndpointRole::Destination,
            });
        }
    }
    context
}

fn legacy(
    nodes: &[Instruction],
    facts: &BTreeMap<InstructionId, OperationFacts>,
) -> Vec<Instruction> {
    let mut copies = Copies::default();
    let mut kinds = Vec::new();
    for node in nodes {
        let Input::File { location, pattern } = source(node) else {
            unreachable!()
        };
        let mut target = destination(node).clone();
        let trimmed = target.path.trim_end_matches('/');
        if !trimmed.is_empty() {
            target.path = trimmed.to_owned();
        }
        copies.operations.push(CopyOp {
            source: location.clone(),
            destination: target,
            pattern: pattern.clone(),
            span: node.span,
        });
        let row = facts[&node.id];
        kinds.push(OperationKinds {
            source: row.source,
            destination: row.destination,
        });
    }
    Plan::build(&copies, &kinds)
        .nodes
        .into_iter()
        .map(|planned| {
            let mut node = nodes[planned.original_index].clone();
            let InstructionKind::Transfer { source, .. } = &mut node.kind else {
                unreachable!()
            };
            *source = Input::File {
                location: planned.operation.source,
                pattern: planned.operation.pattern,
            };
            node
        })
        .collect()
}

fn same_assignments(left: &[Instruction], right: &[Instruction]) -> bool {
    left.len() == right.len()
        && left.iter().zip(right).all(|(a, b)| {
            a.id == b.id && value(source(a)) == value(source(b)) && destination(a) == destination(b)
        })
}

fn source(node: &Instruction) -> &Input {
    let InstructionKind::Transfer { source, .. } = &node.kind else {
        unreachable!("assignment epoch")
    };
    source
}

fn destination(node: &Instruction) -> &Location {
    let InstructionKind::Transfer {
        destination: Output::File { location, .. },
        ..
    } = &node.kind
    else {
        unreachable!("exact-file assignment epoch")
    };
    location
}

#[cfg(test)]
mod tests;
