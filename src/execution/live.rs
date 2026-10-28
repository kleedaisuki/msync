//! Stateful execution of structured file, program and byte-flow regions.
//!
//! Native IO ownership stays with one session. Connected ports are extracted
//! before awaiting completion, so a wait can never hold the lock needed by its
//! own stdin producer. Checkpoints join real processes and drain every flow.

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsString;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Weak};

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::mpsc;
use tokio::task::JoinHandle;
use tokio_util::sync::CancellationToken;

use crate::endpoint::{Backend, Location};
use crate::hir::{
    self, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, ReadPort, Region,
    RollbackScope, TransactionId,
};
use crate::language::{DestinationMode, Isolation};
use crate::process::ProcessConfig;
use crate::syntax::Span;
use crate::transaction::filesystem::{Entry, Snapshot};
use crate::transaction::manager::{NamespaceMergePermit, NamespacePublicationOrder};

use super::domain::{self, Domain, Source, Transfer};
use super::endpoint;
use super::error::{ExecutionError, Result};
use super::events::{
    self, Bindings, CancelReason, CohortKey, Event, ExternalWork, GateState, Ledger, NodeEvent,
    NodeKey, Outcome, OwnerKey, Phase, ProcessEvent, PublicationOrder, SubmissionPlan, Visibility,
};
use super::physical;
use super::program::{self, Program, Reader, Writer};
use super::scheduler::{Admission, Availability, Demand, Lease};
use super::{Engine, ExecutionReport, Options};

#[derive(Clone)]
struct WorkNode {
    plan: Arc<SubmissionPlan>,
    key: NodeKey,
    order: PublicationOrder,
    transaction: TransactionId,
    physical: Option<physical::Spec>,
}

struct NamespaceSlot {
    permit: NamespaceMergePermit,
    target: PathBuf,
}

#[derive(Clone, Debug)]
enum Terminal {
    Flow(NodeKey),
    Process(ProcessId),
}

struct TerminalNotice {
    sender: mpsc::Sender<Terminal>,
    event: Terminal,
    ready: Arc<AtomicBool>,
}

#[derive(Debug)]
struct SharedFailure(Arc<ExecutionError>);

impl std::fmt::Display for SharedFailure {
    fn fmt(&self, output: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        self.0.fmt(output)
    }
}

impl std::error::Error for SharedFailure {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(self.0.as_ref())
    }
}

impl Drop for TerminalNotice {
    fn drop(&mut self) {
        // Wakeups may coalesce; owned handles and the persistent ledger remain
        // the truth of success/failure even when the bounded channel is full.
        self.ready.store(true, Ordering::Release);
        let _ = self.sender.try_send(self.event.clone());
    }
}

struct FlowResult {
    work: WorkNode,
    origin: Option<crate::source::InstructionOrigin>,
    instruction: InstructionId,
    transaction: TransactionId,
    span: Span,
    output: Option<(PathBuf, PathBuf, DestinationMode)>,
    basename: Option<OsString>,
}

struct Flow {
    work: WorkNode,
    ready: Arc<AtomicBool>,
    origin: Option<crate::source::InstructionOrigin>,
    transaction: TransactionId,
    handle: JoinHandle<Result<FlowResult>>,
    managed: Option<PathBuf>,
}

struct Deferred {
    work: WorkNode,
    origin: Option<crate::source::InstructionOrigin>,
    instruction: InstructionId,
    transaction: TransactionId,
    source: Snapshot,
    target: PathBuf,
    mode: DestinationMode,
    span: Span,
    basename: Option<OsString>,
}

struct PinnedInput {
    proof: crate::hir::graph::VersionReadProof,
    _snapshot: Snapshot,
}

/// One lexical runtime counterpart. Finished named origins survive submissions;
/// `shutdown` joins, rolls back and cleans private resources. Dropping only
/// requests cancellation; unconfirmed private journals are deliberately retained.
pub struct RuntimeSession {
    origins: crate::source::Origins,
    pending_origins: crate::source::Origins,
    active_origin: Option<crate::source::InstructionOrigin>,
    options: Options,
    worker: Option<PathBuf>,
    engine: Option<Engine>,
    programs: BTreeMap<ProcessId, Program>,
    flows: Vec<Flow>,
    deferred: Vec<Deferred>,
    pins: BTreeMap<InstructionId, PinnedInput>,
    latest_processes: BTreeSet<ProcessId>,
    ledger: Ledger,
    progress: u64,
    plan: Option<Arc<SubmissionPlan>>,
    retained_plans: BTreeMap<events::SubmissionId, Vec<Weak<SubmissionPlan>>>,
    active_work: Option<WorkNode>,
    candidate: Option<program::SharedView>,
    orders: BTreeMap<InstructionId, PublicationOrder>,
    node_transactions: BTreeMap<NodeKey, TransactionId>,
    process_nodes: BTreeMap<ProcessId, NodeKey>,
    process_cohorts: BTreeMap<ProcessId, CohortKey>,
    pending_nodes: BTreeMap<TransactionId, BTreeSet<NodeKey>>,
    owner_tokens: BTreeMap<TransactionId, CancellationToken>,
    terminal_tx: mpsc::Sender<Terminal>,
    terminal_rx: mpsc::Receiver<Terminal>,
    prepared_readers: BTreeMap<NodeKey, Reader>,
    namespaces: BTreeMap<NodeKey, NamespaceSlot>,
    closed_owners: BTreeSet<TransactionId>,
    complete_submission: bool,
    pending_planning: Option<crate::hir::graph::PlanningContext>,
    planning: crate::hir::graph::PlanningContext,
    native_fifo_pins: BTreeMap<hir::effects::EndpointKey, crate::streams::FifoIdentity>,
    flow_reservations: BTreeMap<InstructionId, Lease>,
    rejected_before_start: bool,
    sparse_roots: BTreeSet<PathBuf>,
    admission: Admission,
    next_instruction: usize,
    implicit: BTreeSet<TransactionId>,
    cancel: CancellationToken,
}

impl Drop for RuntimeSession {
    fn drop(&mut self) {
        // Keep owning tasks alive long enough to close endpoint-specific peers.
        // Aborting their JoinHandles would bypass that asynchronous cleanup.
        self.cancel.cancel();
    }
}

impl RuntimeSession {
    pub fn new(options: &Options) -> Result<Self> {
        options
            .limits
            .validate()
            .map_err(|message| ExecutionError::new(message, Span::default()))?;
        if !options.scratch_root.is_absolute() {
            return Err(ExecutionError::new(
                "execution scratch root must be absolute",
                Span::default(),
            ));
        }
        let capacity = options
            .limits
            .max_streams
            .saturating_add(options.limits.jobs)
            .clamp(1, 1024);
        let (terminal_tx, terminal_rx) = mpsc::channel(capacity);
        Ok(Self {
            origins: crate::source::Origins::default(),
            pending_origins: crate::source::Origins::default(),
            active_origin: None,
            options: options.clone(),
            worker: None,
            engine: None,
            programs: BTreeMap::new(),
            flows: Vec::new(),
            deferred: Vec::new(),
            pins: BTreeMap::new(),
            latest_processes: BTreeSet::new(),
            ledger: Ledger::default(),
            progress: 0,
            plan: None,
            retained_plans: BTreeMap::new(),
            active_work: None,
            candidate: None,
            orders: BTreeMap::new(),
            node_transactions: BTreeMap::new(),
            process_nodes: BTreeMap::new(),
            process_cohorts: BTreeMap::new(),
            pending_nodes: BTreeMap::new(),
            owner_tokens: BTreeMap::new(),
            terminal_tx,
            terminal_rx,
            prepared_readers: BTreeMap::new(),
            namespaces: BTreeMap::new(),
            closed_owners: BTreeSet::new(),
            complete_submission: false,
            pending_planning: None,
            planning: crate::hir::graph::PlanningContext::default(),
            native_fifo_pins: BTreeMap::new(),
            flow_reservations: BTreeMap::new(),
            rejected_before_start: false,
            sparse_roots: BTreeSet::new(),
            admission: Admission::new(&options.limits)?,
            next_instruction: 0,
            implicit: BTreeSet::new(),
            cancel: options.cancel.child_token(),
        })
    }

    /// Register source ownership for the next submission. Existing jobs retain
    /// their captured origins even when instruction IDs are reused later.
    pub fn set_origins(&mut self, origins: crate::source::Origins) {
        self.pending_origins = origins;
    }

    pub fn set_worker(&mut self, path: impl AsRef<Path>) -> Result<()> {
        if !path.as_ref().is_absolute() {
            return Err(ExecutionError::new(
                "the internal program worker must be an absolute executable path",
                Span::default(),
            ));
        }
        self.worker = Some(path.as_ref().to_owned());
        Ok(())
    }

    /// Clone this before moving the session into a background submission. A
    /// completed submission installs a fresh child token, not a poisoned session.
    pub fn cancellation(&self) -> CancellationToken {
        self.cancel.clone()
    }

    pub fn live_processes(&self) -> Vec<ProcessId> {
        self.programs
            .values()
            .filter(|program| program.is_running())
            .map(|program| program.id)
            .collect()
    }

    pub fn has_work(&self) -> bool {
        !self.live_processes().is_empty()
            || !self.flows.is_empty()
            || !self.deferred.is_empty()
            || !self.implicit.is_empty()
    }

    /// Pending publication cohorts, shared by connected submissions. These are
    /// runtime ownership identities, not a count of submitted source statements.
    pub fn work_ids(&self) -> BTreeSet<TransactionId> {
        self.implicit.clone()
    }

    /// Drain completed session progress without attributing it to a later input.
    pub fn take_report(&mut self) -> ExecutionReport {
        self.engine
            .as_mut()
            .map(|engine| std::mem::take(&mut engine.report))
            .unwrap_or_default()
    }

    /// Publish only already-finished physical cohorts without waiting for a live
    /// producer. Interactive callers can then refresh lifecycle/status displays.
    pub async fn poll_ready(&mut self) -> Result<()> {
        self.reap_physical().await
    }

    pub fn has_ready_work(&self) -> bool {
        !self.terminal_rx.is_empty()
            || self.programs.values().any(|program| {
                program.exit_signal.is_cancelled()
                    && self
                        .ledger
                        .outcome(&Event::Process(program.id, ProcessEvent::Exited))
                        .is_none()
            })
            || self.implicit.iter().any(|id| self.is_ready(*id))
    }

    fn is_ready(&self, transaction: TransactionId) -> bool {
        self.flows
            .iter()
            .filter(|flow| flow.transaction == transaction)
            .all(|flow| flow.ready.load(Ordering::Acquire) || flow.handle.is_finished())
            && self
                .programs
                .values()
                .filter(|program| program.transaction == transaction && program.is_running())
                .all(|program| program.exit_signal.is_cancelled())
    }

    /// Only resource-view providers produce these premises; names/argv alone
    /// never turn an opaque child into a pure operation.
    pub fn planning_context(&self) -> crate::hir::graph::PlanningContext {
        let mut context = crate::hir::graph::PlanningContext {
            live_processes: self.live_processes(),
            process_effects: self
                .programs
                .values()
                .filter(|program| program.is_running())
                .map(|program| (program.id, program.effects.clone()))
                .collect(),
            ..crate::hir::graph::PlanningContext::default()
        };
        context.pending_effects = self
            .outstanding_work()
            .filter_map(|work| work.plan.effects(work.key.instruction).ok().cloned())
            .collect();
        self.attach_cohorts(&mut context);
        context
    }

    /// These proofs describe the latest executing submission only. Their
    /// retained immutable leases must not be reused for a later input's IDs.
    pub fn refinement_context(&self) -> crate::hir::graph::PlanningContext {
        let mut context = self.planning_context();
        context.host_unstable = self.planning.host_unstable.clone();
        context.physical_rendezvous = self.planning.physical_rendezvous.clone();
        context.physical_fifos = self.planning.physical_fifos.clone();
        let latest_cohorts = self
            .latest_processes
            .iter()
            .filter_map(|id| self.programs.get(id))
            .map(|program| program.cohort)
            .collect::<BTreeSet<_>>();
        for program in self
            .programs
            .values()
            .filter(|program| latest_cohorts.contains(&program.cohort))
        {
            context
                .process_effects
                .insert(program.id, program.effects.clone());
        }
        context.pinned_inputs = self
            .pins
            .iter()
            .map(|(id, pinned)| (*id, pinned.proof.clone()))
            .collect();
        self.attach_cohorts(&mut context);
        context
    }

    fn attach_cohorts(&self, context: &mut crate::hir::graph::PlanningContext) {
        let mut groups =
            BTreeMap::<ProcessId, Vec<(ProcessId, crate::hir::graph::ProcessEffects)>>::new();
        for (id, effects) in &context.process_effects {
            let cohort = self.programs[id].cohort;
            groups
                .entry(cohort)
                .or_default()
                .push((*id, effects.clone()));
        }
        context.cohort_views.clear();
        for (cohort, members) in groups.into_iter().filter(|(_, members)| members.len() > 1) {
            let program = &self.programs[&cohort];
            let engine = self
                .engine
                .as_ref()
                .expect("live program owns its runtime engine");
            let proof = crate::hir::graph::CohortViewProof::shared(
                format!(
                    "{}:txn-{}",
                    engine.workspace.display(),
                    program.transaction.0
                ),
                format!("{}:cohort-{}", program.effects.view_id(), cohort.0),
                members,
            )
            .expect("native shared-view members retain one actual provider and view identity");
            context.cohort_views.push(proof);
        }
    }

    pub async fn execute_prepared(
        &mut self,
        program: &crate::preparation::PreparedProgram,
    ) -> Result<ExecutionReport> {
        verify_prepared(program)?;
        self.pending_planning = Some(program.context.clone());
        self.execute(&program.program).await
    }

    pub async fn submit_prepared(
        &mut self,
        program: &crate::preparation::PreparedProgram,
    ) -> Result<ExecutionReport> {
        verify_prepared(program)?;
        self.pending_planning = Some(program.context.clone());
        self.submit(&program.program).await
    }

    /// Execute a complete document and join its implicit program cohorts. REPL
    /// incremental declarations use `submit` and explicit `wait` instead.
    pub async fn execute(&mut self, program: &hir::Program) -> Result<ExecutionReport> {
        self.submit_inner(program, true).await
    }

    /// Keep an unconnected program and its ports alive for subsequent inputs.
    /// Named transaction regions still have an implicit commit at their end.
    pub async fn submit(&mut self, program: &hir::Program) -> Result<ExecutionReport> {
        self.submit_inner(program, false).await
    }

    async fn submit_inner(
        &mut self,
        program: &hir::Program,
        complete: bool,
    ) -> Result<ExecutionReport> {
        self.reap_physical().await?;
        self.origins = std::mem::take(&mut self.pending_origins);
        self.active_origin = None;
        let supplied_plan = self.pending_planning.take();
        self.planning = supplied_plan.clone().unwrap_or_default();
        self.rejected_before_start = false;
        let prior_work = self.implicit.clone();
        let prior_flows = self.flows.len();
        if program.body.instructions.is_empty() {
            return Ok(ExecutionReport::default());
        }
        self.preflight_resources(program)?;
        if !self.has_work()
            && let Some(engine) = &mut self.engine
            && !engine.manager.has_active_transactions()
        {
            engine.manager.refresh_host().map_err(|cause| {
                ExecutionError::caused("cannot refresh externally modified resources", Span::default(), cause)
                    .help("Resolve foreign edits or pending cleanup before reusing the retained transaction history.")
            })?;
        }
        self.pins.clear();
        self.latest_processes.clear();
        if supplied_plan.is_none() {
            self.planning =
                crate::preparation::prepare_with_context(program, true, self.planning_context())
                    .map_err(|reports| {
                        let mut reports = reports.into_iter();
                        let report = reports
                            .next()
                            .expect("preparation failures contain a diagnostic");
                        let mut error = ExecutionError::new(
                            report.diagnostic.message.clone(),
                            report.diagnostic.span,
                        );
                        error.report = Box::new(report);
                        error
                            .report
                            .help
                            .extend(reports.map(|report| report.diagnostic.message));
                        error
                    })?
                    .context;
        }
        self.preflight(program)?;
        let rendezvous = physical::rendezvous(program, &self.planning)?;
        self.planning.physical_rendezvous = rendezvous.proofs;
        self.planning.physical_fifos = rendezvous.observations;
        self.native_fifo_pins = rendezvous.pins;
        if !complete {
            self.preflight_fifo_input(program)?;
        }
        self.preflight_fifo_resources(program)?;
        self.preflight_flow_resources(&program.body)?;
        let domain = live_domain(program, &self.options, &self.planning)?;
        self.enroll_physical_namespaces(program)?;
        self.enroll(&domain)?;
        self.planning.namespace_merges.clear();
        let id = self.ledger.next_submission().map_err(Self::binding_error)?;
        self.orders = self
            .ledger
            .publication_orders(program)
            .map_err(Self::binding_error)?;
        self.complete_submission = complete;
        self.active_work = None;
        let mut context = self.refinement_context();
        context.operation_facts = self.planning.operation_facts.clone();
        context.existing_parent_frontiers = self.planning.existing_parent_frontiers.clone();
        context.host_unstable = self.planning.host_unstable.clone();
        context.namespace_merges.clear();
        let graph = crate::hir::graph::build_with_context(program, &context);
        let mut bindings = Bindings {
            external: self.external_work(id)?,
            ..Bindings::default()
        };
        for (process, program) in &self.programs {
            bindings
                .processes
                .insert(*process, self.owner_key(program.transaction)?);
        }
        self.plan = Some(
            SubmissionPlan::bind(
                id,
                Arc::new(program.clone()),
                self.origins.clone(),
                context,
                graph,
                bindings,
            )
            .map_err(Self::binding_error)?,
        );
        self.remember_plan(self.plan.as_ref().expect("submission bound").clone());
        self.collect_plans();
        let result = self.region(&program.body, None, program).await;
        self.flow_reservations.clear();
        let result = match result {
            Ok(()) if complete => self.wait().await,
            other => other,
        };
        if let Err(mut failure) = result {
            if self.rejected_before_start
                && self.latest_processes.is_empty()
                && self.flows.len() == prior_flows
            {
                let pending = self
                    .implicit
                    .difference(&prior_work)
                    .copied()
                    .collect::<Vec<_>>();
                for id in pending {
                    self.engine_mut()?.abort(id, &mut failure);
                    self.implicit.remove(&id);
                }
                return Err(failure);
            }
            let mut affected = self
                .implicit
                .difference(&prior_work)
                .copied()
                .collect::<BTreeSet<_>>();
            if let Some(id) = failure.transaction {
                affected.insert(id);
            }
            if let Some(work) = &self.active_work {
                affected.insert(work.transaction);
            }
            for process in &self.latest_processes {
                affected.insert(self.programs[process].transaction);
            }
            if self.cancel.is_cancelled() {
                affected.extend(self.implicit.iter().copied());
            }
            for id in affected {
                if !self.implicit.contains(&id) {
                    continue;
                }
                let namespace = self
                    .namespaces
                    .keys()
                    .any(|key| self.node_transactions.get(key) == Some(&id));
                self.cancel_transaction(id, &mut failure).await;
                if !namespace && !self.closed_owners.remove(&id) {
                    self.engine_mut()?.abort(id, &mut failure);
                }
                self.implicit.remove(&id);
            }
            let submission = self.plan.as_ref().map(|plan| plan.id());
            let unstarted = self
                .namespaces
                .keys()
                .filter(|key| Some(key.submission) == submission)
                .copied()
                .collect::<Vec<_>>();
            for key in unstarted {
                if let Err(error) = self.release_namespace(key) {
                    failure.report.help.push(error.to_string());
                }
            }
            self.prepared_readers
                .retain(|key, _| Some(key.submission) != submission);
            if !self.has_work() {
                self.cancel = self.options.cancel.child_token();
            }
            return Err(failure);
        }
        if self.live_processes().is_empty() && self.flows.is_empty() && self.implicit.is_empty() {
            self.cancel = self.options.cancel.child_token();
        }
        Ok(std::mem::take(&mut self.engine_mut()?.report))
    }

    pub async fn wait(&mut self) -> Result<()> {
        for program in self.programs.values_mut() {
            program.close_unused_stdin();
        }
        while !self.implicit.is_empty() {
            self.reap_physical().await?;
            if self.implicit.is_empty() {
                break;
            }
            self.wait_event().await?;
        }
        Ok(())
    }

    pub async fn cancel_active(&mut self) -> Result<()> {
        self.cancel.cancel();
        let mut failure =
            ExecutionError::new("active program execution was cancelled", Span::default());
        let ids = self.implicit.iter().copied().collect::<Vec<_>>();
        let mut joined = true;
        for id in ids {
            let reserved = self
                .namespaces
                .keys()
                .any(|key| self.node_transactions.get(key) == Some(&id));
            joined &= self.cancel_transaction(id, &mut failure).await;
            if !reserved && !self.closed_owners.remove(&id) {
                self.engine_mut()?.abort(id, &mut failure);
            }
            self.implicit.remove(&id);
        }
        self.owner_tokens.clear();
        self.prepared_readers.clear();
        self.flow_reservations.clear();
        self.cancel = self.options.cancel.child_token();
        if !joined
            || failure.cause.is_some()
            || !failure.recovery.is_empty()
            || !failure.compensation.is_empty()
        {
            return Err(failure);
        }
        Ok(())
    }

    pub async fn shutdown(&mut self) -> Result<()> {
        self.cancel_active().await?;
        self.programs.clear();
        self.pins.clear();
        self.native_fifo_pins.clear();
        if let Some(mut engine) = self.engine.take() {
            engine.close();
            if let Some(issue) = engine
                .report
                .cleanup
                .into_iter()
                .find(|issue| !issue.resolved)
            {
                let mut failure = ExecutionError::new(
                    "runtime session cleanup remains incomplete",
                    Span::default(),
                );
                failure.cause = Some(issue.cause);
                failure.retained_paths = issue.retained_paths;
                return Err(failure);
            }
        }
        Ok(())
    }

    fn engine_mut(&mut self) -> Result<&mut Engine> {
        self.engine.as_mut().ok_or_else(|| {
            ExecutionError::new(
                "runtime resource domain is not initialized",
                Span::default(),
            )
        })
    }

    fn preflight_resources(&self, program: &hir::Program) -> Result<()> {
        fn needs_program(region: &Region) -> bool {
            region.instructions.iter().any(|node| match &node.kind {
                InstructionKind::Spawn { .. } => true,
                InstructionKind::Transaction { body, .. } => needs_program(body),
                _ => false,
            })
        }
        if needs_program(&program.body)
            && self.options.limits.memory_bytes / self.options.limits.buffer_bytes < 6
        {
            return Err(ExecutionError::new("the payload budget cannot admit one program space", Span::default())
                .help("One program reserves six configured buffers; increase --memory-limit or reduce --buffer-size before execution."));
        }
        Ok(())
    }

    fn preflight_fifo_resources(&self, program: &hir::Program) -> Result<()> {
        for group in hir::graph::physical_groups(&self.planning.physical_rendezvous) {
            let mut processes = BTreeSet::new();
            fifo_consumers(&program.body, &group, &mut processes);
            let processes = processes
                .iter()
                .filter(|process| self.programs.get(process).is_none_or(Program::is_running))
                .count();
            let demand = Demand::flows(group.len()).with_programs(processes);
            if self.admission.availability(demand) != Availability::Impossible {
                continue;
            }
            let span = program
                .body
                .instructions
                .iter()
                .find(|node| group.contains(&node.id))
                .map_or(Span::default(), |node| node.span);
            return Err(ExecutionError::new(format!("this FIFO cohort must co-start {} flows with two buffers each and retain {processes} native program spaces", group.len()), span)
                .help("No payloads were captured and no flow started. Increase --jobs/--max-streams/--memory-limit, reduce --buffer-size, or separate independent channels."));
        }
        Ok(())
    }

    fn preflight_fifo_input(&self, program: &hir::Program) -> Result<()> {
        for (key, endpoint) in &self.planning.physical_fifos {
            let resource = hir::effects::Resource::StreamIdentity {
                provider: endpoint.provider().into(),
                identity: endpoint.identity().into(),
            };
            let busy = self.outstanding_work().any(|work| {
                work.plan
                    .effects(work.key.instruction)
                    .is_ok_and(|effects| {
                        effects
                            .accesses
                            .iter()
                            .any(|access| access.resource.may_overlap(&resource))
                    })
            });
            if !busy {
                continue;
            }
            let span = program
                .body
                .instructions
                .iter()
                .find(|node| node.id == key.instruction)
                .map_or(Span::default(), |node| node.span);
            return Err(ExecutionError::new("this FIFO is still owned by an earlier input; no new endpoint was opened", span)
                .help("If the FIFO peer is another msync statement, /stop the pending half and submit both sides together with /load or one macro call. /wait alone cannot create its missing peer. Otherwise connect the external FIFO reader/writer and wait for the previous flow to finish.")
                .with_operand(self.origins.get(&key.instruction), key.role == hir::effects::EndpointRole::Destination));
        }
        Ok(())
    }

    fn preflight_flow_resources(&self, region: &Region) -> Result<()> {
        for node in &region.instructions {
            if let InstructionKind::Transaction { body, .. } = &node.kind {
                self.preflight_flow_resources(body)?;
            }
            let facts = self.planning.operation_facts.get(&node.id);
            let stream = facts.is_some_and(|facts| {
                facts.source == crate::plan::FileClass::Stream
                    || facts.destination == crate::plan::FileClass::Stream
            });
            if !stream && !port_flow(node) {
                continue;
            }
            let programs = usize::from(matches!(
                node.kind,
                InstructionKind::Transfer {
                    destination: Output::Stdin(_),
                    ..
                }
            ));
            self.admission
                .feasible(Demand::flows(1).with_programs(programs), node.span)?;
        }
        Ok(())
    }

    fn flow_lease(&mut self, instruction: InstructionId, span: Span) -> Result<Lease> {
        if let Some(lease) = self.flow_reservations.remove(&instruction) {
            return Ok(lease);
        }
        self.reserve_flows(instruction, span)?;
        Ok(self
            .flow_reservations
            .remove(&instruction)
            .expect("reservation contains its admitted member"))
    }

    fn flow_group(&self, instruction: InstructionId) -> Vec<InstructionId> {
        hir::graph::physical_groups(&self.planning.physical_rendezvous)
            .into_iter()
            .find(|group| group.contains(&instruction))
            .unwrap_or_else(|| vec![instruction])
    }

    fn reserve_flows(
        &mut self,
        instruction: InstructionId,
        span: Span,
    ) -> Result<Vec<InstructionId>> {
        if self.flow_reservations.contains_key(&instruction) {
            return Ok(Vec::new());
        }
        let group = self.flow_group(instruction);
        // Reserve the entire connected cohort before its first endpoint can
        // open. Failure drops partial reservations, never half-starts a pipe.
        let leases = group
            .iter()
            .copied()
            .map(|member| self.admission.flow(span).map(|lease| (member, lease)))
            .collect::<Result<BTreeMap<_, _>>>()?;
        self.flow_reservations.extend(leases);
        Ok(group)
    }

    fn demand(&self, node: &Instruction, work: &WorkNode) -> Demand {
        if matches!(node.kind, InstructionKind::Spawn { .. }) {
            return Demand::PROGRAM;
        }
        if self.flow_reservations.contains_key(&node.id) {
            return Demand::default();
        }
        let flow = work.physical.is_some() || port_flow(node);
        if flow {
            Demand::flows(self.flow_group(node.id).len())
        } else {
            Demand::default()
        }
    }

    fn owner_capacity_blocked(&self, node: &Instruction, work: &WorkNode, demand: Demand) -> bool {
        if !matches!(
            node.kind,
            InstructionKind::Transfer {
                destination: Output::Stdin(_),
                ..
            }
        ) {
            return false;
        }
        let programs = self
            .programs
            .values()
            .filter(|program| program.transaction == work.transaction && program.is_running())
            .count();
        let streams = self
            .flows
            .iter()
            .filter(|flow| {
                flow.transaction == work.transaction
                    && !flow.ready.load(Ordering::Acquire)
                    && !flow.handle.is_finished()
            })
            .count();
        let retained =
            Demand::flows(streams.saturating_add(demand.streams)).with_programs(programs);
        self.admission.availability(retained) == Availability::Impossible
    }

    fn preflight(&self, program: &hir::Program) -> Result<()> {
        fn walk(
            session: &RuntimeSession,
            region: &Region,
            processes: &mut bool,
            transactional: bool,
        ) -> Result<()> {
            for node in &region.instructions {
                if let InstructionKind::Transfer {
                    source,
                    destination,
                } = &node.kind
                {
                    let unstable = transfer_unstable(&session.planning, node.id);
                    let physical = unstable
                        && known_physical(
                            source,
                            destination,
                            session.planning.operation_facts.get(&node.id),
                        )
                        || !unstable
                            && (physical::inspect(source, destination, node.span)?.is_some()
                                || match (source, destination) {
                                    (Input::File { location, .. }, Output::Stdin(_)) => {
                                        physical::source_stream(location, node.span)?.is_some()
                                    }
                                    _ => false,
                                });
                    if physical {
                        if transactional {
                            return Err(ExecutionError::new("a named transaction requires a transactional peer for external pipe/socket IO", node.span)
                                .help("No effects started. Keep external streams at the top level, or use a peer supporting prepare/commit/abort."));
                        }
                        continue;
                    }
                    if endpoint::inspect(source, destination, node.span)?.is_some() {
                        if transactional || matches!(source, Input::Port { .. }) {
                            return Err(ExecutionError::new("remote publication needs a versioned participant inside a named transaction or program output cohort", node.span)
                                .help("No effects started. Finite file/literal transfers are supported at the top level; remote program cohorts require a prepare/commit/abort participant."));
                        }
                        if !unstable {
                            session.guard_finite(source, destination, node.span)?;
                        }
                        continue;
                    }
                    if !transactional
                        && !unstable
                        && matches!(destination, Output::File { .. })
                        && !matches!(source, Input::Port { .. })
                    {
                        session.guard_finite(source, destination, node.span)?;
                    }
                }
                match &node.kind {
                    InstructionKind::Spawn {
                        executable,
                        arguments,
                        ..
                    } => {
                        *processes = true;
                        if !matches!(executable.backend, Backend::Local | Backend::Path | Backend::Env)
                            || arguments.iter().any(|argument| matches!(argument, hir::Argument::Path(path) if !matches!(path.backend, Backend::Local | Backend::Path | Backend::Env))) {
                            return Err(ExecutionError::new("this program needs a remote process-resource provider", node.span));
                        }
                    }
                    InstructionKind::Transfer {
                        source: Input::File { location, .. },
                        ..
                    } if matches!(location.backend, Backend::Tcp { .. }) => {
                        return Err(ExecutionError::new(
                            "physical sockets require the physical stream preparation adapter",
                            node.span,
                        ));
                    }
                    InstructionKind::Transfer {
                        destination: Output::File { location, .. },
                        ..
                    } if !matches!(location.backend, Backend::Local | Backend::Env) => {
                        return Err(ExecutionError::new(
                            "this destination needs a transactional endpoint publication provider",
                            node.span,
                        ));
                    }
                    InstructionKind::Transaction { body, .. } => {
                        walk(session, body, processes, true)?
                    }
                    _ => {}
                }
            }
            Ok(())
        }
        let mut processes = false;
        walk(self, &program.body, &mut processes, false)?;
        if processes {
            let worker = self.worker.as_ref().ok_or_else(|| {
                ExecutionError::new(
                    "configure the msync internal worker before launching a program",
                    Span::default(),
                )
            })?;
            #[cfg(target_os = "linux")]
            let capability = crate::process::view_linux::Provider::probe()
                .and_then(|provider| provider.with_worker(worker));
            #[cfg(windows)]
            let capability = crate::process::view_windows::Provider::probe()
                .and_then(|provider| provider.with_worker(worker));
            capability.map_err(|cause| {
                ExecutionError::caused(
                    "program resource-view capability is unavailable; no effects started",
                    Span::default(),
                    cause,
                )
            })?;
        }
        Ok(())
    }

    fn guard_finite(&self, source: &Input, destination: &Output, span: Span) -> Result<()> {
        let mut paths = Vec::new();
        if let Input::File { location, .. } = source
            && matches!(
                location.backend,
                Backend::Local | Backend::Env | Backend::Path
            )
        {
            paths.push(local(location, true, span)?);
        }
        if let Output::File { location, .. } = destination
            && matches!(location.backend, Backend::Local | Backend::Env)
        {
            paths.push(local(location, false, span)?);
        }
        let overlap = |other: &Path| {
            paths.iter().any(|path| {
                domain::relative_to(path, other).is_some()
                    || domain::relative_to(other, path).is_some()
            })
        };
        let conflict = self
            .flows
            .iter()
            .filter_map(|flow| flow.managed.as_deref())
            .any(overlap)
            || self
                .deferred
                .iter()
                .any(|assignment| overlap(&assignment.target))
            || self
                .programs
                .values()
                .filter(|program| program.is_running())
                .any(|program| program.baseline.iter().any(|(root, _)| overlap(root)));
        if conflict {
            return Err(ExecutionError::new("finite assignment overlaps an active resource cohort; no effects started", span)
                .help("Finish the conflicting job with /wait, or /stop it before reading or replacing its managed resources. Independent paths can continue without waiting."));
        }
        Ok(())
    }

    fn enroll(&mut self, domain: &Domain) -> Result<()> {
        self.enroll_at(domain, |root| domain.site(root))
    }

    fn enroll_at(&mut self, domain: &Domain, site: impl Fn(&Path) -> Span) -> Result<()> {
        if self.engine.is_none() {
            self.sparse_roots.clear();
            if domain.namespace_roots.is_empty() {
                self.engine = Some(Engine::new(domain, &self.options)?);
                return Ok(());
            }
            self.engine = Some(Engine::new(&Domain::default(), &self.options)?);
        }
        let active_enrollment = self.active_enrollment();
        let sparse_roots = self.sparse_roots.clone();
        let engine = self.engine_mut()?;
        let mut required = domain.roots.clone();
        required.extend(domain.namespace_promotions(&sparse_roots));
        if domain.native_view {
            required.extend(sparse_roots.iter().cloned());
        }
        required.sort_by_key(|root| root.components().count());
        for root in &required {
            if engine
                .roots
                .iter()
                .any(|old| domain::relative_to(root, old).is_some())
            {
                continue;
            }
            let covered_child = sparse_roots.iter().find_map(|parent| {
                let relative = domain::relative_to(root, parent)?;
                let first = relative.components().next()?;
                Some(parent.join(first))
            });
            let complete = covered_child.as_ref().unwrap_or(root);
            let enrolled = match &covered_child {
                Some(child) => match &active_enrollment {
                    Some(owners) => engine.manager.enroll_disjoint_entry(child, owners),
                    None => engine.manager.enroll_entry(child),
                },
                None => match &active_enrollment {
                    Some(owners) if sparse_roots.contains(root) => {
                        engine.manager.enroll_disjoint_root(root, owners)
                    }
                    _ => engine.manager.enroll(root),
                },
            };
            enrolled.map_err(|cause| {
                ExecutionError::caused(
                    "cannot extend the persistent resource domain",
                    site(root),
                    cause,
                )
            })?;
            engine
                .roots
                .retain(|old| domain::relative_to(old, complete).is_none());
            engine.roots.push(complete.clone());
        }
        for parent in &domain.namespace_roots {
            if sparse_roots.contains(parent) {
                continue;
            }
            if engine
                .roots
                .iter()
                .any(|root| domain::relative_to(parent, root).is_some())
            {
                continue;
            }
            engine.manager.enroll_namespace(parent).map_err(|cause| {
                ExecutionError::caused(
                    "cannot enroll destination namespace metadata",
                    site(parent),
                    cause,
                )
            })?;
        }
        let complete = engine.roots.clone();
        self.sparse_roots.retain(|parent| {
            !complete
                .iter()
                .any(|root| domain::relative_to(parent, root).is_some())
        });
        self.sparse_roots.extend(
            domain
                .namespace_roots
                .iter()
                .filter(|parent| {
                    !complete
                        .iter()
                        .any(|root| domain::relative_to(parent, root).is_some())
                })
                .cloned(),
        );
        debug_assert!(
            Domain::default()
                .namespace_promotions(&self.sparse_roots)
                .is_empty()
        );
        Ok(())
    }

    fn active_enrollment(&self) -> Option<BTreeMap<TransactionId, hir::effects::Effects>> {
        let active = self.engine.as_ref()?.manager.active_transaction_ids();
        if active.is_empty() {
            return None;
        }
        let mut owners = BTreeMap::new();
        for id in active {
            if !self.implicit.contains(&id)
                || self
                    .programs
                    .values()
                    .any(|program| program.transaction == id)
            {
                return None;
            }
            let mut effects = hir::effects::Effects::default();
            let mut observed = false;
            for work in self
                .outstanding_work()
                .filter(|work| work.transaction == id)
            {
                effects.append(work.plan.effects(work.key.instruction).ok()?.clone());
                observed = true;
            }
            if !observed {
                return None;
            }
            owners.insert(id, effects);
        }
        Some(owners)
    }

    fn enroll_physical_namespaces(&mut self, program: &hir::Program) -> Result<()> {
        let mut entries = Vec::new();
        for node in &program.body.instructions {
            let InstructionKind::Transfer {
                source,
                destination,
            } = &node.kind
            else {
                continue;
            };
            if transfer_unstable(&self.planning, node.id) {
                continue;
            }
            let Some(spec) = physical::inspect(source, destination, node.span)? else {
                continue;
            };
            let Some((target, _)) = spec.managed else {
                continue;
            };
            let Some(parent) = target.parent() else {
                continue;
            };
            if physical::has_live_children(parent)? {
                entries.push((parent.to_owned(), target, node.span));
            }
        }
        if entries.is_empty() {
            return Ok(());
        }
        if self.engine.is_none() {
            self.sparse_roots.clear();
            self.engine = Some(Engine::new(&Domain::default(), &self.options)?);
        }
        let domain = Domain {
            roots: entries
                .iter()
                .map(|(_, target, _)| target.clone())
                .collect(),
            namespace_roots: entries
                .iter()
                .map(|(parent, _, _)| parent.clone())
                .collect(),
            ..Domain::default()
        };
        self.enroll_at(&domain, |root| {
            entries
                .iter()
                .find(|(parent, _, _)| {
                    domain::relative_to(root, parent).is_some()
                        || domain::relative_to(parent, root).is_some()
                })
                .map_or(Span::default(), |(_, _, span)| *span)
        })
    }

    fn fresh_instruction(&mut self) -> Result<InstructionId> {
        let id = InstructionId(self.next_instruction);
        self.next_instruction = self.next_instruction.checked_add(1).ok_or_else(|| {
            ExecutionError::new("runtime instruction identity exhausted", Span::default())
        })?;
        Ok(id)
    }

    fn binding_error(error: events::BindingError) -> ExecutionError {
        ExecutionError::caused(
            "cannot bind the executable event plan",
            Span::default(),
            error,
        )
    }

    fn retain_cleanup_failure(failure: &mut ExecutionError, error: ExecutionError) {
        failure
            .report
            .help
            .push(format!("owned cleanup failed: {error}"));
        failure
            .retained_paths
            .extend(error.retained_paths.iter().cloned());
        if failure.cause.is_none() {
            failure.cause = Some(Box::new(SharedFailure(Arc::new(error))));
        }
    }

    fn owner_key(&self, transaction: TransactionId) -> Result<OwnerKey> {
        let engine = self.engine.as_ref().ok_or_else(|| {
            ExecutionError::new(
                "an event owner requires an execution workspace",
                Span::default(),
            )
        })?;
        OwnerKey::new(format!(
            "{}:txn-{}",
            engine.workspace.display(),
            transaction.0
        ))
        .map_err(Self::binding_error)
    }

    fn event(&mut self, event: Event) -> Result<()> {
        let changed = self
            .ledger
            .record(event, Outcome::Succeeded)
            .map_err(Self::binding_error)?;
        if changed {
            self.progress = self.progress.checked_add(1).ok_or_else(|| {
                ExecutionError::new("runtime event progress identity exhausted", Span::default())
            })?;
        }
        Ok(())
    }

    fn failure(&mut self, work: &WorkNode, error: ExecutionError) -> ExecutionError {
        let cause = Arc::new(error);
        self.ledger.fail_node(work.key, cause.clone());
        let mut error = ExecutionError::caused(
            "a bound resource operation failed",
            cause.report.diagnostic.span,
            SharedFailure(cause.clone()),
        );
        error.report = cause.report.clone();
        error.origin = cause.origin.clone();
        error.transaction = Some(work.transaction);
        error.retained_paths = cause.retained_paths.clone();
        error
    }

    fn gate(&mut self, work: &WorkNode, phase: Phase) -> Result<bool> {
        match self
            .ledger
            .gate(&work.plan, work.key.instruction, phase)
            .map_err(Self::binding_error)?
        {
            GateState::Ready => Ok(true),
            GateState::Pending(_) => Ok(false),
            GateState::Cancelled {
                event,
                reason,
                cause,
            } => {
                self.ledger.cancel_node(work.key, reason, cause);
                Err(ExecutionError::new(
                    format!("a prerequisite was discarded: {reason:?}"),
                    work.plan
                        .origin(work.key.instruction)
                        .map_or(Span::default(), |origin| origin.site.span),
                )
                .help(format!(
                    "cancelled prerequisite: {event:?}; submit a new producer before retrying"
                )))
            }
            GateState::Failed { event, cause } => {
                let mut error = ExecutionError::caused(
                    "a prerequisite resource operation failed",
                    cause.report.diagnostic.span,
                    SharedFailure(cause.clone()),
                )
                .with_origin(cause.origin.as_deref());
                error
                    .report
                    .help
                    .push(format!("failed prerequisite: {event:?}"));
                Err(self.failure(work, error))
            }
        }
    }

    fn terminal(&mut self, terminal: Terminal) -> Result<()> {
        match terminal {
            Terminal::Flow(key) => {
                if let Some(flow) = self.flows.iter().find(|flow| flow.work.key == key) {
                    debug_assert!(flow.ready.load(Ordering::Acquire));
                }
            }
            Terminal::Process(process) => {
                if self
                    .programs
                    .get(&process)
                    .is_some_and(|program| program.exit_signal.is_cancelled())
                {
                    self.event(Event::Process(process, ProcessEvent::Exited))?;
                }
            }
        }
        Ok(())
    }

    fn drain_terminals(&mut self) -> Result<()> {
        while let Ok(event) = self.terminal_rx.try_recv() {
            self.terminal(event)?;
        }
        // Notifications are bounded hints. A full channel must not hide an
        // exited producer while its cohort peer still waits for its exit data.
        let exited = self
            .programs
            .values()
            .filter(|program| program.exit_signal.is_cancelled())
            .map(|program| program.id)
            .collect::<Vec<_>>();
        for process in exited {
            self.event(Event::Process(process, ProcessEvent::Exited))?;
        }
        Ok(())
    }

    async fn wait_event(&mut self) -> Result<()> {
        let mut changed = self.ledger.subscribe();
        tokio::select! {
            () = self.cancel.cancelled() => Err(ExecutionError::cancelled("execution cancelled while waiting for a resource event", Span::default())),
            terminal = self.terminal_rx.recv() => match terminal {
                Some(event) => self.terminal(event),
                None => Err(ExecutionError::new("execution event channel closed with pending work", Span::default())),
            },
            _ = changed.changed() => Ok(()),
        }
    }

    fn work(&self) -> Result<WorkNode> {
        self.active_work.clone().ok_or_else(|| {
            ExecutionError::new(
                "an executable operation has no bound submission owner",
                Span::default(),
            )
        })
    }

    fn owner_token(&mut self, transaction: TransactionId) -> CancellationToken {
        self.owner_tokens
            .entry(transaction)
            .or_insert_with(|| self.cancel.child_token())
            .clone()
    }

    fn bound_plan(&self, additions: Bindings) -> Result<Arc<SubmissionPlan>> {
        let previous = self
            .plan
            .as_ref()
            .expect("an executing submission retains its plan");
        let mut context = self.refinement_context();
        context.operation_facts = self.planning.operation_facts.clone();
        context.existing_parent_frontiers = self.planning.existing_parent_frontiers.clone();
        context.namespace_merges = self.planning.namespace_merges.clone();
        let graph = crate::hir::graph::build_with_context(previous.program(), &context);
        let mut bindings = additions;
        bindings.external.extend(self.external_work(previous.id())?);
        for (id, program) in &self.programs {
            bindings
                .processes
                .insert(*id, self.owner_key(program.transaction)?);
        }
        let plan = previous
            .rebind(context, graph, bindings)
            .map_err(Self::binding_error)?;
        Ok(plan)
    }

    fn outstanding_work(&self) -> impl Iterator<Item = &WorkNode> {
        self.flows
            .iter()
            .map(|flow| &flow.work)
            .chain(self.deferred.iter().map(|assignment| &assignment.work))
    }

    fn external_work(&self, submission: events::SubmissionId) -> Result<Vec<ExternalWork>> {
        let mut seen = BTreeSet::new();
        self.outstanding_work()
            .filter(|work| work.key.submission != submission && seen.insert(work.key))
            .map(|work| {
                let phase = if self
                    .deferred
                    .iter()
                    .any(|assignment| assignment.work.key == work.key)
                    || self
                        .flows
                        .iter()
                        .any(|flow| flow.work.key == work.key && flow.managed.is_some())
                {
                    Phase::Publish
                } else {
                    Phase::Start
                };
                ExternalWork::new(
                    work.plan.clone(),
                    work.key,
                    self.owner_key(work.transaction)?,
                    work.plan
                        .effects(work.key.instruction)
                        .map_err(Self::binding_error)?
                        .clone(),
                    phase,
                )
                .map_err(Self::binding_error)
            })
            .collect()
    }

    fn refresh_plan(&mut self, additions: Bindings) -> Result<Arc<SubmissionPlan>> {
        let plan = self.bound_plan(additions)?;
        self.remember_plan(plan.clone());
        self.plan = Some(plan.clone());
        Ok(plan)
    }

    fn remember_plan(&mut self, plan: Arc<SubmissionPlan>) {
        let versions = self.retained_plans.entry(plan.id()).or_default();
        if !versions
            .iter()
            .any(|version| version.ptr_eq(&Arc::downgrade(&plan)))
        {
            versions.push(Arc::downgrade(&plan));
        }
    }

    fn collect_plans(&mut self) {
        let protected = self
            .pending_nodes
            .values()
            .flatten()
            .copied()
            .chain(self.process_nodes.values().copied())
            .chain(self.prepared_readers.keys().copied())
            .chain(self.namespaces.keys().copied())
            .map(|key| key.submission)
            .collect::<BTreeSet<_>>();
        let mut retired = Vec::new();
        self.retained_plans.retain(|id, versions| {
            versions.retain(|version| version.strong_count() > 0);
            if versions.is_empty() && !protected.contains(id) {
                retired.push(*id);
                false
            } else {
                true
            }
        });
        for id in retired {
            self.ledger.retire_submission(id);
            self.node_transactions.retain(|key, _| key.submission != id);
        }
    }

    fn prepare_work(
        &mut self,
        node: &Instruction,
        current: Option<TransactionId>,
    ) -> Result<(WorkNode, Option<program::SharedView>)> {
        let plan = self
            .plan
            .as_ref()
            .expect("submission plan bound before execution");
        let key = plan.node_key(node.id).map_err(Self::binding_error)?;
        let shared_peer = if let InstructionKind::Spawn { process, .. } = &node.kind {
            self.programs
                .values()
                .find(|peer| {
                    self.shareable(peer)
                        && hir::graph::same_native_epoch(
                            plan.program(),
                            plan.context(),
                            peer.id,
                            *process,
                        )
                })
                .map(|peer| peer.id)
        } else {
            None
        };
        let transaction = if let Some(id) = self.node_transactions.get(&key) {
            *id
        } else {
            match &node.kind {
                InstructionKind::Transaction { .. } => self.engine_mut()?.allocate()?,
                _ if current.is_some() => current.unwrap(),
                InstructionKind::Spawn { .. } => shared_peer
                    .map(|process| self.programs[&process].transaction)
                    .map_or_else(|| self.engine_mut()?.allocate(), Ok)?,
                InstructionKind::Transfer {
                    destination: Output::Stdin(process),
                    ..
                } => {
                    self.programs
                        .get(process)
                        .ok_or_else(|| {
                            ExecutionError::new("program input has no live owner", node.span)
                        })?
                        .transaction
                }
                InstructionKind::Transfer {
                    source: Input::Port { process, .. },
                    ..
                } => match self
                    .programs
                    .get(process)
                    .map(|program| program.transaction)
                {
                    Some(id) if self.implicit.contains(&id) => id,
                    _ => self.engine_mut()?.allocate()?,
                },
                _ => self.engine_mut()?.allocate()?,
            }
        };
        let unstable = self.host_unstable(node.id, hir::effects::EndpointRole::Source)
            || self.host_unstable(node.id, hir::effects::EndpointRole::Destination);
        if let InstructionKind::Transfer {
            source,
            destination,
        } = &node.kind
        {
            physical::verify_fifo_inputs(
                node.id,
                source,
                destination,
                &self.native_fifo_pins,
                node.span,
            )?;
        }
        let mut physical = match &node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } if !unstable => physical::inspect(source, destination, node.span)?,
            _ => None,
        };
        if let Some(spec) = &mut physical {
            spec.bind_rendezvous(
                node.id,
                &self.planning.physical_rendezvous,
                &self.native_fifo_pins,
            )?;
        }
        self.observe_node(node, physical.as_ref())?;
        if current.is_none() && !unstable {
            self.reserve_namespace(key, node, physical.as_ref())?;
        }
        let mut bindings = Bindings::default();
        bindings.nodes.insert(node.id, self.owner_key(transaction)?);
        let mut plan = self.bound_plan(bindings)?;
        let candidate = if let InstructionKind::Spawn { process, .. } = &node.kind {
            shared_peer
                .and_then(|peer| self.programs.get(&peer))
                .filter(|program| program.transaction == transaction)
                .and_then(Program::fork_shared)
                .map(|candidate| (*process, candidate))
        } else {
            None
        };
        if let Some((process, (cohort, prepared))) = &candidate {
            let effects = prepared.effects().clone();
            let mut context = plan.context().clone();
            context.process_effects.insert(*process, effects.clone());
            context
                .cohort_views
                .retain(|proof| proof.view_id() != effects.view_id());
            let mut members = self
                .programs
                .values()
                .filter(|program| program.is_running() && program.cohort == *cohort)
                .map(|program| (program.id, program.effects.clone()))
                .collect::<Vec<_>>();
            members.push((*process, effects.clone()));
            context.cohort_views.push(
                crate::hir::graph::CohortViewProof::shared(
                    self.owner_key(transaction)?.as_str(),
                    format!("{}:cohort-{}", effects.view_id(), cohort.0),
                    members,
                )
                .map_err(|message| ExecutionError::new(message, node.span))?,
            );
            let graph = crate::hir::graph::build_with_context(plan.program(), &context);
            let mut bindings = Bindings::default();
            bindings
                .processes
                .insert(*process, self.owner_key(transaction)?);
            plan = plan
                .rebind(context, graph, bindings)
                .map_err(Self::binding_error)?;
        }
        Ok((
            WorkNode {
                plan,
                key,
                order: self.orders[&node.id],
                transaction,
                physical,
            },
            candidate.map(|(_, candidate)| candidate),
        ))
    }

    fn observe_node(
        &mut self,
        node: &Instruction,
        physical: Option<&physical::Spec>,
    ) -> Result<()> {
        let InstructionKind::Transfer {
            source,
            destination,
        } = &node.kind
        else {
            return Ok(());
        };
        let classify = |path: &Path| match fs::symlink_metadata(path) {
            Ok(meta) if meta.is_file() => crate::plan::FileClass::Regular,
            Ok(meta) if meta.is_dir() => crate::plan::FileClass::Directory,
            Ok(_) => crate::plan::FileClass::Stream,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => {
                crate::plan::FileClass::Missing
            }
            Err(_) => crate::plan::FileClass::Unknown,
        };
        let source_class = match source {
            _ if self.host_unstable(node.id, hir::effects::EndpointRole::Source) => self
                .planning
                .operation_facts
                .get(&node.id)
                .map_or(crate::plan::FileClass::Unknown, |facts| facts.source),
            Input::File { location, .. }
                if matches!(
                    location.backend,
                    Backend::Local | Backend::Env | Backend::Path
                ) =>
            {
                classify(&local(location, true, node.span)?)
            }
            Input::File { location, .. } if matches!(location.backend, Backend::Tcp { .. }) => {
                crate::plan::FileClass::Stream
            }
            _ => crate::plan::FileClass::Unknown,
        };
        let destination_class = match destination {
            _ if self.host_unstable(node.id, hir::effects::EndpointRole::Destination) => self
                .planning
                .operation_facts
                .get(&node.id)
                .map_or(crate::plan::FileClass::Unknown, |facts| facts.destination),
            Output::File { location, .. }
                if matches!(location.backend, Backend::Local | Backend::Env) =>
            {
                classify(&local(location, false, node.span)?)
            }
            _ => crate::plan::FileClass::Unknown,
        };
        self.planning
            .operation_facts
            .entry(node.id)
            .or_insert_with(|| {
                physical.map_or(
                    crate::hir::effects::OperationFacts {
                        source: source_class,
                        destination: destination_class,
                    },
                    |spec| spec.kinds.into(),
                )
            });
        Ok(())
    }

    fn host_unstable(&self, instruction: InstructionId, role: hir::effects::EndpointRole) -> bool {
        self.planning
            .host_unstable
            .contains(&hir::effects::EndpointKey { instruction, role })
    }

    fn output_path(
        &self,
        instruction: InstructionId,
        location: &Location,
        span: Span,
    ) -> Result<PathBuf> {
        if self.host_unstable(instruction, hir::effects::EndpointRole::Destination) {
            domain::deferred_local(location, false, span)
        } else {
            local(location, false, span)
        }
    }

    fn selected_path(
        &mut self,
        transaction: TransactionId,
        raw: &Path,
        span: Span,
    ) -> Result<PathBuf> {
        self.engine_mut()?
            .manager
            .resolve_path(transaction, raw)
            .map_err(|cause| {
                ExecutionError::caused(
                    "cannot resolve the endpoint in its transaction view",
                    span,
                    cause,
                )
            })
    }

    fn selected_source(
        &mut self,
        instruction: InstructionId,
        source: &Input,
        transaction: TransactionId,
        span: Span,
    ) -> Result<Source> {
        if self.host_unstable(instruction, hir::effects::EndpointRole::Source)
            && let Input::File { location, .. } = source
            && matches!(
                location.backend,
                Backend::Local | Backend::Env | Backend::Path
            )
        {
            let raw = domain::deferred_local(location, true, span)?;
            return self
                .selected_path(transaction, &raw, span)
                .map(Source::Local);
        }
        file_source(source, span)
    }

    fn process_data(&self, process: ProcessId, port: ReadPort, span: Span) -> Result<()> {
        if port == ReadPort::Pid {
            return Ok(());
        }
        match self.ledger.outcome(&Event::Process(process, ProcessEvent::Imported)) {
            Some(Outcome::Cancelled { reason, .. }) => Err(ExecutionError::new(
                format!("program data belongs to a discarded epoch: {reason:?}"), span,
            ).help("Launch a new program before consuming cancelled output or exit data; already committed receipts remain readable.")),
            Some(Outcome::Failed(cause)) => Err(ExecutionError::caused(
                "program data producer failed", span, SharedFailure(cause.clone()),
            )),
            _ => Ok(()),
        }
    }

    fn shareable(&self, program: &Program) -> bool {
        program.is_running()
            && !self
                .flows
                .iter()
                .any(|flow| flow.transaction == program.transaction && flow.managed.is_some())
            && !self
                .deferred
                .iter()
                .any(|assignment| assignment.transaction == program.transaction)
    }

    fn reserve_namespace(
        &mut self,
        key: NodeKey,
        node: &Instruction,
        physical: Option<&physical::Spec>,
    ) -> Result<()> {
        if self.namespaces.contains_key(&key) {
            return Ok(());
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
            return Ok(());
        };
        let facts = self.planning.operation_facts[&node.id];
        if location.backend != Backend::Local
            || facts.destination != crate::plan::FileClass::Missing
        {
            return Ok(());
        }
        // Shape is not a local immutable-reader capability. Remote regular
        // sources must first pass through the import adapter, not this lease.
        let local_file = matches!(source,
            Input::File { location, pattern: None }
            if matches!(location.backend, Backend::Local | Backend::Env | Backend::Path)
                && facts.source == crate::plan::FileClass::Regular
        );
        let captured_flow = physical.is_some_and(|spec| spec.managed.is_some());
        let eligible = matches!(source, Input::Literal(_)) || local_file || captured_flow;
        if !eligible || physical.is_some_and(|spec| spec.managed.is_none()) {
            return Ok(());
        }
        let target = local(location, false, node.span)?;
        let Some(parent) = Path::new(&location.path).parent() else {
            return Ok(());
        };
        let permit = match self
            .engine_mut()?
            .manager
            .certify_namespace_children(std::slice::from_ref(&target))
        {
            Ok(permit) => permit,
            Err(_) => return Ok(()),
        };
        let epoch = crate::hir::effects::NamespaceMergeEpoch::verified(
            permit.provider(),
            permit.epoch_id().to_string(),
            permit.parent_key().clone(),
        )
        .map_err(|message| ExecutionError::new(message, node.span))?;
        let proof = crate::hir::effects::NamespaceMergeProof::insert_file(
            epoch,
            Location {
                backend: Backend::Local,
                path: parent.to_string_lossy().into_owned(),
            },
            location.clone(),
            permit
                .child_key(&target)
                .expect("minted permit contains its exact native child")
                .clone(),
            self.orders[&node.id].0,
        )
        .map_err(|message| ExecutionError::new(message, node.span))?;
        self.planning.existing_parent_frontiers.insert(
            node.id,
            Location {
                backend: Backend::Local,
                path: parent.to_string_lossy().into_owned(),
            },
        );
        self.planning.namespace_merges.insert(node.id, proof);
        self.namespaces
            .insert(key, NamespaceSlot { permit, target });
        Ok(())
    }

    fn release_namespace(&mut self, key: NodeKey) -> Result<()> {
        if let Some(slot) = self.namespaces.remove(&key) {
            self.engine_mut()?
                .manager
                .cancel_namespace_child(&slot.permit, &slot.target)
                .map_err(|cause| {
                    ExecutionError::caused(
                        "cannot release a reserved namespace child",
                        Span::default(),
                        cause,
                    )
                })?;
            if self
                .plan
                .as_ref()
                .is_some_and(|plan| plan.id() == key.submission)
            {
                self.planning.namespace_merges.remove(&key.instruction);
            }
        }
        Ok(())
    }

    fn publish_namespace(
        &mut self,
        work: &WorkNode,
        snapshot: &Snapshot,
        source: &Source,
        span: Span,
    ) -> Result<()> {
        let slot = self
            .namespaces
            .get(&work.key)
            .expect("namespace publication retains its actual permit");
        let (permit, target) = (slot.permit.clone(), slot.target.clone());
        let mut trace = self.engine_mut()?.assignment_trace(
            work.key.instruction,
            source,
            &target,
            DestinationMode::Exact,
            work.transaction,
        );
        trace.attribute("phase", "publication");
        trace.attribute("publication.policy", "namespace-receipt");
        let result = self.publish_namespace_inner(work, snapshot, span, permit, &target);
        trace.finish_result(&result);
        match &result {
            Ok(()) => {
                trace.attribute("phase", "committed");
                trace.metric(
                    "files.assigned",
                    snapshot
                        .entries()
                        .values()
                        .filter(|entry| matches!(entry, Entry::File { .. }))
                        .count() as u64,
                );
                trace.metric(
                    "bytes.assigned",
                    snapshot
                        .entries()
                        .values()
                        .filter_map(|entry| match entry {
                            Entry::File { artifact, .. } => Some(artifact.len()),
                            _ => None,
                        })
                        .sum(),
                );
            }
            Err(failure) => {
                trace.attribute("phase", "failed");
                trace.record_error("copy.publish", failure);
            }
        }
        result
    }

    fn publish_namespace_inner(
        &mut self,
        work: &WorkNode,
        snapshot: &Snapshot,
        span: Span,
        permit: NamespaceMergePermit,
        target: &Path,
    ) -> Result<()> {
        let receipt = self
            .engine_mut()?
            .manager
            .publish_merged_file(
                work.transaction,
                &permit,
                target,
                snapshot,
                NamespacePublicationOrder(work.order.0),
            )
            .map_err(|cause| {
                ExecutionError::caused("cannot publish the reserved namespace child", span, cause)
            })?;
        self.namespaces.remove(&work.key);
        let engine = self.engine_mut()?;
        engine.report.assignments += 1;
        for entry in snapshot.entries().values() {
            if let Entry::File { artifact, .. } = entry {
                engine.report.assigned_files += 1;
                engine.report.assigned_bytes =
                    engine.report.assigned_bytes.saturating_add(artifact.len());
            }
        }
        engine.committed(receipt.report);
        if self.implicit.contains(&work.transaction) {
            self.closed_owners.insert(work.transaction);
        }
        self.logical_complete(work)?;
        self.owner_committed(work.transaction)?;
        Ok(())
    }

    fn logical_complete(&mut self, work: &WorkNode) -> Result<()> {
        self.event(Event::Node(work.key, NodeEvent::LogicalComplete))?;
        self.pending_nodes
            .entry(work.transaction)
            .or_default()
            .insert(work.key);
        Ok(())
    }

    fn owner_committed(&mut self, transaction: TransactionId) -> Result<()> {
        for key in self.pending_nodes.remove(&transaction).unwrap_or_default() {
            self.event(Event::Node(key, NodeEvent::Committed))?;
        }
        let processes = self
            .programs
            .values()
            .filter(|program| program.transaction == transaction && program.is_sealed())
            .map(|program| program.id)
            .collect::<Vec<_>>();
        for process in processes {
            self.event(Event::Process(process, ProcessEvent::Committed))?;
            if let Some(cohort) = self.process_cohorts.get(&process).cloned() {
                self.event(Event::Cohort(cohort, Visibility::Committed))?;
            }
            self.process_nodes.remove(&process);
        }
        self.owner_tokens.remove(&transaction);
        Ok(())
    }

    async fn region(
        &mut self,
        region: &Region,
        current: Option<TransactionId>,
        program: &hir::Program,
    ) -> Result<()> {
        let mut pending = region.instructions.iter().collect::<Vec<_>>();
        while !pending.is_empty() {
            let available = self.admission.available();
            if self.cancel.is_cancelled() {
                return Err(ExecutionError::cancelled(
                    "execution cancelled",
                    pending[0].span,
                ));
            }
            let mut selected = None;
            let mut capacity_blocked = None;
            let mut owner_blocked = None;
            for (index, instruction) in pending.iter().enumerate() {
                if !self.ports_available(instruction) {
                    continue;
                }
                let (mut work, candidate) = self.prepare_work(instruction, current)?;
                // Observe failed gates before considering capacity; a busy
                // resource must not obscure the original producer failure.
                let start_ready = self.gate(&work, Phase::Start)?;
                let pin_input = self.may_pin_input(instruction, &work)?;
                if !start_ready && !pin_input {
                    self.discard_candidate(work.key, candidate, instruction.span)?;
                    continue;
                }
                let demand = self.demand(instruction, &work);
                match self.admission.availability(demand) {
                    Availability::Ready => {}
                    Availability::Impossible => {
                        self.admission.feasible(demand, instruction.span)?
                    }
                    Availability::Busy => {
                        let site = (instruction.span, work.plan.origin(instruction.id).cloned());
                        capacity_blocked.get_or_insert_with(|| site.clone());
                        if self.owner_capacity_blocked(instruction, &work, demand) {
                            owner_blocked.get_or_insert(site);
                        }
                        self.discard_candidate(work.key, candidate, instruction.span)?;
                        continue;
                    }
                }
                let reserved = if demand.streams > 0 {
                    self.reserve_flows(instruction.id, instruction.span)?
                } else {
                    Vec::new()
                };
                if pin_input {
                    self.plan = Some(work.plan.clone());
                    self.active_work = Some(work.clone());
                    let InstructionKind::Transfer { source, .. } = &instruction.kind else {
                        unreachable!()
                    };
                    let reader = self
                        .reader(instruction.id, source, work.transaction, instruction.span)
                        .await?;
                    self.prepared_readers.insert(work.key, reader);
                    work.plan = self.refresh_plan(Bindings::default())?;
                }
                if self.gate(&work, Phase::Start)? {
                    selected = Some((index, work, candidate));
                    break;
                }
                for member in reserved {
                    self.flow_reservations.remove(&member);
                }
                self.discard_candidate(work.key, candidate, instruction.span)?;
            }
            let Some((index, work, candidate)) = selected else {
                let before = self.progress;
                self.reap_physical().await?;
                // A task can release its lease before its owner can import or
                // commit. Capacity progress is not a logical-completion event.
                if before != self.progress || available != self.admission.available() {
                    continue;
                }
                if !self.complete_submission && current.is_none() {
                    self.rejected_before_start = true;
                    if let Some((span, origin)) = capacity_blocked {
                        return Err(ExecutionError::new("concurrent resource capacity is occupied; no blocked operation started", span)
                            .help("Use /wait to finish an existing finite job, /stop to release an open producer, or increase the concurrent resource limits before retrying.").with_origin(origin.as_ref()));
                    }
                    let fifo = pending.iter().any(|node| {
                        self.native_fifo_pins
                            .keys()
                            .any(|key| key.instruction == node.id)
                    });
                    let help = if fifo {
                        "If the FIFO peer is another msync statement, /stop the pending half and submit both sides together with /load or one macro call. /wait alone cannot create its missing peer. Otherwise connect the external FIFO reader/writer before retrying."
                    } else {
                        "Connect the required stdin, finish its producer with /wait, or cancel the conflicting job with /stop before retrying."
                    };
                    return Err(ExecutionError::new("this submission is waiting for an owned resource producer; no blocked operation started", pending[0].span).help(help));
                }
                for program in self
                    .programs
                    .values_mut()
                    .filter(|program| program.is_running())
                {
                    let has_pending_input = pending.iter().any(|node| matches!(node.kind, InstructionKind::Transfer { destination: Output::Stdin(process), .. } if process == program.id));
                    if !has_pending_input {
                        program.close_unused_stdin();
                    }
                }
                if let Some((span, origin)) = owner_blocked {
                    return Err(ExecutionError::new("a live program owner retains the capacity needed by its pending input", span)
                        .help("Connect program input before opening a dependent output drain, or increase --max-streams/--memory-limit. Waiting cannot admit this input while its own owner retains those resources.").with_origin(origin.as_ref()));
                }
                if self.flows.is_empty() && self.live_processes().is_empty() {
                    return Err(ExecutionError::new(
                        "the executable event graph has no producer for its remaining prerequisites",
                        pending[0].span,
                    ));
                }
                self.wait_event().await?;
                continue;
            };
            let instruction = pending.remove(index);
            let origin = work.plan.origin(instruction.id).cloned();
            self.plan = Some(work.plan.clone());
            self.remember_plan(work.plan.clone());
            self.node_transactions.insert(work.key, work.transaction);
            self.candidate = candidate;
            self.active_work = Some(work.clone());
            self.instruction(instruction, current, program)
                .await
                .map_err(|error| error.with_origin(origin.as_ref()))?;
            let deferred = matches!(instruction.kind, InstructionKind::Spawn { .. })
                || self.flows.iter().any(|flow| flow.work.key == work.key)
                || self
                    .deferred
                    .iter()
                    .any(|assignment| assignment.work.key == work.key);
            if !deferred {
                self.event(Event::Node(work.key, NodeEvent::ServiceReady))?;
                self.logical_complete(&work)?;
                if current.is_none()
                    || matches!(
                        instruction.kind,
                        InstructionKind::Commit { .. } | InstructionKind::Transaction { .. }
                    )
                {
                    self.owner_committed(work.transaction)?;
                }
            }
        }
        Ok(())
    }

    fn discard_candidate(
        &mut self,
        key: NodeKey,
        candidate: Option<program::SharedView>,
        span: Span,
    ) -> Result<()> {
        self.release_namespace(key)?;
        if let Some((_, prepared)) = candidate {
            prepared.discard().map_err(|cause| {
                ExecutionError::caused(
                    "cannot release an unadmitted shared-view lease",
                    span,
                    cause,
                )
            })?;
        }
        Ok(())
    }

    fn ports_available(&self, node: &Instruction) -> bool {
        let InstructionKind::Transfer {
            source,
            destination,
        } = &node.kind
        else {
            return true;
        };
        (!matches!(source, Input::Port { process, .. } if !self.programs.contains_key(process)))
            && (!matches!(destination, Output::Stdin(process) if !self.programs.contains_key(process)))
    }

    fn may_pin_input(&self, node: &Instruction, work: &WorkNode) -> Result<bool> {
        let InstructionKind::Transfer {
            source:
                Input::File {
                    location,
                    pattern: None,
                },
            destination: Output::Stdin(_),
        } = &node.kind
        else {
            return Ok(false);
        };
        if self.prepared_readers.contains_key(&work.key)
            || !self.host_unstable(node.id, hir::effects::EndpointRole::Source)
                && physical::source_stream(location, node.span)?.is_some()
        {
            return Ok(false);
        }
        let pending = match self
            .ledger
            .gate(&work.plan, node.id, Phase::Start)
            .map_err(Self::binding_error)?
        {
            GateState::Pending(pending) => pending,
            GateState::Ready => Vec::new(),
            GateState::Failed { .. } | GateState::Cancelled { .. } => return Ok(false),
        };
        let owner = self.owner_key(work.transaction)?;
        Ok(pending.iter().all(|event| match event {
            Event::Process(process, ProcessEvent::Imported) => {
                self.programs.get(process).is_some_and(|program| {
                    self.owner_key(program.transaction)
                        .is_ok_and(|value| value == owner)
                })
            }
            Event::Cohort(cohort, Visibility::Imported) => cohort.owner == owner,
            _ => false,
        }))
    }

    fn instruction<'a>(
        &'a mut self,
        node: &'a Instruction,
        current: Option<TransactionId>,
        program: &'a hir::Program,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<()>> + Send + 'a>> {
        Box::pin(async move {
            self.active_origin = self.origins.get(&node.id).cloned();
            match &node.kind {
                InstructionKind::Spawn { .. } => self.spawn(node, current, program).await,
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    self.transfer(node.id, source, destination, current, node.span)
                        .await
                }
                InstructionKind::Transaction { transaction, body } => {
                    let isolation = program
                        .transactions
                        .iter()
                        .find(|info| info.id == *transaction)
                        .ok_or_else(|| {
                            ExecutionError::new("transaction metadata is missing", node.span)
                        })?
                        .isolation;
                    let work = self.work()?;
                    let id = work.transaction;
                    self.engine_mut()?
                        .manager
                        .begin(id, isolation)
                        .map_err(|cause| {
                            ExecutionError::caused("cannot begin transaction", node.span, cause)
                        })?;
                    self.engine_mut()?.named.insert(*transaction, id);
                    let mut bindings = Bindings::default();
                    let owner = self.owner_key(id)?;
                    for child in &body.instructions {
                        bindings.nodes.insert(child.id, owner.clone());
                        if let InstructionKind::Spawn { process, .. } = &child.kind {
                            bindings.processes.insert(*process, owner.clone());
                        }
                    }
                    self.refresh_plan(bindings)?;
                    let result = self.region(body, Some(id), program).await;
                    let result = match result {
                        Ok(()) => self
                            .settle_region(&work)
                            .await
                            .and_then(|()| self.engine_mut()?.finish(id, node.span)),
                        error => error,
                    };
                    if let Err(mut failure) = result {
                        self.cancel_transaction(id, &mut failure).await;
                        self.engine_mut()?.abort(id, &mut failure);
                        return Err(failure);
                    }
                    Ok(())
                }
                InstructionKind::Commit { transaction } => {
                    let id = self
                        .engine_mut()?
                        .current(*transaction, current, node.span)?;
                    self.quiesce(id).await?;
                    let work = self.work()?;
                    self.await_gate(&work, Phase::Publish).await?;
                    let report = self.engine_mut()?.manager.checkpoint(id).map_err(|cause| {
                        ExecutionError::caused("checkpoint failed", node.span, cause)
                    })?;
                    self.engine_mut()?.committed(report);
                    self.owner_committed(id)?;
                    Ok(())
                }
                InstructionKind::Rollback { transaction, scope } => {
                    let id = *self.engine_mut()?.named.get(transaction).ok_or_else(|| {
                        ExecutionError::new(
                            "rollback has no retained transaction origin",
                            node.span,
                        )
                    })?;
                    match scope {
                        RollbackScope::Checkpoint => {
                            if current != Some(id) {
                                return Err(ExecutionError::new(
                                    "rollback belongs to another transaction",
                                    node.span,
                                ));
                            }
                            let work = self.work()?;
                            let frame = self
                                .ledger
                                .cancel_epoch(work.plan.clone(), node.id)
                                .map_err(Self::binding_error)?;
                            debug_assert_eq!(frame.plan().id(), work.key.submission);
                            debug_assert_eq!(frame.controller(), work.key);
                            let controlled = &frame.cut().processes;
                            debug_assert!(
                                self.programs
                                    .values()
                                    .filter(
                                        |program| program.transaction == id && program.is_running()
                                    )
                                    .all(|program| controlled.contains(&program.id))
                            );
                            let mut failure = ExecutionError::new(
                                "discarding the pending program epoch",
                                node.span,
                            );
                            if !self.cancel_transaction(id, &mut failure).await {
                                let cause = Arc::new(failure);
                                self.ledger
                                    .fail_reset(frame, cause.clone())
                                    .map_err(Self::binding_error)?;
                                return Err(ExecutionError::caused(
                                    "cannot join the discarded epoch before checkpoint restore",
                                    node.span,
                                    SharedFailure(cause),
                                ));
                            }
                            let restored = self.engine_mut()?.manager.rollback_pending(id);
                            if let Err(cause) = restored {
                                let cause = Arc::new(ExecutionError::caused(
                                    "cannot restore checkpoint",
                                    node.span,
                                    cause,
                                ));
                                self.ledger
                                    .fail_reset(frame, cause.clone())
                                    .map_err(Self::binding_error)?;
                                return Err(ExecutionError::caused(
                                    "checkpoint restoration failed",
                                    node.span,
                                    SharedFailure(cause),
                                ));
                            }
                            self.ledger
                                .finish_reset(frame)
                                .map_err(Self::binding_error)?;
                        }
                        RollbackScope::Begin => {
                            if current.is_some() {
                                return Err(ExecutionError::new(
                                    "named rollback is not allowed inside a transaction",
                                    node.span,
                                ));
                            }
                            let report = self.engine_mut()?.manager.rollback_origin(id).map_err(
                                |cause| {
                                    ExecutionError::caused(
                                        "cannot restore transaction origin",
                                        node.span,
                                        cause,
                                    )
                                },
                            )?;
                            self.engine_mut()?.committed(report);
                        }
                    }
                    self.engine_mut()?.report.rollbacks += 1;
                    Ok(())
                }
            }
        })
    }

    async fn spawn(
        &mut self,
        node: &Instruction,
        current: Option<TransactionId>,
        hir: &hir::Program,
    ) -> Result<()> {
        let InstructionKind::Spawn {
            process,
            executable,
            arguments,
            cwd,
        } = &node.kind
        else {
            unreachable!("spawn requires a launch instruction")
        };
        let id = *process;
        let instruction = node.id;
        let span = node.span;
        // Connected opaque programs share one mutable native epoch, not two
        // independent snapshots whose final deltas merely happen to merge.
        let (shared, baseline, owner) = {
            let shared = self.candidate.take();
            let peer = shared.as_ref().and_then(|(cohort, _)| {
                self.programs.values().find(|program| {
                    program.is_running()
                        && program.cohort == *cohort
                        && current.is_none_or(|id| id == program.transaction)
                })
            });
            (
                shared,
                peer.map(|program| program.baseline.clone()),
                peer.map(|program| program.transaction),
            )
        };
        if self.programs.contains_key(&id) {
            return Err(ExecutionError::new(
                "program identity is already owned by this runtime",
                span,
            ));
        }
        let lease = self.admission.program(span)?;
        let transaction = match current {
            Some(id) => id,
            None if owner.is_some() => owner.unwrap(),
            None => {
                let id = self.work()?.transaction;
                self.engine_mut()?
                    .manager
                    .begin(id, Isolation::Serializable)
                    .map_err(|cause| {
                        ExecutionError::caused("cannot begin program context", span, cause)
                    })?;
                self.implicit.insert(id);
                id
            }
        };
        let task = lease.task("program.launch");
        let worker = self.worker.clone().expect("preflight worker");
        let buffer_bytes = self.options.limits.buffer_bytes;
        let origin = self.active_origin.clone();
        let mut context = self.refinement_context();
        context.operation_facts = self.planning.operation_facts.clone();
        let peers = self
            .programs
            .values()
            .filter(|program| program.is_running())
            .map(|program| (program.id, program.cohort, program.effects.clone()))
            .collect::<Vec<_>>();
        context.existing_parent_frontiers = self.planning.existing_parent_frontiers.clone();
        context.namespace_merges = self.planning.namespace_merges.clone();
        let work = self.work()?;
        let owner_key = self.owner_key(transaction)?;
        let ledger = &self.ledger;
        let mut admitted = None;
        let mut rejected = false;
        let engine = self
            .engine
            .as_mut()
            .expect("a bound program has an execution engine");
        let mut roots = baseline.unwrap_or_default();
        if shared.is_none() {
            for root in &engine.roots {
                let snapshot =
                    engine
                        .manager
                        .read_snapshot(transaction, root)
                        .map_err(|cause| {
                            ExecutionError::caused("cannot read the program epoch", span, cause)
                        })?;
                if !snapshot.is_directory() {
                    return Err(ExecutionError::new(
                        "program resource domains must be enrolled as directory roots",
                        span,
                    ));
                }
                roots.push((root.clone(), snapshot));
            }
        }
        let scratch = engine.workspace.join("programs");
        fs::create_dir_all(&scratch).map_err(|cause| {
            ExecutionError::caused("cannot create program workspace", span, cause)
        })?;
        let mut config = ProcessConfig::new(engine.workspace.join("spools"));
        fs::create_dir_all(&config.scratch_root)
            .map_err(|cause| ExecutionError::caused("cannot create program spools", span, cause))?;
        config.buffer_bytes = buffer_bytes;
        let owner_identity = format!("{}:txn-{}", engine.workspace.display(), transaction.0);
        let launched = task
            .run(Program::launch(
                program::Launch {
                    id,
                    transaction,
                    span,
                    executable,
                    arguments,
                    cwd,
                    worker: &worker,
                    scratch: &scratch,
                    config,
                    lease,
                    origin,
                },
                &engine.assets,
                roots,
                shared,
                |cohort, effects| {
                    context.process_effects.insert(id, effects.clone());
                    let mut members = peers
                        .iter()
                        .filter(|(_, peer_cohort, _)| *peer_cohort == cohort)
                        .map(|(id, _, effects)| (*id, effects.clone()))
                        .collect::<Vec<_>>();
                    members.push((id, effects.clone()));
                    if members.len() > 1 {
                        context
                            .cohort_views
                            .retain(|proof| proof.view_id() != effects.view_id());
                        let proof = crate::hir::graph::CohortViewProof::shared(
                            owner_identity,
                            format!("{}:cohort-{}", effects.view_id(), cohort.0),
                            members,
                        )
                        .map_err(|message| ExecutionError::new(message, span))?;
                        context.cohort_views.push(proof);
                    }
                    let graph = crate::hir::graph::build_with_context(hir, &context);
                    let mut bindings = Bindings::default();
                    bindings.processes.insert(id, owner_key);
                    let bound = work.plan.rebind(context, graph, bindings).map_err(Self::binding_error)?;
                    let result = match ledger.gate(&bound, instruction, Phase::Start).map_err(Self::binding_error)? {
                        GateState::Ready => { admitted = Some(bound); Ok(()) },
                        GateState::Pending(events) => Err(ExecutionError::new(format!("prepared program is waiting for {} resource events; no candidate payload started", events.len()), span)),
                        GateState::Failed { cause, .. } => Err(ExecutionError::caused("prepared program has a failed prerequisite", span, SharedFailure(cause))),
                        GateState::Cancelled { reason, .. } => Err(ExecutionError::new(format!("prepared program has a discarded prerequisite: {reason:?}"), span)),
                    };
                    rejected = result.is_err();
                    result
                },
            ))
            .await;
        if rejected {
            self.rejected_before_start = true;
        }
        let program = launched?;
        self.plan = admitted;
        self.programs.insert(id, program);
        self.latest_processes.insert(id);
        let mut work = self.work()?;
        work.plan = self.refresh_plan(Bindings::default())?;
        self.active_work = Some(work.clone());
        self.process_nodes.insert(id, work.key);
        let program = &self.programs[&id];
        let cohort = CohortKey {
            provider: program.effects.provider().into(),
            view_id: program.effects.view_id().into(),
            owner: self.owner_key(transaction)?,
            epoch: format!("{}:cohort-{}", program.effects.view_id(), program.cohort.0).into(),
        };
        self.process_cohorts.insert(id, cohort);
        self.event(Event::Process(id, ProcessEvent::Started))?;
        self.event(Event::Node(work.key, NodeEvent::Admitted))?;
        self.event(Event::Node(work.key, NodeEvent::ServiceReady))?;
        let signal = self.programs[&id].exit_signal.clone();
        let sender = self.terminal_tx.clone();
        tokio::spawn(async move {
            signal.cancelled().await;
            let _ = sender.try_send(Terminal::Process(id));
        });
        Ok(())
    }

    async fn transfer(
        &mut self,
        instruction: InstructionId,
        source: &Input,
        destination: &Output,
        current: Option<TransactionId>,
        span: Span,
    ) -> Result<()> {
        if let Input::Port { process, port } = source {
            self.process_data(*process, *port, span)?;
        }
        let physical = match self.work()?.physical {
            Some(spec) => Some(spec),
            None if known_physical(
                source,
                destination,
                self.planning.operation_facts.get(&instruction),
            ) =>
            {
                physical::inspect(source, destination, span)?
            }
            None => None,
        };
        if let Some(spec) = physical {
            return self
                .physical_transfer(instruction, source, spec, current)
                .await;
        }
        if let Some(spec) = endpoint::inspect(source, destination, span)? {
            return self.remote_transfer(source, spec, current, span).await;
        }
        match (source, destination) {
            (_, Output::Stdin(process)) => {
                let transaction = self
                    .programs
                    .get(process)
                    .ok_or_else(|| {
                        ExecutionError::new("program stdin has no preceding launch", span)
                    })?
                    .transaction;
                if current.is_some_and(|id| id != transaction) {
                    return Err(ExecutionError::new(
                        "a program stream cannot cross transaction contexts",
                        span,
                    ));
                }
                if let Input::File { location, .. } = source
                    && !self.host_unstable(instruction, hir::effects::EndpointRole::Source)
                    && let Some(endpoint) = physical::source_stream(location, span)?
                {
                    if current.is_some() {
                        return Err(ExecutionError::new(
                            "external stream input needs a transactional peer inside a named transaction",
                            span,
                        ));
                    }
                    let sink = self
                        .programs
                        .get_mut(process)
                        .unwrap()
                        .stdin
                        .take()
                        .ok_or_else(|| {
                            ExecutionError::new("program stdin already has a producer", span)
                        })?;
                    let consumer_closed = self.programs.get(process).unwrap().exit_signal.clone();
                    return self.physical_input(
                        instruction,
                        endpoint,
                        sink,
                        transaction,
                        span,
                        consumer_closed,
                    );
                }
                let work = self.work()?;
                let reader = match self.prepared_readers.remove(&work.key) {
                    Some(reader) => reader,
                    None => self.reader(instruction, source, transaction, span).await?,
                };
                let sink = self
                    .programs
                    .get_mut(process)
                    .unwrap()
                    .stdin
                    .take()
                    .ok_or_else(|| {
                        ExecutionError::new("program stdin already has a producer", span)
                    })?;
                self.flow(reader, sink, transaction, None, span)
            }
            (
                Input::Port {
                    process,
                    port: ReadPort::Stdout | ReadPort::Stderr,
                },
                Output::File { location, mode },
            ) => {
                let Input::Port { process: _, port } = source else {
                    unreachable!()
                };
                let transaction = self.connection_transaction(*process, current, span)?;
                let target = self.output_path(instruction, location, span)?;
                let reader = self.programs.get_mut(process).unwrap().take_reader(*port)?;
                let operation = self.fresh_instruction()?;
                let path = self
                    .engine_mut()?
                    .imports
                    .join(format!("program-output-{}", operation.0));
                let mut file = tokio::fs::OpenOptions::new()
                    .write(true)
                    .create_new(true)
                    .open(&path)
                    .await
                    .map_err(|cause| {
                        ExecutionError::caused("cannot stage program output", span, cause)
                    })?;
                file.set_max_buf_size(self.options.limits.buffer_bytes);
                self.flow(
                    reader,
                    Box::pin(file),
                    transaction,
                    Some((path, target, *mode)),
                    span,
                )
            }
            (Input::Port { process, port }, Output::File { location, mode }) => {
                let owner = self
                    .programs
                    .get(process)
                    .ok_or_else(|| {
                        ExecutionError::new("program scalar has no preceding launch", span)
                    })?
                    .transaction;
                let cancel = self.owner_token(owner);
                let bytes = self
                    .programs
                    .get_mut(process)
                    .unwrap()
                    .read_scalar(*port, &cancel)
                    .await?;
                let snapshot = self.capture_bytes(&bytes, span).await?;
                let target = self.output_path(instruction, location, span)?;
                let transaction = self.connection_transaction(*process, current, span)?;
                let instruction = self.fresh_instruction()?;
                let work = self.work()?;
                self.event(Event::Node(work.key, NodeEvent::Admitted))?;
                self.event(Event::Node(work.key, NodeEvent::ServiceReady))?;
                self.deferred.push(Deferred {
                    work,
                    origin: self.active_origin.clone(),
                    instruction,
                    transaction,
                    source: snapshot,
                    target,
                    mode: *mode,
                    span,
                    basename: None,
                });
                if !self
                    .programs
                    .values()
                    .any(|program| program.transaction == transaction && program.is_running())
                {
                    self.quiesce(transaction).await?;
                    // A replayed scalar is a complete atomic statement, not a
                    // pending version for the next sibling to snapshot around.
                    self.finish_implicit(transaction)?;
                }
                Ok(())
            }
            (_, Output::File { location, mode }) => {
                let work = self.work()?;
                if self.namespaces.contains_key(&work.key) {
                    let source = file_source(source, span)?;
                    let snapshot = match &source {
                        Source::Literal(bytes) => {
                            self.capture_bytes(bytes.as_bytes(), span).await?
                        }
                        Source::Local(path) => self
                            .engine_mut()?
                            .manager
                            .read_committed_file(path)
                            .map_err(|cause| {
                                ExecutionError::caused(
                                    "cannot pin the committed namespace source",
                                    span,
                                    cause,
                                )
                            })?,
                        _ => {
                            return Err(ExecutionError::new(
                                "namespace merging requires one prepared ordinary file or literal source",
                                span,
                            ));
                        }
                    };
                    self.event(Event::Node(work.key, NodeEvent::ServiceReady))?;
                    return self.publish_namespace(&work, &snapshot, &source, span);
                }
                if let Some(id) = current {
                    self.quiesce(id).await?;
                }
                let transaction = self.transaction(current, span)?;
                let operation = self.fresh_instruction()?;
                self.engine_mut()?
                    .manager
                    .start_statement(transaction)
                    .map_err(|cause| {
                        ExecutionError::caused("cannot begin statement view", span, cause)
                    })?;
                let raw = self.output_path(instruction, location, span)?;
                let transfer = Transfer {
                    source: self.selected_source(instruction, source, transaction, span)?,
                    destination: self.selected_path(transaction, &raw, span)?,
                    mode: *mode,
                    pattern: match source {
                        Input::File { pattern, .. } => pattern.clone(),
                        _ => None,
                    },
                    span,
                };
                self.engine_mut()?
                    .transfer(operation, &transfer, transaction)?;
                if current.is_none() {
                    self.engine_mut()?.finish(transaction, span)?;
                    self.implicit.remove(&transaction);
                }
                Ok(())
            }
        }
    }

    async fn remote_transfer(
        &mut self,
        source: &Input,
        spec: endpoint::Spec,
        current: Option<TransactionId>,
        span: Span,
    ) -> Result<()> {
        if current.is_some() || matches!(source, Input::Port { .. }) {
            return Err(ExecutionError::new(
                "remote endpoint publication requires a finite top-level source",
                span,
            ));
        }
        let transaction = self.transaction(None, span)?;
        self.engine_mut()?
            .manager
            .start_statement(transaction)
            .map_err(|cause| {
                ExecutionError::caused("cannot begin remote source statement", span, cause)
            })?;
        let operation = self.fresh_instruction()?;
        let (snapshot, basename) = match file_source(source, span)? {
            Source::Local(path) => {
                let snapshot = self
                    .engine_mut()?
                    .manager
                    .read_snapshot(transaction, &path)
                    .map_err(|cause| {
                        ExecutionError::caused("cannot capture remote source version", span, cause)
                    })?;
                (snapshot, path.file_name().map(OsString::from))
            }
            Source::Remote { location, basename } => {
                let engine = self.engine_mut()?;
                (
                    domain::imported(
                        &location,
                        span,
                        &engine.imports,
                        &engine.assets,
                        &engine.options,
                        operation,
                    )?,
                    basename,
                )
            }
            Source::Literal(text) => (self.capture_bytes(text.as_bytes(), span).await?, None),
            Source::Captured { snapshot, basename } => (snapshot, basename),
        };
        if snapshot.is_missing() {
            return Err(ExecutionError::new(
                "remote transfer source does not exist",
                span,
            ));
        }
        let materialized = if let Some(Entry::File { artifact, .. }) =
            snapshot.entries().get(Path::new(""))
        {
            // The snapshot pins an immutable artifact through the remote join;
            // copying it into another private file would add no isolation.
            artifact.path().to_owned()
        } else {
            let path = self
                .engine_mut()?
                .imports
                .join(format!("endpoint-source-{}", operation.0));
            let mut publication = self
                .engine_mut()?
                .assets
                .prepare(&snapshot, &path)
                .map_err(|cause| {
                    ExecutionError::caused("cannot prepare immutable remote source", span, cause)
                })?;
            publication.publish().map_err(|cause| {
                ExecutionError::caused("cannot materialize immutable remote source", span, cause)
            })?;
            publication.finalize().map_err(|cause| {
                ExecutionError::caused("cannot finalize immutable remote source", span, cause)
            })?;
            path
        };
        // Finish the read-only source transaction before publishing externally.
        // No local private program view may remain live across this boundary.
        self.engine_mut()?.finish(transaction, span)?;
        self.implicit.remove(&transaction);
        let report = spec
            .run(
                materialized,
                basename,
                self.options.limits.buffer_bytes,
                self.options.cancel.clone(),
            )
            .await?;
        let engine = self.engine_mut()?;
        engine.report.assignments += 1;
        engine.report.assigned_files += report.transfer.copied_files;
        engine.report.assigned_bytes = engine
            .report
            .assigned_bytes
            .saturating_add(report.transfer.transferred_bytes);
        if let Some(issue) = report.cleanup {
            engine.report.cleanup.push(issue);
        }
        Ok(())
    }

    async fn physical_transfer(
        &mut self,
        instruction: InstructionId,
        input: &Input,
        spec: physical::Spec,
        current: Option<TransactionId>,
    ) -> Result<()> {
        if current.is_some() {
            return Err(ExecutionError::new(
                "external stream effects need a transactional peer inside a named transaction",
                spec.span,
            ));
        }
        let span = spec.span;
        let transaction = match input {
            Input::Port { process, .. } => self.connection_transaction(*process, None, span)?,
            _ if spec.managed.is_some() => {
                // The destination statement starts at EOF, not when an external
                // peer connects. Independent streams must not pin an obsolete
                // parent namespace while waiting for unrelated peers to close.
                let id = self.work()?.transaction;
                if !self.namespaces.contains_key(&self.work()?.key) {
                    self.engine_mut()?
                        .manager
                        .begin(id, Isolation::ReadCommitted)
                        .map_err(|cause| {
                            ExecutionError::caused("cannot begin physical output", span, cause)
                        })?;
                }
                self.implicit.insert(id);
                id
            }
            _ => self.transaction(None, span)?,
        };
        let source = match input {
            Input::Literal(text) => physical::Source::Literal(text.clone()),
            Input::Port { .. } => {
                physical::Source::Reader(self.reader(instruction, input, transaction, span).await?)
            }
            Input::File { .. } => {
                let endpoint = spec
                    .source
                    .as_ref()
                    .expect("file source has a resolved endpoint");
                if spec.kinds.source == crate::plan::FileClass::Stream
                    || endpoint.backend != Backend::Local
                {
                    physical::Source::Endpoint(endpoint.clone())
                } else {
                    let path = PathBuf::from(&endpoint.path);
                    let manager = &mut self.engine_mut()?.manager;
                    let snapshot = manager.read_committed_file(&path).map_err(|cause| {
                        ExecutionError::caused(
                            "cannot read immutable physical flow input",
                            span,
                            cause,
                        )
                    })?;
                    physical::Source::Snapshot(snapshot)
                }
            }
        };
        let operation = self.fresh_instruction()?;
        let output = spec.managed.as_ref().map(|(target, mode)| {
            (
                self.engine
                    .as_ref()
                    .expect("initialized engine")
                    .imports
                    .join(format!("physical-output-{}", operation.0)),
                target.clone(),
                *mode,
            )
        });
        let capture = output.as_ref().map(|(path, _, _)| path.clone());
        let basename = spec.basename.clone();
        let lease = self.flow_lease(instruction, span)?;
        let buffer = self.options.limits.buffer_bytes;
        let cancel = self.owner_token(transaction);
        let managed = output.as_ref().map(|(_, target, _)| target.clone());
        let task = lease.task("physical.transfer");
        let work = self.work()?;
        let flow_work = work.clone();
        let ready = Arc::new(AtomicBool::new(false));
        let notice = TerminalNotice {
            sender: self.terminal_tx.clone(),
            event: Terminal::Flow(work.key),
            ready: ready.clone(),
        };
        let handle = tokio::spawn(task.run(async move {
            let _notice = notice;
            let _lease = lease;
            spec.run(source, capture, buffer, cancel).await?;
            Ok(FlowResult {
                work,
                origin: None,
                instruction: operation,
                transaction,
                span,
                output,
                basename,
            })
        }));
        self.flows.push(Flow {
            work: flow_work,
            ready,
            origin: self.active_origin.clone(),
            transaction,
            handle,
            managed,
        });
        self.event(Event::Node(self.work()?.key, NodeEvent::Admitted))?;
        Ok(())
    }

    fn physical_input(
        &mut self,
        instruction: InstructionId,
        source: Location,
        sink: Writer,
        transaction: TransactionId,
        span: Span,
        consumer_closed: CancellationToken,
    ) -> Result<()> {
        let lease = self.flow_lease(instruction, span)?;
        let fifo = self
            .native_fifo_pins
            .get(&hir::effects::EndpointKey {
                instruction,
                role: hir::effects::EndpointRole::Source,
            })
            .cloned();
        let buffer = self.options.limits.buffer_bytes;
        let cancel = self.owner_token(transaction);
        let task = lease.task("physical.input");
        let work = self.work()?;
        let flow_work = work.clone();
        let ready = Arc::new(AtomicBool::new(false));
        let notice = TerminalNotice {
            sender: self.terminal_tx.clone(),
            event: Terminal::Flow(work.key),
            ready: ready.clone(),
        };
        let handle = tokio::spawn(task.run(async move {
            let _notice = notice;
            let _lease = lease;
            physical::to_writer(source, sink, buffer, cancel, span, consumer_closed, fifo).await?;
            Ok(FlowResult {
                work,
                origin: None,
                instruction,
                transaction,
                span,
                output: None,
                basename: None,
            })
        }));
        self.flows.push(Flow {
            work: flow_work,
            ready,
            origin: self
                .active_origin
                .as_ref()
                .map(|origin| origin.for_operand(false)),
            transaction,
            handle,
            managed: None,
        });
        self.event(Event::Node(self.work()?.key, NodeEvent::Admitted))?;
        Ok(())
    }

    async fn reap_physical(&mut self) -> Result<()> {
        loop {
            let before = self.progress;
            self.reap_ready().await?;
            if before == self.progress {
                return Ok(());
            }
        }
    }

    async fn reap_ready(&mut self) -> Result<()> {
        self.drain_terminals()?;
        let owners = self
            .implicit
            .iter()
            .copied()
            .chain(self.flows.iter().map(|flow| flow.transaction))
            .chain(
                self.deferred
                    .iter()
                    .map(|assignment| assignment.transaction),
            )
            .chain(
                self.programs
                    .values()
                    .filter(|program| program.is_running())
                    .map(|program| program.transaction),
            )
            .collect::<BTreeSet<_>>();
        let ready = owners
            .into_iter()
            .filter(|id| self.is_ready(*id))
            .collect::<Vec<_>>();
        for transaction in ready {
            self.quiesce(transaction).await.map_err(|mut error| {
                error.transaction = Some(transaction);
                error
            })?;
            if self
                .deferred
                .iter()
                .any(|assignment| assignment.transaction == transaction)
            {
                continue;
            }
            self.finish_implicit(transaction)?;
        }
        Ok(())
    }

    fn finish_implicit(&mut self, transaction: TransactionId) -> Result<()> {
        if !self.implicit.contains(&transaction)
            || self
                .deferred
                .iter()
                .any(|assignment| assignment.transaction == transaction)
        {
            return Ok(());
        }
        if !self.closed_owners.remove(&transaction) {
            self.engine_mut()?
                .finish(transaction, Span::default())
                .map_err(|mut error| {
                    error.transaction = Some(transaction);
                    error
                })?;
        }
        self.owner_committed(transaction)?;
        self.implicit.remove(&transaction);
        Ok(())
    }

    fn transaction(&mut self, current: Option<TransactionId>, span: Span) -> Result<TransactionId> {
        if let Some(id) = current {
            return Ok(id);
        }
        let id = self.work()?.transaction;
        if self.implicit.contains(&id) {
            return Ok(id);
        }
        self.engine_mut()?
            .manager
            .begin(id, Isolation::ReadCommitted)
            .map_err(|cause| {
                ExecutionError::caused("cannot begin atomic statement", span, cause)
            })?;
        self.implicit.insert(id);
        Ok(id)
    }

    fn connection_transaction(
        &mut self,
        process: ProcessId,
        current: Option<TransactionId>,
        span: Span,
    ) -> Result<TransactionId> {
        let program = self
            .programs
            .get(&process)
            .ok_or_else(|| ExecutionError::new("program member has no preceding launch", span))?;
        if let Some(id) = current {
            if program.is_running() && id != program.transaction {
                return Err(ExecutionError::new(
                    "a live program stream cannot cross transaction contexts",
                    span,
                ));
            }
            return Ok(id);
        }
        if self.implicit.contains(&program.transaction) {
            return Ok(program.transaction);
        }
        self.transaction(None, span)
    }

    async fn reader(
        &mut self,
        instruction: InstructionId,
        source: &Input,
        transaction: TransactionId,
        span: Span,
    ) -> Result<Reader> {
        if let Input::Port { process, port } = source {
            self.process_data(*process, *port, span)?;
        }
        match source {
            Input::Port {
                process,
                port: ReadPort::Stdout | ReadPort::Stderr,
            } => {
                let Input::Port { port, .. } = source else {
                    unreachable!()
                };
                self.programs
                    .get_mut(process)
                    .ok_or_else(|| {
                        ExecutionError::new("program stream has no preceding launch", span)
                    })?
                    .take_reader(*port)
            }
            Input::Port { process, port } => {
                let owner = self
                    .programs
                    .get(process)
                    .ok_or_else(|| {
                        ExecutionError::new("program scalar has no preceding launch", span)
                    })?
                    .transaction;
                let cancel = self.owner_token(owner);
                let bytes = self
                    .programs
                    .get_mut(process)
                    .unwrap()
                    .read_scalar(*port, &cancel)
                    .await?;
                Ok(Box::pin(std::io::Cursor::new(bytes)))
            }
            Input::Literal(bytes) => Ok(Box::pin(std::io::Cursor::new(bytes.as_bytes().to_vec()))),
            Input::File { location, pattern } => {
                if pattern.is_some() {
                    return Err(ExecutionError::new(
                        "stdin needs one byte source, not a directory selector",
                        span,
                    ));
                }
                let path = if self.host_unstable(instruction, hir::effects::EndpointRole::Source) {
                    let raw = domain::deferred_local(location, true, span)?;
                    self.selected_path(transaction, &raw, span)?
                } else {
                    local(location, true, span)?
                };
                let snapshot = self
                    .engine_mut()?
                    .manager
                    .read_snapshot(transaction, &path)
                    .map_err(|cause| {
                        ExecutionError::caused("cannot read program input version", span, cause)
                    })?;
                let Some(Entry::File { artifact, .. }) = snapshot.entries().get(Path::new(""))
                else {
                    return Err(ExecutionError::new(
                        "program stdin requires a regular non-directory source",
                        span,
                    ));
                };
                let mut file = tokio::fs::File::open(artifact.path())
                    .await
                    .map_err(|cause| {
                        ExecutionError::caused("cannot open immutable program input", span, cause)
                    })?;
                file.set_max_buf_size(self.options.limits.buffer_bytes);
                let lease = self.fresh_instruction()?;
                let engine = self.engine_mut()?;
                let logical = if matches!(location.backend, Backend::Local) {
                    location.clone()
                } else {
                    Location {
                        backend: Backend::Local,
                        path: path.to_string_lossy().into_owned(),
                    }
                };
                let proof = crate::hir::graph::VersionReadProof::pinned_file(
                    logical,
                    "manager-immutable-snapshot",
                    format!(
                        "{}:txn-{}:epoch-{}:lease-{}",
                        engine.workspace.display(),
                        transaction.0,
                        engine.manager.epoch().0,
                        lease.0
                    ),
                )
                .map_err(|message| ExecutionError::new(message, span))?;
                self.pins.insert(
                    instruction,
                    PinnedInput {
                        proof,
                        _snapshot: snapshot.clone(),
                    },
                );
                Ok(Box::pin(OwnedReader {
                    file,
                    _snapshot: snapshot,
                }))
            }
        }
    }

    fn flow(
        &mut self,
        mut source: Reader,
        mut destination: Writer,
        transaction: TransactionId,
        output: Option<(PathBuf, PathBuf, DestinationMode)>,
        span: Span,
    ) -> Result<()> {
        let work = self.work()?;
        let lease = self.flow_lease(work.key.instruction, span)?;
        let instruction = self.fresh_instruction()?;
        let size = self.options.limits.buffer_bytes;
        let cancel = self.owner_token(transaction);
        let managed = output.as_ref().map(|(_, target, _)| target.clone());
        let task = lease.task("program.port");
        let origin = self.active_origin.clone();
        let flow_work = work.clone();
        let ready = Arc::new(AtomicBool::new(false));
        let notice = TerminalNotice {
            sender: self.terminal_tx.clone(),
            event: Terminal::Flow(work.key),
            ready: ready.clone(),
        };
        let handle = tokio::spawn(task.run(async move {
            let _notice = notice;
            let _lease = lease;
            let mut bytes = vec![0; size];
            loop {
                let count = tokio::select! { () = cancel.cancelled() => return Err(ExecutionError::cancelled("program flow cancelled", span)), read = source.read(&mut bytes) => read.map_err(|cause| ExecutionError::caused("cannot read program flow", span, cause).with_operand(origin.as_ref(), false))? };
                if count == 0 {
                    break;
                }
                tokio::select! { () = cancel.cancelled() => return Err(ExecutionError::cancelled("program flow cancelled", span)), written = destination.write_all(&bytes[..count]) => written.map_err(|cause| ExecutionError::caused("cannot write program flow", span, cause).with_operand(origin.as_ref(), true))? };
            }
            destination.shutdown().await.map_err(|cause| {
                ExecutionError::caused("cannot close program flow", span, cause).with_operand(origin.as_ref(), true)
            })?;
            Ok(FlowResult {
                work,
                origin: None,
                instruction,
                transaction,
                span,
                output,
                basename: None,
            })
        }));
        self.flows.push(Flow {
            work: flow_work,
            ready,
            origin: self.active_origin.clone(),
            transaction,
            handle,
            managed,
        });
        self.event(Event::Node(self.work()?.key, NodeEvent::Admitted))?;
        Ok(())
    }

    async fn quiesce(&mut self, transaction: TransactionId) -> Result<()> {
        let owner_cancel = self.owner_token(transaction);
        for program in self
            .programs
            .values_mut()
            .filter(|program| program.transaction == transaction)
        {
            program.close_unused_stdin();
        }
        let mut completed = Vec::new();
        let (selected, remaining): (Vec<_>, Vec<_>) = std::mem::take(&mut self.flows)
            .into_iter()
            .partition(|flow| flow.transaction == transaction);
        self.flows = remaining;
        let mut selected = selected.into_iter();
        while let Some(mut flow) = selected.next() {
            let result = tokio::select! {
                () = owner_cancel.cancelled() => {
                    let origin = flow.origin.clone();
                    self.flows.push(flow);
                    self.flows.extend(selected);
                    return Err(ExecutionError::cancelled("execution cancelled while draining program IO", Span::default()).with_origin(origin.as_ref()));
                },
                joined = &mut flow.handle => joined
                    .map_err(|cause| ExecutionError::caused("program flow worker failed", Span::default(), cause))
                    .and_then(|result| result),
            };
            match result {
                Ok(mut result) => {
                    result.origin = flow.origin;
                    completed.push(result);
                }
                Err(failure) => {
                    self.flows.extend(selected);
                    return Err(failure.with_origin(flow.origin.as_ref()));
                }
            }
        }
        let ids = self
            .programs
            .values()
            .filter(|program| program.transaction == transaction && program.is_running())
            .map(|program| program.id)
            .collect::<Vec<_>>();
        for id in &ids {
            let program = self.programs.get_mut(id).unwrap();
            program
                .join(&owner_cancel)
                .await
                .map_err(|error| error.with_origin(program.origin.as_ref()))?;
        }
        let mut imported = BTreeSet::new();
        for id in &ids {
            let engine = self.engine.as_mut().expect("initialized engine");
            let program = self.programs.get_mut(id).unwrap();
            let cohort = program.cohort;
            let origin = program.origin.clone();
            let receipt = program
                .seal(&engine.assets, &owner_cancel)
                .await
                .map_err(|error| error.with_origin(origin.as_ref()))?;
            if let Some(receipt) = receipt
                && imported.insert(cohort)
            {
                let roots = receipt.roots.clone();
                for (path, snapshot) in roots {
                    let before = program
                        .baseline
                        .iter()
                        .find(|(logical, _)| logical == &path)
                        .map(|(_, snapshot)| snapshot)
                        .ok_or_else(|| {
                            ExecutionError::new(
                                "sealed program root has no initial version",
                                program.span,
                            )
                            .with_origin(origin.as_ref())
                        })?;
                    engine
                        .manager
                        .stage_view_delta(transaction, &path, before, &snapshot)
                        .map_err(|cause| {
                            ExecutionError::caused(
                                "cannot stage sealed program changes",
                                program.span,
                                cause,
                            )
                            .with_origin(origin.as_ref())
                        })?;
                }
            }
        }
        for id in ids {
            self.event(Event::Process(id, ProcessEvent::Exited))?;
            self.event(Event::Process(id, ProcessEvent::Imported))?;
            if let Some(cohort) = self.process_cohorts.get(&id).cloned() {
                self.event(Event::Cohort(cohort, Visibility::Imported))?;
            }
            if let Some(key) = self.process_nodes.get(&id).copied() {
                self.event(Event::Node(key, NodeEvent::LogicalComplete))?;
                self.pending_nodes
                    .entry(transaction)
                    .or_default()
                    .insert(key);
            }
        }
        for flow in completed {
            self.event(Event::Node(flow.work.key, NodeEvent::ServiceReady))?;
            if let Some((path, target, mode)) = flow.output {
                let snapshot = self.engine_mut()?.assets.capture(&path).map_err(|cause| {
                    ExecutionError::caused(
                        "cannot capture completed program output",
                        flow.span,
                        cause,
                    )
                    .with_origin(flow.origin.as_ref())
                })?;
                self.deferred.push(Deferred {
                    work: flow.work,
                    origin: flow.origin.clone(),
                    instruction: flow.instruction,
                    transaction: flow.transaction,
                    source: snapshot,
                    target,
                    mode,
                    span: flow.span,
                    basename: flow.basename,
                });
            } else {
                self.logical_complete(&flow.work)?;
            }
        }
        let mut remaining = Vec::new();
        let mut assignments = Vec::new();
        for assignment in std::mem::take(&mut self.deferred) {
            if assignment.transaction == transaction {
                assignments.push(assignment);
            } else {
                remaining.push(assignment);
            }
        }
        self.deferred = remaining;
        assignments.sort_by_key(|assignment| assignment.work.order);
        for assignment in assignments {
            if !self.gate(&assignment.work, Phase::Publish)? {
                self.deferred.push(assignment);
                continue;
            }
            if self.namespaces.contains_key(&assignment.work.key) {
                let source = Source::Captured {
                    snapshot: assignment.source.clone(),
                    basename: assignment.basename.clone(),
                };
                self.publish_namespace(
                    &assignment.work,
                    &assignment.source,
                    &source,
                    assignment.span,
                )?;
                continue;
            }
            self.engine_mut()?
                .manager
                .start_statement(transaction)
                .map_err(|cause| {
                    ExecutionError::caused(
                        "cannot start the gated assignment publication",
                        assignment.span,
                        cause,
                    )
                })?;
            let transfer = Transfer {
                source: Source::Captured {
                    snapshot: assignment.source,
                    basename: assignment.basename,
                },
                destination: self.selected_path(
                    transaction,
                    &assignment.target,
                    assignment.span,
                )?,
                mode: assignment.mode,
                pattern: None,
                span: assignment.span,
            };
            self.engine_mut()?
                .transfer(assignment.instruction, &transfer, transaction)
                .map_err(|error| error.with_operand(assignment.origin.as_ref(), true))?;
            self.logical_complete(&assignment.work)?;
        }
        Ok(())
    }

    async fn await_gate(&mut self, work: &WorkNode, phase: Phase) -> Result<()> {
        loop {
            self.reap_physical().await?;
            if self.gate(work, phase)? {
                return Ok(());
            }
            if !self.has_work() {
                return Err(ExecutionError::new(
                    "an execution prerequisite has no remaining producer",
                    work.plan
                        .origin(work.key.instruction)
                        .map_or(Span::default(), |origin| origin.site.span),
                ));
            }
            self.wait_event().await?;
        }
    }

    async fn settle_region(&mut self, work: &WorkNode) -> Result<()> {
        loop {
            self.quiesce(work.transaction).await?;
            if self.gate(work, Phase::RegionCommit)? {
                return Ok(());
            }
            self.reap_physical().await?;
            if self.gate(work, Phase::RegionCommit)? {
                return Ok(());
            }
            if !self.has_work() {
                return Err(ExecutionError::new(
                    "transaction commit has an incomplete child with no remaining producer",
                    Span::default(),
                ));
            }
            self.wait_event().await?;
        }
    }

    async fn capture_bytes(&mut self, bytes: &[u8], span: Span) -> Result<Snapshot> {
        let operation = self.fresh_instruction()?;
        let path = self
            .engine_mut()?
            .imports
            .join(format!("scalar-{}", operation.0));
        tokio::fs::write(&path, bytes).await.map_err(|cause| {
            ExecutionError::caused("cannot capture program scalar", span, cause)
        })?;
        self.engine_mut()?.assets.capture(&path).map_err(|cause| {
            ExecutionError::caused("cannot capture program scalar version", span, cause)
        })
    }

    async fn cancel_transaction(
        &mut self,
        transaction: TransactionId,
        failure: &mut ExecutionError,
    ) -> bool {
        let mut joined = true;
        let works = self
            .flows
            .iter()
            .filter(|flow| flow.transaction == transaction)
            .map(|flow| flow.work.clone())
            .chain(
                self.deferred
                    .iter()
                    .filter(|assignment| assignment.transaction == transaction)
                    .map(|assignment| assignment.work.clone()),
            )
            .collect::<Vec<_>>();
        for work in works {
            self.ledger
                .cancel_node(work.key, CancelReason::Stopped, None);
        }
        for key in self
            .pending_nodes
            .get(&transaction)
            .into_iter()
            .flatten()
            .copied()
            .collect::<Vec<_>>()
        {
            self.ledger.cancel_node(key, CancelReason::Stopped, None);
        }
        for process in self
            .programs
            .values()
            .filter(|program| program.transaction == transaction)
            .map(|program| program.id)
            .collect::<Vec<_>>()
        {
            self.ledger
                .cancel_process(process, CancelReason::Stopped, None);
            if let Some(key) = self.process_nodes.get(&process).copied() {
                self.ledger.cancel_node(key, CancelReason::Stopped, None);
            }
            if let Some(cohort) = self.process_cohorts.get(&process).cloned() {
                self.ledger
                    .cancel_cohort(cohort, CancelReason::Stopped, None);
            }
        }
        self.owner_token(transaction).cancel();
        for program in self
            .programs
            .values_mut()
            .filter(|program| program.transaction == transaction)
        {
            if let Err(error) = program.cancel().await {
                joined = false;
                Self::retain_cleanup_failure(failure, error);
            }
        }
        let mut remaining = Vec::new();
        for flow in std::mem::take(&mut self.flows) {
            if flow.transaction == transaction {
                match flow.handle.await {
                    Err(error) => {
                        joined = false;
                        Self::retain_cleanup_failure(
                            failure,
                            ExecutionError::caused(
                                "owned flow did not join cleanly",
                                Span::default(),
                                error,
                            ),
                        );
                    }
                    Ok(Err(error)) if !error.is_cancelled() => {
                        joined = false;
                        Self::retain_cleanup_failure(failure, error);
                    }
                    _ => {}
                }
            } else {
                remaining.push(flow);
            }
        }
        self.flows = remaining;
        self.deferred
            .retain(|assignment| assignment.transaction != transaction);
        let keys = self
            .namespaces
            .keys()
            .filter(|key| self.node_transactions.get(key) == Some(&transaction))
            .copied()
            .collect::<Vec<_>>();
        for key in keys {
            if let Err(error) = self.release_namespace(key) {
                joined = false;
                Self::retain_cleanup_failure(failure, error);
            }
        }
        self.pending_nodes.remove(&transaction);
        self.owner_tokens.remove(&transaction);
        joined
    }
}

struct OwnedReader {
    file: tokio::fs::File,
    _snapshot: Snapshot,
}

impl tokio::io::AsyncRead for OwnedReader {
    fn poll_read(
        mut self: std::pin::Pin<&mut Self>,
        context: &mut std::task::Context<'_>,
        buffer: &mut tokio::io::ReadBuf<'_>,
    ) -> std::task::Poll<std::io::Result<()>> {
        std::pin::Pin::new(&mut self.file).poll_read(context, buffer)
    }
}

fn local(location: &Location, source: bool, span: Span) -> Result<PathBuf> {
    let path = crate::runtime::local_path(location, source)
        .map_err(|message| ExecutionError::new(message, span))?
        .ok_or_else(|| ExecutionError::new("a native local resource is required", span))?;
    domain::normalize(&path, span)
}

fn verify_prepared(program: &crate::preparation::PreparedProgram) -> Result<()> {
    program.verify().map_err(|reports| {
        let mut reports = reports.into_iter();
        let primary = reports.next().unwrap_or_else(|| {
            crate::syntax::Report::new("prepared program verification failed", Span::default())
        });
        let mut failure =
            ExecutionError::new(primary.diagnostic.message.clone(), primary.diagnostic.span);
        failure.report = Box::new(primary);
        failure
            .report
            .help
            .extend(reports.map(|report| report.diagnostic.message));
        failure
    })
}

fn file_source(source: &Input, span: Span) -> Result<Source> {
    match source {
        Input::Literal(text) => Ok(Source::Literal(text.clone())),
        Input::File { location, .. } => match crate::runtime::local_path(location, true)
            .map_err(|message| ExecutionError::new(message, span))?
        {
            Some(path) => Ok(Source::Local(domain::normalize(&path, span)?)),
            None => Ok(Source::Remote {
                location: location.clone(),
                basename: location
                    .path
                    .trim_end_matches('/')
                    .rsplit('/')
                    .next()
                    .map(std::ffi::OsString::from),
            }),
        },
        _ => Err(ExecutionError::new(
            "a finite file source was expected",
            span,
        )),
    }
}

fn endpoint_unstable(
    context: &crate::hir::graph::PlanningContext,
    instruction: InstructionId,
    role: crate::hir::effects::EndpointRole,
) -> bool {
    context
        .host_unstable
        .contains(&crate::hir::effects::EndpointKey { instruction, role })
}

fn transfer_unstable(context: &crate::hir::graph::PlanningContext, id: InstructionId) -> bool {
    use crate::hir::effects::EndpointRole;
    endpoint_unstable(context, id, EndpointRole::Source)
        || endpoint_unstable(context, id, EndpointRole::Destination)
}

fn known_physical(
    source: &Input,
    destination: &Output,
    facts: Option<&hir::effects::OperationFacts>,
) -> bool {
    transfer_locations(source, destination).any(|(location, source)| {
        matches!(location.backend, Backend::Tcp { .. })
            || facts.is_some_and(|facts| if source { facts.source } else { facts.destination } == crate::plan::FileClass::Stream)
    })
}

fn transfer_locations<'a>(
    source: &'a Input,
    destination: &'a Output,
) -> impl Iterator<Item = (&'a Location, bool)> {
    let source = match source {
        Input::File { location, .. } => Some((location, true)),
        _ => None,
    };
    let destination = match destination {
        Output::File { location, .. } => Some((location, false)),
        _ => None,
    };
    source.into_iter().chain(destination)
}

fn live_domain(
    program: &hir::Program,
    options: &Options,
    context: &crate::hir::graph::PlanningContext,
) -> Result<Domain> {
    fn sparse_destinations(
        region: &Region,
        context: &crate::hir::graph::PlanningContext,
        result: &mut BTreeSet<InstructionId>,
    ) {
        use crate::plan::FileClass;
        for node in &region.instructions {
            if let InstructionKind::Transaction { body, .. } = &node.kind {
                sparse_destinations(body, context, result);
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
                continue;
            };
            let Some(facts) = context.operation_facts.get(&node.id) else {
                continue;
            };
            let finite = matches!(source, Input::Literal(_))
                || matches!(source,
                Input::File { location, pattern: None }
                if matches!(location.backend, Backend::Local | Backend::Env | Backend::Path)
                    && facts.source == FileClass::Regular);
            if finite
                && facts.destination == FileClass::Missing
                && matches!(location.backend, Backend::Local | Backend::Env)
                && !transfer_unstable(context, node.id)
            {
                result.insert(node.id);
            }
        }
    }
    fn has_program(region: &Region) -> bool {
        region.instructions.iter().any(|node| match &node.kind {
            InstructionKind::Spawn { .. } => true,
            InstructionKind::Transaction { body, .. } => has_program(body),
            _ => false,
        })
    }
    fn finite(
        region: &Region,
        roots: &mut Vec<PathBuf>,
        context: &crate::hir::graph::PlanningContext,
    ) -> Result<Region> {
        use crate::hir::effects::EndpointRole;
        let mut result = Region::default();
        for node in &region.instructions {
            if let InstructionKind::Transfer {
                source,
                destination,
            } = &node.kind
            {
                let unstable = transfer_unstable(context, node.id);
                if !unstable && let Some(spec) = physical::inspect(source, destination, node.span)?
                {
                    if spec.kinds.source == crate::plan::FileClass::Regular
                        && let Some(location) = spec
                            .source
                            .filter(|location| location.backend == Backend::Local)
                    {
                        roots.push(domain::normalize(Path::new(&location.path), node.span)?);
                    }
                    if let Some((target, _)) = spec.managed {
                        let mut root = target.parent().unwrap_or(Path::new(".")).to_owned();
                        while !root.exists() {
                            root = root
                                .parent()
                                .ok_or_else(|| {
                                    ExecutionError::new("output has no existing parent", node.span)
                                })?
                                .to_owned();
                        }
                        if !physical::has_live_children(&root)? {
                            roots.push(domain::normalize(&root, node.span)?);
                        }
                    }
                    continue;
                }
                if endpoint::inspect(source, destination, node.span)?.is_some() {
                    if let Input::File { location, .. } = source
                        && let Some(path) = crate::runtime::local_path(location, true)
                            .map_err(|message| ExecutionError::new(message, node.span))?
                    {
                        roots.push(
                            if endpoint_unstable(context, node.id, EndpointRole::Source) {
                                domain::deferred_envelope(&path, node.span)?
                            } else {
                                domain::normalize(&path, node.span)?
                            },
                        );
                    }
                    continue;
                }
                if unstable
                    && known_physical(source, destination, context.operation_facts.get(&node.id))
                {
                    for (location, source) in transfer_locations(source, destination) {
                        let facts = context
                            .operation_facts
                            .get(&node.id)
                            .copied()
                            .unwrap_or_default();
                        if if source {
                            facts.source
                        } else {
                            facts.destination
                        } == crate::plan::FileClass::Stream
                        {
                            continue;
                        }
                        if let Some(path) = crate::runtime::local_path(location, source)
                            .map_err(|message| ExecutionError::new(message, node.span))?
                        {
                            roots.push(domain::deferred_envelope(&path, node.span)?);
                        }
                    }
                    continue;
                }
                if let (Input::File { location, .. }, Output::Stdin(_)) = (source, destination)
                    && !endpoint_unstable(context, node.id, EndpointRole::Source)
                    && physical::source_stream(location, node.span)?.is_some()
                {
                    continue;
                }
            }
            match &node.kind {
                InstructionKind::Spawn { cwd, arguments, .. } => {
                    roots.push(domain::normalize(cwd, node.span)?);
                    for argument in arguments {
                        if let hir::Argument::Path(location) = argument {
                            let path = domain::deferred_local(location, true, node.span)?;
                            roots.push(domain::deferred_envelope(&path, node.span)?);
                        }
                    }
                }
                InstructionKind::Transfer {
                    source: Input::Port { .. },
                    destination: Output::File { location, .. },
                } => {
                    if endpoint_unstable(context, node.id, EndpointRole::Destination) {
                        let path = domain::deferred_local(location, false, node.span)?;
                        roots.push(domain::deferred_envelope(&path, node.span)?);
                        continue;
                    }
                    let path = local(location, false, node.span)?;
                    let mut parent = path.parent().unwrap_or(Path::new(".")).to_owned();
                    while !parent.exists() {
                        parent = parent
                            .parent()
                            .ok_or_else(|| {
                                ExecutionError::new("output has no existing parent", node.span)
                            })?
                            .to_owned();
                    }
                    roots.push(parent);
                }
                InstructionKind::Transfer {
                    source: Input::Port { .. },
                    ..
                } => {}
                InstructionKind::Transfer {
                    source: Input::File { location, .. },
                    destination: Output::Stdin(_),
                } => {
                    if endpoint_unstable(context, node.id, EndpointRole::Source) {
                        let path = domain::deferred_local(location, true, node.span)?;
                        roots.push(domain::deferred_envelope(&path, node.span)?);
                        continue;
                    }
                    let path = local(location, true, node.span)?;
                    roots.push(path.parent().unwrap_or(Path::new(".")).to_owned());
                }
                InstructionKind::Transfer {
                    destination: Output::Stdin(_),
                    ..
                } => {}
                InstructionKind::Transaction { transaction, body } => {
                    result.instructions.push(Instruction {
                        id: node.id,
                        span: node.span,
                        expansion: node.expansion.clone(),
                        kind: InstructionKind::Transaction {
                            transaction: *transaction,
                            body: finite(body, roots, context)?,
                        },
                    })
                }
                _ => result.instructions.push(node.clone()),
            }
        }
        Ok(result)
    }
    let mut roots = Vec::new();
    let filtered = hir::Program {
        body: finite(&program.body, &mut roots, context)?,
        transactions: program.transactions.clone(),
    };
    let mut sparse = BTreeSet::new();
    if !has_program(&program.body) {
        sparse_destinations(&filtered.body, context, &mut sparse);
    }
    let mut domain =
        Domain::resolve_enrollment(&filtered, options, &context.host_unstable, &sparse)?;
    domain.native_view = has_program(&program.body);
    domain.roots.extend(roots);
    domain.normalize_coverage();
    Ok(domain)
}

#[cfg(test)]
#[path = "source_tests.rs"]
mod source_tests;

fn port_flow(node: &Instruction) -> bool {
    matches!(
        node.kind,
        InstructionKind::Transfer {
            destination: Output::Stdin(_),
            ..
        } | InstructionKind::Transfer {
            source: Input::Port {
                port: ReadPort::Stdout | ReadPort::Stderr,
                ..
            },
            ..
        }
    )
}

fn fifo_consumers(region: &Region, group: &[InstructionId], result: &mut BTreeSet<ProcessId>) {
    for node in &region.instructions {
        match &node.kind {
            InstructionKind::Transfer {
                destination: Output::Stdin(process),
                ..
            } if group.contains(&node.id) => {
                // Outputs/scalars can replay after exit; a stdin consumer must
                // stay live while its FIFO peers open together.
                result.insert(*process);
            }
            InstructionKind::Transaction { body, .. } => fifo_consumers(body, group, result),
            _ => {}
        }
    }
}
