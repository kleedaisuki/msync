//! Submission-owned dependency bindings and persistent runtime event outcomes.
//!
//! Owner-local assignment visibility and foreign committed visibility are
//! different events. A private stream's EOF/readiness cannot fulfil either one.
//! Unknown future owners conservatively require committed prerequisites; native
//! admission extends bindings without mutating plans retained by existing work.
//! Graph-local cohort ordinals are resolved through their retained certificates.
//! Outstanding transfers from older submissions remain separate witnesses, not
//! fake nodes in the new HIR. Their exact captured effects use the same conflict
//! algebra, including native namespace-merge permits and consuming endpoints.
//! A shared owner/view does not turn an unpublished managed-file assignment
//! into permission for a native process to read an older baseline.
//!
//! `gate` checks dependency outcomes only. The caller must acquire the correct
//! input version, retain its lease, and check physical/type refinement before
//! starting payload effects. The single scheduler records actual transitions;
//! channel/watch wakeups are never reconstructed into completion facts.

use std::collections::BTreeMap;
use std::fmt;
use std::sync::Arc;

use tokio::sync::watch;

use crate::hir::effects::Effects;
use crate::hir::graph::{CohortViewProof, PlanGraph, PlanningContext, Wait};
use crate::hir::{self, InstructionId, ProcessId};
use crate::source::{InstructionOrigin, Origins};

use super::ExecutionError;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub(crate) struct SubmissionId(pub u64);

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Hash)]
pub(crate) struct NodeKey {
    pub submission: SubmissionId,
    pub instruction: InstructionId,
}

/// Source-order publication ticket; it is never an HIR instruction identity.
#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) struct PublicationOrder(pub u64);

#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) struct OwnerKey(Arc<str>);

impl OwnerKey {
    pub(crate) fn new(value: impl Into<Arc<str>>) -> Result<Self, BindingError> {
        let value = value.into();
        if value.is_empty() {
            return Err(BindingError::EmptyIdentity);
        }
        Ok(Self(value))
    }

    pub(crate) fn as_str(&self) -> &str {
        &self.0
    }
}

/// Identity of a real native view lease, not a graph-local cohort ordinal.
#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) struct CohortKey {
    pub provider: Arc<str>,
    pub view_id: Arc<str>,
    pub owner: OwnerKey,
    pub epoch: Arc<str>,
}

impl CohortKey {
    pub(crate) fn from_proof(proof: &CohortViewProof) -> Self {
        Self {
            provider: proof.provider().into(),
            view_id: proof.view_id().into(),
            owner: OwnerKey(proof.owner().into()),
            epoch: proof.epoch().into(),
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum NodeEvent {
    Admitted,
    ServiceReady,
    LogicalComplete,
    Committed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum ProcessEvent {
    Started,
    Exited,
    Imported,
    Committed,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum Visibility {
    Imported,
    Committed,
}

#[derive(Clone, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum Event {
    Node(NodeKey, NodeEvent),
    Process(ProcessId, ProcessEvent),
    Cohort(CohortKey, Visibility),
    /// A node's uncommitted contribution was discarded, not completed.
    EpochDiscarded(NodeKey),
    /// The controlling rollback really restored its checkpoint after joining IO.
    EpochReset(NodeKey),
    /// A historical IO observation, derived from actual logical completion.
    ObservationComplete(NodeKey),
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub(crate) enum CancelReason {
    Rollback(NodeKey),
    Stopped,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub(crate) enum Phase {
    Start,
    Publish,
    RegionCommit,
}

/// Future nodes may be unbound. A bound owner cannot change during refinement.
#[derive(Clone, Debug, Default)]
pub(crate) struct Bindings {
    pub nodes: BTreeMap<InstructionId, OwnerKey>,
    pub processes: BTreeMap<ProcessId, OwnerKey>,
    pub external: Vec<ExternalWork>,
}

/// An actual outstanding operation in another submission. Its original plan
/// keeps source sites and proof leases alive until the dependent gates retire.
#[derive(Clone, Debug)]
pub(crate) struct ExternalWork {
    plan: Arc<SubmissionPlan>,
    key: NodeKey,
    owner: OwnerKey,
    effects: Effects,
    phase: Phase,
}

impl ExternalWork {
    pub(crate) fn new(
        plan: Arc<SubmissionPlan>,
        key: NodeKey,
        owner: OwnerKey,
        effects: Effects,
        phase: Phase,
    ) -> Result<Self, BindingError> {
        if plan.node_key(key.instruction)? != key {
            return Err(BindingError::ExternalNodeMismatch(key));
        }
        let actual = plan
            .bindings
            .nodes
            .get(&key.instruction)
            .ok_or(BindingError::UnboundExternalOwner(key))?;
        if actual != &owner {
            return Err(BindingError::ExternalOwnerMismatch(key));
        }
        if plan.effects(key.instruction)? != &effects {
            return Err(BindingError::ExternalEffectsMismatch(key));
        }
        Ok(Self {
            plan,
            key,
            owner,
            effects,
            phase,
        })
    }

    pub(crate) fn plan(&self) -> &Arc<SubmissionPlan> {
        &self.plan
    }
    pub(crate) fn key(&self) -> NodeKey {
        self.key
    }
    pub(crate) fn owner(&self) -> &OwnerKey {
        &self.owner
    }
    pub(crate) fn effects(&self) -> &Effects {
        &self.effects
    }
    pub(crate) fn phase(&self) -> Phase {
        self.phase
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub(crate) enum BindingError {
    EmptyIdentity,
    IdentityExhausted,
    UnknownNode(InstructionId),
    DuplicateInstruction(InstructionId),
    UnknownCohort(usize),
    MissingCompletionSource,
    NodeOwnerChanged(InstructionId),
    ProcessOwnerChanged(ProcessId),
    CohortOwnerMismatch(ProcessId),
    InvalidGraph(Vec<crate::hir::graph::Issue>),
    AlreadyTerminal(Event),
    ExternalNodeMismatch(NodeKey),
    UnboundExternalOwner(NodeKey),
    ExternalOwnerMismatch(NodeKey),
    ExternalEffectsMismatch(NodeKey),
    ExternalPhaseRegression(NodeKey),
    FutureExternalSubmission(NodeKey),
    UnknownEpochReset(InstructionId),
    ResetRequiresAdmission(NodeKey),
    ResetOwnerMismatch(NodeKey),
    CommittedEpochMember(NodeKey),
    CommittedEpochProcess(ProcessId),
    ResetProcessOwnerMismatch(ProcessId),
    FailedEpochMember(NodeKey),
    PartialCohortReset(CohortKey),
    DerivedEvent(Event),
}

/// An admitted reset cut. Keeping the original plan preserves its source and
/// checkpoint identity while cancellation drains asynchronously.
#[derive(Debug)]
pub(crate) struct ResetFrame {
    plan: Arc<SubmissionPlan>,
    controller: NodeKey,
    cut: crate::hir::graph::EpochReset,
}

impl ResetFrame {
    pub(crate) fn plan(&self) -> &Arc<SubmissionPlan> {
        &self.plan
    }
    pub(crate) fn controller(&self) -> NodeKey {
        self.controller
    }
    pub(crate) fn cut(&self) -> &crate::hir::graph::EpochReset {
        &self.cut
    }
}

impl fmt::Display for BindingError {
    fn fmt(&self, output: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(output, "runtime event binding failed: {self:?}")
    }
}

impl std::error::Error for BindingError {}

/// A flow retains this exact graph and source context, not a session's latest IR.
#[derive(Debug)]
pub(crate) struct SubmissionPlan {
    id: SubmissionId,
    program: Arc<hir::Program>,
    origins: Origins,
    context: PlanningContext,
    graph: PlanGraph,
    bindings: Bindings,
    gates: BTreeMap<(InstructionId, Phase), Vec<Event>>,
}

impl SubmissionPlan {
    pub(crate) fn bind(
        id: SubmissionId,
        program: Arc<hir::Program>,
        origins: Origins,
        context: PlanningContext,
        graph: PlanGraph,
        bindings: Bindings,
    ) -> Result<Arc<Self>, BindingError> {
        graph
            .verify_with_context(&program, &context)
            .map_err(BindingError::InvalidGraph)?;
        let mut plan = Self {
            id,
            program,
            origins,
            context,
            graph,
            bindings,
            gates: BTreeMap::new(),
        };
        plan.gates = plan.bind_region(&plan.graph)?;
        for witness in &plan.bindings.external {
            if witness.plan().id() >= id {
                return Err(BindingError::FutureExternalSubmission(witness.key()));
            }
        }
        for instruction in plan.bindings.nodes.keys() {
            plan.key(*instruction)?;
        }
        for proof in &plan.context.cohort_views {
            for process in proof.members().keys() {
                if let Some(owner) = plan.bindings.processes.get(process)
                    && owner.as_str() != proof.owner()
                {
                    return Err(BindingError::CohortOwnerMismatch(*process));
                }
            }
        }
        Ok(Arc::new(plan))
    }

    pub(crate) fn rebind(
        &self,
        context: PlanningContext,
        graph: PlanGraph,
        additional: Bindings,
    ) -> Result<Arc<Self>, BindingError> {
        let mut bindings = self.bindings.clone();
        for (node, owner) in additional.nodes {
            if bindings.nodes.get(&node).is_some_and(|old| *old != owner) {
                return Err(BindingError::NodeOwnerChanged(node));
            }
            bindings.nodes.insert(node, owner);
        }
        for (process, owner) in additional.processes {
            if bindings
                .processes
                .get(&process)
                .is_some_and(|old| *old != owner)
            {
                return Err(BindingError::ProcessOwnerChanged(process));
            }
            bindings.processes.insert(process, owner);
        }
        for witness in additional.external {
            if let Some(previous) = bindings
                .external
                .iter_mut()
                .find(|old| old.key() == witness.key())
            {
                if previous.owner() != witness.owner() {
                    return Err(BindingError::ExternalOwnerMismatch(witness.key()));
                }
                if previous.effects() != witness.effects() {
                    return Err(BindingError::ExternalEffectsMismatch(witness.key()));
                }
                if previous.phase() > witness.phase() {
                    return Err(BindingError::ExternalPhaseRegression(witness.key()));
                }
                *previous = witness;
            } else {
                bindings.external.push(witness);
            }
        }
        Self::bind(
            self.id,
            self.program.clone(),
            self.origins.clone(),
            context,
            graph,
            bindings,
        )
    }

    pub(crate) fn id(&self) -> SubmissionId {
        self.id
    }

    pub(crate) fn program(&self) -> &hir::Program {
        &self.program
    }

    pub(crate) fn context(&self) -> &PlanningContext {
        &self.context
    }

    pub(crate) fn effects(&self, instruction: InstructionId) -> Result<&Effects, BindingError> {
        region_graph(&self.graph, instruction)
            .and_then(|graph| {
                graph
                    .nodes
                    .iter()
                    .find(|node| node.instruction == instruction)
            })
            .map(|node| &node.effects)
            .ok_or(BindingError::UnknownNode(instruction))
    }

    pub(crate) fn origin(&self, instruction: InstructionId) -> Option<&InstructionOrigin> {
        self.origins.get(&instruction)
    }

    pub(crate) fn node_key(&self, instruction: InstructionId) -> Result<NodeKey, BindingError> {
        self.key(instruction)
    }

    pub(crate) fn key(&self, instruction: InstructionId) -> Result<NodeKey, BindingError> {
        if !self.gates.contains_key(&(instruction, Phase::Start)) {
            return Err(BindingError::UnknownNode(instruction));
        }
        Ok(NodeKey {
            submission: self.id,
            instruction,
        })
    }

    fn same_owner(&self, target: InstructionId, before: Option<&OwnerKey>) -> bool {
        self.bindings
            .nodes
            .get(&target)
            .zip(before)
            .is_some_and(|(left, right)| left == right)
    }

    fn event(
        &self,
        graph: &PlanGraph,
        target: InstructionId,
        before: Option<InstructionId>,
        wait: Wait,
    ) -> Result<Event, BindingError> {
        let event = match wait {
            Wait::ObservationComplete => Event::ObservationComplete(NodeKey {
                submission: self.id,
                instruction: before.ok_or(BindingError::MissingCompletionSource)?,
            }),
            Wait::Admitted => Event::Node(
                NodeKey {
                    submission: self.id,
                    instruction: before.ok_or(BindingError::MissingCompletionSource)?,
                },
                NodeEvent::Admitted,
            ),
            Wait::EpochReset => Event::EpochReset(NodeKey {
                submission: self.id,
                instruction: before.ok_or(BindingError::MissingCompletionSource)?,
            }),
            Wait::Completion => {
                let before = before.ok_or(BindingError::MissingCompletionSource)?;
                let local = self.same_owner(target, self.bindings.nodes.get(&before));
                Event::Node(
                    NodeKey {
                        submission: self.id,
                        instruction: before,
                    },
                    if local {
                        NodeEvent::LogicalComplete
                    } else {
                        NodeEvent::Committed
                    },
                )
            }
            Wait::ProcessStarted(process) => Event::Process(process, ProcessEvent::Started),
            Wait::ProcessExited(process) => Event::Process(process, ProcessEvent::Exited),
            Wait::ProcessSealed(process) => {
                let local = self.same_owner(target, self.bindings.processes.get(&process));
                Event::Process(
                    process,
                    if local {
                        ProcessEvent::Imported
                    } else {
                        ProcessEvent::Committed
                    },
                )
            }
            Wait::CohortSealed(id) => {
                let proof = graph
                    .cohort_views
                    .get(id.0)
                    .ok_or(BindingError::UnknownCohort(id.0))?;
                let key = CohortKey::from_proof(proof);
                let local = self.same_owner(target, Some(&key.owner));
                Event::Cohort(
                    key,
                    if local {
                        Visibility::Imported
                    } else {
                        Visibility::Committed
                    },
                )
            }
        };
        Ok(event)
    }

    fn bind_region(
        &self,
        graph: &PlanGraph,
    ) -> Result<BTreeMap<(InstructionId, Phase), Vec<Event>>, BindingError> {
        let mut gates = BTreeMap::new();
        for node in &graph.nodes {
            for phase in [Phase::Start, Phase::Publish, Phase::RegionCommit] {
                gates.insert((node.instruction, phase), Vec::new());
            }
            for witness in &self.bindings.external {
                let conflict = witness.effects().unknown_external
                    || node.effects.unknown_external
                    || !witness.effects().conflicts(&node.effects).is_empty();
                if conflict {
                    let local = self.same_owner(node.instruction, Some(witness.owner()));
                    gates
                        .get_mut(&(node.instruction, Phase::Start))
                        .unwrap()
                        .push(Event::Node(
                            witness.key(),
                            if local {
                                NodeEvent::LogicalComplete
                            } else {
                                NodeEvent::Committed
                            },
                        ));
                }
            }
            if let Some(children) = &node.children {
                let events = children
                    .nodes
                    .iter()
                    .map(|child| {
                        let reset = children.epoch_resets.iter().rev().find(|reset| {
                            reset.controller == child.instruction
                                || reset.reverted.contains(&child.instruction)
                        });
                        if let Some(reset) = reset {
                            Event::EpochReset(NodeKey {
                                submission: self.id,
                                instruction: reset.controller,
                            })
                        } else {
                            Event::Node(
                                NodeKey {
                                    submission: self.id,
                                    instruction: child.instruction,
                                },
                                NodeEvent::LogicalComplete,
                            )
                        }
                    })
                    .collect();
                gates.insert((node.instruction, Phase::RegionCommit), events);
                gates.extend(self.bind_region(children)?);
            }
        }
        for (phase, dependencies, external) in [
            (Phase::Start, &graph.dependencies, &graph.external_processes),
            (
                Phase::Publish,
                &graph.publication_dependencies,
                &graph.external_publications,
            ),
        ] {
            for edge in dependencies {
                let event = self.event(graph, edge.after, Some(edge.before), edge.wait)?;
                gates
                    .get_mut(&(edge.after, phase))
                    .ok_or(BindingError::UnknownNode(edge.after))?
                    .push(event);
            }
            for edge in external {
                let event = self.event(graph, edge.instruction, None, edge.wait)?;
                gates
                    .get_mut(&(edge.instruction, phase))
                    .ok_or(BindingError::UnknownNode(edge.instruction))?
                    .push(event);
            }
        }
        for group in crate::hir::graph::physical_groups(&graph.physical_rendezvous) {
            if !group
                .iter()
                .all(|member| gates.contains_key(&(*member, Phase::Start)))
            {
                continue;
            }
            // Reserve the group only after every member's external gate is
            // ready. Copy bound events, not Wait edges: another member's owner
            // must not reinterpret a foreign commit as private completion.
            let prerequisites = group
                .iter()
                .flat_map(|member| &gates[&(*member, Phase::Start)])
                .filter(|event| {
                    !matches!(event, Event::Node(key, NodeEvent::Admitted)
                    if key.submission == self.id && group.contains(&key.instruction))
                })
                .cloned()
                .collect::<Vec<_>>();
            for member in group {
                gates
                    .get_mut(&(member, Phase::Start))
                    .unwrap()
                    .extend(prerequisites.clone());
            }
        }
        for events in gates.values_mut() {
            events.sort();
            events.dedup();
        }
        Ok(gates)
    }
}

fn region_graph(graph: &PlanGraph, instruction: InstructionId) -> Option<&PlanGraph> {
    if graph
        .nodes
        .iter()
        .any(|node| node.instruction == instruction)
    {
        return Some(graph);
    }
    graph
        .nodes
        .iter()
        .filter_map(|node| node.children.as_deref())
        .find_map(|child| region_graph(child, instruction))
}

#[derive(Clone, Debug)]
pub(crate) enum Outcome {
    Succeeded,
    Failed(Arc<ExecutionError>),
    Cancelled {
        reason: CancelReason,
        cause: Option<Arc<ExecutionError>>,
    },
}

#[derive(Clone, Debug)]
pub(crate) enum GateState {
    Ready,
    Pending(Vec<Event>),
    Failed {
        event: Event,
        cause: Arc<ExecutionError>,
    },
    Cancelled {
        event: Event,
        reason: CancelReason,
        cause: Option<Arc<ExecutionError>>,
    },
}

/// One session scheduler mutates truth; watch notifications merely request a poll.
pub(crate) struct Ledger {
    outcomes: BTreeMap<Event, Outcome>,
    changed: watch::Sender<()>,
    next_submission: u64,
    next_publication: u64,
}

impl Default for Ledger {
    fn default() -> Self {
        Self {
            outcomes: BTreeMap::new(),
            changed: watch::channel(()).0,
            next_submission: 0,
            next_publication: 0,
        }
    }
}

impl Ledger {
    pub(crate) fn next_submission(&mut self) -> Result<SubmissionId, BindingError> {
        let id = SubmissionId(self.next_submission);
        self.next_submission = self
            .next_submission
            .checked_add(1)
            .ok_or(BindingError::IdentityExhausted)?;
        Ok(id)
    }

    pub(crate) fn next_publication(&mut self) -> Result<PublicationOrder, BindingError> {
        let order = PublicationOrder(self.next_publication);
        self.next_publication = self
            .next_publication
            .checked_add(1)
            .ok_or(BindingError::IdentityExhausted)?;
        Ok(order)
    }

    pub(crate) fn publication_orders(
        &mut self,
        program: &hir::Program,
    ) -> Result<BTreeMap<InstructionId, PublicationOrder>, BindingError> {
        fn reserve(
            ledger: &mut Ledger,
            region: &hir::Region,
            orders: &mut BTreeMap<InstructionId, PublicationOrder>,
        ) -> Result<(), BindingError> {
            for instruction in &region.instructions {
                if orders
                    .insert(instruction.id, ledger.next_publication()?)
                    .is_some()
                {
                    return Err(BindingError::DuplicateInstruction(instruction.id));
                }
                if let hir::InstructionKind::Transaction { body, .. } = &instruction.kind {
                    reserve(ledger, body, orders)?;
                }
            }
            Ok(())
        }
        let mut orders = BTreeMap::new();
        reserve(self, &program.body, &mut orders)?;
        Ok(orders)
    }

    pub(crate) fn subscribe(&self) -> watch::Receiver<()> {
        self.changed.subscribe()
    }

    pub(crate) fn outcome(&self, event: &Event) -> Option<&Outcome> {
        match event {
            Event::ObservationComplete(node) => self
                .outcomes
                .get(&Event::Node(*node, NodeEvent::LogicalComplete)),
            _ => self.outcomes.get(event),
        }
    }

    /// The runtime must first release every work/region plan revision and pending
    /// owner/spawn reference for this submission. Global port receipts outlive it.
    pub(crate) fn retire_submission(&mut self, id: SubmissionId) -> usize {
        let before = self.outcomes.len();
        self.outcomes.retain(|event, _| {
            !matches!(event,
                Event::Node(node, _) | Event::EpochDiscarded(node) | Event::EpochReset(node) | Event::ObservationComplete(node)
                if node.submission == id
            )
        });
        before - self.outcomes.len()
    }

    pub(crate) fn record(&mut self, event: Event, outcome: Outcome) -> Result<bool, BindingError> {
        if matches!(event, Event::ObservationComplete(_)) {
            return Err(BindingError::DerivedEvent(event));
        }
        if let Some(previous) = self.outcomes.get(&event) {
            let repeated = match (previous, &outcome) {
                (Outcome::Succeeded, Outcome::Succeeded) => true,
                (Outcome::Failed(left), Outcome::Failed(right)) => Arc::ptr_eq(left, right),
                (
                    Outcome::Cancelled {
                        reason: left,
                        cause: left_cause,
                    },
                    Outcome::Cancelled {
                        reason: right,
                        cause: right_cause,
                    },
                ) => {
                    left == right
                        && match (left_cause, right_cause) {
                            (None, None) => true,
                            (Some(left), Some(right)) => Arc::ptr_eq(left, right),
                            _ => false,
                        }
                }
                _ => false,
            };
            return if repeated {
                Ok(false)
            } else {
                Err(BindingError::AlreadyTerminal(event))
            };
        }
        self.outcomes.insert(event, outcome);
        self.changed.send_replace(());
        Ok(true)
    }

    /// A scheduler propagates a blocked gate's typed cause, never a Drop/EOF guess.
    pub(crate) fn fail_node(&mut self, node: NodeKey, cause: Arc<ExecutionError>) {
        for phase in [
            NodeEvent::Admitted,
            NodeEvent::ServiceReady,
            NodeEvent::LogicalComplete,
            NodeEvent::Committed,
        ] {
            let event = Event::Node(node, phase);
            self.outcomes
                .entry(event)
                .or_insert_with(|| Outcome::Failed(cause.clone()));
        }
        self.changed.send_replace(());
    }

    pub(crate) fn cancel_node(
        &mut self,
        node: NodeKey,
        reason: CancelReason,
        cause: Option<Arc<ExecutionError>>,
    ) -> bool {
        if matches!(
            self.outcomes.get(&Event::Node(node, NodeEvent::Committed)),
            Some(Outcome::Succeeded)
        ) || self.outcomes.contains_key(&Event::EpochDiscarded(node))
        {
            return false;
        }
        if [
            NodeEvent::Admitted,
            NodeEvent::ServiceReady,
            NodeEvent::LogicalComplete,
            NodeEvent::Committed,
        ]
        .iter()
        .any(|phase| {
            matches!(
                self.outcome(&Event::Node(node, *phase)),
                Some(Outcome::Failed(_))
            )
        }) {
            return false;
        }
        let cancelled = Outcome::Cancelled { reason, cause };
        self.outcomes
            .insert(Event::EpochDiscarded(node), cancelled.clone());
        for phase in [
            NodeEvent::ServiceReady,
            NodeEvent::LogicalComplete,
            NodeEvent::Committed,
        ] {
            self.outcomes
                .entry(Event::Node(node, phase))
                .or_insert_with(|| cancelled.clone());
        }
        self.changed.send_replace(());
        true
    }

    pub(crate) fn cancel_process(
        &mut self,
        process: ProcessId,
        reason: CancelReason,
        cause: Option<Arc<ExecutionError>>,
    ) {
        self.cancel_pending(
            [
                Event::Process(process, ProcessEvent::Imported),
                Event::Process(process, ProcessEvent::Committed),
            ],
            reason,
            cause,
        );
    }

    pub(crate) fn cancel_cohort(
        &mut self,
        cohort: CohortKey,
        reason: CancelReason,
        cause: Option<Arc<ExecutionError>>,
    ) {
        self.cancel_pending(
            [
                Event::Cohort(cohort.clone(), Visibility::Imported),
                Event::Cohort(cohort, Visibility::Committed),
            ],
            reason,
            cause,
        );
    }

    fn cancel_pending(
        &mut self,
        events: [Event; 2],
        reason: CancelReason,
        cause: Option<Arc<ExecutionError>>,
    ) {
        if matches!(self.outcome(&events[1]), Some(Outcome::Succeeded)) {
            return;
        }
        let outcome = events
            .iter()
            .find_map(|event| match self.outcome(event) {
                Some(Outcome::Failed(cause)) => Some(Outcome::Failed(cause.clone())),
                _ => None,
            })
            .unwrap_or(Outcome::Cancelled { reason, cause });
        for event in events {
            self.outcomes
                .entry(event)
                .or_insert_with(|| outcome.clone());
        }
        self.changed.send_replace(());
    }

    pub(crate) fn cancel_epoch(
        &mut self,
        plan: Arc<SubmissionPlan>,
        controller: InstructionId,
    ) -> Result<ResetFrame, BindingError> {
        let key = plan.node_key(controller)?;
        let cut = plan
            .graph
            .epoch_resets
            .iter()
            .find(|cut| cut.controller == controller)
            .cloned()
            .ok_or(BindingError::UnknownEpochReset(controller))?;
        if !matches!(
            self.gate(&plan, controller, Phase::Start)?,
            GateState::Ready
        ) {
            return Err(BindingError::ResetRequiresAdmission(key));
        }
        let owner = plan
            .bindings
            .nodes
            .get(&controller)
            .ok_or(BindingError::ResetOwnerMismatch(key))?;
        let mut nodes = Vec::new();
        for instruction in &cut.reverted {
            let node = plan.node_key(*instruction)?;
            if plan
                .bindings
                .nodes
                .get(instruction)
                .is_some_and(|value| value != owner)
            {
                return Err(BindingError::ResetOwnerMismatch(node));
            }
            if matches!(
                self.outcome(&Event::Node(node, NodeEvent::Committed)),
                Some(Outcome::Succeeded)
            ) {
                return Err(BindingError::CommittedEpochMember(node));
            }
            if [
                NodeEvent::Admitted,
                NodeEvent::ServiceReady,
                NodeEvent::LogicalComplete,
                NodeEvent::Committed,
            ]
            .iter()
            .any(|phase| {
                matches!(
                    self.outcome(&Event::Node(node, *phase)),
                    Some(Outcome::Failed(_))
                )
            }) {
                return Err(BindingError::FailedEpochMember(node));
            }
            nodes.push(node);
        }
        for process in &cut.processes {
            if plan
                .bindings
                .processes
                .get(process)
                .is_some_and(|value| value != owner)
            {
                return Err(BindingError::ResetProcessOwnerMismatch(*process));
            }
            if matches!(
                self.outcome(&Event::Process(*process, ProcessEvent::Committed)),
                Some(Outcome::Succeeded)
            ) {
                return Err(BindingError::CommittedEpochProcess(*process));
            }
        }
        let mut cohorts = Vec::new();
        for proof in &plan.context.cohort_views {
            if !proof
                .members()
                .keys()
                .any(|process| cut.processes.contains(process))
            {
                continue;
            }
            let cohort = CohortKey::from_proof(proof);
            if proof
                .members()
                .keys()
                .any(|process| !cut.processes.contains(process))
            {
                return Err(BindingError::PartialCohortReset(cohort));
            }
            cohorts.push(cohort);
        }
        let reason = CancelReason::Rollback(key);
        for node in nodes {
            self.cancel_node(node, reason, None);
        }
        for process in &cut.processes {
            self.cancel_process(*process, reason, None);
        }
        for cohort in &cohorts {
            self.cancel_cohort(cohort.clone(), reason, None);
        }
        Ok(ResetFrame {
            plan,
            controller: key,
            cut,
        })
    }

    /// Called only after actual IO/native joins and a successful Manager reset.
    pub(crate) fn finish_reset(&mut self, frame: ResetFrame) -> Result<(), BindingError> {
        self.record(Event::EpochReset(frame.controller), Outcome::Succeeded)?;
        Ok(())
    }

    pub(crate) fn fail_reset(
        &mut self,
        frame: ResetFrame,
        cause: Arc<ExecutionError>,
    ) -> Result<(), BindingError> {
        self.record(Event::EpochReset(frame.controller), Outcome::Failed(cause))?;
        Ok(())
    }

    pub(crate) fn gate(
        &self,
        plan: &SubmissionPlan,
        instruction: InstructionId,
        phase: Phase,
    ) -> Result<GateState, BindingError> {
        let events = plan
            .gates
            .get(&(instruction, phase))
            .ok_or(BindingError::UnknownNode(instruction))?;
        let target = plan.node_key(instruction)?;
        if let Some(Outcome::Cancelled { reason, cause }) =
            self.outcome(&Event::EpochDiscarded(target))
        {
            return Ok(GateState::Cancelled {
                event: Event::EpochDiscarded(target),
                reason: *reason,
                cause: cause.clone(),
            });
        }
        let mut pending = Vec::new();
        for event in events {
            if let Event::Node(node, phase) = event
                && *phase != NodeEvent::Admitted
                && let Some(Outcome::Cancelled { reason, cause }) =
                    self.outcome(&Event::EpochDiscarded(*node))
            {
                return Ok(GateState::Cancelled {
                    event: event.clone(),
                    reason: *reason,
                    cause: cause.clone(),
                });
            }
            match self.outcome(event) {
                Some(Outcome::Succeeded) => {}
                Some(Outcome::Failed(cause)) => {
                    return Ok(GateState::Failed {
                        event: event.clone(),
                        cause: cause.clone(),
                    });
                }
                Some(Outcome::Cancelled { reason, cause }) => {
                    return Ok(GateState::Cancelled {
                        event: event.clone(),
                        reason: *reason,
                        cause: cause.clone(),
                    });
                }
                None => pending.push(event.clone()),
            }
        }
        Ok(if pending.is_empty() {
            GateState::Ready
        } else {
            GateState::Pending(pending)
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::endpoint::{Backend, Location};
    use crate::hir::effects::{Effects, OperationFacts};
    use crate::hir::graph::{self, ProcessEffects};
    use crate::hir::{Input, Instruction, InstructionKind, Output, ReadPort, Region};
    use crate::language::DestinationMode;
    use crate::plan::FileClass;
    use crate::syntax::Span;

    fn local(path: &str) -> Location {
        Location {
            backend: Backend::Local,
            path: path.into(),
        }
    }

    fn instruction(id: usize, source: Input, destination: Output) -> Instruction {
        Instruction {
            id: InstructionId(id),
            kind: InstructionKind::Transfer {
                source,
                destination,
            },
            span: Span::default(),
            expansion: Vec::new(),
        }
    }

    fn file(path: &str) -> Output {
        Output::File {
            location: local(path),
            mode: DestinationMode::Exact,
        }
    }

    fn owner(name: &str) -> OwnerKey {
        OwnerKey::new(name).unwrap()
    }

    #[test]
    fn fifo_group_reservation_preserves_every_members_bound_external_visibility() {
        use crate::hir::effects::{
            EndpointKey, EndpointRole, FifoEndpointProof, FifoRendezvousProof,
        };
        let program = Arc::new(hir::Program {
            body: Region {
                instructions: vec![
                    instruction(0, Input::Literal("earlier".into()), file("/target")),
                    instruction(1, Input::Literal("later".into()), file("/fifo")),
                    instruction(
                        2,
                        Input::File {
                            location: local("/fifo"),
                            pattern: None,
                        },
                        file("/target"),
                    ),
                ],
            },
            transactions: Vec::new(),
        });
        let mut context = PlanningContext::default();
        for (id, source, destination) in [
            (0, FileClass::Regular, FileClass::Regular),
            (1, FileClass::Regular, FileClass::Stream),
            (2, FileClass::Stream, FileClass::Regular),
        ] {
            context.operation_facts.insert(
                InstructionId(id),
                OperationFacts {
                    source,
                    destination,
                },
            );
        }
        let endpoint = |id, role| {
            FifoEndpointProof::verified_fifo(
                EndpointKey {
                    instruction: InstructionId(id),
                    role,
                },
                local("/fifo"),
                "test-fifo",
                "inode-42",
            )
            .unwrap()
        };
        context.physical_rendezvous.push(
            FifoRendezvousProof::connect(
                endpoint(1, EndpointRole::Destination),
                endpoint(2, EndpointRole::Source),
            )
            .unwrap(),
        );
        let graph = graph::build_with_context(&program, &context);
        let plan = SubmissionPlan::bind(
            SubmissionId(0),
            program,
            Origins::default(),
            context,
            graph,
            Bindings {
                nodes: [
                    (InstructionId(0), owner("A")),
                    (InstructionId(1), owner("A")),
                    (InstructionId(2), owner("B")),
                ]
                .into(),
                ..Bindings::default()
            },
        )
        .unwrap();
        let mut ledger = Ledger::default();
        let producer = plan.node_key(InstructionId(0)).unwrap();
        ledger
            .record(
                Event::Node(producer, NodeEvent::LogicalComplete),
                Outcome::Succeeded,
            )
            .unwrap();
        let GateState::Pending(events) =
            ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap()
        else {
            panic!(
                "one ready member cannot reserve capacity before its peer's external producer commits"
            )
        };
        assert!(events.contains(&Event::Node(producer, NodeEvent::Committed)));
        ledger
            .record(
                Event::Node(producer, NodeEvent::Committed),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap(),
            GateState::Ready
        ));
        let writer = plan.node_key(InstructionId(1)).unwrap();
        ledger
            .record(Event::Node(writer, NodeEvent::Admitted), Outcome::Succeeded)
            .unwrap();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(2), Phase::Start).unwrap(),
            GateState::Ready
        ));
    }

    fn fixture(id: SubmissionId, consumer: OwnerKey) -> Arc<SubmissionPlan> {
        let program = Arc::new(hir::Program {
            body: Region {
                instructions: vec![
                    instruction(0, Input::Literal("value".into()), file("/virtual/source")),
                    instruction(
                        1,
                        Input::File {
                            location: local("/virtual/source"),
                            pattern: None,
                        },
                        file("/virtual/output"),
                    ),
                ],
            },
            transactions: Vec::new(),
        });
        let mut context = PlanningContext::default();
        for instruction in [InstructionId(0), InstructionId(1)] {
            context.operation_facts.insert(
                instruction,
                OperationFacts {
                    source: FileClass::Regular,
                    destination: FileClass::Missing,
                },
            );
        }
        let graph = graph::build_with_context(&program, &context);
        SubmissionPlan::bind(
            id,
            program,
            Origins::default(),
            context,
            graph,
            Bindings {
                nodes: [
                    (InstructionId(0), owner("owner-A")),
                    (InstructionId(1), consumer),
                ]
                .into(),
                processes: BTreeMap::new(),
                external: Vec::new(),
            },
        )
        .unwrap()
    }

    #[test]
    fn identical_hir_ids_in_different_submissions_never_share_node_completion() {
        let mut ledger = Ledger::default();
        let first = fixture(ledger.next_submission().unwrap(), owner("owner-A"));
        let second = fixture(ledger.next_submission().unwrap(), owner("owner-A"));
        assert_ne!(
            first.node_key(InstructionId(0)).unwrap(),
            second.node_key(InstructionId(0)).unwrap()
        );
        ledger
            .record(
                Event::Node(
                    first.node_key(InstructionId(0)).unwrap(),
                    NodeEvent::LogicalComplete,
                ),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger.gate(&first, InstructionId(1), Phase::Start).unwrap(),
            GateState::Ready
        ));
        assert!(matches!(
            ledger
                .gate(&second, InstructionId(1), Phase::Start)
                .unwrap(),
            GateState::Pending(_)
        ));
    }

    #[test]
    fn same_owner_file_to_stdin_needs_logical_assignment_but_not_host_commit() {
        let process = ProcessId(7);
        let program = Arc::new(hir::Program {
            body: Region {
                instructions: vec![
                    instruction(0, Input::Literal("value".into()), file("/virtual/input")),
                    instruction(
                        1,
                        Input::File {
                            location: local("/virtual/input"),
                            pattern: None,
                        },
                        Output::Stdin(process),
                    ),
                ],
            },
            transactions: Vec::new(),
        });
        let mut context = PlanningContext {
            live_processes: vec![process],
            ..PlanningContext::default()
        };
        context.process_effects.insert(
            process,
            ProcessEffects::mediated(Effects::default(), "fixture-provider", "native-view")
                .unwrap(),
        );
        context.operation_facts.insert(
            InstructionId(0),
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Missing,
            },
        );
        context.operation_facts.insert(
            InstructionId(1),
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Stream,
            },
        );
        let graph = graph::build_with_context(&program, &context);
        let plan = SubmissionPlan::bind(
            SubmissionId(0),
            program,
            Origins::default(),
            context,
            graph,
            Bindings {
                nodes: [
                    (InstructionId(0), owner("same-owner")),
                    (InstructionId(1), owner("same-owner")),
                ]
                .into(),
                processes: [(process, owner("same-owner"))].into(),
                external: Vec::new(),
            },
        )
        .unwrap();
        let mut ledger = Ledger::default();
        ledger
            .record(
                Event::Process(process, ProcessEvent::Started),
                Outcome::Succeeded,
            )
            .unwrap();
        let producer = plan.node_key(InstructionId(0)).unwrap();
        ledger
            .record(
                Event::Node(producer, NodeEvent::ServiceReady),
                Outcome::Succeeded,
            )
            .unwrap();
        let GateState::Pending(events) =
            ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap()
        else {
            panic!("private readiness is not a logical assignment")
        };
        assert!(events.contains(&Event::Node(producer, NodeEvent::LogicalComplete)));
        ledger
            .record(
                Event::Node(producer, NodeEvent::LogicalComplete),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap(),
            GateState::Ready
        ));
        assert!(
            ledger
                .outcome(&Event::Node(producer, NodeEvent::Committed))
                .is_none()
        );
    }

    #[test]
    fn foreign_reader_waits_for_commit_not_private_or_owner_local_completion() {
        let plan = fixture(SubmissionId(0), owner("foreign-owner"));
        let mut ledger = Ledger::default();
        let producer = plan.node_key(InstructionId(0)).unwrap();
        for event in [NodeEvent::ServiceReady, NodeEvent::LogicalComplete] {
            ledger
                .record(Event::Node(producer, event), Outcome::Succeeded)
                .unwrap();
        }
        let GateState::Pending(events) =
            ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap()
        else {
            panic!("foreign owner cannot consume pending assignment")
        };
        assert!(events.contains(&Event::Node(producer, NodeEvent::Committed)));
        ledger
            .record(
                Event::Node(producer, NodeEvent::Committed),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap(),
            GateState::Ready
        ));
    }

    fn cohort_plan(id: u64, view: &str, consumer: &str, base: usize) -> Arc<SubmissionPlan> {
        let process = ProcessId(base);
        let sibling = ProcessId(base + 1);
        let effects =
            ProcessEffects::mediated(Effects::default(), "fixture-provider", view).unwrap();
        let proof = CohortViewProof::shared(
            "native-owner",
            "native-epoch",
            [(process, effects.clone()), (sibling, effects.clone())],
        )
        .unwrap();
        let program = Arc::new(hir::Program {
            body: Region {
                instructions: vec![instruction(
                    0,
                    Input::Port {
                        process,
                        port: ReadPort::Stdout,
                    },
                    file("/virtual/output"),
                )],
            },
            transactions: Vec::new(),
        });
        let mut context = PlanningContext {
            live_processes: vec![process, sibling],
            process_effects: [(process, effects.clone()), (sibling, effects)].into(),
            cohort_views: vec![proof],
            ..PlanningContext::default()
        };
        context.operation_facts.insert(
            InstructionId(0),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
        );
        let graph = graph::build_with_context(&program, &context);
        SubmissionPlan::bind(
            SubmissionId(id),
            program,
            Origins::default(),
            context,
            graph,
            Bindings {
                nodes: [(InstructionId(0), owner(consumer))].into(),
                processes: [
                    (process, owner("native-owner")),
                    (sibling, owner("native-owner")),
                ]
                .into(),
                external: Vec::new(),
            },
        )
        .unwrap()
    }

    #[test]
    fn cohort_import_is_owner_local_and_local_ordinals_are_not_event_ids() {
        let local = cohort_plan(0, "actual-view-A", "native-owner", 10);
        let foreign = cohort_plan(1, "actual-view-A", "foreign-owner", 10);
        let other_view = cohort_plan(2, "actual-view-B", "native-owner", 20);
        let mut ledger = Ledger::default();
        let cohort = CohortKey::from_proof(&local.context().cohort_views[0]);
        ledger
            .record(
                Event::Cohort(cohort.clone(), Visibility::Imported),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger
                .gate(&local, InstructionId(0), Phase::Publish)
                .unwrap(),
            GateState::Ready
        ));
        assert!(matches!(
            ledger
                .gate(&foreign, InstructionId(0), Phase::Publish)
                .unwrap(),
            GateState::Pending(_)
        ));
        assert!(matches!(
            ledger
                .gate(&other_view, InstructionId(0), Phase::Publish)
                .unwrap(),
            GateState::Pending(_)
        ));
        ledger
            .record(
                Event::Cohort(cohort, Visibility::Committed),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger
                .gate(&foreign, InstructionId(0), Phase::Publish)
                .unwrap(),
            GateState::Ready
        ));
    }

    #[tokio::test]
    async fn terminal_failure_wakes_dependents_and_survives_late_subscription() {
        let plan = fixture(SubmissionId(0), owner("owner-A"));
        let mut ledger = Ledger::default();
        let mut changes = ledger.subscribe();
        let cause = Arc::new(ExecutionError::caused(
            "typed failure",
            Span::default(),
            std::io::Error::from_raw_os_error(13),
        ));
        ledger.fail_node(plan.node_key(InstructionId(0)).unwrap(), cause.clone());
        tokio::time::timeout(std::time::Duration::from_secs(1), changes.changed())
            .await
            .unwrap()
            .unwrap();
        let GateState::Failed {
            cause: observed, ..
        } = ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap()
        else {
            panic!("failed dependency must not remain pending")
        };
        assert!(Arc::ptr_eq(&cause, &observed));
        let _late = ledger.subscribe();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap(),
            GateState::Failed { .. }
        ));
        ledger.fail_node(plan.node_key(InstructionId(1)).unwrap(), observed);
        assert!(matches!(
            ledger.outcome(&Event::Node(
                plan.node_key(InstructionId(1)).unwrap(),
                NodeEvent::Committed
            )),
            Some(Outcome::Failed(_))
        ));
        assert!(matches!(
            ledger.record(
                Event::Node(
                    plan.node_key(InstructionId(0)).unwrap(),
                    NodeEvent::LogicalComplete
                ),
                Outcome::Succeeded
            ),
            Err(BindingError::AlreadyTerminal(_))
        ));
    }

    #[test]
    fn rebind_extends_actual_owners_without_mutating_retained_plan_context() {
        let initial = fixture(SubmissionId(0), owner("owner-A"));
        let old = SubmissionPlan::bind(
            initial.id(),
            initial.program.clone(),
            Origins::default(),
            initial.context.clone(),
            initial.graph.clone(),
            Bindings {
                nodes: [(InstructionId(0), owner("owner-A"))].into(),
                processes: BTreeMap::new(),
                external: Vec::new(),
            },
        )
        .unwrap();
        let current = old
            .rebind(
                old.context.clone(),
                old.graph.clone(),
                Bindings {
                    nodes: [(InstructionId(1), owner("owner-A"))].into(),
                    processes: BTreeMap::new(),
                    external: Vec::new(),
                },
            )
            .unwrap();
        assert_eq!(
            old.node_key(InstructionId(0)).unwrap(),
            current.node_key(InstructionId(0)).unwrap()
        );
        assert!(!old.bindings.nodes.contains_key(&InstructionId(1)));
        assert!(current.bindings.nodes.contains_key(&InstructionId(1)));
        let mut ledger = Ledger::default();
        ledger
            .record(
                Event::Node(
                    old.node_key(InstructionId(0)).unwrap(),
                    NodeEvent::LogicalComplete,
                ),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger.gate(&old, InstructionId(1), Phase::Start).unwrap(),
            GateState::Pending(_)
        ));
        assert!(matches!(
            ledger
                .gate(&current, InstructionId(1), Phase::Start)
                .unwrap(),
            GateState::Ready
        ));
        assert!(matches!(
            current.rebind(
                current.context.clone(),
                current.graph.clone(),
                Bindings {
                    nodes: [(InstructionId(0), owner("different-owner"))].into(),
                    processes: BTreeMap::new(),
                    external: Vec::new()
                }
            ),
            Err(BindingError::NodeOwnerChanged(InstructionId(0)))
        ));
    }

    #[test]
    fn publication_tickets_follow_source_order_not_hir_numeric_order() {
        let mut ledger = Ledger::default();
        assert_eq!(ledger.next_submission().unwrap(), SubmissionId(0));
        let program = hir::Program {
            body: Region {
                instructions: vec![
                    instruction(9, Input::Literal("a".into()), file("/a")),
                    instruction(2, Input::Literal("b".into()), file("/b")),
                ],
            },
            transactions: Vec::new(),
        };
        let orders = ledger.publication_orders(&program).unwrap();
        assert!(orders[&InstructionId(9)] < orders[&InstructionId(2)]);
        assert!(ledger.next_publication().unwrap() > orders[&InstructionId(2)]);
        assert_eq!(ledger.next_submission().unwrap(), SubmissionId(1));
    }

    #[test]
    fn retained_plan_preserves_source_and_refinement_context_across_rebind() {
        let initial = fixture(SubmissionId(0), owner("owner-A"));
        let source = crate::source::Source::new("old-input.msync", "literal -> output\n");
        let span = Span::default();
        let origin = InstructionOrigin {
            site: source.at(span),
            definition: source.at(span),
            expansion: Vec::new(),
            operands: None,
        };
        let old = SubmissionPlan::bind(
            initial.id(),
            initial.program.clone(),
            [(InstructionId(0), origin)].into(),
            initial.context.clone(),
            initial.graph.clone(),
            initial.bindings.clone(),
        )
        .unwrap();
        assert!(old.graph.refinements.is_empty());
        let mut context = old.context().clone();
        context
            .operation_facts
            .get_mut(&InstructionId(1))
            .unwrap()
            .source = FileClass::Unknown;
        let graph = graph::build_with_context(old.program(), &context);
        let new = old.rebind(context, graph, Bindings::default()).unwrap();
        assert_eq!(
            old.context().operation_facts[&InstructionId(1)].source,
            FileClass::Regular
        );
        assert_eq!(
            new.context().operation_facts[&InstructionId(1)].source,
            FileClass::Unknown
        );
        assert!(
            new.graph
                .refinements
                .iter()
                .any(|refinement| refinement.instruction == InstructionId(1)
                    && refinement.kind == crate::hir::graph::RefinementKind::SourceType)
        );
        assert_ne!(old.graph, new.graph);
        assert_eq!(old.origins.len(), 1);
        assert_eq!(
            new.origin(InstructionId(0))
                .unwrap()
                .site
                .source
                .name
                .as_ref(),
            "old-input.msync"
        );
        assert!(Arc::ptr_eq(
            &old.origin(InstructionId(0)).unwrap().site.source,
            &new.origin(InstructionId(0)).unwrap().site.source
        ));
    }

    #[test]
    fn region_commit_requires_logical_children_and_foreign_reader_requires_wrapper_commit() {
        let transaction = crate::hir::TransactionId(4);
        let program = Arc::new(hir::Program {
            body: Region {
                instructions: vec![
                    Instruction {
                        id: InstructionId(0),
                        kind: InstructionKind::Transaction {
                            transaction,
                            body: Region {
                                instructions: vec![instruction(
                                    9,
                                    Input::Literal("inside".into()),
                                    file("/virtual/input"),
                                )],
                            },
                        },
                        span: Span::default(),
                        expansion: Vec::new(),
                    },
                    instruction(
                        2,
                        Input::File {
                            location: local("/virtual/input"),
                            pattern: None,
                        },
                        file("/virtual/output"),
                    ),
                ],
            },
            transactions: vec![crate::hir::TransactionInfo {
                id: transaction,
                name: "scope".into(),
                isolation: crate::language::Isolation::Serializable,
                span: Span::default(),
            }],
        });
        let mut context = PlanningContext::default();
        for id in [InstructionId(9), InstructionId(2)] {
            context.operation_facts.insert(
                id,
                OperationFacts {
                    source: FileClass::Regular,
                    destination: FileClass::Missing,
                },
            );
        }
        let graph = graph::build_with_context(&program, &context);
        let plan = SubmissionPlan::bind(
            SubmissionId(0),
            program,
            Origins::default(),
            context,
            graph,
            Bindings {
                nodes: [
                    (InstructionId(0), owner("scope")),
                    (InstructionId(9), owner("scope")),
                    (InstructionId(2), owner("foreign")),
                ]
                .into(),
                processes: BTreeMap::new(),
                external: Vec::new(),
            },
        )
        .unwrap();
        let mut ledger = Ledger::default();
        let child = plan.node_key(InstructionId(9)).unwrap();
        ledger
            .record(
                Event::Node(child, NodeEvent::ServiceReady),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger
                .gate(&plan, InstructionId(0), Phase::RegionCommit)
                .unwrap(),
            GateState::Pending(_)
        ));
        ledger
            .record(
                Event::Node(child, NodeEvent::LogicalComplete),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger
                .gate(&plan, InstructionId(0), Phase::RegionCommit)
                .unwrap(),
            GateState::Ready
        ));
        let wrapper = plan.node_key(InstructionId(0)).unwrap();
        ledger
            .record(
                Event::Node(wrapper, NodeEvent::LogicalComplete),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(2), Phase::Start).unwrap(),
            GateState::Pending(_)
        ));
        ledger
            .record(
                Event::Node(wrapper, NodeEvent::Committed),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(2), Phase::Start).unwrap(),
            GateState::Ready
        ));
    }

    #[test]
    fn retired_submission_drops_only_its_node_outcomes_not_global_receipts() {
        let mut ledger = Ledger::default();
        let old = NodeKey {
            submission: SubmissionId(0),
            instruction: InstructionId(0),
        };
        let current = NodeKey {
            submission: SubmissionId(1),
            instruction: InstructionId(0),
        };
        let plan = cohort_plan(2, "retained-native-view", "native-owner", 10);
        let cohort = CohortKey::from_proof(&plan.context().cohort_views[0]);
        let global = [
            Event::Process(ProcessId(10), ProcessEvent::Exited),
            Event::Cohort(cohort, Visibility::Committed),
        ];
        for event in [
            Event::Node(old, NodeEvent::LogicalComplete),
            Event::Node(old, NodeEvent::Committed),
            Event::Node(current, NodeEvent::LogicalComplete),
        ]
        .into_iter()
        .chain(global.clone())
        {
            ledger.record(event, Outcome::Succeeded).unwrap();
        }
        assert_eq!(ledger.retire_submission(old.submission), 2);
        assert!(
            ledger
                .outcome(&Event::Node(old, NodeEvent::LogicalComplete))
                .is_none()
        );
        assert!(matches!(
            ledger.outcome(&Event::Node(current, NodeEvent::LogicalComplete)),
            Some(Outcome::Succeeded)
        ));
        for event in global {
            assert!(matches!(ledger.outcome(&event), Some(Outcome::Succeeded)));
        }
        assert_eq!(ledger.retire_submission(old.submission), 0);
    }

    fn external_single(
        id: u64,
        instruction: Instruction,
        facts: OperationFacts,
        merge: Option<crate::hir::effects::NamespaceMergeProof>,
        external: Vec<ExternalWork>,
    ) -> Arc<SubmissionPlan> {
        let node = instruction.id;
        let mut context = PlanningContext::default();
        context.operation_facts.insert(node, facts);
        if let InstructionKind::Transfer {
            destination: Output::File { location, .. },
            ..
        } = &instruction.kind
            && let Some(parent) = std::path::Path::new(&location.path).parent()
        {
            context
                .existing_parent_frontiers
                .insert(node, local(&parent.to_string_lossy()));
        }
        if let Some(merge) = merge {
            context.namespace_merges.insert(node, merge);
        }
        let program = Arc::new(hir::Program {
            body: Region {
                instructions: vec![instruction],
            },
            transactions: Vec::new(),
        });
        let graph = graph::build_with_context(&program, &context);
        SubmissionPlan::bind(
            SubmissionId(id),
            program,
            Origins::default(),
            context,
            graph,
            Bindings {
                nodes: [(node, owner(&format!("owner-{id}")))].into(),
                processes: BTreeMap::new(),
                external,
            },
        )
        .unwrap()
    }

    fn tcp(port: u16) -> Input {
        Input::File {
            location: Location {
                backend: Backend::Tcp {
                    host: "127.0.0.1".into(),
                    port,
                },
                path: "/".into(),
            },
            pattern: None,
        }
    }

    fn namespace_proof(
        target: &str,
        order: u64,
        epoch: &str,
    ) -> crate::hir::effects::NamespaceMergeProof {
        use crate::hir::effects::{NamespaceMergeEpoch, NamespaceMergeProof};
        use crate::transaction::ResourceKey;
        let parent = ResourceKey::new("actual-native-index", "/native-parent").unwrap();
        let child =
            ResourceKey::new("actual-native-index", &format!("/native-parent/{target}")).unwrap();
        NamespaceMergeProof::insert_file(
            NamespaceMergeEpoch::verified("actual-native-manager", epoch, parent).unwrap(),
            local("/shared"),
            local(&format!("/shared/{target}")),
            child,
            order,
        )
        .unwrap()
    }

    fn external_work(plan: &Arc<SubmissionPlan>, phase: Phase) -> ExternalWork {
        let key = plan.node_key(InstructionId(0)).unwrap();
        ExternalWork::new(
            plan.clone(),
            key,
            plan.bindings.nodes[&key.instruction].clone(),
            plan.effects(key.instruction).unwrap().clone(),
            phase,
        )
        .unwrap()
    }

    #[test]
    fn late_submission_reads_wait_on_the_old_flow_not_its_reused_hir_zero() {
        let old = external_single(
            0,
            instruction(0, tcp(8000), file("/shared/result")),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
            None,
            Vec::new(),
        );
        let current = external_single(
            1,
            instruction(
                0,
                Input::File {
                    location: local("/shared/result"),
                    pattern: None,
                },
                file("/shared/copied"),
            ),
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Missing,
            },
            None,
            vec![external_work(&old, Phase::Start)],
        );
        let old_key = old.node_key(InstructionId(0)).unwrap();
        let current_key = current.node_key(InstructionId(0)).unwrap();
        assert_ne!(old_key, current_key);
        let mut ledger = Ledger::default();
        ledger
            .record(
                Event::Node(current_key, NodeEvent::Committed),
                Outcome::Succeeded,
            )
            .unwrap();
        ledger
            .record(
                Event::Node(old_key, NodeEvent::ServiceReady),
                Outcome::Succeeded,
            )
            .unwrap();
        ledger
            .record(
                Event::Node(old_key, NodeEvent::LogicalComplete),
                Outcome::Succeeded,
            )
            .unwrap();
        let GateState::Pending(pending) = ledger
            .gate(&current, InstructionId(0), Phase::Start)
            .unwrap()
        else {
            panic!("old owner is not committed")
        };
        assert!(pending.contains(&Event::Node(old_key, NodeEvent::Committed)));
        ledger
            .record(
                Event::Node(old_key, NodeEvent::Committed),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger
                .gate(&current, InstructionId(0), Phase::Start)
                .unwrap(),
            GateState::Ready
        ));
    }

    #[test]
    fn held_namespace_permits_commute_but_directory_predicates_and_epoch_changes_do_not() {
        let old = external_single(
            0,
            instruction(0, tcp(8000), file("/shared/a")),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
            Some(namespace_proof("a", 0, "same-epoch")),
            Vec::new(),
        );
        let current = external_single(
            1,
            instruction(0, tcp(8001), file("/shared/b")),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
            Some(namespace_proof("b", 1, "same-epoch")),
            vec![external_work(&old, Phase::Publish)],
        );
        let ledger = Ledger::default();
        assert!(
            old.effects(InstructionId(0))
                .unwrap()
                .conflicts(current.effects(InstructionId(0)).unwrap())
                .is_empty()
        );
        assert!(matches!(
            ledger
                .gate(&current, InstructionId(0), Phase::Start)
                .unwrap(),
            GateState::Ready
        ));

        let changed_epoch = external_single(
            2,
            instruction(0, tcp(8001), file("/shared/b")),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
            Some(namespace_proof("b", 2, "different-epoch")),
            vec![external_work(&old, Phase::Publish)],
        );
        assert!(matches!(
            ledger
                .gate(&changed_epoch, InstructionId(0), Phase::Start)
                .unwrap(),
            GateState::Pending(_)
        ));
        let predicate = external_single(
            3,
            instruction(
                0,
                Input::File {
                    location: local("/shared"),
                    pattern: Some(".*".into()),
                },
                file("/snapshot"),
            ),
            OperationFacts {
                source: FileClass::Directory,
                destination: FileClass::Missing,
            },
            None,
            vec![external_work(&old, Phase::Publish)],
        );
        assert!(matches!(
            ledger
                .gate(&predicate, InstructionId(0), Phase::Start)
                .unwrap(),
            GateState::Pending(_)
        ));
    }

    #[test]
    fn a_consuming_tcp_endpoint_is_still_blocked_before_a_new_payload_starts() {
        let old = external_single(
            0,
            instruction(0, tcp(8000), file("/shared/a")),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
            Some(namespace_proof("a", 0, "same-epoch")),
            Vec::new(),
        );
        let current = external_single(
            1,
            instruction(0, tcp(8000), file("/shared/b")),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
            Some(namespace_proof("b", 1, "same-epoch")),
            vec![external_work(&old, Phase::Start)],
        );
        assert!(
            old.effects(InstructionId(0))
                .unwrap()
                .conflicts(current.effects(InstructionId(0)).unwrap())
                .iter()
                .any(|conflict| conflict.kind == crate::hir::effects::Hazard::ConsumingRead)
        );
        assert!(matches!(
            Ledger::default()
                .gate(&current, InstructionId(0), Phase::Start)
                .unwrap(),
            GateState::Pending(_)
        ));
    }

    #[test]
    fn a_failed_old_flow_fails_late_dependents_with_the_original_typed_cause() {
        let old = external_single(
            0,
            instruction(0, tcp(8000), file("/shared/result")),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
            None,
            Vec::new(),
        );
        let current = external_single(
            1,
            instruction(
                0,
                Input::File {
                    location: local("/shared/result"),
                    pattern: None,
                },
                file("/shared/copied"),
            ),
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Missing,
            },
            None,
            vec![external_work(&old, Phase::Publish)],
        );
        let old_key = old.node_key(InstructionId(0)).unwrap();
        let failure = Arc::new(ExecutionError::new("old task failed", Span::default()));
        let mut ledger = Ledger::default();
        ledger.fail_node(old_key, failure.clone());
        let GateState::Failed { event, cause } = ledger
            .gate(&current, InstructionId(0), Phase::Start)
            .unwrap()
        else {
            panic!("a failed old producer cannot leave a late consumer pending")
        };
        assert_eq!(event, Event::Node(old_key, NodeEvent::Committed));
        assert!(Arc::ptr_eq(&cause, &failure));
        ledger.fail_node(current.node_key(InstructionId(0)).unwrap(), cause);
        assert!(matches!(
            ledger.outcome(&Event::Node(
                current.node_key(InstructionId(0)).unwrap(),
                NodeEvent::LogicalComplete
            )),
            Some(Outcome::Failed(_))
        ));
    }

    #[test]
    fn external_witnesses_retain_exact_producer_context_and_validate_identity() {
        let old = external_single(
            0,
            instruction(0, tcp(8000), file("/shared/result")),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
            None,
            Vec::new(),
        );
        let key = old.node_key(InstructionId(0)).unwrap();
        let effects = old.effects(InstructionId(0)).unwrap().clone();
        assert!(matches!(
            ExternalWork::new(
                old.clone(),
                NodeKey {
                    submission: SubmissionId(99),
                    ..key
                },
                owner("owner-0"),
                effects.clone(),
                Phase::Start
            ),
            Err(BindingError::ExternalNodeMismatch(_))
        ));
        assert!(matches!(
            ExternalWork::new(
                old.clone(),
                key,
                owner("spoofed-owner"),
                effects.clone(),
                Phase::Start
            ),
            Err(BindingError::ExternalOwnerMismatch(_))
        ));
        assert!(matches!(
            ExternalWork::new(
                old.clone(),
                key,
                owner("owner-0"),
                Effects::default(),
                Phase::Start
            ),
            Err(BindingError::ExternalEffectsMismatch(_))
        ));
        let unbound = SubmissionPlan::bind(
            old.id(),
            old.program.clone(),
            old.origins.clone(),
            old.context.clone(),
            old.graph.clone(),
            Bindings::default(),
        )
        .unwrap();
        assert!(matches!(
            ExternalWork::new(
                unbound,
                key,
                owner("owner-0"),
                effects.clone(),
                Phase::Start
            ),
            Err(BindingError::UnboundExternalOwner(_))
        ));
        let witness = external_work(&old, Phase::Publish);
        assert!(Arc::ptr_eq(witness.plan(), &old));
        assert_eq!(witness.key(), key);
        assert_eq!(witness.owner(), &owner("owner-0"));
        assert_eq!(witness.effects(), &effects);
        assert_eq!(witness.phase(), Phase::Publish);
        let retained = Arc::downgrade(&old);
        let current = external_single(
            1,
            instruction(
                0,
                Input::File {
                    location: local("/shared/result"),
                    pattern: None,
                },
                file("/shared/copied"),
            ),
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Missing,
            },
            None,
            vec![witness],
        );
        let failure = current
            .rebind(
                current.context().clone(),
                current.graph.clone(),
                Bindings {
                    external: vec![external_work(&old, Phase::Start)],
                    ..Bindings::default()
                },
            )
            .unwrap_err();
        assert_eq!(failure, BindingError::ExternalPhaseRegression(key));
        drop(old);
        assert!(
            retained.upgrade().is_some(),
            "dependent gate owns the producer's plan context"
        );
        drop(current);
        assert!(
            retained.upgrade().is_none(),
            "retired dependencies release their source/proof context"
        );
    }

    fn reset_plan(observed: Input) -> Arc<SubmissionPlan> {
        let transaction = crate::hir::TransactionId(4);
        let process = ProcessId(7);
        let body = Region {
            instructions: vec![
                instruction(1, Input::Literal("saved".into()), file("/virtual/saved")),
                Instruction {
                    id: InstructionId(2),
                    kind: InstructionKind::Commit { transaction },
                    span: Span::default(),
                    expansion: Vec::new(),
                },
                instruction(7, observed, file("/virtual/discarded")),
                Instruction {
                    id: InstructionId(3),
                    kind: InstructionKind::Spawn {
                        process,
                        executable: local("/fixture-program"),
                        arguments: Vec::new(),
                        cwd: std::env::current_dir().unwrap(),
                    },
                    span: Span::default(),
                    expansion: Vec::new(),
                },
                instruction(
                    4,
                    Input::Port {
                        process,
                        port: ReadPort::Stdout,
                    },
                    file("/virtual/pending"),
                ),
                Instruction {
                    id: InstructionId(5),
                    kind: InstructionKind::Rollback {
                        transaction,
                        scope: crate::hir::RollbackScope::Checkpoint,
                    },
                    span: Span::default(),
                    expansion: Vec::new(),
                },
                instruction(6, Input::Literal("after".into()), file("/virtual/after")),
            ],
        };
        let program = Arc::new(hir::Program {
            body: Region {
                instructions: vec![Instruction {
                    id: InstructionId(0),
                    kind: InstructionKind::Transaction { transaction, body },
                    span: Span::default(),
                    expansion: Vec::new(),
                }],
            },
            transactions: vec![crate::hir::TransactionInfo {
                id: transaction,
                name: "scope".into(),
                isolation: crate::language::Isolation::Serializable,
                span: Span::default(),
            }],
        });
        let mut context = PlanningContext::default();
        context.process_effects.insert(
            process,
            ProcessEffects::mediated(Effects::default(), "fixture-provider", "uncommitted-view")
                .unwrap(),
        );
        for id in [1, 7, 6] {
            context.operation_facts.insert(
                InstructionId(id),
                OperationFacts {
                    source: FileClass::Regular,
                    destination: FileClass::Missing,
                },
            );
        }
        context.operation_facts.insert(
            InstructionId(4),
            OperationFacts {
                source: FileClass::Stream,
                destination: FileClass::Missing,
            },
        );
        let graph = graph::build_with_context(&program, &context);
        let source =
            crate::source::Source::new("original-epoch.msync", "transaction scope\n    rollback\n");
        let origin = InstructionOrigin {
            site: source.at(Span::default()),
            definition: source.at(Span::default()),
            expansion: Vec::new(),
            operands: None,
        };
        SubmissionPlan::bind(
            SubmissionId(0),
            program,
            [(InstructionId(5), origin)].into(),
            context,
            graph,
            Bindings {
                nodes: [0, 1, 2, 7, 3, 4, 5, 6]
                    .into_iter()
                    .map(|id| (InstructionId(id), owner("same-transaction")))
                    .collect(),
                processes: [(process, owner("same-transaction"))].into(),
                external: Vec::new(),
            },
        )
        .unwrap()
    }

    fn reset_observations(ledger: &mut Ledger, plan: &SubmissionPlan) {
        for instruction in [1, 2, 7] {
            ledger
                .record(
                    Event::Node(
                        plan.node_key(InstructionId(instruction)).unwrap(),
                        NodeEvent::LogicalComplete,
                    ),
                    Outcome::Succeeded,
                )
                .unwrap();
        }
        for instruction in [1, 2] {
            ledger
                .record(
                    Event::Node(
                        plan.node_key(InstructionId(instruction)).unwrap(),
                        NodeEvent::Committed,
                    ),
                    Outcome::Succeeded,
                )
                .unwrap();
        }
        ledger
            .record(
                Event::Process(ProcessId(7), ProcessEvent::Started),
                Outcome::Succeeded,
            )
            .unwrap();
        ledger
            .record(
                Event::Node(
                    plan.node_key(InstructionId(4)).unwrap(),
                    NodeEvent::Admitted,
                ),
                Outcome::Succeeded,
            )
            .unwrap();
    }

    #[test]
    fn epoch_reset_cancels_long_spawn_and_pending_output_without_waiting_for_eof() {
        let plan = reset_plan(Input::Literal("discarded".into()));
        let child = plan.graph.nodes[0].children.as_ref().unwrap();
        assert_eq!(child.epoch_resets, plan.graph.epoch_resets);
        assert_eq!(plan.graph.epoch_resets[0].controller, InstructionId(5));
        let mut ledger = Ledger::default();
        assert!(matches!(
            ledger.cancel_epoch(plan.clone(), InstructionId(5)),
            Err(BindingError::ResetRequiresAdmission(_))
        ));
        reset_observations(&mut ledger, &plan);
        assert!(
            ledger
                .outcome(&Event::Process(ProcessId(7), ProcessEvent::Exited))
                .is_none()
        );
        assert!(
            ledger
                .outcome(&Event::Node(
                    plan.node_key(InstructionId(4)).unwrap(),
                    NodeEvent::ServiceReady
                ))
                .is_none()
        );
        let frame = ledger.cancel_epoch(plan.clone(), InstructionId(5)).unwrap();
        assert_eq!(frame.cut().checkpoint, Some(InstructionId(2)));
        assert!(
            frame.cut().cancel.contains(&InstructionId(3))
                && frame.cut().cancel.contains(&InstructionId(4))
        );
        let reset = frame.controller();
        for instruction in [3, 4] {
            let key = plan.node_key(InstructionId(instruction)).unwrap();
            assert!(
                matches!(ledger.outcome(&Event::Node(key, NodeEvent::LogicalComplete)), Some(Outcome::Cancelled { reason: CancelReason::Rollback(control), .. }) if *control == reset)
            );
            assert!(matches!(
                ledger
                    .gate(&plan, InstructionId(instruction), Phase::Publish)
                    .unwrap(),
                GateState::Cancelled { .. }
            ));
            assert!(matches!(
                ledger.record(Event::Node(key, NodeEvent::Committed), Outcome::Succeeded),
                Err(BindingError::AlreadyTerminal(_))
            ));
        }
        assert!(matches!(
            ledger
                .gate(&plan, InstructionId(0), Phase::RegionCommit)
                .unwrap(),
            GateState::Pending(_)
        ));
        assert!(matches!(
            ledger.gate(&plan, InstructionId(6), Phase::Start).unwrap(),
            GateState::Pending(_)
        ));
        ledger.finish_reset(frame).unwrap();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(6), Phase::Start).unwrap(),
            GateState::Ready
        ));
        ledger
            .record(
                Event::Node(
                    plan.node_key(InstructionId(6)).unwrap(),
                    NodeEvent::LogicalComplete,
                ),
                Outcome::Succeeded,
            )
            .unwrap();
        assert!(matches!(
            ledger
                .gate(&plan, InstructionId(0), Phase::RegionCommit)
                .unwrap(),
            GateState::Ready
        ));
    }

    #[test]
    fn reset_preserves_checkpoint_receipts_and_original_control_source() {
        let plan = reset_plan(Input::Literal("discarded".into()));
        let mut ledger = Ledger::default();
        reset_observations(&mut ledger, &plan);
        let frame = ledger.cancel_epoch(plan.clone(), InstructionId(5)).unwrap();
        assert!(Arc::ptr_eq(frame.plan(), &plan));
        let origin = frame.plan().origin(frame.controller().instruction).unwrap();
        assert_eq!(origin.site.source.name.as_ref(), "original-epoch.msync");
        assert!(!frame.cut().reverted.contains(&InstructionId(1)));
        assert!(!frame.cut().reverted.contains(&InstructionId(2)));
        for instruction in [1, 2] {
            let key = plan.node_key(InstructionId(instruction)).unwrap();
            assert!(matches!(
                ledger.outcome(&Event::Node(key, NodeEvent::Committed)),
                Some(Outcome::Succeeded)
            ));
            assert!(ledger.outcome(&Event::EpochDiscarded(key)).is_none());
        }
        ledger.finish_reset(frame).unwrap();
    }

    #[test]
    fn historical_http_observation_survives_discard_but_old_mutable_consumers_do_not() {
        let plan = reset_plan(Input::File {
            location: Location {
                backend: Backend::Http {
                    base: "https://fixture.invalid/".into(),
                },
                path: "counter".into(),
            },
            pattern: None,
        });
        let mut ledger = Ledger::default();
        reset_observations(&mut ledger, &plan);
        let key = plan.node_key(InstructionId(7)).unwrap();
        assert!(matches!(
            ledger.record(Event::ObservationComplete(key), Outcome::Succeeded),
            Err(BindingError::DerivedEvent(_))
        ));
        let base = external_single(
            1,
            instruction(
                0,
                Input::File {
                    location: local("/virtual/discarded"),
                    pattern: None,
                },
                file("/later"),
            ),
            OperationFacts {
                source: FileClass::Regular,
                destination: FileClass::Missing,
            },
            None,
            Vec::new(),
        );
        let witness = ExternalWork::new(
            plan.clone(),
            key,
            owner("same-transaction"),
            plan.effects(key.instruction).unwrap().clone(),
            Phase::RegionCommit,
        )
        .unwrap();
        let old_reader = SubmissionPlan::bind(
            base.id(),
            base.program.clone(),
            Origins::default(),
            base.context.clone(),
            base.graph.clone(),
            Bindings {
                nodes: [(InstructionId(0), owner("same-transaction"))].into(),
                external: vec![witness],
                ..Bindings::default()
            },
        )
        .unwrap();
        assert!(matches!(
            ledger
                .gate(&old_reader, InstructionId(0), Phase::Start)
                .unwrap(),
            GateState::Ready
        ));
        let frame = ledger.cancel_epoch(plan.clone(), InstructionId(5)).unwrap();
        assert!(matches!(
            ledger.outcome(&Event::Node(key, NodeEvent::LogicalComplete)),
            Some(Outcome::Succeeded)
        ));
        assert!(matches!(
            ledger.outcome(&Event::ObservationComplete(key)),
            Some(Outcome::Succeeded)
        ));
        assert!(matches!(
            ledger
                .gate(&old_reader, InstructionId(0), Phase::Start)
                .unwrap(),
            GateState::Cancelled {
                reason: CancelReason::Rollback(_),
                ..
            }
        ));
        assert!(
            matches!(
                ledger.gate(&plan, InstructionId(5), Phase::Start).unwrap(),
                GateState::Ready
            ),
            "only the typed historical observation bypasses visibility discard"
        );
        ledger.finish_reset(frame).unwrap();
        assert!(matches!(
            ledger.gate(&plan, InstructionId(6), Phase::Start).unwrap(),
            GateState::Ready
        ));
    }

    #[test]
    fn failed_checkpoint_restore_does_not_satisfy_region_commit() {
        let plan = reset_plan(Input::Literal("discarded".into()));
        let mut ledger = Ledger::default();
        reset_observations(&mut ledger, &plan);
        let frame = ledger.cancel_epoch(plan.clone(), InstructionId(5)).unwrap();
        let controller = frame.controller();
        let cause = Arc::new(ExecutionError::caused(
            "native restore failed",
            Span::default(),
            std::io::Error::from_raw_os_error(13),
        ));
        ledger.fail_reset(frame, cause.clone()).unwrap();
        let GateState::Failed {
            event,
            cause: original,
        } = ledger
            .gate(&plan, InstructionId(0), Phase::RegionCommit)
            .unwrap()
        else {
            panic!("failed restoration cannot finish the region")
        };
        assert_eq!(event, Event::EpochReset(controller));
        assert!(Arc::ptr_eq(&original, &cause));
    }

    #[test]
    fn administrative_stop_has_no_fake_controller_and_never_masks_real_failure() {
        let plan = fixture(SubmissionId(0), owner("owner-A"));
        let mut ledger = Ledger::default();
        let key = plan.node_key(InstructionId(0)).unwrap();
        assert!(ledger.cancel_node(key, CancelReason::Stopped, None));
        assert!(matches!(
            ledger.gate(&plan, InstructionId(1), Phase::Start).unwrap(),
            GateState::Cancelled {
                reason: CancelReason::Stopped,
                ..
            }
        ));
        assert!(ledger.outcome(&Event::EpochReset(key)).is_none());
        let failed = plan.node_key(InstructionId(1)).unwrap();
        let cause = Arc::new(ExecutionError::new("real IO failure", Span::default()));
        ledger.fail_node(failed, cause.clone());
        assert!(!ledger.cancel_node(failed, CancelReason::Stopped, None));
        assert!(
            matches!(ledger.outcome(&Event::Node(failed, NodeEvent::LogicalComplete)), Some(Outcome::Failed(original)) if Arc::ptr_eq(original, &cause))
        );
        ledger
            .record(
                Event::Process(ProcessId(8), ProcessEvent::Started),
                Outcome::Succeeded,
            )
            .unwrap();
        ledger.cancel_process(ProcessId(8), CancelReason::Stopped, None);
        assert!(matches!(
            ledger.outcome(&Event::Process(ProcessId(8), ProcessEvent::Started)),
            Some(Outcome::Succeeded)
        ));
        assert!(matches!(
            ledger.outcome(&Event::Process(ProcessId(8), ProcessEvent::Imported)),
            Some(Outcome::Cancelled {
                reason: CancelReason::Stopped,
                ..
            })
        ));
    }
}
