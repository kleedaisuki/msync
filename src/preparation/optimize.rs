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
    for mut node in std::mem::take(&mut region.instructions) {
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
        if let InstructionKind::Transaction { body, .. } = &mut node.kind {
            self::region(body, facts, resources, unstable, use_legacy, records);
        }
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

fn forward(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    for index in 0..nodes.len() {
        let Some(node) = nodes[index].as_ref() else {
            continue;
        };
        let Input::File { location: read, .. } = source(node) else {
            continue;
        };
        let read_resource = &resources[read];
        let producer = (0..index).rev().find(|previous| {
            nodes[*previous]
                .as_ref()
                .is_some_and(|node| resources[destination(node)].overlaps(read_resource))
        });
        let Some(producer) = producer else { continue };
        let producer_node = nodes[producer].as_ref().expect("retained producer");
        if !resources[destination(producer_node)].same_path(read_resource) {
            continue;
        }
        let replacement = source(producer_node).clone();
        // A literal stream has no file metadata. Once materialized, its file
        // version has a creation mtime that a later file copy must preserve.
        // Replacing that read with a fresh literal would preserve bytes only.
        if matches!(replacement, Input::Literal(_)) {
            continue;
        }
        if value(&replacement) == value(source(node)) {
            continue;
        }
        if let Input::File {
            location: origin, ..
        } = &replacement
        {
            let crosses_snapshot =
                !matches!(origin.backend, Backend::Local) && origin.backend != read.backend;
            if crosses_snapshot || resources[origin].overlaps(&resources[destination(node)]) {
                continue;
            }
            if nodes[producer + 1..index]
                .iter()
                .flatten()
                .any(|node| resources[destination(node)].overlaps(&resources[origin]))
            {
                continue;
            }
        }
        records.push(Rewrite {
            instruction: node.id,
            epoch: epoch.clone(),
            kind: RewriteKind::Forward {
                producer: producer_node.id,
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
    changed
}

fn remove_redundant(
    nodes: &mut [Option<Instruction>],
    resources: &HashMap<Location, Resource>,
    permissions: &BTreeMap<InstructionId, Permission>,
    epoch: &Arc<[InstructionId]>,
    records: &mut Vec<Rewrite>,
) -> bool {
    let mut changed = false;
    for index in 0..nodes.len() {
        let Some(node) = nodes[index].as_ref() else {
            continue;
        };
        let Input::File {
            location: origin, ..
        } = source(node)
        else {
            continue;
        };
        let target = &resources[destination(node)];
        let Some(producer) = (0..index).rev().find(|previous| {
            nodes[*previous]
                .as_ref()
                .is_some_and(|previous| resources[destination(previous)].overlaps(target))
        }) else {
            continue;
        };
        let previous = nodes[producer].as_ref().expect("retained write");
        if !permissions[&node.id].overwrite || permissions[&previous.id].result != Some(true) {
            continue;
        }
        let Input::File {
            location: prior, ..
        } = source(previous)
        else {
            continue;
        };
        if !target.same_path(&resources[destination(previous)])
            || !resources[origin].same_path(&resources[prior])
        {
            continue;
        }
        let observed_or_changed = nodes[producer + 1..index].iter().flatten().any(|between| {
            let write = &resources[destination(between)];
            if write.overlaps(&resources[origin]) || write.overlaps(target) { return true; }
            matches!(source(between), Input::File { location, .. }
                if resources[location].overlaps(target) || resources[location].overlaps(&resources[origin]))
        });
        if observed_or_changed {
            continue;
        }
        records.push(Rewrite {
            instruction: node.id,
            epoch: epoch.clone(),
            kind: RewriteKind::RedundantWrite {
                same_as: previous.id,
            },
        });
        nodes[index] = None;
        changed = true;
    }
    changed
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
    for index in (0..nodes.len()).rev() {
        let Some(node) = &nodes[index] else { continue };
        // Creation stamps its containing namespace. Keep that event until the
        // IR can carry its metadata provenance separately from payload copying.
        if facts[&node.id].destination == FileClass::Missing {
            continue;
        }
        if !permissions[&node.id].overwrite || permissions[&node.id].result != Some(true) {
            continue;
        }
        let target = &resources[destination(node)];
        let mut overwritten_by = None;
        for later in nodes[index + 1..].iter().flatten() {
            if let Input::File { location, .. } = source(later)
                && resources[location].overlaps(target)
            {
                break;
            }
            let write = &resources[destination(later)];
            if !write.overlaps(target) {
                continue;
            }
            // Literal publication replaces bytes/mtime but inherits destination
            // permissions. It therefore observes the preceding file version;
            // only a file-valued assignment can kill its metadata as well.
            if write.same_path(target) && matches!(source(later), Input::File { .. }) {
                overwritten_by = Some(later.id);
            }
            break;
        }
        if let Some(overwritten_by) = overwritten_by {
            records.push(Rewrite {
                instruction: node.id,
                epoch: epoch.clone(),
                kind: RewriteKind::DeadWrite { overwritten_by },
            });
            nodes[index] = None;
            changed = true;
        }
    }
    changed
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
