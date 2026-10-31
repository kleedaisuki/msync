//! The interpreter owns current-block specialization, children, and byte flows.

#[path = "control.rs"]
mod control;

use super::blocks::BlockSchedule;
use super::error::{ExecutionError, Result};
use super::{ExecutionReport, Options, merge};
use super::{flow, ordinary, program};
use crate::hir::graph::{self, PlanningContext};
use crate::hir::{
    self, Input, Instruction, InstructionId, InstructionKind, Output, ProcessId, ReadPort,
};
use crate::syntax::Span;
use std::collections::{BTreeMap, BTreeSet};
use std::path::{Path, PathBuf};
use tokio::task::JoinSet;
use tokio_util::sync::CancellationToken;

struct ActiveFlow {
    instruction: InstructionId,
    submission: u64,
    task: flow::Flow,
    processes: BTreeSet<ProcessId>,
    destination: Option<crate::endpoint::Location>,
    origin: Option<crate::source::InstructionOrigin>,
    _lease: super::scheduler::Lease,
    pins: crate::streams::FifoPins,
    cancel: CancellationToken,
    program_output: bool,
    source_label: Option<String>,
    destination_label: Option<String>,
}

struct Acquisition {
    completion: CancellationToken,
    instruction: InstructionId,
    submission: u64,
    span: Span,
    origin: Option<crate::source::InstructionOrigin>,
    worker: tokio::task::JoinHandle<(Result<flow::Source>, super::scheduler::Lease)>,
}

struct FiniteResult {
    instructions: Vec<InstructionId>,
    result: Result<FiniteOutcome>,
    original: Instruction,
}
enum FiniteOutcome {
    Statement(ordinary::Attempt),
    Fold(ordinary::FoldAttempt),
}
#[cfg(test)]
#[derive(Default)]
struct GateState {
    entered: bool,
    released: bool,
}
#[cfg(test)]
#[derive(Clone, Default)]
struct WorkerGate {
    state: std::sync::Arc<(std::sync::Mutex<GateState>, std::sync::Condvar)>,
    handle: std::sync::Arc<std::sync::Mutex<Option<tokio::task::AbortHandle>>>,
}
#[cfg(test)]
impl WorkerGate {
    fn wait(&self) {
        let (state, changed) = &*self.state;
        let mut state = state.lock().unwrap();
        state.entered = true;
        changed.notify_all();
        drop(changed.wait_while(state, |state| !state.released).unwrap());
    }
    fn release(&self) {
        let (state, changed) = &*self.state;
        state.lock().unwrap().released = true;
        changed.notify_all();
    }
    fn entered(&self) -> bool {
        self.state.0.lock().unwrap().entered
    }
    fn finished(&self) -> bool {
        self.handle
            .lock()
            .unwrap()
            .as_ref()
            .is_some_and(tokio::task::AbortHandle::is_finished)
    }
}
struct Retiring {
    finite: JoinSet<FiniteResult>,
    acquisition: Option<Acquisition>,
    flows: Vec<ActiveFlow>,
    programs: BTreeMap<ProcessId, program::Program>,
    invocation: Option<crate::filesystem::InvocationRoot>,
    registration: Option<crate::filesystem::recovery::Registration>,
    context: Option<crate::telemetry::TraceContext>,
    _leases: Vec<super::scheduler::Lease>,
}
impl Retiring {
    async fn run(mut self) {
        if let Some(mut acquisition) = self.acquisition.take() {
            let result = (&mut acquisition.worker).await;
            match result {
                Ok((Err(error), _lease)) => self.error(&error),
                Err(error) => self.error(&error),
                _ => {}
            }
        }
        while let Some(result) = self.finite.join_next().await {
            match result {
                Err(error) => self.error(&error),
                Ok(FiniteResult {
                    result: Err(error), ..
                }) => self.error(&error),
                Ok(FiniteResult {
                    result:
                        Ok(
                            FiniteOutcome::Statement(ordinary::Attempt::Completed(report))
                            | FiniteOutcome::Fold(ordinary::FoldAttempt::Completed(report)),
                        ),
                    ..
                }) => {
                    for issue in report.cleanup {
                        self.error(issue.cause.as_ref());
                    }
                }
                _ => {}
            }
        }
        for program in self.programs.values_mut() {
            if let Err(error) = program.wait().await
                && let Some(context) = &self.context
            {
                context.record_error("runtime.drop.program", &error);
            }
        }
        for flow in &mut self.flows {
            if let Err(error) = flow.task.join().await
                && let Some(context) = &self.context
            {
                context.record_error("runtime.drop.flow", &error);
            }
        }
        self.programs.clear();
        self.flows.clear();
        if let Some(invocation) = self.invocation.take()
            && let Err(error) = invocation.cleanup()
        {
            self.error(&error);
            return;
        }
        if let Some(registration) = self.registration.take()
            && let Err(error) = registration.finish()
        {
            self.error(&error);
        }
    }
    fn error(&self, error: &(dyn std::error::Error + 'static)) {
        if let Some(context) = &self.context {
            context.record_error("runtime.drop.cleanup", error);
        }
    }
}

/// No snapshots or history are retained across completed statements.
pub struct RuntimeSession {
    options: Options,
    cancel: CancellationToken,
    origins: crate::source::Origins,
    observer: Option<super::progress::Progress>,
    programs: BTreeMap<ProcessId, program::Program>,
    program_instructions: BTreeMap<ProcessId, InstructionId>,
    finite_signals: BTreeMap<tokio::task::Id, CancellationToken>,
    flows: Vec<ActiveFlow>,
    finite: JoinSet<FiniteResult>,
    acquisition: Option<Acquisition>,
    report: ExecutionReport,
    initialized: bool,
    admission: super::scheduler::Admission,
    program_leases: BTreeMap<ProcessId, super::scheduler::Lease>,
    fifo: Option<super::fifo::FifoState>,
    rendezvous: BTreeSet<(InstructionId, InstructionId)>,
    flow_reservations: BTreeMap<InstructionId, super::scheduler::Lease>,
    reserved_groups: BTreeSet<Vec<InstructionId>>,
    submission: u64,
    control_values: BTreeMap<hir::control::SlotId, control::Value>,
    range_patterns: BTreeMap<String, regex::Regex>,
    control_break: Option<Option<control::Value>>,
    next_dynamic_process: usize,
    batch_mode: bool,
    stage_registration: Option<crate::filesystem::recovery::Registration>,
    executor: Option<tokio::runtime::Handle>,
    #[cfg(test)]
    worker_gate: Option<WorkerGate>,
    #[cfg(test)]
    acquisition_gate: Option<WorkerGate>,
}
impl Drop for RuntimeSession {
    fn drop(&mut self) {
        self.cancel.cancel();
        if self.finite.is_empty()
            && self.acquisition.is_none()
            && self.flows.is_empty()
            && self
                .programs
                .values()
                .all(|program| program.receipt().is_some())
        {
            self.programs.clear();
            if let Err(error) = self.close_invocation()
                && let Some(context) = crate::telemetry::context()
            {
                context.record_error("runtime.drop.cleanup", &error);
            }
            return;
        }
        if let Some(executor) = self.executor.as_ref() {
            let retiring = Retiring {
                finite: std::mem::take(&mut self.finite),
                acquisition: self.acquisition.take(),
                flows: std::mem::take(&mut self.flows),
                programs: std::mem::take(&mut self.programs),
                invocation: self.options.shared_invocation.take(),
                registration: self.stage_registration.take(),
                context: crate::telemetry::context(),
                _leases: std::mem::take(&mut self.program_leases)
                    .into_values()
                    .chain(std::mem::take(&mut self.flow_reservations).into_values())
                    .collect(),
            };
            executor.spawn(retiring.run());
        }
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
        if options
            .fallback_scratch_root
            .as_ref()
            .is_some_and(|root| !root.is_absolute())
        {
            return Err(ExecutionError::new(
                "fallback execution scratch root must be absolute",
                Span::default(),
            ));
        }
        Ok(Self {
            options: options.clone(),
            cancel: options.cancel.child_token(),
            origins: Default::default(),
            observer: None,
            programs: BTreeMap::new(),
            program_instructions: BTreeMap::new(),
            finite_signals: BTreeMap::new(),
            flows: Vec::new(),
            finite: JoinSet::new(),
            acquisition: None,
            report: Default::default(),
            initialized: false,
            admission: super::scheduler::Admission::new(&options.limits)?,
            program_leases: BTreeMap::new(),
            fifo: None,
            rendezvous: BTreeSet::new(),
            flow_reservations: BTreeMap::new(),
            reserved_groups: BTreeSet::new(),
            submission: 0,
            control_values: BTreeMap::new(),
            range_patterns: BTreeMap::new(),
            control_break: None,
            next_dynamic_process: usize::MAX,
            batch_mode: false,
            stage_registration: None,
            executor: tokio::runtime::Handle::try_current().ok(),
            #[cfg(test)]
            worker_gate: None,
            #[cfg(test)]
            acquisition_gate: None,
        })
    }
    pub fn set_origins(&mut self, origins: crate::source::Origins) {
        self.origins = origins;
    }
    pub fn set_progress(&mut self, progress: Option<super::progress::Progress>) {
        self.observer = progress;
    }
    pub fn cancellation(&self) -> CancellationToken {
        self.cancel.clone()
    }
    /// Inspect only the identity-verified runtime backing, without cleanup authority.
    pub fn private_root(&self) -> Result<Option<&Path>> {
        let Some(invocation) = self.options.shared_invocation.as_ref() else {
            return Ok(None);
        };
        invocation.verify().map_err(|cause| {
            ExecutionError::caused(
                "runtime-private resource identity changed",
                Span::default(),
                cause,
            )
        })?;
        Ok(Some(invocation.path()))
    }
    pub fn private_stage_record(&self) -> Result<Option<&Path>> {
        self.private_root()?;
        Ok(self
            .stage_registration
            .as_ref()
            .map(|registration| registration.path()))
    }
    pub fn live_processes(&self) -> Vec<ProcessId> {
        self.programs.keys().copied().collect()
    }
    pub fn has_work(&self) -> bool {
        !self.finite.is_empty()
            || self.acquisition.is_some()
            || !self.flows.is_empty()
            || self.programs.values().any(|p| p.receipt().is_none())
    }
    pub fn work_ids(&self) -> BTreeSet<super::WorkId> {
        self.programs
            .iter()
            .filter_map(|(id, p)| p.receipt().is_none().then_some(super::WorkId::Program(*id)))
            .chain(self.flows.iter().map(|flow| super::WorkId::Flow {
                submission: flow.submission,
                instruction: flow.instruction,
            }))
            .chain(
                self.acquisition
                    .iter()
                    .map(|acquisition| super::WorkId::Flow {
                        submission: acquisition.submission,
                        instruction: acquisition.instruction,
                    }),
            )
            .collect()
    }
    /// Maps runtime identities, including fresh loop-local children, to semantic source nodes.
    pub fn work_instructions(&self) -> BTreeMap<super::WorkId, InstructionId> {
        self.work_ids()
            .into_iter()
            .filter_map(|work| {
                let instruction = match work {
                    super::WorkId::Program(process) => *self.program_instructions.get(&process)?,
                    super::WorkId::Flow { instruction, .. } => instruction,
                };
                Some((work, instruction))
            })
            .collect()
    }

    /// Actual program identities connected to retained flows, not static template identities.
    pub fn work_processes(&self) -> BTreeMap<super::WorkId, Vec<ProcessId>> {
        self.flows
            .iter()
            .map(|flow| {
                (
                    super::WorkId::Flow {
                        submission: flow.submission,
                        instruction: flow.instruction,
                    },
                    flow.processes.iter().copied().collect(),
                )
            })
            .collect()
    }

    pub fn take_report(&mut self) -> ExecutionReport {
        std::mem::take(&mut self.report)
    }
    pub fn has_ready_work(&self) -> bool {
        self.flows.iter().any(|f| f.task.is_finished())
            || self
                .programs
                .values()
                .any(|p| p.receipt().is_none() && p.is_finished())
            || self
                .finite_signals
                .values()
                .any(CancellationToken::is_cancelled)
            || self.acquisition.as_ref().is_some_and(|acquisition| {
                acquisition.completion.is_cancelled() || acquisition.worker.is_finished()
            })
    }
    pub fn planning_context(&self) -> PlanningContext {
        PlanningContext {
            live_processes: self.live_processes(),
            ..Default::default()
        }
    }
    pub fn refinement_context(&self) -> PlanningContext {
        self.planning_context()
    }

    pub async fn execute_prepared(
        &mut self,
        prepared: &crate::preparation::PreparedProgram,
    ) -> Result<ExecutionReport> {
        verify(prepared)?;
        self.execute(prepared.semantic_program()).await
    }
    pub async fn submit_prepared(
        &mut self,
        prepared: &crate::preparation::PreparedProgram,
    ) -> Result<ExecutionReport> {
        verify(prepared)?;
        self.submit(prepared.semantic_program()).await
    }
    pub async fn execute(&mut self, program: &hir::Program) -> Result<ExecutionReport> {
        let initial = self.submit_mode(program, true).await?;
        merge(&mut self.report, initial);
        self.wait().await?;
        Ok(self.take_report())
    }
    pub async fn submit(&mut self, program: &hir::Program) -> Result<ExecutionReport> {
        self.submit_mode(program, false).await
    }
    async fn submit_mode(
        &mut self,
        program: &hir::Program,
        batch: bool,
    ) -> Result<ExecutionReport> {
        self.flow_reservations.clear();
        self.reserved_groups.clear();
        self.drain_acquisition().await?;
        if let Err(error) = self.drain_finite().await {
            self.cancel = self.options.cancel.child_token();
            return Err(error);
        }
        self.batch_mode = batch;
        self.submission = self.submission.checked_add(1).ok_or_else(|| {
            ExecutionError::new("runtime submission identity exhausted", Span::default())
        })?;
        self.initialize().await?;
        let result = self.interpret(program).await;
        if let Err(mut error) = result {
            if error.context().category == super::DiagnosticCategory::Capability {
                self.flow_reservations.clear();
                return Err(error);
            }
            self.cancel.cancel();
            if let Err(cleanup) = self.settle_all().await {
                error
                    .report
                    .help
                    .push(format!("while settling already-started work: {cleanup}"));
                error.retained_paths.extend(cleanup.retained_paths);
            }
            self.cancel = self.options.cancel.child_token();
            return Err(error);
        }
        if self.cancel.is_cancelled() {
            self.settle_all().await?;
            self.cancel = self.options.cancel.child_token();
        }
        Ok(self.take_report())
    }
    /// Inspect the roots owned by this session, including after a caller changes cwd.
    pub fn recovery_required(&self) -> bool {
        super::recovery_required(
            &self.options.scratch_root,
            self.options.fallback_scratch_root.as_deref(),
        )
    }

    async fn initialize(&mut self) -> Result<()> {
        if self.initialized && !self.recovery_required() {
            return Ok(());
        }
        self.executor = tokio::runtime::Handle::try_current().ok();
        self.observe(
            super::progress::Phase::Inspect,
            None,
            || Some("recover interrupted statements".into()),
            || None,
        );
        if super::root_may_need_recovery(&self.options.scratch_root) {
            let scratch = self.options.scratch_root.clone();
            crate::runtime::ssh::spawn_blocking(move || {
                crate::filesystem::recovery::recover(&scratch)
            })
            .await
            .map_err(|cause| {
                ExecutionError::caused("statement recovery worker failed", Span::default(), cause)
            })?
            .map_err(|cause| {
                ExecutionError::caused(
                    "cannot recover an interrupted statement before execution",
                    Span::default(),
                    cause,
                )
            })?;
            super::endpoint::recover(&self.options).await?;
        }
        if let Some(fallback) = &self.options.fallback_scratch_root
            && super::root_may_need_recovery(fallback)
        {
            let scratch = fallback.clone();
            crate::runtime::ssh::spawn_blocking(move || {
                crate::filesystem::recovery::recover(&scratch)
            })
            .await
            .map_err(|cause| {
                ExecutionError::caused("fallback recovery worker failed", Span::default(), cause)
            })?
            .map_err(|cause| {
                ExecutionError::caused(
                    "cannot recover fallback runtime state",
                    Span::default(),
                    cause,
                )
            })?;
            let mut options = self.options.clone();
            options.scratch_root = fallback.clone();
            super::endpoint::recover(&options).await?;
        }

        self.initialized = true;
        Ok(())
    }
    async fn interpret(&mut self, program: &hir::Program) -> Result<()> {
        self.interpret_region(&program.body).await
    }

    async fn interpret_region(&mut self, region: &hir::Region) -> Result<()> {
        for block in region.blocks() {
            self.interpret_block(block).await?;
            if self.control_break.is_some() {
                break;
            }
        }
        Ok(())
    }

    fn interpret_block<'a>(&'a mut self, block: hir::BasicBlock<'a>) -> super::trace::Step<'a, ()> {
        let parent = crate::telemetry::context();
        // Construct graph/scheduler state only for an ordinary block, not in every control future.
        let work: super::trace::Step<'a, ()> = match block.instructions {
            [node] if matches!(node.kind, InstructionKind::Control(_)) => {
                Box::pin(self.interpret_control_block(node))
            }
            _ => Box::pin(self.interpret_ordinary_block(block)),
        };
        let Some(parent) = parent else {
            return work;
        };
        let mut span = crate::telemetry::linked_span("interpreter.block", Some(&parent));
        span.metric("block.id", block.id.0 as u64);
        span.metric("instructions", block.instructions.len() as u64);
        if let Some(first) = block.instructions.first() {
            span.metric("first_instruction.id", first.id.0 as u64);
        }
        super::trace::run(span, "interpreter.block", work)
    }

    async fn interpret_control_block(&mut self, node: &Instruction) -> Result<()> {
        self.check(node.span)?;
        self.drain_acquisition().await?;
        self.drain_finite().await?;
        let InstructionKind::Control(operation) = &node.kind else {
            unreachable!("control block selected by its instruction kind")
        };
        let work = self.execute_control(operation, node);
        let result = if crate::telemetry::context().is_some() {
            super::trace::run(
                super::trace::instruction(node),
                "interpreter.instruction",
                work,
            )
            .await
        } else {
            work.await
        };
        result.map_err(|error| error.with_origin(self.origins.get(&node.id)))
    }

    async fn interpret_ordinary_block(&mut self, block: hir::BasicBlock<'_>) -> Result<()> {
        self.check(Span::default())?;
        self.observe(
            super::progress::Phase::Inspect,
            None,
            || {
                Some(format!(
                    "basic block {} ({} instructions)",
                    block.id.0,
                    block.instructions.len()
                ))
            },
            || None,
        );
        // No future block is observed until this block's finite work settles.
        let mut preparation = crate::telemetry::linked_span(
            "interpreter.prepare",
            crate::telemetry::context().as_ref(),
        );
        preparation.metric("block.id", block.id.0 as u64);
        let prepared = crate::telemetry::with_context(preparation.context().as_ref(), || {
            crate::preparation::prepare_block(&block, self.observation_context())
        });
        if preparation.is_recording() {
            preparation.metric("fallback", u64::from(prepared.is_err()));
            if let Err(reports) = &prepared
                && let Some(report) = reports.first()
            {
                preparation.event(
                    "preparation.fallback",
                    &[("reason", &report.report.diagnostic.message)],
                    &[],
                );
            }
        }
        drop(preparation);
        let prepared = prepared.ok();
        let instructions = prepared
            .as_ref()
            .map(|p| p.original.body.instructions.as_slice())
            .unwrap_or(block.instructions);
        let mut context = prepared
            .as_ref()
            .map(|p| p.context.clone())
            .unwrap_or_default();
        context.live_processes = self.live_processes();
        let contextual_block = hir::BasicBlock {
            id: block.id,
            instructions,
        };
        self.fifo = Some(super::fifo::FifoState::inspect(
            contextual_block,
            &mut context,
        )?);
        self.rendezvous = context
            .physical_rendezvous
            .iter()
            .map(|proof| {
                (
                    proof.writer().key().instruction,
                    proof.reader().key().instruction,
                )
            })
            .collect();
        self.reserved_groups.clear();
        let graph = {
            let mut planning = crate::telemetry::span("interpreter.schedule");
            planning.metric("block.id", block.id.0 as u64);
            let graph = graph::build_block_with_context(contextual_block, &context);
            planning.metric("nodes", graph.nodes.len() as u64);
            planning.metric("dependencies", graph.dependencies.len() as u64);
            graph
        };
        let mut schedule = BlockSchedule::new(&graph).map_err(|cause| {
            ExecutionError::caused(
                "invalid basic-block dependency graph",
                Span::default(),
                cause,
            )
        })?;
        let nodes: BTreeMap<_, _> = block
            .instructions
            .iter()
            .map(|node| (node.id, node))
            .collect();
        let positions: BTreeMap<_, _> = block
            .instructions
            .iter()
            .enumerate()
            .map(|(index, node)| (node.id, index))
            .collect();
        let mut deoptimized = BTreeSet::new();
        while !schedule.is_finished() {
            crate::telemetry::event(
                "scheduler.ready",
                &[],
                &[
                    ("ready", schedule.ready_count() as u64),
                    ("finite.running", self.finite.len() as u64),
                    ("flows.running", self.flows.len() as u64),
                ],
            );
            let mut reserved: Vec<_> = self
                .flow_reservations
                .keys()
                .filter_map(|id| schedule.ready_position(*id).map(|position| (position, *id)))
                .collect();
            reserved.sort_unstable_by_key(|&(position, _)| position);
            let mut reserved = reserved.into_iter();
            let mut after = None;
            let mut admitted = false;
            loop {
                if self.finite.len() >= self.copy_capacity() {
                    break;
                }
                let id = if let Some((_, id)) = reserved.next() {
                    if schedule.ready_position(id).is_none() {
                        continue;
                    }
                    id
                } else {
                    let Some((position, id)) = schedule.next_ready(after) else {
                        break;
                    };
                    after = Some(position);
                    if self.flow_reservations.contains_key(&id) {
                        continue;
                    }
                    id
                };
                let node = nodes[&id];
                if !self.rendezvous_ready(node, &schedule) {
                    continue;
                }
                let cohort_started = self.reserve_rendezvous(node)?;
                if !deoptimized.contains(&id)
                    && !self.physical_flow(node)
                    && let Some(run) =
                        hir::rewrite::overwrite_run(&block.instructions[positions[&id]..])
                {
                    let ids: Vec<_> = run.iter().map(|node| node.id).collect();
                    if schedule.group_ready(&ids)
                        && self.admission.copy_availability()
                            == super::scheduler::Availability::Ready
                    {
                        for node in run {
                            self.join_conflicting_flows(node).await?;
                        }
                        schedule.admit_group(&ids).map_err(schedule_error)?;
                        let mut span = super::trace::instruction(&run[0]);
                        span.metric("instructions", run.len() as u64);
                        super::trace::run(span, "interpreter.instruction", async {
                            self.start_fold(run.to_vec(), ids)
                        })
                        .await?;
                        admitted = true;
                        continue;
                    }
                }
                if matches!(
                    node.kind,
                    InstructionKind::Transfer {
                        source: Input::File { .. } | Input::Literal(_),
                        destination: Output::File { .. }
                    }
                ) && !self.physical_flow(node)
                    && self.admission.copy_availability() != super::scheduler::Availability::Ready
                {
                    if !self.finite.is_empty() {
                        break;
                    }
                    return Err(admission_error(
                        self.admission
                            .copy(node.span)
                            .err()
                            .expect("copy is unavailable"),
                    ));
                }
                self.check(node.span)?;
                schedule.admit(id).map_err(schedule_error)?;
                admitted = true;
                let is_flow = matches!(node.kind, InstructionKind::Transfer { .. })
                    && self.physical_flow(node);
                let dispatch = super::trace::instruction(node);
                super::trace::run(dispatch, "interpreter.instruction", async {
                    match &node.kind {
                        InstructionKind::Transfer { .. } if is_flow => {
                            if self.start_flow(node).await? {
                                schedule.captured_input(id).map_err(schedule_error)?;
                            }
                        }
                        InstructionKind::Transfer { .. } => {
                            self.join_conflicting_flows(node).await?;
                            self.start_finite(node.clone(), vec![id])?;
                        }
                        InstructionKind::Spawn {
                            process,
                            executable,
                            arguments,
                            cwd,
                        } => {
                            self.admit_program(*process, node, executable, arguments, cwd)
                                .await?;
                            schedule.complete(id).map_err(schedule_error)?;
                        }
                        InstructionKind::Await { process } => {
                            self.await_process(*process).await?;
                            if process.is_none() {
                                self.join_all_flows().await?;
                            }
                            schedule.complete(id).map_err(schedule_error)?;
                        }
                        InstructionKind::Control(_) => {
                            unreachable!("control is an isolated interpretation block")
                        }
                        InstructionKind::Barrier { .. } => {
                            schedule.complete(id).map_err(schedule_error)?;
                        }
                    }
                    Ok(())
                })
                .await?;
                if is_flow && cohort_started {
                    break;
                }
            }
            if !self.finite.is_empty() {
                self.finish_one(&mut schedule, &mut deoptimized).await?;
            } else if schedule.all_admitted() {
                break;
            } else if !admitted && !schedule.is_finished() {
                if let Some(index) = self.flows.iter().position(|flow| {
                    flow.submission == self.submission && schedule.is_running(flow.instruction)
                }) {
                    let id = self.flows[index].instruction;
                    self.join_flow(index).await?;
                    schedule.complete(id).map_err(schedule_error)?;
                    continue;
                }
                return Err(ExecutionError::new(
                    "basic-block scheduler has no ready work",
                    Span::default(),
                ));
            }
        }
        self.poll_ready().await?;
        Ok(())
    }
    fn copy_capacity(&self) -> usize {
        self.options
            .limits
            .jobs
            .min(self.options.limits.memory_bytes / self.options.limits.buffer_bytes)
            .max(1)
    }
    fn physical_flow(&self, node: &Instruction) -> bool {
        let InstructionKind::Transfer {
            source,
            destination,
        } = &node.kind
        else {
            return false;
        };
        if matches!(source, Input::Port { .. }) || matches!(destination, Output::Stdin(_)) {
            return true;
        }
        let pins = self
            .fifo
            .as_ref()
            .map(|fifo| fifo.pins(node.id))
            .unwrap_or_default();
        if pins.source.is_some() || pins.destination.is_some() {
            return true;
        }
        matches!(source, Input::File { location, .. } if flow::native_stream(location, true))
            || matches!(destination, Output::File { location, .. } if flow::native_stream(location, false))
    }
    fn observation_context(&self) -> PlanningContext {
        PlanningContext {
            live_processes: self
                .programs
                .iter()
                .filter_map(|(id, p)| p.is_running().then_some(*id))
                .collect(),
            ..Default::default()
        }
    }
    fn check(&self, span: Span) -> Result<()> {
        if self.cancel.is_cancelled() {
            Err(ExecutionError::cancelled(
                "execution cancelled; no later statement was started",
                span,
            ))
        } else {
            Ok(())
        }
    }
    fn reserve_rendezvous(&mut self, node: &Instruction) -> Result<bool> {
        let Some(group) = self
            .fifo
            .as_ref()
            .and_then(|fifo| fifo.groups().iter().find(|group| group.contains(&node.id)))
            .cloned()
        else {
            return Ok(false);
        };
        if self.reserved_groups.contains(&group) {
            return Ok(false);
        }
        self.admission
            .feasible(super::scheduler::Demand::flows(group.len()), node.span)
            .map_err(|mut error| {
                error.report.diagnostic.message = format!(
                    "FIFO rendezvous must co-start {} flows: {}",
                    group.len(),
                    error.report.diagnostic.message
                );
                admission_error(error)
            })?;
        let mut leases = Vec::new();
        for id in &group {
            leases.push((
                *id,
                self.admission.flow(node.span).map_err(admission_error)?,
            ));
        }
        self.flow_reservations.extend(leases);
        self.reserved_groups.insert(group);
        Ok(true)
    }
    fn rendezvous_ready(&self, node: &Instruction, schedule: &BlockSchedule) -> bool {
        self.fifo
            .as_ref()
            .and_then(|fifo| fifo.groups().iter().find(|group| group.contains(&node.id)))
            .is_none_or(|group| {
                self.reserved_groups.contains(group) || schedule.coadmission_ready(group)
            })
    }
    fn start_fold(&mut self, run: Vec<Instruction>, ids: Vec<InstructionId>) -> Result<()> {
        self.ensure_invocation()?;
        let lease = self.admission.copy(run[0].span).map_err(admission_error)?;
        let mut options = self.options.clone();
        options.cancel = self.cancel.clone();
        let progress = self.observer.clone();
        let origin = self
            .origins
            .get(&run.last().expect("nonempty group").id)
            .cloned();
        let original = run[0].clone();
        let trace = crate::telemetry::context();
        #[cfg(test)]
        let gate = self.worker_gate.take();
        #[cfg(test)]
        let observed = gate.clone();
        let completion = CancellationToken::new();
        let completed = completion.clone().drop_guard();
        let handle = self
            .finite
            .spawn_blocking(crate::runtime::ssh::blocking(move || {
                let _completed = completed;
                #[cfg(test)]
                if let Some(gate) = gate {
                    gate.wait();
                }
                crate::telemetry::with_context(trace.as_ref(), || {
                    let _lease = lease;
                    let mut owner = ordinary::Ordinary::new(&options);
                    owner.set_progress(progress);
                    owner.set_origin(origin.clone());
                    let mut result = owner
                        .try_fold_run(&run)
                        .map_err(|error| error.with_origin(origin.as_ref()));
                    let mut cleanup = owner.close();
                    if matches!(&result, Ok(ordinary::FoldAttempt::Deoptimized))
                        && !cleanup.is_empty()
                    {
                        result = Err(cleanup_failure(std::mem::take(&mut cleanup), original.span));
                    }
                    match &mut result {
                        Ok(ordinary::FoldAttempt::Completed(report)) => {
                            report.cleanup.extend(cleanup)
                        }
                        Err(error) => {
                            for issue in cleanup {
                                error.retained_paths.extend(issue.retained_paths);
                                error.report.help.push(format!("cleanup: {}", issue.cause));
                            }
                        }
                        _ => {}
                    }
                    FiniteResult {
                        instructions: ids,
                        result: result.map(FiniteOutcome::Fold),
                        original,
                    }
                })
            }));
        #[cfg(test)]
        if let Some(gate) = observed {
            *gate.handle.lock().unwrap() = Some(handle.clone());
        }
        self.finite_signals.insert(handle.id(), completion);
        drop(handle);
        Ok(())
    }
    fn start_finite(&mut self, node: Instruction, ids: Vec<InstructionId>) -> Result<()> {
        self.ensure_invocation()?;
        let lease = self.admission.copy(node.span).map_err(admission_error)?;
        let mut options = self.options.clone();
        options.cancel = self.cancel.clone();
        let progress = self.observer.clone();
        let origin = self.origins.get(&node.id).cloned();
        let trace = crate::telemetry::context();
        if matches!(&node.kind,InstructionKind::Transfer {destination:Output::File {location,..},..} if matches!(location.backend,crate::endpoint::Backend::Wsl|crate::endpoint::Backend::Ssh{..}))
        {
            let completion = CancellationToken::new();
            let completed = completion.clone().drop_guard();
            let handle = self
                .finite
                .spawn(crate::runtime::ssh::scope_current(async move {
                    let _completed = completed;
                    let task = lease.task("finite-remote-publication");
                    let _lease = lease;
                    let result = task
                        .run(super::endpoint::execute(&node, &options, origin.as_ref()))
                        .await;
                    FiniteResult {
                        instructions: ids,
                        result: result.map(|report| {
                            FiniteOutcome::Statement(report.map_or(
                                ordinary::Attempt::Unsupported,
                                ordinary::Attempt::Completed,
                            ))
                        }),
                        original: node,
                    }
                }));
            self.finite_signals.insert(handle.id(), completion);
            return Ok(());
        }
        #[cfg(test)]
        let gate = self.worker_gate.take();
        #[cfg(test)]
        let observed = gate.clone();
        let completion = CancellationToken::new();
        let completed = completion.clone().drop_guard();
        let handle = self
            .finite
            .spawn_blocking(crate::runtime::ssh::blocking(move || {
                let _completed = completed;
                #[cfg(test)]
                if let Some(gate) = gate {
                    gate.wait();
                }
                crate::telemetry::with_context(trace.as_ref(), || {
                    let _lease = lease;
                    let mut owner = ordinary::Ordinary::new(&options);
                    owner.set_progress(progress);
                    owner.set_origin(origin.clone());
                    let mut result = owner
                        .execute(&node)
                        .map_err(|error| error.with_origin(origin.as_ref()));
                    let mut cleanup = owner.close();
                    if matches!(&result, Ok(ordinary::Attempt::Unsupported)) && !cleanup.is_empty()
                    {
                        result = Err(cleanup_failure(std::mem::take(&mut cleanup), node.span));
                    }
                    match &mut result {
                        Ok(ordinary::Attempt::Completed(report)) => report.cleanup.extend(cleanup),
                        Err(error) => {
                            for issue in cleanup {
                                error.retained_paths.extend(issue.retained_paths);
                                error.report.help.push(format!("cleanup: {}", issue.cause));
                            }
                        }
                        Ok(ordinary::Attempt::Unsupported) => {}
                    }
                    FiniteResult {
                        instructions: ids,
                        result: result.map(FiniteOutcome::Statement),
                        original: node,
                    }
                })
            }));
        #[cfg(test)]
        if let Some(gate) = observed {
            *gate.handle.lock().unwrap() = Some(handle.clone());
        }
        self.finite_signals.insert(handle.id(), completion);
        drop(handle);
        Ok(())
    }
    async fn finish_one(
        &mut self,
        schedule: &mut BlockSchedule,
        deoptimized: &mut BTreeSet<InstructionId>,
    ) -> Result<()> {
        let mut wait =
            crate::telemetry::linked_span("runtime.wait", crate::telemetry::context().as_ref());
        wait.attribute("wait.kind", "finite_capacity");
        wait.metric("finite.running", self.finite.len() as u64);
        let finished = super::trace::run(wait, "runtime.wait", async {
            let result = self
                .finite
                .join_next_with_id()
                .await
                .expect("a finite worker is retained");
            let id = match &result {
                Ok((id, _)) => *id,
                Err(cause) => cause.id(),
            };
            self.finite_signals.remove(&id);
            result.map(|(_, result)| result).map_err(|cause| {
                ExecutionError::caused("statement worker failed", Span::default(), cause)
            })
        })
        .await?;
        match finished.result? {
            FiniteOutcome::Statement(ordinary::Attempt::Completed(report))
            | FiniteOutcome::Fold(ordinary::FoldAttempt::Completed(report)) => {
                merge(&mut self.report, report)
            }
            FiniteOutcome::Statement(ordinary::Attempt::Unsupported) => {
                if self.start_flow(&finished.original).await? {
                    schedule
                        .captured_input(finished.original.id)
                        .map_err(schedule_error)?;
                }
                return Ok(());
            }
            FiniteOutcome::Fold(ordinary::FoldAttempt::Deoptimized) => {
                schedule
                    .retry_group(&finished.instructions)
                    .map_err(schedule_error)?;
                deoptimized.extend(finished.instructions);
                return Ok(());
            }
        }
        schedule
            .complete_group(&finished.instructions)
            .map_err(schedule_error)?;
        Ok(())
    }
    async fn admit_program(
        &mut self,
        id: ProcessId,
        node: &Instruction,
        executable: &crate::endpoint::Location,
        arguments: &[hir::Argument],
        cwd: &Path,
    ) -> Result<()> {
        self.ensure_invocation()?;
        if self.programs.contains_key(&id) {
            return Err(ExecutionError::new(
                "program identity is already instantiated",
                node.span,
            ));
        }
        let lease = self.admission.program(node.span).map_err(admission_error)?;
        let mut config = crate::process::ProcessConfig::new(
            self.options
                .shared_invocation
                .as_ref()
                .expect("runtime invocation")
                .path(),
        );
        config.buffer_bytes = self.options.limits.buffer_bytes;
        config.max_output_bytes = self.options.max_output_bytes;
        self.observe(
            super::progress::Phase::Launch,
            Some(node.id),
            || Some(executable.to_string()),
            || None,
        );
        let task = lease.task("external-program-launch");
        let launched = task
            .run(program::Program::launch(program::Launch {
                span: node.span,
                origin: self.origins.get(&node.id).cloned(),
                executable,
                arguments,
                cwd,
                config,
            }))
            .await
            .map_err(|error| {
                let context = super::DiagnosticContext {
                    phase: super::DiagnosticPhase::Execute,
                    category: super::DiagnosticCategory::Operation,
                    location: super::DiagnosticLocation::Primary,
                    action: "launch external program",
                    backend: None,
                    instruction: Some(node.id),
                };
                error.with_context(context)
            })?;
        self.programs.insert(id, launched);
        self.program_instructions.insert(id, node.id);
        self.program_leases.insert(id, lease);
        Ok(())
    }
    async fn start_flow(&mut self, node: &Instruction) -> Result<bool> {
        flow::validate_native_transfer(node)
            .map_err(|error| error.with_operand(self.origins.get(&node.id), false))?;
        self.ensure_invocation()?;
        let InstructionKind::Transfer {
            source,
            destination,
        } = &node.kind
        else {
            unreachable!()
        };
        self.join_conflicting_flows(node).await?;
        let lease = match self.flow_reservations.remove(&node.id) {
            Some(lease) => lease,
            None => self.flow_lease(node.span).await?,
        };
        let source_label = self.observer.as_ref().map(|_| input_label(source));
        let destination_label = self.observer.as_ref().map(|_| output_label(destination));
        self.observe(
            super::progress::Phase::Acquire,
            Some(node.id),
            || source_label.clone(),
            || destination_label.clone(),
        );
        let mut processes = BTreeSet::new();
        let (source, lease) = match source {
            Input::File {
                location,
                pattern: None,
            } => self.acquire_source(location, node, lease).await?,
            Input::File {
                pattern: Some(_), ..
            } => {
                return Err(ExecutionError::new(
                    "a selector cannot read a physical byte stream",
                    node.span,
                ));
            }
            Input::Literal(text) => (flow::Source::Literal(text.clone()), lease),
            Input::Port { process, port } => {
                processes.insert(*process);
                let p = self.programs.get_mut(process).ok_or_else(|| {
                    ExecutionError::new("program has not been launched", node.span)
                })?;
                let source = if matches!(port, ReadPort::Stdout | ReadPort::Stderr) {
                    flow::Source::Reader(p.take_reader(*port)?)
                } else {
                    flow::Source::Reader(p.scalar_reader(*port)?)
                };
                (source, lease)
            }
        };
        let captured = matches!(&source, flow::Source::Captured(_));
        let target = match destination {
            Output::File { location, .. } => Some(resource_location(location, false, node.span)?),
            _ => None,
        };
        let destination = match destination {
            Output::File { location, mode } => flow::Destination::Endpoint {
                location: location.clone(),
                mode: *mode,
            },
            Output::Stdin(process) => {
                processes.insert(*process);
                let p = self.programs.get_mut(process).ok_or_else(|| {
                    ExecutionError::new("program has not been launched", node.span)
                })?;
                let closed = p.exit_signal();
                flow::Destination::Writer {
                    writer: p.take_stdin()?,
                    closed,
                }
            }
        };
        let pins = self
            .fifo
            .as_ref()
            .map(|fifo| fifo.pins(node.id))
            .unwrap_or_default();
        let program_output = match &node.kind {
            InstructionKind::Transfer {
                source: Input::Port { .. },
                destination: Output::File { location, .. },
            } => drain_output(location, node.span)?,
            _ => false,
        };
        let cancel = self.cancel.child_token();
        let task = crate::telemetry::with_context(lease.context(), || {
            flow::Flow::start_with_pins(
                source,
                destination,
                self.options.limits.buffer_bytes,
                cancel.clone(),
                node.span,
                pins.clone(),
            )
        })?;
        self.flows.push(ActiveFlow {
            instruction: node.id,
            submission: self.submission,
            task,
            processes,
            destination: target,
            origin: self.origins.get(&node.id).cloned(),
            _lease: lease,
            pins,
            cancel,
            program_output,
            source_label,
            destination_label,
        });
        Ok(captured)
    }
    async fn acquire_source(
        &mut self,
        location: &crate::endpoint::Location,
        node: &Instruction,
        lease: super::scheduler::Lease,
    ) -> Result<(flow::Source, super::scheduler::Lease)> {
        let invocation = self
            .options
            .shared_invocation
            .as_ref()
            .expect("runtime invocation")
            .clone();
        let location = location.clone();
        let span = node.span;
        #[cfg(test)]
        let gate = self.acquisition_gate.take();
        #[cfg(test)]
        let observed = gate.clone();
        let completion = CancellationToken::new();
        let completed = completion.clone().drop_guard();
        let worker = crate::runtime::ssh::spawn_blocking(move || {
            let _completed = completed;
            let source = crate::telemetry::with_context(lease.context(), || {
                flow::Source::capture(location, invocation.path(), span)
            });
            #[cfg(test)]
            if let Some(gate) = gate {
                gate.wait();
            }
            // Started blocking work cannot be aborted. Its lease and backing
            // must outlive a dropped submit future, not just its local await.
            (source, lease)
        });
        #[cfg(test)]
        if let Some(gate) = observed {
            *gate.handle.lock().unwrap() = Some(worker.abort_handle());
        }
        self.acquisition = Some(Acquisition {
            completion,
            instruction: node.id,
            submission: self.submission,
            span,
            origin: self.origins.get(&node.id).cloned(),
            worker,
        });
        self.finish_acquisition().await
    }
    async fn finish_acquisition(&mut self) -> Result<(flow::Source, super::scheduler::Lease)> {
        let result = (&mut self
            .acquisition
            .as_mut()
            .expect("retained source acquisition")
            .worker)
            .await;
        let acquisition = self.acquisition.take().expect("joined source acquisition");
        let (source, lease) = result.map_err(|cause| {
            ExecutionError::caused(
                "flow-source acquisition worker failed",
                acquisition.span,
                cause,
            )
            .with_origin(acquisition.origin.as_ref())
        })?;
        source
            .map(|source| (source, lease))
            .map_err(|error| error.with_origin(acquisition.origin.as_ref()))
    }
    async fn drain_acquisition(&mut self) -> Result<()> {
        if self.acquisition.is_some() {
            // An abandoned submission has not admitted a destination yet.
            // Join and retire its capture rather than replaying its instruction.
            drop(self.finish_acquisition().await?);
        }
        Ok(())
    }
    async fn join_flow(&mut self, index: usize) -> Result<()> {
        self.observe(
            super::progress::Phase::Wait,
            Some(self.flows[index].instruction),
            || {
                self.flows[index]
                    .source_label
                    .clone()
                    .or_else(|| Some(format!("flow {}", self.flows[index].instruction.0)))
            },
            || self.flows[index].destination_label.clone(),
        );
        let active = &mut self.flows[index];
        let requested = self.cancel.is_cancelled() || active.cancel.is_cancelled();
        let mut wait =
            crate::telemetry::linked_span("runtime.wait", crate::telemetry::context().as_ref());
        wait.attribute("wait.kind", "flow_completion");
        wait.metric("instruction.id", active.instruction.0 as u64);
        let result = super::trace::run(wait, "runtime.wait", active.task.join())
            .await
            .map_err(|error| error.with_origin(active.origin.as_ref()));
        self.flows.remove(index);
        let result = result?;
        self.report.assignments += 1;
        self.report.assigned_bytes = self.report.assigned_bytes.saturating_add(result.bytes);
        if result.end == crate::io::FlowEnd::Cancelled && !requested {
            return Err(ExecutionError::new(
                "byte flow was cancelled",
                Span::default(),
            ));
        }
        Ok(())
    }
    async fn flow_lease(&mut self, span: Span) -> Result<super::scheduler::Lease> {
        let demand = super::scheduler::Demand::flows(1);
        self.admission
            .feasible(demand, span)
            .map_err(admission_error)?;
        while self.admission.availability(demand) == super::scheduler::Availability::Busy {
            let pending = self.flows.iter().position(|flow| flow.processes.is_empty());
            if !self.batch_mode || pending.is_none() {
                return Err(admission_error(ExecutionError::new("background flow capacity is occupied",span).help("Use /wait to drain existing work or /stop to stop streams and drain programs; alternatively increase --max-streams/--memory-limit or reduce --buffer-size.")));
            }
            self.join_flow(pending.expect("a queueable independent flow"))
                .await?;
        }
        self.admission.flow(span).map_err(admission_error)
    }
    async fn join_all_flows(&mut self) -> Result<()> {
        let mut failure = None;
        while !self.flows.is_empty() {
            record_failure(&mut failure, self.join_flow(0).await);
        }
        failure.map_or(Ok(()), Err)
    }
    async fn join_conflicting_flows(&mut self, node: &Instruction) -> Result<()> {
        let InstructionKind::Transfer {
            source,
            destination,
        } = &node.kind
        else {
            return Ok(());
        };
        let raw_locations = [
            match source {
                Input::File { location, .. } => Some(location),
                _ => None,
            },
            match destination {
                Output::File { location, .. } => Some(location),
                _ => None,
            },
        ];
        let locations = raw_locations
            .into_iter()
            .flatten()
            .map(|location| resource_location(location, true, node.span))
            .collect::<Result<Vec<_>>>()?;
        let mut index = 0;
        let current_pins = self
            .fifo
            .as_ref()
            .map(|fifo| fifo.pins(node.id))
            .unwrap_or_default();
        while index < self.flows.len() {
            if current_pins
                .source
                .as_ref()
                .is_some_and(|pin| self.flows[index].pins.destination.as_ref() == Some(pin))
            {
                index += 1;
                continue;
            }
            if self.flows[index].submission == self.submission
                && self
                    .rendezvous
                    .contains(&(self.flows[index].instruction, node.id))
            {
                index += 1;
                continue;
            }
            let overlaps = self.flows[index]
                .destination
                .as_ref()
                .is_some_and(|target| locations.iter().any(|location| overlaps(target, location)));
            if overlaps {
                self.join_flow(index).await?;
            } else {
                index += 1;
            }
        }
        Ok(())
    }
    async fn await_process(&mut self, id: Option<ProcessId>) -> Result<()> {
        let ids: Vec<_> = id.map(|id| vec![id]).unwrap_or_else(|| {
            self.programs
                .iter()
                .filter_map(|(id, program)| program.receipt().is_none().then_some(*id))
                .collect()
        });
        let selected: BTreeSet<_> = id
            .map(|id| BTreeSet::from([id]))
            .unwrap_or_else(|| self.programs.keys().copied().collect());
        for id in &ids {
            self.programs
                .get_mut(id)
                .ok_or_else(|| {
                    ExecutionError::new(
                        "await refers to a program not launched in this session",
                        Span::default(),
                    )
                })?
                .close_unused_stdin();
        }
        // Flow tasks run independently while native children are being joined.
        let mut failure = None;
        for id in ids {
            self.observe(
                super::progress::Phase::Wait,
                None,
                || {
                    Some(format!(
                        "program {} (PID {}): natural exit and output drain",
                        id.0, self.programs[&id].pid
                    ))
                },
                || None,
            );
            let mut wait =
                crate::telemetry::linked_span("runtime.wait", crate::telemetry::context().as_ref());
            wait.attribute("wait.kind", "program_completion");
            wait.metric("process.id", id.0 as u64);
            record_failure(
                &mut failure,
                super::trace::run(
                    wait,
                    "runtime.wait",
                    self.programs
                        .get_mut(&id)
                        .expect("validated program")
                        .wait(),
                )
                .await,
            );
            self.program_leases.remove(&id);
        }
        let mut index = 0;
        while index < self.flows.len() {
            if self.flows[index]
                .processes
                .iter()
                .any(|id| selected.contains(id))
            {
                record_failure(&mut failure, self.join_flow(index).await);
            } else {
                index += 1;
            }
        }
        failure.map_or(Ok(()), Err)
    }
    /// Wait for any retained completion without consuming it or acquiring other work.
    pub async fn wait_ready(&self) {
        use std::future::{Future, poll_fn};
        use std::task::Poll;

        let mut activity =
            crate::telemetry::linked_span("runtime.wait", crate::telemetry::context().as_ref());
        activity.attribute("wait.kind", "activity_readiness");
        activity.attribute("wait.scope", "subscription");
        activity.expect_completion();
        let signals = self
            .flows
            .iter()
            .map(|flow| flow.task.completion_signal())
            .chain(
                self.programs
                    .values()
                    .filter(|program| program.receipt().is_none())
                    .map(program::Program::completion_signal),
            )
            .chain(
                self.acquisition
                    .iter()
                    .map(|acquisition| acquisition.completion.clone()),
            )
            .chain(self.finite_signals.values().cloned());
        let mut waits: Vec<_> = signals
            .map(|signal| Box::pin(signal.cancelled_owned()))
            .collect();
        poll_fn(|cx| {
            if self.has_ready_work() {
                return Poll::Ready(());
            }
            for wait in &mut waits {
                if wait.as_mut().poll(cx).is_ready() {
                    return Poll::Ready(());
                }
            }
            Poll::Pending
        })
        .await;
        activity.finish_result(&Ok::<(), ExecutionError>(()));
    }

    pub async fn poll_ready(&mut self) -> Result<()> {
        let mut failure = None;
        while self
            .finite_signals
            .values()
            .any(CancellationToken::is_cancelled)
        {
            let result = self
                .finite
                .join_next_with_id()
                .await
                .expect("completion belongs to retained finite work");
            record_failure(&mut failure, self.retire_finite(result));
        }
        if let Some(error) = failure {
            return Err(error);
        }
        if self.acquisition.as_ref().is_some_and(|acquisition| {
            acquisition.completion.is_cancelled() || acquisition.worker.is_finished()
        }) {
            self.drain_acquisition().await?;
        }
        let mut index = 0;
        while index < self.flows.len() {
            if self.flows[index].task.is_finished() {
                self.join_flow(index).await?;
            } else {
                index += 1;
            }
        }
        for (id, program) in &mut self.programs {
            if program.receipt().is_none() && program.is_finished() {
                self.program_leases.remove(id);
                program.wait().await?;
            }
        }
        Ok(())
    }
    pub async fn wait(&mut self) -> Result<()> {
        let result = self.settle_all().await;
        if self.cancel.is_cancelled() {
            self.cancel = self.options.cancel.child_token();
        }
        result
    }
    pub async fn cancel_active(&mut self) -> Result<()> {
        self.cancel.cancel();
        let result = self.settle_all().await;
        self.cancel = self.options.cancel.child_token();
        result
    }
    /// Explicitly destructive escape hatch; ordinary cancellation never uses it.
    pub async fn force_stop_active(&mut self) -> Result<()> {
        self.cancel.cancel();
        let mut failure = None;
        for program in self.programs.values_mut() {
            record_failure(&mut failure, program.force_stop().await);
        }
        record_failure(&mut failure, self.settle_all().await);
        self.program_leases.clear();
        self.cancel = self.options.cancel.child_token();
        failure.map_or(Ok(()), Err)
    }
    pub async fn shutdown(&mut self) -> Result<()> {
        for flow in &self.flows {
            if !flow.program_output {
                flow.cancel.cancel();
            }
        }
        let result = self.settle_all().await;
        self.programs.clear();
        self.fifo.take();
        self.flow_reservations.clear();
        if let Err(cleanup) = self.close_invocation() {
            return match result {
                Err(mut error) => {
                    error.report.help.push(format!("cleanup: {cleanup}"));
                    error.retained_paths.extend(cleanup.retained_paths);
                    Err(error)
                }
                Ok(()) => Err(cleanup),
            };
        }
        result
    }
    fn close_invocation(&mut self) -> Result<()> {
        let Some(invocation) = self.options.shared_invocation.as_ref() else {
            return Ok(());
        };
        invocation.cleanup().map_err(|cause| {
            let mut error = ExecutionError::caused(
                "cannot clean runtime-private resources",
                Span::default(),
                cause,
            );
            error.retained_paths.push(invocation.path().to_owned());
            error.with_context(super::DiagnosticContext {
                phase: super::DiagnosticPhase::Cleanup,
                category: super::DiagnosticCategory::Cleanup,
                location: super::DiagnosticLocation::None,
                action: "retire runtime-private resources",
                backend: None,
                instruction: None,
            })
        })?;
        self.options.shared_invocation.take();
        if let Some(registration) = self.stage_registration.take() {
            registration.finish().map_err(|cause| {
                ExecutionError::caused(
                    "cannot retire runtime recovery registration",
                    Span::default(),
                    cause,
                )
            })?;
        }
        Ok(())
    }
    fn ensure_invocation(&mut self) -> Result<()> {
        if self.options.shared_invocation.is_none() {
            let registry =
                match crate::filesystem::recovery::Registry::new(&self.options.scratch_root) {
                    Err(cause)
                        if cause.kind() == std::io::ErrorKind::PermissionDenied
                            && self.options.fallback_scratch_root.is_some() =>
                    {
                        self.options.scratch_root = self
                            .options
                            .fallback_scratch_root
                            .take()
                            .expect("configured fallback");
                        crate::filesystem::recovery::Registry::new(&self.options.scratch_root)
                    }
                    result => result,
                }
                .map_err(|cause| {
                    ExecutionError::caused(
                        "cannot open runtime recovery registry",
                        Span::default(),
                        cause,
                    )
                })?;
            let lease = registry.enter().map_err(|cause| {
                ExecutionError::caused(
                    "cannot enter runtime recovery registry",
                    Span::default(),
                    cause,
                )
            })?;
            let invocation = crate::filesystem::InvocationRoot::create(&self.options.scratch_root)
                .map_err(|cause| {
                    ExecutionError::caused(
                        "cannot allocate runtime-private resources",
                        Span::default(),
                        cause,
                    )
                })?;
            let registration = lease.register_stage(&self.options.scratch_root, invocation.path());
            match registration {
                Ok(registration) => {
                    self.stage_registration = Some(registration);
                    self.options.shared_invocation = Some(invocation);
                }
                Err(cause) => {
                    let mut error = ExecutionError::caused(
                        "cannot register runtime-private resources before use",
                        Span::default(),
                        cause,
                    );
                    if let Err(cleanup) = invocation.cleanup() {
                        error.retained_paths.push(invocation.path().to_owned());
                        error.report.help.push(format!("cleanup: {cleanup}"));
                    }
                    return Err(error);
                }
            }
        }
        Ok(())
    }
    fn observe(
        &self,
        phase: super::progress::Phase,
        instruction: Option<InstructionId>,
        source: impl FnOnce() -> Option<String>,
        destination: impl FnOnce() -> Option<String>,
    ) {
        let Some(observer) = self.observer.as_ref() else {
            return;
        };
        observer.update(super::progress::Event {
            phase,
            instruction,
            source: source(),
            destination: destination(),
            ..Default::default()
        });
    }
    async fn settle_all(&mut self) -> Result<()> {
        let mut failure = None;
        self.flow_reservations.clear();
        self.reserved_groups.clear();
        record_failure(&mut failure, self.drain_acquisition().await);
        record_failure(&mut failure, self.drain_finite().await);
        record_failure(&mut failure, self.await_process(None).await);
        record_failure(&mut failure, self.join_all_flows().await);
        failure.map_or(Ok(()), Err)
    }
    async fn drain_finite(&mut self) -> Result<()> {
        let mut failure = None;
        while let Some(result) = self.finite.join_next_with_id().await {
            record_failure(&mut failure, self.retire_finite(result));
        }
        failure.map_or(Ok(()), Err)
    }

    fn retire_finite(
        &mut self,
        result: std::result::Result<(tokio::task::Id, FiniteResult), tokio::task::JoinError>,
    ) -> Result<()> {
        let id = match &result {
            Ok((id, _)) => *id,
            Err(cause) => cause.id(),
        };
        self.finite_signals.remove(&id);
        match result {
            Ok((
                _,
                FiniteResult {
                    result:
                        Ok(
                            FiniteOutcome::Statement(ordinary::Attempt::Completed(report))
                            | FiniteOutcome::Fold(ordinary::FoldAttempt::Completed(report)),
                        ),
                    ..
                },
            )) => merge(&mut self.report, report),
            Ok((
                _,
                FiniteResult {
                    result: Err(error), ..
                },
            )) => return Err(error),
            Err(cause) => {
                return Err(ExecutionError::caused(
                    "statement worker failed",
                    Span::default(),
                    cause,
                ));
            }
            _ => {}
        }
        Ok(())
    }
}
fn record_failure<T>(first: &mut Option<ExecutionError>, result: Result<T>) {
    let Err(mut error) = result else {
        return;
    };
    let Some(primary) = first.as_mut() else {
        *first = Some(error);
        return;
    };
    primary
        .report
        .help
        .push(format!("while joining remaining work: {error}"));
    primary.retained_paths.append(&mut error.retained_paths);
    primary.compensation.append(&mut error.compensation);
}

fn schedule_error(cause: super::blocks::ScheduleError) -> ExecutionError {
    ExecutionError::caused("invalid runtime schedule", Span::default(), cause)
}
fn admission_error(error: ExecutionError) -> ExecutionError {
    let context = super::DiagnosticContext {
        phase: super::DiagnosticPhase::Evaluate,
        category: super::DiagnosticCategory::Capability,
        location: super::DiagnosticLocation::Context,
        action: "admit execution resources",
        backend: None,
        instruction: None,
    };
    error.with_context(context)
}
fn cleanup_failure(issues: Vec<super::CleanupIssue>, span: Span) -> ExecutionError {
    let mut error = ExecutionError::new("private statement resources could not be retired", span);
    for issue in issues {
        error.retained_paths.extend(issue.retained_paths);
        error.report.help.push(format!("cleanup: {}", issue.cause));
    }
    error.with_context(super::DiagnosticContext {
        phase: super::DiagnosticPhase::Cleanup,
        category: super::DiagnosticCategory::Cleanup,
        location: super::DiagnosticLocation::Context,
        action: "retire private statement resources",
        backend: None,
        instruction: None,
    })
}
fn verify(prepared: &crate::preparation::PreparedProgram) -> Result<()> {
    prepared.verify().map_err(|reports| {
        ExecutionError::new(
            reports
                .first()
                .map(|r| r.diagnostic.message.clone())
                .unwrap_or_else(|| "invalid prepared IR".into()),
            Span::default(),
        )
    })
}
fn overlaps(a: &crate::endpoint::Location, b: &crate::endpoint::Location) -> bool {
    a.backend == b.backend
        && (PathBuf::from(&a.path).starts_with(&b.path)
            || PathBuf::from(&b.path).starts_with(&a.path))
}
fn resource_location(
    location: &crate::endpoint::Location,
    source: bool,
    span: Span,
) -> Result<crate::endpoint::Location> {
    #[cfg(windows)]
    if crate::streams::is_named_pipe_path(&location.path) {
        return Ok(location.clone());
    }
    let Some(path) = crate::runtime::local_path(location, source)
        .map_err(|message| ExecutionError::new(message, span))?
    else {
        return Ok(location.clone());
    };
    #[cfg(windows)]
    if crate::streams::is_named_pipe_path(&path.to_string_lossy()) {
        return Ok(crate::endpoint::Location {
            backend: crate::endpoint::Backend::Local,
            path: path.to_string_lossy().into_owned(),
        });
    }
    let path = super::domain::normalize(&path, span)?;
    Ok(crate::endpoint::Location {
        backend: crate::endpoint::Backend::Local,
        path: path.to_string_lossy().into_owned(),
    })
}
fn drain_output(location: &crate::endpoint::Location, span: Span) -> Result<bool> {
    if matches!(location.backend, crate::endpoint::Backend::Tcp { .. }) {
        return Ok(false);
    }
    #[cfg(windows)]
    if crate::streams::is_named_pipe_path(&location.path) {
        return Ok(false);
    }
    let Some(path) = crate::runtime::local_path(location, false)
        .map_err(|message| ExecutionError::new(message, span))?
    else {
        return Ok(true);
    };
    #[cfg(windows)]
    if crate::streams::is_named_pipe_path(&path.to_string_lossy()) {
        return Ok(false);
    }
    match std::fs::symlink_metadata(path) {
        Ok(metadata) => Ok(metadata.is_file() || metadata.is_dir()),
        Err(cause) if cause.kind() == std::io::ErrorKind::NotFound => Ok(true),
        Err(cause) => Err(ExecutionError::caused(
            "cannot classify program output for shutdown",
            span,
            cause,
        )),
    }
}
fn input_label(input: &Input) -> String {
    match input {
        Input::File { location, .. } => location.to_string(),
        Input::Literal(_) => "literal data".into(),
        Input::Port { process, port } => format!("program {}::{port:?}", process.0),
    }
}
fn output_label(output: &Output) -> String {
    match output {
        Output::File { location, .. } => location.to_string(),
        Output::Stdin(process) => format!("program {}::stdin", process.0),
    }
}

#[cfg(test)]
mod cleanup_tests {
    use super::*;
    use std::future::Future;

    #[test]
    fn joining_more_work_preserves_the_primary_error_and_secondary_recovery_paths() {
        let mut primary = Some(ExecutionError::new("first failure", Span::default()));
        let mut later = ExecutionError::new("later cleanup failure", Span::default());
        later.retained_paths.push(PathBuf::from("retained-stage"));
        record_failure(&mut primary, Err::<(), _>(later));
        let primary = primary.unwrap();
        assert_eq!(primary.report.diagnostic.message, "first failure");
        assert_eq!(primary.retained_paths, [PathBuf::from("retained-stage")]);
        assert!(
            primary
                .report
                .help
                .iter()
                .any(|help| help.contains("later cleanup failure"))
        );
    }
    struct ReleaseOnDrop(WorkerGate);
    impl Drop for ReleaseOnDrop {
        fn drop(&mut self) {
            self.0.release();
        }
    }

    async fn until(mut condition: impl FnMut() -> bool) {
        tokio::time::timeout(std::time::Duration::from_secs(5), async {
            while !condition() {
                tokio::task::yield_now().await;
            }
        })
        .await
        .unwrap();
    }
    fn literal(target: &Path, text: &str) -> hir::Program {
        hir::Program {
            body: hir::Region {
                instructions: vec![Instruction {
                    id: InstructionId(0),
                    span: Span::default(),
                    expansion: vec![],
                    kind: InstructionKind::Transfer {
                        source: Input::Literal(text.into()),
                        destination: Output::File {
                            location: crate::endpoint::Location {
                                backend: crate::endpoint::Backend::Local,
                                path: target.to_string_lossy().into_owned(),
                            },
                            mode: crate::language::DestinationMode::Exact,
                        },
                    },
                }],
            },
        }
    }

    fn file_flow(source: &Path, port: u16) -> hir::Program {
        let mut program = literal(Path::new("unused"), "unused");
        program.body.instructions[0].kind = InstructionKind::Transfer {
            source: Input::File {
                location: crate::endpoint::Location {
                    backend: crate::endpoint::Backend::Local,
                    path: source.to_string_lossy().into_owned(),
                },
                pattern: None,
            },
            destination: Output::File {
                location: crate::endpoint::Location {
                    backend: crate::endpoint::Backend::Tcp {
                        host: "127.0.0.1".into(),
                        port,
                    },
                    path: "/".into(),
                },
                mode: crate::language::DestinationMode::Exact,
            },
        };
        program
    }

    fn capture_fixture() -> (tempfile::TempDir, RuntimeSession, std::net::TcpListener) {
        let base = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        std::fs::create_dir_all(&base).unwrap();
        let root = tempfile::Builder::new()
            .prefix("capture-owner-")
            .tempdir_in(base)
            .unwrap();
        std::fs::write(root.path().join("source"), b"owned capture bytes").unwrap();
        let mut options = Options::new(root.path().join("scratch"));
        options.limits.max_streams = 1;
        let runtime = RuntimeSession::new(&options).unwrap();
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        listener.set_nonblocking(true).unwrap();
        (root, runtime, listener)
    }

    fn assert_unstarted(listener: &std::net::TcpListener) {
        assert_eq!(
            listener.accept().unwrap_err().kind(),
            std::io::ErrorKind::WouldBlock,
            "an abandoned acquisition must never dispatch its destination"
        );
    }

    #[tokio::test]
    async fn dropped_submit_retains_capture_budget_and_shutdown_joins_the_worker() {
        let (root, mut runtime, listener) = capture_fixture();
        let program = file_flow(
            &root.path().join("source"),
            listener.local_addr().unwrap().port(),
        );
        let gate = WorkerGate::default();
        let _release = ReleaseOnDrop(gate.clone());
        runtime.acquisition_gate = Some(gate.clone());
        let mut submit = Box::pin(runtime.submit(&program));
        tokio::select! {
            result=&mut submit=>panic!("capture escaped its worker gate: {result:?}"),
            ()=until(||gate.entered())=>{},
        }
        drop(submit);
        let backing = runtime.private_root().unwrap().unwrap().to_owned();
        let record = runtime.private_stage_record().unwrap().unwrap().to_owned();
        assert!(runtime.has_work());
        assert_eq!(runtime.work_ids().len(), 1);
        assert_eq!(
            runtime
                .admission
                .availability(super::super::scheduler::Demand::flows(1)),
            super::super::scheduler::Availability::Busy,
        );
        assert_unstarted(&listener);
        let mut shutdown = Box::pin(runtime.shutdown());
        assert!(
            std::future::poll_fn(|cx| std::task::Poll::Ready(
                shutdown.as_mut().poll(cx).is_pending()
            ))
            .await
        );
        assert!(backing.is_dir());
        assert!(record.is_file());
        gate.release();
        shutdown.await.unwrap();
        assert!(!backing.exists());
        assert!(!record.exists());
        assert!(!runtime.has_work());
        assert_unstarted(&listener);
        assert_eq!(
            std::fs::read(root.path().join("source")).unwrap(),
            b"owned capture bytes"
        );
    }

    #[tokio::test]
    async fn dropped_session_retires_a_retained_capture_before_its_backing() {
        let (root, mut runtime, listener) = capture_fixture();
        let program = file_flow(
            &root.path().join("source"),
            listener.local_addr().unwrap().port(),
        );
        let gate = WorkerGate::default();
        let _release = ReleaseOnDrop(gate.clone());
        runtime.acquisition_gate = Some(gate.clone());
        let mut submit = Box::pin(runtime.submit(&program));
        tokio::select! {
            result=&mut submit=>panic!("capture escaped its worker gate: {result:?}"),
            ()=until(||gate.entered())=>{},
        }
        drop(submit);
        let backing = runtime.private_root().unwrap().unwrap().to_owned();
        let record = runtime.private_stage_record().unwrap().unwrap().to_owned();
        drop(runtime);
        tokio::task::yield_now().await;
        assert!(backing.is_dir());
        assert!(record.is_file());
        assert!(!gate.finished());
        gate.release();
        until(|| !backing.exists() && !record.exists()).await;
        assert_unstarted(&listener);
        assert_eq!(
            std::fs::read(root.path().join("source")).unwrap(),
            b"owned capture bytes"
        );
    }

    #[tokio::test]
    async fn next_submit_joins_abandoned_capture_without_replaying_its_flow() {
        let (root, mut runtime, listener) = capture_fixture();
        let program = file_flow(
            &root.path().join("source"),
            listener.local_addr().unwrap().port(),
        );
        let gate = WorkerGate::default();
        let _release = ReleaseOnDrop(gate.clone());
        runtime.acquisition_gate = Some(gate.clone());
        let mut submit = Box::pin(runtime.submit(&program));
        tokio::select! {
            result=&mut submit=>panic!("capture escaped its worker gate: {result:?}"),
            ()=until(||gate.entered())=>{},
        }
        drop(submit);
        let next = literal(&root.path().join("next"), "new statement");
        let mut submit = Box::pin(runtime.submit(&next));
        assert!(
            std::future::poll_fn(|cx| std::task::Poll::Ready(
                submit.as_mut().poll(cx).is_pending()
            ))
            .await
        );
        assert!(!root.path().join("next").exists());
        gate.release();
        let report = submit.await.unwrap();
        assert_eq!(report.assignments, 1);
        assert_eq!(
            std::fs::read(root.path().join("next")).unwrap(),
            b"new statement"
        );
        assert_unstarted(&listener);
        runtime.shutdown().await.unwrap();
    }
    #[tokio::test]
    async fn a_dropped_submit_cannot_complete_a_new_submission_with_reused_ids() {
        use std::task::Poll;
        let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        std::fs::create_dir_all(&scratch).unwrap();
        let root = tempfile::Builder::new()
            .prefix("submission-owner-")
            .tempdir_in(scratch)
            .unwrap();
        let mut runtime = RuntimeSession::new(&Options::new(root.path().join("scratch"))).unwrap();
        let first = literal(&root.path().join("first"), "first data");
        let next = literal(&root.path().join("next"), "next data");
        let old_gate = WorkerGate::default();
        let _release_old = ReleaseOnDrop(old_gate.clone());
        runtime.worker_gate = Some(old_gate.clone());
        let mut old = Box::pin(runtime.submit(&first));
        tokio::select! {
            result=&mut old=>panic!("first publication escaped its gate: {result:?}"),
            ()=until(||old_gate.entered())=>{},
        }
        drop(old);
        old_gate.release();
        until(|| old_gate.finished()).await;
        assert_eq!(
            std::fs::read(root.path().join("first")).unwrap(),
            b"first data"
        );

        let next_gate = WorkerGate::default();
        let _release_next = ReleaseOnDrop(next_gate.clone());
        runtime.worker_gate = Some(next_gate.clone());
        let mut operation = Box::pin(runtime.submit(&next));
        tokio::select! {
            result=&mut operation=>panic!("old completion falsely completed the new statement: {result:?}"),
            ()=until(||next_gate.entered())=>{},
        }
        let pending =
            std::future::poll_fn(|cx| Poll::Ready(operation.as_mut().poll(cx).is_pending())).await;
        assert!(
            pending,
            "the new statement must retain its own publication gate"
        );
        assert!(!root.path().join("next").exists());
        next_gate.release();
        let report = operation.await.unwrap();
        assert_eq!(report.assignments, 2);
        assert_eq!(
            std::fs::read(root.path().join("next")).unwrap(),
            b"next data"
        );
        runtime.shutdown().await.unwrap();
    }

    #[tokio::test]
    async fn idle_readiness_tracks_abandoned_finite_completion_without_spinning() {
        let (root, mut runtime, _listener) = capture_fixture();
        let destination = root.path().join("finite-result");
        let program = literal(&destination, "finished bytes");
        let gate = WorkerGate::default();
        let _release = ReleaseOnDrop(gate.clone());
        runtime.worker_gate = Some(gate.clone());
        let mut submit = Box::pin(runtime.submit(&program));
        tokio::select! {
            result = &mut submit => panic!("worker escaped its gate: {result:?}"),
            () = until(|| gate.entered()) => {},
        }
        drop(submit);
        assert!(runtime.has_work());
        assert!(!runtime.has_ready_work());
        assert!(
            tokio::time::timeout(std::time::Duration::from_millis(20), runtime.wait_ready())
                .await
                .is_err()
        );
        gate.release();
        tokio::time::timeout(std::time::Duration::from_secs(3), runtime.wait_ready())
            .await
            .unwrap();
        runtime.poll_ready().await.unwrap();
        assert_eq!(std::fs::read(&destination).unwrap(), b"finished bytes");
        assert!(!runtime.has_work());
        assert!(!runtime.has_ready_work());
        assert_eq!(runtime.take_report().assignments, 1);
        runtime.shutdown().await.unwrap();
    }
}
