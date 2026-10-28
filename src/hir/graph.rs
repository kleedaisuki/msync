//! Explainable scheduling obligations for structured HIR regions.
//!
//! This pass does not inspect files, guess subprocess effects, or erase writes
//! without regular-file observations. It establishes commutation proofs and
//! distinguishes launch, exit and ordinary completion events. Transaction
//! children remain a separate graph: their parent's completion is atomic.

use std::collections::{BTreeMap, BTreeSet};

pub use super::effects::VersionReadProof;
use super::effects::{
    self, Conflict, Effects, EndpointKey, FifoEndpointProof, FifoRendezvousProof,
    NamespaceMergeProof, OperationFacts, Port, Resource,
};
use crate::compiler::Location;
use crate::hir::{
    Instruction, InstructionId, InstructionKind, ProcessId, Program, Region, RollbackScope,
    TransactionId,
};
use crate::plan::FileClass;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum Wait {
    Completion,
    /// Owned asynchronous work was actually activated; not EOF or publication.
    Admitted,
    /// The controlling local rollback joined/discarded work and restored state.
    EpochReset,
    /// Source-ordered IO actually completed before this control action; later
    /// visibility discard does not erase that historical observation.
    ObservationComplete,
    ProcessStarted(ProcessId),
    ProcessExited(ProcessId),
    /// Process exited and its private filesystem delta has been imported.
    ProcessSealed(ProcessId),
    /// Every member joined and this shared mutable epoch was exported once.
    CohortSealed(CohortId),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub struct CohortId(pub usize);

#[derive(Clone, Debug, PartialEq, Eq)]
pub enum Reason {
    Hazard(Box<Conflict>),
    UnknownExternalEffects,
    CheckpointBoundary,
    PortAvailability,
    RetainedTransactionOrigin(TransactionId),
    OutputPublication(ProcessId),
    SharedViewEpoch(CohortId),
    CancelEpoch(InstructionId),
    EpochRestored(InstructionId),
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

/// Evidence for one actual shared mutable native view, not independent snapshots
/// that happen to have overlapping roots. The provider/runtime owns the epoch,
/// joins all member descendants before export, and imports that delta once.
///
/// Like ProcessEffects, construction checks the certificate shape, not the OS.
/// The owner and epoch must name the real runtime view lease. A prepared member
/// may be certified before launch only while its actual native fork lease is
/// retained and that ProcessId's launch is bound to it. Desired grouping, argv,
/// equal final changes, and separate snapshots are not evidence of sharing.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CohortViewProof {
    owner: String,
    epoch: String,
    provider: String,
    view_id: String,
    members: BTreeMap<ProcessId, ProcessEffects>,
}

impl CohortViewProof {
    pub fn shared(
        owner: impl Into<String>,
        epoch: impl Into<String>,
        members: impl IntoIterator<Item = (ProcessId, ProcessEffects)>,
    ) -> Result<Self, String> {
        let owner = owner.into();
        let epoch = epoch.into();
        let mut checked = BTreeMap::new();
        for (process, effects) in members {
            if checked.insert(process, effects).is_some() {
                return Err("a shared view member occurs more than once".into());
            }
        }
        let Some(first) = checked.values().next() else {
            return Err("a shared view needs at least two process members".into());
        };
        if owner.is_empty()
            || epoch.is_empty()
            || checked.len() < 2
            || checked.values().any(|effects| {
                effects.provider != first.provider || effects.view_id != first.view_id
            })
        {
            return Err("a shared view needs one nonempty owner/epoch and at least two complete member certificates for the same actual provider/ViewId".into());
        }
        Ok(Self {
            owner,
            epoch,
            provider: first.provider.clone(),
            view_id: first.view_id.clone(),
            members: checked,
        })
    }

    pub fn owner(&self) -> &str {
        &self.owner
    }
    pub fn epoch(&self) -> &str {
        &self.epoch
    }
    pub fn provider(&self) -> &str {
        &self.provider
    }
    pub fn view_id(&self) -> &str {
        &self.view_id
    }
    pub fn members(&self) -> &BTreeMap<ProcessId, ProcessEffects> {
        &self.members
    }
}

/// Classifies only one native-launch witness within a real shared mutable view.
/// Other pending witnesses must still be checked independently. In particular,
/// a deferred managed-file assignment is never a native launch: a later program
/// that reads that path needs its actual publication and a fresh view epoch.
pub fn shared_native_launch_order(
    predecessor: &Instruction,
    predecessor_effects: &Effects,
    candidate: &Instruction,
    candidate_effects: &Effects,
    proof: &CohortViewProof,
) -> Option<Wait> {
    let before = spawn(predecessor)?;
    let after = spawn(candidate)?;
    if before == after
        || proof.members.get(&before)?.footprint() != predecessor_effects
        || proof.members.get(&after)?.footprint() != candidate_effects
    {
        return None;
    }
    Some(Wait::ProcessStarted(before))
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PlanningContext {
    pub live_processes: Vec<ProcessId>,
    pub process_effects: BTreeMap<ProcessId, ProcessEffects>,
    /// Actual retained flow/deferred-node footprints. These invalidate stale
    /// host metadata, but do not identify a producer or waive its event gate.
    pub pending_effects: Vec<Effects>,
    /// Endpoints whose logical epoch differs from the current host tree.
    pub host_unstable: BTreeSet<EndpointKey>,
    /// Type observations/version-store premises at this instruction's epoch.
    pub operation_facts: BTreeMap<InstructionId, OperationFacts>,
    /// Metadata-only premise: first existing directory above this destination.
    /// Namespace writes include that directory, but not its unchanged ancestors.
    pub existing_parent_frontiers: BTreeMap<InstructionId, Location>,
    /// Actual retained readers selected at this instruction's logical epoch;
    /// regular-file type facts alone cannot establish this premise.
    pub pinned_inputs: BTreeMap<InstructionId, VersionReadProof>,
    pub cohort_views: Vec<CohortViewProof>,
    pub namespace_merges: BTreeMap<InstructionId, NamespaceMergeProof>,
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

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum CohortRefinementKind {
    SharedMutableView,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct CohortRefinement {
    pub processes: Vec<ProcessId>,
    pub instructions: Vec<InstructionId>,
    pub kind: CohortRefinementKind,
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct SharedCohort {
    pub id: CohortId,
    pub proof: CohortViewProof,
    pub launches: Vec<(ProcessId, InstructionId)>,
    pub services: Vec<InstructionId>,
    /// Kept on the outside boundary; membership never proves purity/disjointness.
    pub effects: Effects,
}

/// A local rollback cancels/joins only this uncommitted epoch and restores the
/// latest checkpoint. These IDs are discarded definitions, not successful
/// publications. Already successful observations retain their historical truth.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct EpochReset {
    pub controller: InstructionId,
    pub transaction: TransactionId,
    pub checkpoint: Option<InstructionId>,
    pub reverted: Vec<InstructionId>,
    pub cancel: Vec<InstructionId>,
    pub processes: Vec<ProcessId>,
}

#[derive(Clone, Debug, Default, PartialEq, Eq)]
pub struct PlanGraph {
    pub nodes: Vec<Node>,
    pub dependencies: Vec<Dependency>,
    /// Gates final managed-file publication, not the private pipe-drain service.
    /// The executor imports a producer's sealed delta before assigning its
    /// subsequent stdout/stderr file result into the managed version store.
    pub publication_dependencies: Vec<Dependency>,
    pub co_start: Vec<CoStart>,
    pub commutations: Vec<CommutationProof>,
    pub external_processes: Vec<ExternalProcessRequirement>,
    pub external_publications: Vec<ExternalProcessRequirement>,
    pub process_proofs: BTreeMap<ProcessId, ProcessEffects>,
    pub pending_effects: Vec<Effects>,
    pub host_unstable: BTreeSet<EndpointKey>,
    pub operation_facts: BTreeMap<InstructionId, OperationFacts>,
    pub existing_parent_frontiers: BTreeMap<InstructionId, Location>,
    pub pinned_inputs: BTreeMap<InstructionId, VersionReadProof>,
    pub cohort_views: Vec<CohortViewProof>,
    pub shared_cohorts: Vec<SharedCohort>,
    pub cohort_refinements: Vec<CohortRefinement>,
    pub namespace_merges: BTreeMap<InstructionId, NamespaceMergeProof>,
    pub physical_rendezvous: Vec<FifoRendezvousProof>,
    pub physical_fifos: BTreeMap<EndpointKey, FifoEndpointProof>,
    /// Includes descendant regions' reset cuts, keyed by globally unique HIR IDs.
    /// A retained submission can resolve a child controller without replacing
    /// its original whole-program proof/source owner.
    pub epoch_resets: Vec<EpochReset>,
    /// Physical streams require identity/co-start preparation. A valid pure
    /// scheduling certificate alone does not make this an executable I/O plan.
    pub refinements: Vec<Refinement>,
    pub issues: Vec<Issue>,
}

impl PlanGraph {
    /// Structural/type/physical-scheduling readiness only. The execution adapter
    /// must still validate resource versions and any subprocess mediation.
    pub fn is_execution_ready(&self) -> bool {
        self.issues.is_empty()
            && self.refinements.is_empty()
            && self.cohort_refinements.is_empty()
            && self.nodes.iter().all(|node| {
                node.children
                    .as_ref()
                    .is_none_or(|children| children.is_execution_ready())
            })
    }
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
    let mut effective = context.clone();
    effective.namespace_merges = validate_namespace_merges(program, context, &mut issues);
    effective.physical_rendezvous = validate_physical_rendezvous(program, context, &mut issues);
    effective.physical_fifos = validate_fifo_endpoints(program, context, &mut issues);
    let context = &effective;
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
    collect_port_owners(&program.body, &mut BTreeMap::new(), &mut issues);
    let external = used.difference(&launched).copied().collect();
    let cohorts = validate_cohorts(program, context, &mut issues);
    let mut graph = build_region(&program.body, &transactions, &external, context, &cohorts);
    graph.issues.extend(issues);
    graph
}

fn validate_namespace_merges(
    program: &Program,
    context: &PlanningContext,
    issues: &mut Vec<Issue>,
) -> BTreeMap<InstructionId, NamespaceMergeProof> {
    fn visit(
        region: &Region,
        named: bool,
        context: &PlanningContext,
        valid: &mut BTreeMap<InstructionId, NamespaceMergeProof>,
        seen: &mut BTreeSet<InstructionId>,
        issues: &mut Vec<Issue>,
    ) {
        for instruction in &region.instructions {
            seen.insert(instruction.id);
            if let Some(proof) = context.namespace_merges.get(&instruction.id) {
                let facts = context
                    .operation_facts
                    .get(&instruction.id)
                    .copied()
                    .unwrap_or_default();
                let parent_consistent = context
                    .existing_parent_frontiers
                    .get(&instruction.id)
                    .is_none_or(|parent| proof.parent_is(parent));
                if !named && parent_consistent && proof.supports(instruction, facts) {
                    valid.insert(instruction.id, proof.clone());
                } else {
                    issues.push(Issue { instructions: vec![instruction.id], message: "namespace merge proof does not cover an implicit exact missing-file insertion; named transactions, trees, selectors and new-parent creation remain strict".into() });
                }
            }
            if let InstructionKind::Transaction { body, .. } = &instruction.kind {
                visit(body, true, context, valid, seen, issues);
            }
        }
    }
    let mut valid = BTreeMap::new();
    let mut seen = BTreeSet::new();
    visit(&program.body, false, context, &mut valid, &mut seen, issues);
    for id in context
        .namespace_merges
        .keys()
        .filter(|id| !seen.contains(id))
    {
        issues.push(Issue {
            instructions: vec![*id],
            message: "namespace merge proof refers to an instruction outside this submission"
                .into(),
        });
    }
    let mut orders = BTreeMap::new();
    for (id, proof) in &valid {
        if let Some(previous) = orders.insert((proof.epoch().clone(), proof.logical_order()), *id) {
            issues.push(Issue {
                instructions: vec![previous, *id],
                message: "a namespace merge epoch reused a logical publication ticket".into(),
            });
        }
    }
    valid
}

fn validate_physical_rendezvous(
    program: &Program,
    context: &PlanningContext,
    issues: &mut Vec<Issue>,
) -> Vec<FifoRendezvousProof> {
    fn find<'a>(
        region: &'a Region,
        id: InstructionId,
        path: &[InstructionId],
    ) -> Option<(&'a Instruction, Vec<InstructionId>)> {
        for instruction in &region.instructions {
            if instruction.id == id {
                return Some((instruction, path.to_vec()));
            }
            if let InstructionKind::Transaction { body, .. } = &instruction.kind {
                let mut child = path.to_vec();
                child.push(instruction.id);
                if let Some(found) = find(body, id, &child) {
                    return Some(found);
                }
            }
        }
        None
    }
    let mut valid = Vec::new();
    let mut endpoints = BTreeSet::new();
    for proof in &context.physical_rendezvous {
        let writer = proof.writer().key();
        let reader = proof.reader().key();
        let pair = find(&program.body, writer.instruction, &[]).zip(find(
            &program.body,
            reader.instruction,
            &[],
        ));
        let supports = pair.is_some_and(|((write, write_region), (read, read_region))| {
            write_region == read_region
                && !context.host_unstable.contains(&writer)
                && !context.host_unstable.contains(&reader)
                && proof.supports(
                    write,
                    context
                        .operation_facts
                        .get(&write.id)
                        .copied()
                        .unwrap_or_default(),
                    read,
                    context
                        .operation_facts
                        .get(&read.id)
                        .copied()
                        .unwrap_or_default(),
                )
        });
        let unique = !endpoints.contains(&writer) && !endpoints.contains(&reader);
        if supports && unique {
            endpoints.insert(writer);
            endpoints.insert(reader);
            valid.push(proof.clone());
        } else {
            issues.push(Issue { instructions: vec![writer.instruction, reader.instruction], message: "FIFO rendezvous certificate must cover distinct opposite local stream endpoints in one region with a stable native identity and exclusive endpoint ownership".into() });
        }
    }
    valid
}

fn validate_fifo_endpoints(
    program: &Program,
    context: &PlanningContext,
    issues: &mut Vec<Issue>,
) -> BTreeMap<EndpointKey, FifoEndpointProof> {
    fn find(region: &Region, id: InstructionId) -> Option<&Instruction> {
        region.instructions.iter().find_map(|node| {
            if node.id == id {
                return Some(node);
            }
            if let InstructionKind::Transaction { body, .. } = &node.kind {
                return find(body, id);
            }
            None
        })
    }
    let mut observations = context.physical_fifos.clone();
    for proof in &context.physical_rendezvous {
        for endpoint in [proof.writer(), proof.reader()] {
            if let Some(old) = observations.insert(endpoint.key(), endpoint.clone())
                && old != *endpoint
            {
                issues.push(Issue {
                    instructions: vec![endpoint.key().instruction],
                    message: "conflicting native FIFO endpoint observations".into(),
                });
            }
        }
    }
    observations.retain(|key, proof| {
        let valid = *key == proof.key()
            && !context.host_unstable.contains(key)
            && find(&program.body, key.instruction).is_some_and(|node| {
                proof.supports(
                    node,
                    context
                        .operation_facts
                        .get(&node.id)
                        .copied()
                        .unwrap_or_default(),
                )
            });
        if !valid {
            issues.push(Issue {
                instructions: vec![key.instruction],
                message: "native FIFO observation must cover its actual stable stream operand"
                    .into(),
            });
        }
        valid
    });
    observations
}

fn collect_port_owners(
    region: &Region,
    owners: &mut BTreeMap<(ProcessId, u8), InstructionId>,
    issues: &mut Vec<Issue>,
) {
    for instruction in &region.instructions {
        if let InstructionKind::Transaction { body, .. } = &instruction.kind {
            collect_port_owners(body, owners, issues);
        }
        if !matches!(instruction.kind, InstructionKind::Transfer { .. }) {
            continue;
        }
        for access in effects::summarize(instruction).accesses {
            let Resource::Port { process, port } = access.resource else {
                continue;
            };
            let port_id = match port {
                Port::Stdin => 0,
                Port::Stdout => 1,
                Port::Stderr => 2,
                _ => continue,
            };
            if let Some(owner) = owners.insert((process, port_id), instruction.id)
                && owner != instruction.id
            {
                issues.push(Issue {
                    instructions: vec![owner, instruction.id],
                    message: format!(
                        "process {} {port:?} is a consuming stream and requires one owner",
                        process.0
                    ),
                });
            }
        }
    }
}

#[derive(Default)]
struct CohortIndex {
    proofs: BTreeMap<CohortId, CohortViewProof>,
    membership: BTreeMap<ProcessId, CohortId>,
}

impl CohortIndex {
    fn same(&self, left: ProcessId, right: ProcessId) -> Option<CohortId> {
        self.membership
            .get(&left)
            .copied()
            .filter(|cohort| self.membership.get(&right) == Some(cohort))
    }

    fn gate(&self, wait: Wait) -> Wait {
        match wait {
            Wait::ProcessSealed(process) => self
                .membership
                .get(&process)
                .copied()
                .map_or(wait, Wait::CohortSealed),
            _ => wait,
        }
    }
}

type EpochSlot = (Vec<InstructionId>, usize);

fn validate_cohorts(
    program: &Program,
    context: &PlanningContext,
    issues: &mut Vec<Issue>,
) -> CohortIndex {
    let mut slots = BTreeMap::new();
    collect_epoch_slots(&program.body, &[], context, &mut slots);
    let mut index = CohortIndex::default();
    for (number, proof) in context.cohort_views.iter().enumerate() {
        let mut expected_slot = None;
        let mut valid = true;
        for (process, effects) in &proof.members {
            let slot = slots.get(process).cloned().unwrap_or_default();
            valid &= expected_slot
                .as_ref()
                .is_none_or(|expected| expected == &slot);
            expected_slot = Some(slot);
            valid &= context.process_effects.get(process) == Some(effects);
            valid &= !index.membership.contains_key(process);
        }
        // A retained stdout spool can be drained after a managed-file epoch
        // boundary. View membership certifies native launches, not the source
        // position of private IO services; their Start/Publish gates still apply.
        valid &= context.process_effects.iter().all(|(process, effects)| {
            effects.provider != proof.provider
                || effects.view_id != proof.view_id
                || proof.members.contains_key(process)
        });
        if !valid {
            issues.push(Issue { instructions: proof.members.keys().filter_map(|process| find_launch(&program.body, *process)).collect(), message: "shared-view certificate has incomplete/mismatched membership or crosses a transaction, checkpoint, rollback or external-file epoch boundary".into() });
            continue;
        }
        let id = CohortId(number);
        for process in proof.members.keys() {
            index.membership.insert(*process, id);
        }
        index.proofs.insert(id, proof.clone());
    }
    index
}

fn find_launch(region: &Region, process: ProcessId) -> Option<InstructionId> {
    region.instructions.iter().find_map(|instruction| {
        if spawn(instruction) == Some(process) {
            return Some(instruction.id);
        }
        match &instruction.kind {
            InstructionKind::Transaction { body, .. } => find_launch(body, process),
            _ => None,
        }
    })
}

fn collect_epoch_slots(
    region: &Region,
    path: &[InstructionId],
    context: &PlanningContext,
    slots: &mut BTreeMap<ProcessId, EpochSlot>,
) {
    let epochs = region_epochs(region, context);
    for instruction in &region.instructions {
        let epoch = epochs[&instruction.id];
        if let Some(process) = spawn(instruction) {
            slots.insert(process, (path.to_vec(), epoch));
        }
        if let InstructionKind::Transaction { body, .. } = &instruction.kind {
            let mut child = path.to_vec();
            child.push(instruction.id);
            collect_epoch_slots(body, &child, context, slots);
        }
    }
}

/// Whether two launches can belong to one native mutable-view epoch. This is
/// only a structural prerequisite: the runtime must still prove actual shared
/// provider/view ownership and satisfy the ordinary effect and version gates.
/// Retained live processes precede the new submission's root epoch.
pub fn same_native_epoch(
    program: &Program,
    context: &PlanningContext,
    left: ProcessId,
    right: ProcessId,
) -> bool {
    let mut slots = BTreeMap::new();
    collect_epoch_slots(&program.body, &[], context, &mut slots);
    let slot = |process| {
        slots.get(&process).cloned().or_else(|| {
            context
                .live_processes
                .contains(&process)
                .then(EpochSlot::default)
        })
    };
    slot(left)
        .zip(slot(right))
        .is_some_and(|(left, right)| left == right)
}

fn region_epochs(region: &Region, context: &PlanningContext) -> BTreeMap<InstructionId, usize> {
    let mut epoch = 0;
    let mut unpublished_file = false;
    let mut epochs = BTreeMap::new();
    for instruction in &region.instructions {
        if spawn(instruction).is_some() && unpublished_file {
            epoch += 1;
            unpublished_file = false;
        }
        epochs.insert(instruction.id, epoch);
        if epoch_boundary(instruction, context) {
            epoch += 1;
            unpublished_file = false;
        } else if let InstructionKind::Transfer {
            source: crate::hir::Input::Port { .. },
            destination: crate::hir::Output::File { location, .. },
        } = &instruction.kind
        {
            let destination = context
                .operation_facts
                .get(&instruction.id)
                .map_or(FileClass::Unknown, |facts| facts.destination);
            unpublished_file |= effects::file_class(location, destination) != FileClass::Stream;
        }
    }
    epochs
}

fn live_ports(instruction: &Instruction) -> Vec<ProcessId> {
    let InstructionKind::Transfer {
        source,
        destination,
    } = &instruction.kind
    else {
        return Vec::new();
    };
    let mut processes = Vec::new();
    if let crate::hir::Input::Port {
        process,
        port: crate::hir::ReadPort::Stdout | crate::hir::ReadPort::Stderr,
    } = source
    {
        processes.push(*process);
    }
    if let crate::hir::Output::Stdin(process) = destination {
        processes.push(*process);
    }
    processes
}

fn epoch_boundary(instruction: &Instruction, context: &PlanningContext) -> bool {
    match &instruction.kind {
        InstructionKind::Commit { .. }
        | InstructionKind::Rollback { .. }
        | InstructionKind::Transaction { .. } => true,
        InstructionKind::Transfer {
            source:
                crate::hir::Input::Port {
                    port: crate::hir::ReadPort::ExitCode | crate::hir::ReadPort::Meta,
                    ..
                },
            ..
        } => true,
        InstructionKind::Transfer {
            source,
            destination: crate::hir::Output::File { location, .. },
        } if !matches!(source, crate::hir::Input::Port { .. }) => {
            let facts = context
                .operation_facts
                .get(&instruction.id)
                .copied()
                .unwrap_or_default();
            let physical_source = matches!(source, crate::hir::Input::File { location, .. } if effects::file_class(location, facts.source) == FileClass::Stream);
            !physical_source
                && effects::file_class(location, facts.destination) != FileClass::Stream
        }
        _ => false,
    }
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
    cohorts: &CohortIndex,
) -> PlanGraph {
    let resets = local_epoch_resets(region, context);
    let reset_positions = region
        .instructions
        .iter()
        .enumerate()
        .filter_map(|(index, instruction)| {
            matches!(
                instruction.kind,
                InstructionKind::Rollback {
                    scope: RollbackScope::Checkpoint,
                    ..
                }
            )
            .then_some((instruction.id, index))
        })
        .collect::<BTreeMap<_, _>>();
    let discarded_at = resets
        .iter()
        .flat_map(|reset| {
            reset
                .reverted
                .iter()
                .map(|id| (*id, (reset.controller, reset_positions[&reset.controller])))
        })
        .collect::<BTreeMap<_, _>>();
    let cancel_candidates = resets
        .iter()
        .flat_map(|reset| reset.cancel.iter().copied())
        .collect::<BTreeSet<_>>();
    let mut graph = PlanGraph {
        process_proofs: context.process_effects.clone(),
        pending_effects: context.pending_effects.clone(),
        host_unstable: context.host_unstable.clone(),
        operation_facts: context.operation_facts.clone(),
        existing_parent_frontiers: context.existing_parent_frontiers.clone(),
        pinned_inputs: context.pinned_inputs.clone(),
        cohort_views: context.cohort_views.clone(),
        namespace_merges: context.namespace_merges.clone(),
        physical_rendezvous: context.physical_rendezvous.clone(),
        physical_fifos: context.physical_fifos.clone(),
        ..PlanGraph::default()
    };
    for instruction in &region.instructions {
        let children = match &instruction.kind {
            InstructionKind::Transaction { body, .. } => Some(Box::new(build_region(
                body,
                transactions,
                external,
                context,
                cohorts,
            ))),
            _ => None,
        };
        if let Some(children) = &children {
            graph.issues.extend(children.issues.iter().cloned());
            graph
                .refinements
                .extend(children.refinements.iter().cloned());
            graph
                .cohort_refinements
                .extend(children.cohort_refinements.iter().cloned());
            graph
                .epoch_resets
                .extend(children.epoch_resets.iter().cloned());
        }
        refine(instruction, context, &mut graph.refinements);
        let effects = footprint(instruction, transactions, context);
        for process in external {
            if spawn(instruction)
                .and_then(|launched| cohorts.same(*process, launched))
                .is_some()
            {
                graph.external_processes.push(ExternalProcessRequirement {
                    instruction: instruction.id,
                    process: *process,
                    wait: Wait::ProcessStarted(*process),
                });
                continue;
            }
            let used = uses_process(&effects, *process);
            let conflicts = context
                .process_effects
                .get(process)
                .map(|proof| proof.footprint.conflicts(&effects));
            if !used
                && context.process_effects.get(process).is_some_and(|proof| {
                    !effects.unknown_external && proof.footprint.conflicts(&effects).is_empty()
                })
            {
                continue;
            }
            if !used
                && staged_output(instruction, context).is_some()
                && conflicts.as_ref().is_some_and(|conflicts| {
                    !conflicts.is_empty() && conflicts.iter().all(publication_conflict)
                })
            {
                let requirement = ExternalProcessRequirement {
                    instruction: instruction.id,
                    process: *process,
                    wait: cohorts.gate(Wait::ProcessSealed(*process)),
                };
                if !graph.external_publications.contains(&requirement) {
                    graph.external_publications.push(requirement);
                }
                continue;
            }
            let wait = if !used {
                cohorts.gate(Wait::ProcessSealed(*process))
            } else if waits_for_exit(&effects, *process) {
                Wait::ProcessExited(*process)
            } else {
                Wait::ProcessStarted(*process)
            };
            graph.external_processes.push(ExternalProcessRequirement {
                instruction: instruction.id,
                process: *process,
                wait,
            });
            if used && let Some(proof) = context.process_effects.get(process) {
                let conflicts = proof.footprint.conflicts(&effects);
                if !conflicts.is_empty() {
                    let requirement = ExternalProcessRequirement {
                        instruction: instruction.id,
                        process: *process,
                        wait: cohorts.gate(Wait::ProcessSealed(*process)),
                    };
                    if staged_output(instruction, context).is_some()
                        && conflicts.iter().all(publication_conflict)
                    {
                        if !graph.external_publications.contains(&requirement) {
                            graph.external_publications.push(requirement);
                        }
                    } else if !graph.external_processes.contains(&requirement) {
                        graph.external_processes.push(requirement);
                    }
                }
            }
            if staged_output(instruction, context) == Some(*process) {
                let requirement = ExternalProcessRequirement {
                    instruction: instruction.id,
                    process: *process,
                    wait: cohorts.gate(Wait::ProcessSealed(*process)),
                };
                if !graph.external_publications.contains(&requirement) {
                    graph.external_publications.push(requirement);
                }
            }
        }
        graph.nodes.push(Node {
            instruction: instruction.id,
            effects,
            children,
        });
    }
    for reset in &resets {
        let mut restored = Effects::default();
        for node in &graph.nodes {
            if reset.reverted.contains(&node.instruction) {
                restored.append(restoration_effects(&node.effects));
            }
        }
        if let Some(controller) = graph
            .nodes
            .iter_mut()
            .find(|node| node.instruction == reset.controller)
        {
            controller.effects = restored;
        }
    }
    graph.epoch_resets.extend(resets.iter().cloned());
    graph.shared_cohorts = cohorts
        .proofs
        .iter()
        .map(|(id, proof)| {
            let mut aggregate = Effects::default();
            for effects in proof.members.values() {
                aggregate.append(effects.footprint.clone());
            }
            let launches = region
                .instructions
                .iter()
                .filter_map(|instruction| {
                    spawn(instruction)
                        .filter(|process| proof.members.contains_key(process))
                        .map(|process| (process, instruction.id))
                })
                .collect();
            let services = graph
                .nodes
                .iter()
                .filter(|node| {
                    proof
                        .members
                        .keys()
                        .any(|process| uses_process(&node.effects, *process))
                })
                .map(|node| node.instruction)
                .collect();
            SharedCohort {
                id: *id,
                proof: proof.clone(),
                launches,
                services,
                effects: aggregate,
            }
        })
        .collect();
    graph.cohort_refinements.extend(
        stream_components(region, context)
            .into_iter()
            .filter(|component| needs_shared_view(component, context, cohorts)),
    );
    let mut dependencies = BTreeMap::new();
    let mut publications = BTreeMap::new();
    for (right, after) in region.instructions.iter().enumerate() {
        for (left, before) in region.instructions[..right].iter().enumerate() {
            if discarded_at
                .get(&before.id)
                .is_some_and(|(controller, position)| *position < right && *controller != after.id)
            {
                // The reset event replaces these discarded reaching definitions.
                // This is a control dependency, never a commutation proof.
                continue;
            }
            let mut reasons = ordering(
                before,
                after,
                &graph.nodes[left].effects,
                &graph.nodes[right].effects,
                context,
            );
            if discarded_at
                .get(&before.id)
                .is_some_and(|(controller, _)| *controller == after.id)
            {
                let wait = if cancel_candidates.contains(&before.id) {
                    spawn(before).map_or(Wait::Admitted, Wait::ProcessStarted)
                } else {
                    Wait::ObservationComplete
                };
                reasons = vec![(wait, Reason::CancelEpoch(after.id))];
            } else if reset_positions.contains_key(&before.id) {
                reasons = vec![(Wait::EpochReset, Reason::EpochRestored(before.id))];
            }
            if let (Some(before_process), Some(after_process)) = (spawn(before), spawn(after))
                && let Some(cohort) = cohorts.same(before_process, after_process)
                && let Some(wait) = shared_native_launch_order(
                    before,
                    &graph.nodes[left].effects,
                    after,
                    &graph.nodes[right].effects,
                    &cohorts.proofs[&cohort],
                )
            {
                reasons = vec![(wait, Reason::SharedViewEpoch(cohort))];
            }
            let staged = staged_output(after, context);
            let producer = staged.filter(|process| spawn(before) == Some(*process));
            if reasons.is_empty() && producer.is_none() {
                graph.commutations.push(CommutationProof {
                    left: before.id,
                    right: after.id,
                    left_effects: graph.nodes[left].effects.clone(),
                    right_effects: graph.nodes[right].effects.clone(),
                });
            }
            if let Some(process) = producer {
                publications
                    .entry((
                        before.id,
                        after.id,
                        cohorts.gate(Wait::ProcessSealed(process)),
                    ))
                    .or_insert_with(Vec::new)
                    .push(Reason::OutputPublication(process));
            }
            for (wait, reason) in reasons.drain(..) {
                let edges = if staged.is_some() && publication_write(&reason) {
                    &mut publications
                } else {
                    &mut dependencies
                };
                edges
                    .entry((before.id, after.id, cohorts.gate(wait)))
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
    graph.publication_dependencies = publications
        .into_iter()
        .map(|((before, after, wait), reasons)| Dependency {
            before,
            after,
            wait,
            reasons,
        })
        .collect();
    connect_ports(region, context, &mut graph);
    validate_rendezvous_groups(&mut graph);
    graph
}

/// Connected physical co-start members for bounded admission. Callers must
/// validate the containing graph/certificates before treating these as groups.
pub(crate) fn physical_groups(proofs: &[FifoRendezvousProof]) -> Vec<Vec<InstructionId>> {
    fn owner(
        owners: &BTreeMap<InstructionId, InstructionId>,
        mut node: InstructionId,
    ) -> InstructionId {
        while owners[&node] != node {
            node = owners[&node];
        }
        node
    }
    let mut owners = BTreeMap::new();
    for proof in proofs {
        let writer = proof.writer().key().instruction;
        let reader = proof.reader().key().instruction;
        owners.entry(writer).or_insert(writer);
        owners.entry(reader).or_insert(reader);
        let writer = owner(&owners, writer);
        let reader = owner(&owners, reader);
        owners.insert(reader, writer);
    }
    let mut groups = BTreeMap::<_, Vec<_>>::new();
    for node in owners.keys() {
        groups.entry(owner(&owners, *node)).or_default().push(*node);
    }
    groups.into_values().collect()
}

fn validate_rendezvous_groups(graph: &mut PlanGraph) {
    let mut owners = graph
        .nodes
        .iter()
        .map(|node| (node.instruction, node.instruction))
        .collect::<BTreeMap<_, _>>();
    let mut connected = false;
    for group in physical_groups(&graph.physical_rendezvous) {
        let first = group[0];
        if !owners.contains_key(&first) {
            continue;
        }
        for node in group {
            owners.insert(node, first);
        }
        connected = true;
    }
    if !connected {
        return;
    }
    let connections = graph
        .physical_rendezvous
        .iter()
        .filter_map(|proof| {
            let writer = proof.writer().key().instruction;
            let reader = proof.reader().key().instruction;
            (owners.contains_key(&writer) && owners.contains_key(&reader))
                .then_some((writer, reader))
        })
        .collect::<BTreeSet<_>>();
    let closed = cyclic_nodes(owners.keys().copied().collect(), connections);
    if !closed.is_empty() {
        graph.issues.push(Issue { instructions: closed, message: "a closed FIFO cycle has no independent source/sink; break the cycle or use distinct channels".into() });
    }
    let collapsed = owners;
    let groups = collapsed.values().copied().collect::<BTreeSet<_>>();
    let mut edges = BTreeSet::new();
    for edge in graph
        .dependencies
        .iter()
        .chain(&graph.publication_dependencies)
    {
        if matches!(edge.wait, Wait::Admitted | Wait::ProcessStarted(_)) {
            continue;
        }
        let before = collapsed[&edge.before];
        let after = collapsed[&edge.after];
        if before == after {
            graph.issues.push(Issue { instructions: vec![edge.before, edge.after], message: "a FIFO rendezvous group depends on its own completion; stage the conflicting file resource or separate the channels".into() });
        } else {
            edges.insert((before, after));
        }
    }
    let blocked = cyclic_nodes(groups, edges);
    if !blocked.is_empty() {
        graph.issues.push(Issue {
            instructions: collapsed.iter().filter_map(|(node, group)| blocked.contains(group).then_some(*node)).collect(),
            message: "FIFO co-start groups form a completion-dependency cycle; separate the channels or stage the conflicting dependency".into(),
        });
    }
}

fn cyclic_nodes(
    groups: BTreeSet<InstructionId>,
    edges: BTreeSet<(InstructionId, InstructionId)>,
) -> Vec<InstructionId> {
    let mut incoming = groups
        .iter()
        .map(|group| (*group, 0_usize))
        .collect::<BTreeMap<_, _>>();
    let mut followers = BTreeMap::<_, Vec<_>>::new();
    for (before, after) in edges {
        *incoming.get_mut(&after).unwrap() += 1;
        followers.entry(before).or_default().push(after);
    }
    let mut ready = incoming
        .iter()
        .filter_map(|(group, count)| (*count == 0).then_some(*group))
        .collect::<Vec<_>>();
    let mut completed = 0;
    while let Some(group) = ready.pop() {
        completed += 1;
        for next in followers.get(&group).into_iter().flatten() {
            let count = incoming.get_mut(next).unwrap();
            *count -= 1;
            if *count == 0 {
                ready.push(*next);
            }
        }
    }
    if completed == groups.len() {
        return Vec::new();
    }
    incoming
        .into_iter()
        .filter_map(|(node, count)| (count > 0).then_some(node))
        .collect()
}

fn local_epoch_resets(region: &Region, context: &PlanningContext) -> Vec<EpochReset> {
    let mut pending = Vec::<&Instruction>::new();
    let mut checkpoint = None;
    let mut resets = Vec::new();
    for instruction in &region.instructions {
        match instruction.kind {
            InstructionKind::Commit { .. } => {
                pending.clear();
                checkpoint = Some(instruction.id);
            }
            InstructionKind::Rollback {
                transaction,
                scope: RollbackScope::Checkpoint,
            } => {
                resets.push(EpochReset {
                    controller: instruction.id,
                    transaction,
                    checkpoint,
                    reverted: pending.iter().map(|node| node.id).collect(),
                    cancel: pending
                        .iter()
                        .filter(|node| cancellable_epoch_work(node, context))
                        .map(|node| node.id)
                        .collect(),
                    processes: pending.iter().filter_map(|node| spawn(node)).collect(),
                });
                pending.clear();
            }
            InstructionKind::Spawn { .. } | InstructionKind::Transfer { .. } => {
                pending.push(instruction)
            }
            _ => {}
        }
    }
    resets
}

fn cancellable_epoch_work(instruction: &Instruction, context: &PlanningContext) -> bool {
    let InstructionKind::Transfer {
        source,
        destination,
    } = &instruction.kind
    else {
        return spawn(instruction).is_some();
    };
    if matches!(
        source,
        crate::hir::Input::Port {
            port: crate::hir::ReadPort::ExitCode | crate::hir::ReadPort::Meta,
            ..
        }
    ) {
        return false;
    }
    let facts = context
        .operation_facts
        .get(&instruction.id)
        .copied()
        .unwrap_or_default();
    match destination {
        crate::hir::Output::Stdin(_) => match source {
            crate::hir::Input::Literal(_) | crate::hir::Input::Port { .. } => true,
            crate::hir::Input::File {
                location,
                pattern: None,
            } => {
                !matches!(
                    location.backend,
                    crate::compiler::Backend::Http { .. } | crate::compiler::Backend::Tcp { .. }
                ) && !matches!(
                    effects::file_class(location, facts.source),
                    FileClass::Stream | FileClass::Directory
                )
            }
            _ => false,
        },
        crate::hir::Output::File { location, .. } => {
            matches!(source, crate::hir::Input::Port { .. })
                && effects::file_class(location, facts.destination) != FileClass::Stream
        }
    }
}

fn restoration_effects(effects: &Effects) -> Effects {
    let mut restored = Effects {
        unknown_external: effects.unknown_external,
        ..Effects::default()
    };
    for access in &effects.accesses {
        let resource = match (&access.resource, access.kind) {
            (Resource::File(_) | Resource::DirectoryMetadata(_), effects::AccessKind::Write) => {
                access.resource.clone()
            }
            (Resource::NamespaceEntry(proof), effects::AccessKind::Write) => Resource::File(
                effects::FileResource::new(proof.target().clone(), effects::Scope::Exact),
            ),
            (Resource::NamespaceChange(proof), effects::AccessKind::NamespaceMerge) => {
                Resource::DirectoryMetadata(effects::FileResource::new(
                    proof.parent().clone(),
                    effects::Scope::Exact,
                ))
            }
            _ => continue,
        };
        restored.accesses.push(effects::Access {
            resource,
            kind: effects::AccessKind::Write,
        });
    }
    restored
}

fn needs_shared_view(
    component: &CohortRefinement,
    context: &PlanningContext,
    cohorts: &CohortIndex,
) -> bool {
    let first = component.processes[0];
    if component
        .processes
        .iter()
        .all(|process| cohorts.same(first, *process).is_some())
    {
        return false;
    }
    for (index, left) in component.processes.iter().enumerate() {
        for right in &component.processes[index + 1..] {
            let (Some(left), Some(right)) = (
                context.process_effects.get(left),
                context.process_effects.get(right),
            ) else {
                return true;
            };
            if left.footprint.unknown_external
                || right.footprint.unknown_external
                || !left.footprint.conflicts(&right.footprint).is_empty()
            {
                return true;
            }
        }
    }
    false
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
        return transactions
            .get(&transaction)
            .map(|origin| Effects {
                // Origin undo restores managed versions; it does not consume a
                // process port or issue an observable HTTP request a second time.
                accesses: origin
                    .accesses
                    .iter()
                    .filter(|access| {
                        access.kind == effects::AccessKind::Write
                            && matches!(
                                access.resource,
                                Resource::File(_) | Resource::DirectoryMetadata(_)
                            )
                    })
                    .cloned()
                    .collect(),
                unknown_external: origin.unknown_external,
            })
            .unwrap_or(Effects {
                unknown_external: true,
                ..Effects::default()
            });
    }
    summarize(instruction, context)
}

fn summarize(instruction: &Instruction, context: &PlanningContext) -> Effects {
    let mut summary = match &instruction.kind {
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
        _ => effects::summarize_prepared(
            instruction,
            context
                .operation_facts
                .get(&instruction.id)
                .copied()
                .unwrap_or_default(),
            context.existing_parent_frontiers.get(&instruction.id),
            context.pinned_inputs.get(&instruction.id),
            context.namespace_merges.get(&instruction.id),
        ),
    };
    for (key, proof) in &context.physical_fifos {
        if key.instruction != instruction.id {
            continue;
        }
        summary.accesses.push(effects::Access {
            resource: Resource::StreamIdentity {
                provider: proof.provider().into(),
                identity: proof.identity().into(),
            },
            kind: match key.role {
                effects::EndpointRole::Source => effects::AccessKind::Consume,
                effects::EndpointRole::Destination => effects::AccessKind::Write,
                effects::EndpointRole::Executable => {
                    unreachable!("FIFO observation validated before summarizing")
                }
            },
        });
    }
    summary
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
    if let crate::hir::Input::File { location, pattern } = source {
        let pinned = pattern.is_none()
            && context
                .pinned_inputs
                .get(&instruction.id)
                .is_some_and(|proof| proof.covers(location));
        if !pinned {
            match effects::file_class(location, facts.source) {
            FileClass::Unknown | FileClass::Missing => refinements.push(Refinement { instruction: instruction.id, location: location.clone(), kind: RefinementKind::SourceType, message: "source is not proved replayable; inspect its epoch-specific type before choosing finite-file or physical-stream scheduling".into() }),
            FileClass::Stream => refinements.push(Refinement { instruction: instruction.id, location: location.clone(), kind: RefinementKind::PhysicalStreamScheduling, message: "physical stream requires identity-aware co-start/ownership preparation; this HIR graph is not its executable stream plan".into() }),
            _ => {}
        }
        }
        let needs_version = !pinned
            && (context.pinned_inputs.contains_key(&instruction.id)
                || (matches!(destination, crate::hir::Output::Stdin(_))
                    && effects::file_class(location, facts.source) != FileClass::Stream));
        if needs_version {
            refinements.push(Refinement { instruction: instruction.id, location: location.clone(), kind: RefinementKind::InputVersion, message: "select and retain this input's immutable regular-file version at its logical read epoch before co-start; file type/path alone does not prove a pinned reader".into() });
        }
        if pattern.is_some() {
            refinements.push(Refinement { instruction: instruction.id, location: location.clone(), kind: RefinementKind::SelectorExpansion, message: "selector entries require type/identity preparation before their physical transfers can be scheduled".into() });
        }
    }
    if let crate::hir::Output::File { location, .. } = destination {
        match effects::file_class(location, facts.destination) {
            FileClass::Unknown => refinements.push(Refinement { instruction: instruction.id, location: location.clone(), kind: RefinementKind::DestinationType, message: "destination may be a file, directory or physical stream; inspect its epoch-specific type before execution".into() }),
            FileClass::Stream => refinements.push(Refinement { instruction: instruction.id, location: location.clone(), kind: RefinementKind::PhysicalStreamScheduling, message: "physical stream destination requires identity-aware co-start preparation, not an ordinary completion-only graph".into() }),
            _ => {}
        }
        if matches!(source, crate::hir::Input::Port { .. })
            && effects::file_class(location, facts.destination) != FileClass::Stream
            && (facts.destination == FileClass::Unknown
                || staged_output(instruction, context).is_none())
        {
            refinements.push(Refinement { instruction: instruction.id, location: location.clone(), kind: RefinementKind::OutputPublication, message: "prove this program output is drained to a private regular-file stage before scheduling publication after the producer's sealed resource delta".into() });
        }
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
    spawn(before).map_or(Wait::Completion, Wait::ProcessSealed)
}

fn staged_output(instruction: &Instruction, context: &PlanningContext) -> Option<ProcessId> {
    let InstructionKind::Transfer {
        source: crate::hir::Input::Port { process, .. },
        destination:
            crate::hir::Output::File {
                location,
                mode: crate::language::DestinationMode::Exact,
            },
    } = &instruction.kind
    else {
        return None;
    };
    let destination = context
        .operation_facts
        .get(&instruction.id)
        .map_or(FileClass::Unknown, |facts| facts.destination);
    matches!(
        effects::file_class(location, destination),
        FileClass::Regular | FileClass::Missing | FileClass::Unknown
    )
    .then_some(*process)
}

fn publication_write(reason: &Reason) -> bool {
    matches!(reason, Reason::Hazard(conflict) if publication_conflict(conflict))
}

fn publication_conflict(conflict: &Conflict) -> bool {
    matches!(
        conflict.after.kind,
        effects::AccessKind::Write | effects::AccessKind::NamespaceMerge
    ) && matches!(
        conflict.after.resource,
        Resource::File(_)
            | Resource::DirectoryMetadata(_)
            | Resource::NamespaceEntry(_)
            | Resource::NamespaceChange(_)
    )
}

fn ordering(
    before: &Instruction,
    after: &Instruction,
    left: &Effects,
    right: &Effects,
    context: &PlanningContext,
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
    reasons.extend(left.conflicts(right).into_iter().map(|conflict| {
        let wait = if context
            .physical_rendezvous
            .iter()
            .any(|proof| proof.matches_conflict(before, after, &conflict))
        {
            Wait::Admitted
        } else {
            completion(before)
        };
        (wait, Reason::Hazard(Box::new(conflict)))
    }));
    reasons
}

fn connect_ports(region: &Region, context: &PlanningContext, graph: &mut PlanGraph) {
    let mut launches = BTreeMap::new();
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
        // A transaction summary describes effects, not another live consumer.
        if !matches!(instruction.kind, InstructionKind::Transfer { .. }) {
            continue;
        }
        for access in &node.effects.accesses {
            let Resource::Port { process, port } = access.resource else {
                continue;
            };
            match port {
                Port::Stdin | Port::Stdout | Port::Stderr => {}
                _ => continue,
            }
            members.entry(process).or_default().insert(instruction.id);
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
    reject_internal_waits(region, context, graph);
}

fn reject_cross_checkpoint_services(region: &Region, graph: &mut PlanGraph) {
    let mut epoch = 0;
    let epochs = region
        .instructions
        .iter()
        .map(|instruction| {
            let current = epoch;
            if cut(instruction) {
                epoch += 1;
            }
            (instruction.id, current)
        })
        .collect::<BTreeMap<_, _>>();
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
            if epochs[&cohort.launch] != epochs[member] {
                graph.issues.push(Issue { instructions: vec![cohort.launch, *member], message: "a live process pipe crosses a checkpoint; drain/stage it before the checkpoint or launch a new process afterward".into() });
            }
        }
    }
}

fn reject_internal_waits(region: &Region, context: &PlanningContext, graph: &mut PlanGraph) {
    let components = stream_components(region, context);
    let mut deferred = Vec::new();
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
                && !has_pending_service_refinement(graph, cohort, edge.after)
            {
                graph.issues.push(Issue { instructions: vec![edge.before, edge.after], message: format!("process {} pipe services have a completion hazard; stage/version the conflicting resource before co-start", cohort.process.0) });
            }
            if seals_process(graph, edge.wait, cohort.process)
                && cohort
                    .members
                    .iter()
                    .any(|member| reaches(graph, edge.after, *member))
                && !has_pending_service_refinement(graph, cohort, edge.after)
            {
                if let Some(component) = components
                    .iter()
                    .find(|component| component.processes.contains(&cohort.process))
                {
                    if !deferred.contains(component) {
                        deferred.push(component.clone());
                    }
                } else {
                    graph.issues.push(Issue { instructions: vec![edge.before, edge.after], message: format!("process {} pipe service depends on that process exiting; provide a mediated disjoint-effect proof or stage the dependency before co-start", cohort.process.0) });
                }
            }
        }
    }
    for component in deferred {
        if !graph.cohort_refinements.contains(&component) {
            graph.cohort_refinements.push(component);
        }
    }
}

fn seals_process(graph: &PlanGraph, wait: Wait, process: ProcessId) -> bool {
    match wait {
        Wait::ProcessExited(member) | Wait::ProcessSealed(member) => member == process,
        Wait::CohortSealed(id) => graph
            .cohort_views
            .get(id.0)
            .is_some_and(|proof| proof.members.contains_key(&process)),
        _ => false,
    }
}

fn stream_components(region: &Region, context: &PlanningContext) -> Vec<CohortRefinement> {
    let epochs = region_epochs(region, context);
    let mut adjacent = BTreeMap::<ProcessId, BTreeSet<ProcessId>>::new();
    for instruction in &region.instructions {
        let InstructionKind::Transfer {
            source:
                crate::hir::Input::Port {
                    process: source,
                    port: crate::hir::ReadPort::Stdout | crate::hir::ReadPort::Stderr,
                },
            destination: crate::hir::Output::Stdin(destination),
        } = &instruction.kind
        else {
            continue;
        };
        if source == destination {
            continue;
        }
        adjacent.entry(*source).or_default().insert(*destination);
        adjacent.entry(*destination).or_default().insert(*source);
    }
    let mut visited = BTreeSet::new();
    let mut result = Vec::new();
    for seed in adjacent.keys() {
        if visited.contains(seed) {
            continue;
        }
        let mut pending = vec![*seed];
        let mut processes = BTreeSet::new();
        while let Some(process) = pending.pop() {
            if visited.insert(process) {
                processes.insert(process);
                pending.extend(adjacent[&process].iter().copied());
            }
        }
        let mut slots = BTreeSet::new();
        let mut instructions = Vec::new();
        for instruction in &region.instructions {
            if spawn(instruction).is_some_and(|process| processes.contains(&process))
                || live_ports(instruction)
                    .iter()
                    .any(|process| processes.contains(process))
            {
                slots.insert(epochs[&instruction.id]);
                instructions.push(instruction.id);
            }
        }
        if slots.len() == 1 {
            result.push(CohortRefinement {
                processes: processes.into_iter().collect(),
                instructions,
                kind: CohortRefinementKind::SharedMutableView,
            });
        }
    }
    result
}

fn has_pending_service_refinement(
    graph: &PlanGraph,
    cohort: &CoStart,
    start: InstructionId,
) -> bool {
    cohort.members.iter().any(|member| {
        reaches(graph, start, *member)
            && graph.refinements.iter().any(|refinement| {
                refinement.instruction == *member
                    && matches!(
                        refinement.kind,
                        RefinementKind::InputVersion
                            | RefinementKind::PhysicalStreamScheduling
                            | RefinementKind::OutputPublication
                    )
            })
    })
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
