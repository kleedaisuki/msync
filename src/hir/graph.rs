//! Pure dependency graphs whose optimization domain is one serial basic block.

use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

use super::control::{ControlInstruction, TransferInput, TransferOutput};
pub use super::effects::VersionReadProof;
use super::effects::{
    self, Conflict, Effects, EndpointKey, FifoEndpointProof, FifoRendezvousProof, OperationFacts,
};
use crate::compiler::Location;
use crate::hir::{
    BasicBlock, BlockId, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId,
    Program, ReadPort,
};
use crate::plan::FileClass;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Wait {
    Completion,
    Admitted,
    ProcessStarted(ProcessId),
    ProcessExited(ProcessId),
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Reason {
    Hazard(Box<Conflict>),
    UnknownExternalEffects,
    SynchronizationBoundary,
    PortAvailability,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Dependency {
    pub before: InstructionId,
    pub after: InstructionId,
    pub wait: Wait,
    pub reasons: Vec<Reason>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CommutationProof {
    pub left: InstructionId,
    pub right: InstructionId,
    pub left_effects: Arc<Effects>,
    pub right_effects: Arc<Effects>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CoStart {
    pub process: ProcessId,
    pub launch: InstructionId,
    pub members: Vec<InstructionId>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ExternalProcessRequirement {
    pub instruction: InstructionId,
    pub process: ProcessId,
    pub wait: Wait,
}
#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PlanningContext {
    pub live_processes: Vec<ProcessId>,
    /// Actual retained flow/deferred-node footprints. These invalidate stale
    /// host metadata, but do not identify a producer or waive its event gate.
    pub pending_effects: Vec<Effects>,
    /// Endpoints whose logical epoch differs from the current host tree.
    pub host_unstable: BTreeSet<EndpointKey>,
    /// Fresh type observations scoped to this interpreted basic block.
    pub operation_facts: BTreeMap<InstructionId, OperationFacts>,
    /// Metadata-only premise: first existing directory above this destination.
    /// Missing binding effects stop before this directory and its ancestors.
    pub existing_parent_frontiers: BTreeMap<InstructionId, Location>,
    /// Actual retained readers selected at this instruction's logical epoch;
    /// regular-file type facts alone cannot establish this premise.
    pub pinned_inputs: BTreeMap<InstructionId, VersionReadProof>,
    /// Actual native FIFO endpoint observations retained by this submission.
    pub physical_rendezvous: Vec<FifoRendezvousProof>,
    pub physical_fifos: BTreeMap<EndpointKey, FifoEndpointProof>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Node {
    pub instruction: InstructionId,
    pub effects: Effects,
    pub children: Option<Box<PlanGraph>>,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Issue {
    pub instructions: Vec<InstructionId>,
    pub message: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum RefinementKind {
    SourceType,
    DestinationType,
    PhysicalStreamScheduling,
    SelectorExpansion,
    InputVersion,
    OutputPublication,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Refinement {
    pub instruction: InstructionId,
    pub location: Location,
    pub kind: RefinementKind,
    pub message: String,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct BlockPlan {
    pub id: BlockId,
    pub instructions: Vec<InstructionId>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PlanGraph {
    pub blocks: Vec<BlockPlan>,
    pub nodes: Vec<Node>,
    pub dependencies: Vec<Dependency>,
    pub co_start: Vec<CoStart>,
    pub commutations: Vec<CommutationProof>,
    pub external_processes: Vec<ExternalProcessRequirement>,
    pub pending_effects: Vec<Effects>,
    pub host_unstable: BTreeSet<EndpointKey>,
    pub operation_facts: BTreeMap<InstructionId, OperationFacts>,
    pub existing_parent_frontiers: BTreeMap<InstructionId, Location>,
    pub pinned_inputs: BTreeMap<InstructionId, VersionReadProof>,
    pub physical_rendezvous: Vec<FifoRendezvousProof>,
    pub physical_fifos: BTreeMap<EndpointKey, FifoEndpointProof>,
    pub refinements: Vec<Refinement>,
    pub issues: Vec<Issue>,
}

impl PlanGraph {
    pub fn is_execution_ready(&self) -> bool {
        self.issues.is_empty() && self.refinements.is_empty()
    }

    pub fn verify(&self, program: &Program) -> Result<(), Vec<Issue>> {
        self.verify_with_context(program, &PlanningContext::default())
    }

    pub fn verify_with_live_processes(
        &self,
        program: &Program,
        live: impl IntoIterator<Item = ProcessId>,
    ) -> Result<(), Vec<Issue>> {
        self.verify_with_context(
            program,
            &PlanningContext {
                live_processes: live.into_iter().collect(),
                ..PlanningContext::default()
            },
        )
    }

    pub fn verify_with_context(
        &self,
        program: &Program,
        context: &PlanningContext,
    ) -> Result<(), Vec<Issue>> {
        let expected = build_with_context(program, context);
        if !expected.issues.is_empty() {
            return Err(expected.issues);
        }
        if self == &expected {
            return Ok(());
        }
        Err(vec![Issue {
            instructions: self.nodes.iter().map(|node| node.instruction).collect(),
            message:
                "scheduling graph does not match this IR and its block-local resource premises"
                    .into(),
        }])
    }
}

pub fn build(program: &Program) -> PlanGraph {
    build_with_context(program, &PlanningContext::default())
}

pub fn build_with_live_processes(
    program: &Program,
    live: impl IntoIterator<Item = ProcessId>,
) -> PlanGraph {
    build_with_context(
        program,
        &PlanningContext {
            live_processes: live.into_iter().collect(),
            ..PlanningContext::default()
        },
    )
}

pub fn build_with_context(program: &Program, context: &PlanningContext) -> PlanGraph {
    let mut result = graph_context(context);
    let mut active = context.clone();
    let mut ids = BTreeSet::new();
    let mut processes = active
        .live_processes
        .iter()
        .copied()
        .collect::<BTreeSet<_>>();
    let mut consumers = BTreeMap::new();
    for block in program.blocks() {
        let graph = build_block_with_context(block, &active);
        for instruction in block.instructions {
            if !ids.insert(instruction.id) {
                result.issues.push(issue(
                    instruction.id,
                    "instruction IDs must be unique; this ID is defined more than once",
                ));
            }
            if let Some(process) = spawned_process(instruction) {
                if !processes.insert(process) {
                    result.issues.push(issue(
                        instruction.id,
                        "program ID is defined more than once",
                    ));
                }
                active.live_processes.push(process);
            }
            if let Some((process, port)) = source_port(instruction)
                .filter(|(_, port)| matches!(port, ReadPort::Stdout | ReadPort::Stderr))
                && let Some(previous) = consumers.insert((process, port), instruction.id)
            {
                result.issues.push(Issue {
                    instructions: vec![previous, instruction.id],
                    message: "a consuming program output port may have only one owner".into(),
                });
            }
        }
        result.blocks.extend(graph.blocks);
        result.nodes.extend(graph.nodes);
        result.dependencies.extend(graph.dependencies);
        result.co_start.extend(graph.co_start);
        result.commutations.extend(graph.commutations);
        result.external_processes.extend(graph.external_processes);
        result.refinements.extend(graph.refinements);
        result.issues.extend(graph.issues);
    }
    result
}

/// Construct only the graph that may now be contextualized and admitted.
pub fn build_block_with_context(block: BasicBlock<'_>, context: &PlanningContext) -> PlanGraph {
    let mut graph = graph_context(context);
    validate_physical(block, context, &mut graph.issues);
    graph.blocks.push(BlockPlan {
        id: block.id,
        instructions: block.instructions.iter().map(|node| node.id).collect(),
    });
    let mut known = context
        .live_processes
        .iter()
        .copied()
        .collect::<BTreeSet<_>>();
    for instruction in block.instructions {
        if let Some(process) = spawned_process(instruction) {
            known.insert(process);
        }
        validate_processes(instruction, &known, &mut graph);
        refine(instruction, context, &mut graph.refinements);
        graph.nodes.push(Node {
            instruction: instruction.id,
            effects: summarize(instruction, context),
            children: None,
        });
    }
    let mut footprints: Vec<Option<Arc<Effects>>> = vec![None; graph.nodes.len()];
    let mut index = effects::CandidateIndex::default();
    let mut candidates = BTreeSet::new();
    let mut marked = vec![usize::MAX; graph.nodes.len()];
    for right in 1..graph.nodes.len() {
        index.insert(&graph.nodes[right - 1].effects, right - 1);
        candidates.clear();
        index.candidates(&graph.nodes[right].effects, &mut candidates);
        for &left in &candidates {
            marked[left] = right;
        }
        for left in 0..right {
            let before = &graph.nodes[left];
            let after = &graph.nodes[right];
            let conflicts = if marked[left] == right {
                before.effects.conflicts(&after.effects)
            } else {
                Vec::new()
            };
            let boundary = matches!(
                block.instructions[right].kind,
                InstructionKind::Await { .. } | InstructionKind::Barrier { .. }
            );
            let unknown = before.effects.unknown_external || after.effects.unknown_external;
            if conflicts.is_empty() && !boundary && !unknown {
                graph.commutations.push(CommutationProof {
                    left: before.instruction,
                    right: after.instruction,
                    left_effects: footprints[left]
                        .get_or_insert_with(|| Arc::new(before.effects.clone()))
                        .clone(),
                    right_effects: footprints[right]
                        .get_or_insert_with(|| Arc::new(after.effects.clone()))
                        .clone(),
                });
                continue;
            }
            let mut completion = Vec::new();
            let mut admission = Vec::new();
            for conflict in conflicts {
                let paired = context.physical_rendezvous.iter().any(|proof| {
                    proof.matches_conflict(
                        &block.instructions[left],
                        &block.instructions[right],
                        &conflict,
                    )
                });
                let reason = Reason::Hazard(Box::new(conflict));
                if paired {
                    admission.push(reason);
                } else {
                    completion.push(reason);
                }
            }
            if boundary {
                completion.push(Reason::SynchronizationBoundary);
            }
            if unknown {
                completion.push(Reason::UnknownExternalEffects);
            }
            for (wait, reasons) in [(Wait::Admitted, admission), (Wait::Completion, completion)] {
                if !reasons.is_empty() {
                    graph.dependencies.push(Dependency {
                        before: before.instruction,
                        after: after.instruction,
                        wait,
                        reasons,
                    });
                }
            }
        }
    }
    validate_groups(&mut graph);
    graph
}

fn graph_context(context: &PlanningContext) -> PlanGraph {
    PlanGraph {
        pending_effects: context.pending_effects.clone(),
        host_unstable: context.host_unstable.clone(),
        operation_facts: context.operation_facts.clone(),
        existing_parent_frontiers: context.existing_parent_frontiers.clone(),
        pinned_inputs: context.pinned_inputs.clone(),
        physical_rendezvous: context.physical_rendezvous.clone(),
        physical_fifos: context.physical_fifos.clone(),
        ..PlanGraph::default()
    }
}

fn source_port(instruction: &Instruction) -> Option<(ProcessId, ReadPort)> {
    match &instruction.kind {
        InstructionKind::Transfer {
            source: Input::Port { process, port },
            ..
        }
        | InstructionKind::Control(ControlInstruction::Transfer {
            source: TransferInput::Port { process, port },
            ..
        }) => Some((*process, *port)),
        _ => None,
    }
}

fn stdin_process(instruction: &Instruction) -> Option<ProcessId> {
    match &instruction.kind {
        InstructionKind::Transfer {
            destination: Output::Stdin(process),
            ..
        }
        | InstructionKind::Control(ControlInstruction::Transfer {
            destination: TransferOutput::Stdin(process),
            ..
        }) => Some(*process),
        _ => None,
    }
}

fn spawned_process(instruction: &Instruction) -> Option<ProcessId> {
    match &instruction.kind {
        InstructionKind::Spawn { process, .. }
        | InstructionKind::Control(ControlInstruction::Spawn { process, .. }) => Some(*process),
        _ => None,
    }
}

fn issue(instruction: InstructionId, message: &str) -> Issue {
    Issue {
        instructions: vec![instruction],
        message: message.into(),
    }
}

fn validate_processes(
    instruction: &Instruction,
    known: &BTreeSet<ProcessId>,
    graph: &mut PlanGraph,
) {
    let mut requirements = Vec::new();
    if let Some((process, port)) = source_port(instruction) {
        let wait = if matches!(port, ReadPort::ExitCode | ReadPort::Meta) {
            Wait::ProcessExited(process)
        } else {
            Wait::ProcessStarted(process)
        };
        requirements.push((process, wait));
    }
    if let Some(process) = stdin_process(instruction) {
        requirements.push((process, Wait::ProcessStarted(process)));
    }
    match &instruction.kind {
        InstructionKind::Await {
            process: Some(process),
        } => requirements.push((*process, Wait::ProcessExited(*process))),
        InstructionKind::Await { process: None } => requirements.extend(
            known
                .iter()
                .map(|process| (*process, Wait::ProcessExited(*process))),
        ),
        _ => {}
    }
    for (process, wait) in requirements {
        if !known.contains(&process) {
            graph.issues.push(issue(
                instruction.id,
                "program port or acquire refers to a program that has not been launched",
            ));
        } else {
            graph.external_processes.push(ExternalProcessRequirement {
                instruction: instruction.id,
                process,
                wait,
            });
        }
    }
}

fn summarize(instruction: &Instruction, context: &PlanningContext) -> Effects {
    let mut summary = effects::summarize_prepared(
        instruction,
        context
            .operation_facts
            .get(&instruction.id)
            .copied()
            .unwrap_or_default(),
        context.existing_parent_frontiers.get(&instruction.id),
        context.pinned_inputs.get(&instruction.id),
    );
    let endpoints = context.physical_fifos.values().chain(
        context
            .physical_rendezvous
            .iter()
            .flat_map(|proof| [proof.writer(), proof.reader()]),
    );
    for proof in endpoints {
        let key = proof.key();
        if key.instruction == instruction.id {
            summary.accesses.push(effects::Access {
                resource: effects::Resource::StreamIdentity {
                    provider: proof.provider().into(),
                    identity: proof.identity().into(),
                },
                kind: if key.role == effects::EndpointRole::Source {
                    effects::AccessKind::Consume
                } else {
                    effects::AccessKind::Write
                },
            });
        }
    }
    summary
}

fn validate_physical(block: BasicBlock<'_>, context: &PlanningContext, issues: &mut Vec<Issue>) {
    let nodes = block
        .instructions
        .iter()
        .map(|node| (node.id, node))
        .collect::<BTreeMap<_, _>>();
    for (key, proof) in &context.physical_fifos {
        let Some(node) = nodes.get(&key.instruction) else {
            continue;
        };
        let facts = context
            .operation_facts
            .get(&node.id)
            .copied()
            .unwrap_or_default();
        if *key != proof.key()
            || !proof.supports(node, facts)
            || context.host_unstable.contains(key)
        {
            issues.push(issue(
                node.id,
                "FIFO endpoint proof does not match this instruction and its type facts",
            ));
        }
    }
    for proof in &context.physical_rendezvous {
        let Some(writer) = nodes.get(&proof.writer().key().instruction) else {
            continue;
        };
        let Some(reader) = nodes.get(&proof.reader().key().instruction) else {
            continue;
        };
        let writer_facts = context
            .operation_facts
            .get(&writer.id)
            .copied()
            .unwrap_or_default();
        let reader_facts = context
            .operation_facts
            .get(&reader.id)
            .copied()
            .unwrap_or_default();
        if !proof.supports(writer, writer_facts, reader, reader_facts)
            || context.host_unstable.contains(&proof.writer().key())
            || context.host_unstable.contains(&proof.reader().key())
        {
            issues.push(Issue {
                instructions: vec![writer.id, reader.id],
                message: "FIFO rendezvous proof does not match this block's endpoint facts".into(),
            });
        }
    }
}

fn refine(instruction: &Instruction, context: &PlanningContext, refinements: &mut Vec<Refinement>) {
    let InstructionKind::Transfer {
        source,
        destination,
    } = &instruction.kind
    else {
        return;
    };
    let facts = context
        .operation_facts
        .get(&instruction.id)
        .copied()
        .unwrap_or_default();
    if let Input::File { location, pattern } = source {
        physical_refinement(
            instruction.id,
            location,
            facts.source,
            effects::EndpointRole::Source,
            context,
            refinements,
        );
        if matches!(
            effects::file_class(location, facts.source),
            FileClass::Unknown | FileClass::Missing
        ) {
            refinements.push(Refinement {
                instruction: instruction.id,
                location: location.clone(),
                kind: RefinementKind::SourceType,
                message: "inspect this block's source before selecting finite or streaming IO"
                    .into(),
            });
        }
        if pattern.is_some() {
            refinements.push(Refinement {
                instruction: instruction.id,
                location: location.clone(),
                kind: RefinementKind::SelectorExpansion,
                message: "enumerate selector entries when this block becomes current".into(),
            });
        }
    }
    if let Output::File { location, .. } = destination {
        physical_refinement(
            instruction.id,
            location,
            facts.destination,
            effects::EndpointRole::Destination,
            context,
            refinements,
        );
        if effects::file_class(location, facts.destination) == FileClass::Unknown {
            refinements.push(Refinement {
                instruction: instruction.id,
                location: location.clone(),
                kind: RefinementKind::DestinationType,
                message: "inspect this block's destination before publication or stream admission"
                    .into(),
            });
        }
    }
}

fn physical_refinement(
    instruction: InstructionId,
    location: &Location,
    class: FileClass,
    role: effects::EndpointRole,
    context: &PlanningContext,
    refinements: &mut Vec<Refinement>,
) {
    if effects::file_class(location, class) != FileClass::Stream {
        return;
    }
    let key = EndpointKey { instruction, role };
    let observed = context.physical_fifos.contains_key(&key)
        || context
            .physical_rendezvous
            .iter()
            .any(|proof| proof.writer().key() == key || proof.reader().key() == key);
    if !observed {
        refinements.push(Refinement {
            instruction,
            location: location.clone(),
            kind: RefinementKind::PhysicalStreamScheduling,
            message: "a stream type does not establish native identity or co-admission; refine at dispatch".into(),
        });
    }
}

fn validate_groups(graph: &mut PlanGraph) {
    let ids = graph
        .nodes
        .iter()
        .map(|node| node.instruction)
        .collect::<BTreeSet<_>>();
    let proofs = graph
        .physical_rendezvous
        .iter()
        .filter(|proof| {
            ids.contains(&proof.writer().key().instruction)
                && ids.contains(&proof.reader().key().instruction)
        })
        .cloned()
        .collect::<Vec<_>>();
    if proofs.is_empty() {
        return;
    }
    let flow_edges = proofs
        .iter()
        .map(|proof| {
            (
                proof.writer().key().instruction,
                proof.reader().key().instruction,
            )
        })
        .collect::<BTreeSet<_>>();
    if cyclic(&ids, &flow_edges) {
        graph.issues.push(Issue {
            instructions: ids.iter().copied().collect(),
            message: "closed FIFO cycle has no independent source or sink".into(),
        });
    }
    let mut owners = ids.iter().map(|id| (*id, *id)).collect::<BTreeMap<_, _>>();
    for group in physical_groups(&proofs) {
        let owner = group[0];
        for member in group {
            owners.insert(member, owner);
        }
    }
    let mut edges = BTreeSet::new();
    for edge in &graph.dependencies {
        if edge.wait == Wait::Admitted {
            continue;
        }
        let pair = (owners[&edge.before], owners[&edge.after]);
        if pair.0 == pair.1 {
            graph.issues.push(Issue {
                instructions: vec![edge.before, edge.after],
                message: "FIFO co-admission conflicts with a completion-dependency cycle".into(),
            });
            return;
        }
        edges.insert(pair);
    }
    let groups = owners.values().copied().collect::<BTreeSet<_>>();
    if cyclic(&groups, &edges) {
        graph.issues.push(Issue {
            instructions: ids.iter().copied().collect(),
            message: "FIFO groups form a completion-dependency cycle".into(),
        });
    }
}

fn cyclic(
    nodes: &BTreeSet<InstructionId>,
    edges: &BTreeSet<(InstructionId, InstructionId)>,
) -> bool {
    let mut degree = nodes
        .iter()
        .map(|id| (*id, 0_usize))
        .collect::<BTreeMap<_, _>>();
    for (_, after) in edges {
        *degree
            .get_mut(after)
            .expect("edge endpoint belongs to graph") += 1;
    }
    let mut ready = degree
        .iter()
        .filter_map(|(id, degree)| (*degree == 0).then_some(*id))
        .collect::<Vec<_>>();
    let mut visited = 0;
    while let Some(id) = ready.pop() {
        visited += 1;
        for (_, after) in edges.range((id, InstructionId(0))..=(id, InstructionId(usize::MAX))) {
            let degree = degree
                .get_mut(after)
                .expect("edge endpoint belongs to graph");
            *degree -= 1;
            if *degree == 0 {
                ready.push(*after);
            }
        }
    }
    visited != nodes.len()
}

/// Connected FIFO endpoints need co-admission, never a completion-before-open edge.
pub(crate) fn physical_groups(proofs: &[FifoRendezvousProof]) -> Vec<Vec<InstructionId>> {
    let mut adjacent = BTreeMap::<InstructionId, BTreeSet<InstructionId>>::new();
    for proof in proofs {
        let left = proof.writer().key().instruction;
        let right = proof.reader().key().instruction;
        adjacent.entry(left).or_default().insert(right);
        adjacent.entry(right).or_default().insert(left);
    }
    let mut visited = BTreeSet::new();
    let mut groups = Vec::new();
    for node in adjacent.keys() {
        if visited.contains(node) {
            continue;
        }
        let mut pending = vec![*node];
        let mut group = Vec::new();
        while let Some(next) = pending.pop() {
            if visited.insert(next) {
                group.push(next);
                pending.extend(adjacent[&next].iter().copied());
            }
        }
        group.sort();
        groups.push(group);
    }
    groups
}

#[cfg(test)]
mod control_spawn_tests {
    use super::*;
    use crate::endpoint::{Backend, Location};
    use crate::hir::Region;
    use crate::hir::control::{ControlInstruction, Expr, ExprKind, Type};
    use crate::language::DestinationMode;
    use crate::syntax::Span;

    fn node(id: usize, kind: InstructionKind) -> Instruction {
        Instruction {
            id: InstructionId(id),
            kind,
            span: Span::default(),
            expansion: Vec::new(),
        }
    }

    fn spawn(id: usize, process: ProcessId) -> Instruction {
        let path = || Expr {
            ty: Type::Path,
            kind: ExprKind::Path {
                location: Location {
                    backend: Backend::Local,
                    path: "program".into(),
                },
                mode: DestinationMode::Exact,
                relocation: None,
            },
        };
        node(
            id,
            InstructionKind::Control(ControlInstruction::Spawn {
                process,
                executable: path(),
                arguments: Vec::new(),
                cwd: path(),
            }),
        )
    }

    #[test]
    fn pair_iteration_keeps_empty_and_singleton_blocks_fully_validated() {
        let context = PlanningContext::default();
        let empty = build_block_with_context(
            BasicBlock {
                id: BlockId(0),
                instructions: &[],
            },
            &context,
        );
        assert!(empty.nodes.is_empty());
        assert!(empty.dependencies.is_empty());
        assert!(empty.commutations.is_empty());
        assert!(empty.issues.is_empty());

        let instructions = [node(
            7,
            InstructionKind::Await {
                process: Some(ProcessId(99)),
            },
        )];
        let singleton = build_block_with_context(
            BasicBlock {
                id: BlockId(1),
                instructions: &instructions,
            },
            &context,
        );
        assert_eq!(singleton.nodes.len(), 1);
        assert_eq!(singleton.nodes[0].instruction, InstructionId(7));
        assert!(singleton.dependencies.is_empty());
        assert!(singleton.commutations.is_empty());
        assert!(
            singleton
                .issues
                .iter()
                .any(|issue| issue.instructions == [InstructionId(7)]
                    && issue.message.contains("has not been launched"))
        );
    }

    #[test]
    fn dynamic_spawn_is_known_to_following_await_blocks() {
        let process = ProcessId(7);
        let program = Program {
            body: Region {
                instructions: vec![
                    spawn(0, process),
                    node(
                        1,
                        InstructionKind::Await {
                            process: Some(process),
                        },
                    ),
                ],
            },
        };
        let graph = build(&program);
        assert!(graph.issues.is_empty(), "{:?}", graph.issues);
        assert!(
            graph
                .external_processes
                .iter()
                .any(|requirement| requirement.process == process
                    && requirement.wait == Wait::ProcessExited(process))
        );
    }

    #[test]
    fn dynamic_spawn_is_known_in_directly_constructed_block() {
        let process = ProcessId(7);
        let instructions = [
            spawn(0, process),
            node(
                1,
                InstructionKind::Await {
                    process: Some(process),
                },
            ),
        ];
        let graph = build_block_with_context(
            BasicBlock {
                id: BlockId(0),
                instructions: &instructions,
            },
            &PlanningContext::default(),
        );
        assert!(graph.issues.is_empty(), "{:?}", graph.issues);
    }

    #[test]
    fn dynamic_spawn_rejects_duplicate_process_identity() {
        let process = ProcessId(7);
        let program = Program {
            body: Region {
                instructions: vec![spawn(0, process), spawn(1, process)],
            },
        };
        assert!(
            build(&program)
                .issues
                .iter()
                .any(|issue| issue.message == "program ID is defined more than once")
        );
    }

    #[test]
    fn dynamic_transfer_ports_retain_process_requirements() {
        let process = ProcessId(7);
        for port in [
            ReadPort::Stdout,
            ReadPort::Stderr,
            ReadPort::Pid,
            ReadPort::ExitCode,
            ReadPort::Meta,
        ] {
            let instruction = node(
                0,
                InstructionKind::Control(ControlInstruction::Transfer {
                    source: TransferInput::Port { process, port },
                    destination: TransferOutput::Stdin(process),
                }),
            );
            let program = Program {
                body: Region {
                    instructions: vec![instruction],
                },
            };
            let graph = build_with_live_processes(&program, [process]);
            assert!(graph.issues.is_empty(), "{:?}", graph.issues);
            let source_wait = if matches!(port, ReadPort::ExitCode | ReadPort::Meta) {
                Wait::ProcessExited(process)
            } else {
                Wait::ProcessStarted(process)
            };
            assert_eq!(graph.external_processes.len(), 2);
            assert_eq!(graph.external_processes[0].wait, source_wait);
            assert_eq!(
                graph.external_processes[1].wait,
                Wait::ProcessStarted(process)
            );
            assert!(!build(&program).issues.is_empty());
        }
    }

    #[test]
    fn consuming_port_cannot_be_shared_between_control_and_ordinary_transfer() {
        let process = ProcessId(7);
        let program = Program {
            body: Region {
                instructions: vec![
                    node(
                        0,
                        InstructionKind::Control(ControlInstruction::Transfer {
                            source: TransferInput::Port {
                                process,
                                port: ReadPort::Stdout,
                            },
                            destination: TransferOutput::Stdin(process),
                        }),
                    ),
                    node(
                        1,
                        InstructionKind::Transfer {
                            source: Input::Port {
                                process,
                                port: ReadPort::Stdout,
                            },
                            destination: Output::Stdin(process),
                        },
                    ),
                ],
            },
        };
        assert!(
            build_with_live_processes(&program, [process])
                .issues
                .iter()
                .any(|issue| issue.message
                    == "a consuming program output port may have only one owner")
        );
    }
}
