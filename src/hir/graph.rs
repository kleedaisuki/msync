//! Explainable scheduling obligations for structured HIR regions.
//!
//! This pass does not inspect files, guess subprocess effects, or erase writes
//! without regular-file observations. It establishes commutation proofs and
//! distinguishes launch, exit and ordinary completion events. Transaction
//! children remain a separate graph: their parent's completion is atomic.

use std::collections::{BTreeMap, BTreeSet};

use super::effects::{self, Conflict, Effects, Port, Resource};
use crate::hir::{
    Instruction, InstructionId, InstructionKind, ProcessId, Program, Region, RollbackScope,
    TransactionId,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Wait {
    Completion,
    ProcessStarted(ProcessId),
    ProcessExited(ProcessId),
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Reason {
    Hazard(Box<Conflict>),
    UnknownExternalEffects,
    CheckpointBoundary,
    PortAvailability,
    RetainedTransactionOrigin(TransactionId),
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
    /// These are the exact checked footprints, including selector/tree reads.
    pub left_effects: Effects,
    pub right_effects: Effects,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CoStart {
    pub process: ProcessId,
    pub launch: InstructionId,
    pub members: Vec<InstructionId>,
}

/// A process retained by a previous submission has no launch node in this IR.
/// Its lifecycle still constrains this region; ignoring it is not independence.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ExternalProcessRequirement {
    pub instruction: InstructionId,
    pub process: ProcessId,
    pub wait: Wait,
}

/// A trusted resource-view provider's complete managed-resource footprint.
///
/// The constructor validates shape, not OS isolation. Supplying this value is
/// an explicit planner trust boundary: the caller must have verified that the
/// named view mediates all relevant accesses by the process and its children.
/// Merely changing cwd, rewriting argv, or observing a directory afterward does
/// not satisfy that contract. Overlapping proven footprints still serialize;
/// this certificate alone does not promise dynamic conflict coordination.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ProcessEffects {
    footprint: Effects,
    provider: String,
    view_id: String,
}

impl ProcessEffects {
    pub fn mediated(
        footprint: Effects,
        provider: impl Into<String>,
        view_id: impl Into<String>,
    ) -> Result<Self, String> {
        let provider = provider.into();
        let view_id = view_id.into();
        if footprint.unknown_external || provider.is_empty() || view_id.is_empty() {
            return Err("a process-effect certificate needs a complete footprint and nonempty verified provider/view identities".into());
        }
        Ok(Self {
            footprint,
            provider,
            view_id,
        })
    }

    pub fn footprint(&self) -> &Effects {
        &self.footprint
    }

    pub fn provider(&self) -> &str {
        &self.provider
    }

    pub fn view_id(&self) -> &str {
        &self.view_id
    }
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PlanningContext {
    pub live_processes: Vec<ProcessId>,
    pub process_effects: BTreeMap<ProcessId, ProcessEffects>,
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

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PlanGraph {
    pub nodes: Vec<Node>,
    pub dependencies: Vec<Dependency>,
    pub co_start: Vec<CoStart>,
    pub commutations: Vec<CommutationProof>,
    pub external_processes: Vec<ExternalProcessRequirement>,
    pub process_proofs: BTreeMap<ProcessId, ProcessEffects>,
    pub issues: Vec<Issue>,
}

impl PlanGraph {
    /// Replay the pure effect obligations against the original expanded IR.
    /// This is a checkable scheduling certificate, not a metadata/type proof for
    /// ordinary-file dead-store elimination.
    pub fn verify(&self, program: &Program) -> Result<(), Vec<Issue>> {
        self.verify_with_live_processes(program, [])
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
            message: "scheduling certificate does not match the HIR's resource, lifetime and process-event obligations".into(),
        }])
    }
}

pub fn build(program: &Program) -> PlanGraph {
    build_with_live_processes(program, [])
}

/// Include still-live processes retained by a REPL/runtime session, including
/// ones not explicitly mentioned by this submission. Their unknown effects
/// quiesce before unrelated work; declared stream services may co-start.
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
    let mut transactions = BTreeMap::new();
    let mut ids = BTreeSet::new();
    let mut issues = Vec::new();
    collect(
        &program.body,
        &mut transactions,
        &mut ids,
        &mut issues,
        context,
    );
    let mut launched = BTreeSet::new();
    let mut used = context
        .live_processes
        .iter()
        .copied()
        .collect::<BTreeSet<_>>();
    collect_processes(&program.body, &mut launched, &mut used, &mut issues);
    let external = used.difference(&launched).copied().collect();
    let mut graph = build_region(&program.body, &transactions, &external, context);
    graph.issues.extend(issues);
    graph
}

fn collect_processes(
    region: &Region,
    launched: &mut BTreeSet<ProcessId>,
    used: &mut BTreeSet<ProcessId>,
    issues: &mut Vec<Issue>,
) {
    for instruction in &region.instructions {
        if let Some(process) = spawn(instruction)
            && !launched.insert(process)
        {
            issues.push(Issue {
                instructions: vec![instruction.id],
                message: "a process ID has more than one launch across regions".into(),
            });
        }
        for access in effects::summarize(instruction).accesses {
            if let Resource::Port { process, .. } = access.resource {
                used.insert(process);
            }
        }
        if let InstructionKind::Transaction { body, .. } = &instruction.kind {
            collect_processes(body, launched, used, issues);
        }
    }
}

fn collect(
    region: &Region,
    transactions: &mut BTreeMap<TransactionId, Effects>,
    ids: &mut BTreeSet<InstructionId>,
    issues: &mut Vec<Issue>,
    context: &PlanningContext,
) {
    for instruction in &region.instructions {
        if !ids.insert(instruction.id) {
            issues.push(Issue {
                instructions: vec![instruction.id],
                message: "instruction IDs must be unique across all regions".into(),
            });
        }
        if let InstructionKind::Transaction { transaction, body } = &instruction.kind {
            if transactions
                .insert(*transaction, summarize(instruction, context))
                .is_some()
            {
                issues.push(Issue {
                    instructions: vec![instruction.id],
                    message: "a transaction ID has more than one origin region".into(),
                });
            }
            collect(body, transactions, ids, issues, context);
        }
    }
}

fn build_region(
    region: &Region,
    transactions: &BTreeMap<TransactionId, Effects>,
    external: &BTreeSet<ProcessId>,
    context: &PlanningContext,
) -> PlanGraph {
    let mut graph = PlanGraph {
        process_proofs: context.process_effects.clone(),
        ..PlanGraph::default()
    };
    for instruction in &region.instructions {
        let children = match &instruction.kind {
            InstructionKind::Transaction { body, .. } => Some(Box::new(build_region(
                body,
                transactions,
                external,
                context,
            ))),
            _ => None,
        };
        if let Some(children) = &children {
            graph.issues.extend(children.issues.iter().cloned());
        }
        let effects = footprint(instruction, transactions, context);
        for process in external {
            let used = uses_process(&effects, *process);
            if !used
                && context.process_effects.get(process).is_some_and(|proof| {
                    !effects.unknown_external && proof.footprint.conflicts(&effects).is_empty()
                })
            {
                continue;
            }
            let wait = if used && !waits_for_exit(&effects, *process) {
                Wait::ProcessStarted(*process)
            } else {
                Wait::ProcessExited(*process)
            };
            graph.external_processes.push(ExternalProcessRequirement {
                instruction: instruction.id,
                process: *process,
                wait,
            });
        }
        graph.nodes.push(Node {
            instruction: instruction.id,
            effects,
            children,
        });
    }
    let mut dependencies = BTreeMap::new();
    for (right, after) in region.instructions.iter().enumerate() {
        for (left, before) in region.instructions[..right].iter().enumerate() {
            let reasons = ordering(
                before,
                after,
                &graph.nodes[left].effects,
                &graph.nodes[right].effects,
            );
            if reasons.is_empty() {
                graph.commutations.push(CommutationProof {
                    left: before.id,
                    right: after.id,
                    left_effects: graph.nodes[left].effects.clone(),
                    right_effects: graph.nodes[right].effects.clone(),
                });
            }
            for (wait, reason) in reasons {
                dependencies
                    .entry((before.id, after.id, wait))
                    .or_insert_with(Vec::new)
                    .push(reason);
            }
        }
    }
    graph.dependencies = dependencies
        .into_iter()
        .map(|((before, after, wait), reasons)| Dependency {
            before,
            after,
            wait,
            reasons,
        })
        .collect();
    connect_ports(region, &mut graph);
    graph
}

fn footprint(
    instruction: &Instruction,
    transactions: &BTreeMap<TransactionId, Effects>,
    context: &PlanningContext,
) -> Effects {
    if let InstructionKind::Rollback {
        transaction,
        scope: RollbackScope::Begin,
    } = instruction.kind
    {
        // An origin from a previous REPL submission is owned by the runtime;
        // absence here means unknown effects, not an empty write set.
        return transactions.get(&transaction).cloned().unwrap_or(Effects {
            unknown_external: true,
            ..Effects::default()
        });
    }
    summarize(instruction, context)
}

fn summarize(instruction: &Instruction, context: &PlanningContext) -> Effects {
    match &instruction.kind {
        InstructionKind::Spawn { process, .. } => context
            .process_effects
            .get(process)
            .map(|proof| proof.footprint.clone())
            .unwrap_or_else(|| effects::summarize(instruction)),
        InstructionKind::Transaction { body, .. } => {
            let mut summary = Effects::default();
            for instruction in &body.instructions {
                summary.append(summarize(instruction, context));
            }
            summary
        }
        _ => effects::summarize(instruction),
    }
}

fn cut(instruction: &Instruction) -> bool {
    matches!(
        instruction.kind,
        InstructionKind::Commit { .. }
            | InstructionKind::Rollback {
                scope: RollbackScope::Checkpoint,
                ..
            }
    )
}

fn spawn(instruction: &Instruction) -> Option<ProcessId> {
    match instruction.kind {
        InstructionKind::Spawn { process, .. } => Some(process),
        _ => None,
    }
}

fn uses_process(effects: &Effects, process: ProcessId) -> bool {
    effects.accesses.iter().any(
        |access| matches!(access.resource, Resource::Port { process: used, .. } if process == used),
    )
}

fn waits_for_exit(effects: &Effects, process: ProcessId) -> bool {
    effects.accesses.iter().any(|access| matches!(access.resource, Resource::Port { process: used, port: Port::ExitCode | Port::Meta } if process == used))
}

fn completion(before: &Instruction) -> Wait {
    spawn(before).map_or(Wait::Completion, Wait::ProcessExited)
}

fn ordering(
    before: &Instruction,
    after: &Instruction,
    left: &Effects,
    right: &Effects,
) -> Vec<(Wait, Reason)> {
    let mut reasons = Vec::new();
    if cut(before) || cut(after) {
        reasons.push((completion(before), Reason::CheckpointBoundary));
    }
    if let (
        InstructionKind::Transaction {
            transaction: origin,
            ..
        },
        InstructionKind::Rollback {
            transaction,
            scope: RollbackScope::Begin,
        },
    ) = (&before.kind, &after.kind)
        && origin == transaction
    {
        reasons.push((
            Wait::Completion,
            Reason::RetainedTransactionOrigin(*transaction),
        ));
    }
    let port_connection = spawn(before).filter(|process| {
        matches!(after.kind, InstructionKind::Transfer { .. }) && uses_process(right, *process)
    });
    if let Some(process) = port_connection {
        reasons.push((
            if waits_for_exit(right, process) {
                Wait::ProcessExited(process)
            } else {
                Wait::ProcessStarted(process)
            },
            Reason::PortAvailability,
        ));
    }
    // A process's own declared ports must be serviced before it exits. All
    // unrelated accesses still await its exit, not merely successful spawn.
    if (left.unknown_external || right.unknown_external) && port_connection.is_none() {
        reasons.push((completion(before), Reason::UnknownExternalEffects));
    }
    reasons.extend(
        left.conflicts(right)
            .into_iter()
            .map(|conflict| (completion(before), Reason::Hazard(Box::new(conflict)))),
    );
    reasons
}

fn connect_ports(region: &Region, graph: &mut PlanGraph) {
    let mut launches = BTreeMap::new();
    let mut owners = BTreeMap::<(ProcessId, u8), InstructionId>::new();
    let mut members = BTreeMap::<ProcessId, BTreeSet<InstructionId>>::new();
    for (instruction, node) in region.instructions.iter().zip(&graph.nodes) {
        if let Some(process) = spawn(instruction)
            && launches.insert(process, instruction.id).is_some()
        {
            graph.issues.push(Issue {
                instructions: vec![instruction.id],
                message: "a process ID has more than one launch".into(),
            });
        }
        for access in &node.effects.accesses {
            let Resource::Port { process, port } = access.resource else {
                continue;
            };
            let port_id = match port {
                Port::Stdin => 0,
                Port::Stdout => 1,
                Port::Stderr => 2,
                _ => continue,
            };
            members.entry(process).or_default().insert(instruction.id);
            if let Some(owner) = owners.insert((process, port_id), instruction.id)
                && owner != instruction.id
            {
                graph.issues.push(Issue {
                    instructions: vec![owner, instruction.id],
                    message: format!(
                        "process {} {port:?} is a consuming stream and requires one owner",
                        process.0
                    ),
                });
            }
        }
    }
    for (process, members) in members {
        if let Some(launch) = launches.get(&process) {
            graph.co_start.push(CoStart {
                process,
                launch: *launch,
                members: members.into_iter().collect(),
            });
        }
    }
    reject_cross_checkpoint_services(region, graph);
    reject_internal_waits(graph);
}

fn reject_cross_checkpoint_services(region: &Region, graph: &mut PlanGraph) {
    let positions: BTreeMap<_, _> = region
        .instructions
        .iter()
        .enumerate()
        .map(|(index, instruction)| (instruction.id, index))
        .collect();
    for cohort in &graph.co_start {
        let launch = positions[&cohort.launch];
        for member in &cohort.members {
            let position = positions[member];
            if position < launch {
                graph.issues.push(Issue {
                    instructions: vec![*member, cohort.launch],
                    message: "a process port is used before its launch".into(),
                });
                continue;
            }
            if region.instructions[launch..position].iter().any(cut) {
                graph.issues.push(Issue { instructions: vec![cohort.launch, *member], message: "a live process pipe crosses a checkpoint; drain/stage it before the checkpoint or launch a new process afterward".into() });
            }
        }
    }
}

fn reject_internal_waits(graph: &mut PlanGraph) {
    for cohort in &graph.co_start {
        // File hazards between pipe services are real; they cannot be erased
        // just to launch a pipe cohort. Report a staging obligation explicitly.
        for edge in &graph.dependencies {
            if cohort.members.contains(&edge.before)
                && cohort
                    .members
                    .iter()
                    .any(|member| reaches(graph, edge.after, *member))
                && edge.wait == Wait::Completion
            {
                graph.issues.push(Issue { instructions: vec![edge.before, edge.after], message: format!("process {} pipe services have a completion hazard; stage/version the conflicting resource before co-start", cohort.process.0) });
            }
            if edge.wait == Wait::ProcessExited(cohort.process)
                && cohort
                    .members
                    .iter()
                    .any(|member| reaches(graph, edge.after, *member))
            {
                graph.issues.push(Issue { instructions: vec![edge.before, edge.after], message: format!("process {} pipe service depends on that process exiting; provide a mediated disjoint-effect proof or stage the dependency before co-start", cohort.process.0) });
            }
        }
    }
}

fn reaches(graph: &PlanGraph, start: InstructionId, target: InstructionId) -> bool {
    let mut pending = vec![start];
    let mut visited = BTreeSet::new();
    while let Some(instruction) = pending.pop() {
        if instruction == target {
            return true;
        }
        if visited.insert(instruction) {
            pending.extend(
                graph
                    .dependencies
                    .iter()
                    .filter(|edge| edge.before == instruction)
                    .map(|edge| edge.after),
            );
        }
    }
    false
}
