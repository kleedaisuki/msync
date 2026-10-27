//! Stateful execution of structured file, program and byte-flow regions.
//!
//! Native IO ownership stays with one session. Connected ports are extracted
//! before awaiting completion, so a wait can never hold the lock needed by its
//! own stdin producer. Checkpoints join real processes and drain every flow.

use std::collections::{BTreeMap, BTreeSet};
use std::ffi::OsString;
use std::fs;
use std::path::{Path, PathBuf};

use tokio::io::{AsyncReadExt, AsyncWriteExt};
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

use super::domain::{self, Domain, Source, Transfer};
use super::endpoint;
use super::error::{ExecutionError, Result};
use super::physical;
use super::program::{self, Program, Reader, Writer};
use super::scheduler::Admission;
use super::{Engine, ExecutionReport, Options};

struct FlowResult {
    origin: Option<crate::source::InstructionOrigin>,
    instruction: InstructionId,
    transaction: TransactionId,
    span: Span,
    output: Option<(PathBuf, PathBuf, DestinationMode)>,
    basename: Option<OsString>,
}

struct Flow {
    origin: Option<crate::source::InstructionOrigin>,
    transaction: TransactionId,
    handle: JoinHandle<Result<FlowResult>>,
    physical: bool,
    managed: Option<PathBuf>,
}

struct Deferred {
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
    completed_nodes: BTreeSet<InstructionId>,
    pending_planning: Option<crate::hir::graph::PlanningContext>,
    planning: crate::hir::graph::PlanningContext,
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
            completed_nodes: BTreeSet::new(),
            pending_planning: None,
            planning: crate::hir::graph::PlanningContext::default(),
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
        self.implicit.iter().any(|id| self.is_ready(*id))
    }

    fn is_ready(&self, transaction: TransactionId) -> bool {
        self.flows
            .iter()
            .filter(|flow| flow.transaction == transaction)
            .all(|flow| flow.handle.is_finished())
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
        self.attach_cohorts(&mut context);
        context
    }

    /// These proofs describe the latest executing submission only. Their
    /// retained immutable leases must not be reused for a later input's IDs.
    pub fn refinement_context(&self) -> crate::hir::graph::PlanningContext {
        let mut context = self.planning_context();
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
        self.planning = self.pending_planning.take().unwrap_or_default();
        self.completed_nodes.clear();
        self.rejected_before_start = false;
        let prior_work = self.implicit.clone();
        let prior_flows = self.flows.len();
        if program.body.instructions.is_empty() {
            return Ok(ExecutionReport::default());
        }
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
        self.preflight(program)?;
        let domain = live_domain(program, &self.options)?;
        self.enroll_physical_namespaces(program)?;
        self.enroll(&domain)?;
        let result = self.region(&program.body, None, program).await;
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
            self.cancel.cancel();
            self.cancel_programs(&mut failure).await;
            self.cancel_flows().await;
            let pending = self.implicit.iter().copied().collect::<Vec<_>>();
            for id in pending {
                self.engine_mut()?.abort(id, &mut failure);
                self.implicit.remove(&id);
            }
            self.cancel = self.options.cancel.child_token();
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
            tokio::select! {
                () = self.cancel.cancelled() => return Err(ExecutionError::new("execution cancelled while waiting for ready cohorts", Span::default())),
                () = tokio::time::sleep(std::time::Duration::from_millis(10)) => {}
            }
        }
        Ok(())
    }

    pub async fn cancel_active(&mut self) -> Result<()> {
        self.cancel.cancel();
        let mut failure =
            ExecutionError::new("active program execution was cancelled", Span::default());
        self.cancel_programs(&mut failure).await;
        self.cancel_flows().await;
        let ids = self.implicit.iter().copied().collect::<Vec<_>>();
        for id in ids {
            self.engine_mut()?.abort(id, &mut failure);
            self.implicit.remove(&id);
        }
        self.cancel = self.options.cancel.child_token();
        if failure.cause.is_some()
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
                    let physical = physical::inspect(source, destination, node.span)?.is_some()
                        || match (source, destination) {
                            (Input::File { location, .. }, Output::Stdin(_)) => {
                                physical::source_stream(location, node.span)?.is_some()
                            }
                            _ => false,
                        };
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
                        session.guard_finite(source, destination, node.span)?;
                        continue;
                    }
                    if !transactional
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
            if self.options.limits.memory_bytes / self.options.limits.buffer_bytes < 6 {
                return Err(ExecutionError::new("the payload budget cannot admit one program space", Span::default())
                    .help("One program reserves six configured buffers; increase --memory-limit or reduce --buffer-size before execution."));
            }
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
        if self.engine.is_none() {
            self.engine = Some(Engine::new(domain, &self.options)?);
            return Ok(());
        }
        let sparse_roots = self.sparse_roots.clone();
        let engine = self.engine_mut()?;
        for root in &domain.roots {
            if engine
                .roots
                .iter()
                .any(|old| domain::relative_to(root, old).is_some())
            {
                if let Some(sparse) = sparse_roots
                    .iter()
                    .find(|sparse| domain::relative_to(root, sparse).is_some())
                {
                    let mut entries = BTreeSet::new();
                    for transfer in domain.transfers.values() {
                        let paths =
                            std::iter::once(&transfer.destination).chain(match &transfer.source {
                                Source::Local(path) => Some(path),
                                _ => None,
                            });
                        for path in paths {
                            if let Some(relative) = domain::relative_to(path, sparse)
                                && let Some(first) = relative.components().next()
                            {
                                entries.insert(sparse.join(first));
                            }
                        }
                    }
                    for entry in entries {
                        engine.manager.enroll_entry(&entry).map_err(|cause| {
                            ExecutionError::caused(
                                "cannot enroll managed entry",
                                domain.site(root),
                                cause,
                            )
                        })?;
                    }
                }
                continue;
            }
            engine.manager.enroll(root).map_err(|cause| {
                ExecutionError::caused(
                    "cannot extend the persistent resource domain",
                    domain.site(root),
                    cause,
                )
            })?;
            engine.roots.push(root.clone());
        }
        Ok(())
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
            self.engine = Some(Engine::new(&Domain::default(), &self.options)?);
        }
        self.sparse_roots
            .extend(entries.iter().map(|(parent, _, _)| parent.clone()));
        let engine = self.engine_mut()?;
        for (parent, target, span) in entries {
            engine
                .manager
                .enroll_namespace(&parent)
                .and_then(|()| engine.manager.enroll_entry(&target))
                .map_err(|cause| {
                    ExecutionError::caused("cannot enroll physical output namespace", span, cause)
                })?;
            if !engine
                .roots
                .iter()
                .any(|root| domain::relative_to(&parent, root).is_some())
            {
                engine.roots.push(parent);
            }
        }
        Ok(())
    }

    fn fresh_instruction(&mut self) -> Result<InstructionId> {
        let id = InstructionId(self.next_instruction);
        self.next_instruction = self.next_instruction.checked_add(1).ok_or_else(|| {
            ExecutionError::new("runtime instruction identity exhausted", Span::default())
        })?;
        Ok(id)
    }

    async fn region(
        &mut self,
        region: &Region,
        current: Option<TransactionId>,
        program: &hir::Program,
    ) -> Result<()> {
        for instruction in &region.instructions {
            if self.cancel.is_cancelled() {
                return Err(ExecutionError::new("execution cancelled", instruction.span));
            }
            let origin = self.origins.get(&instruction.id).cloned();
            self.instruction(instruction, current, program)
                .await
                .map_err(|error| error.with_origin(origin.as_ref()))?;
            let deferred = match &instruction.kind {
                InstructionKind::Spawn { .. } => true,
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    matches!(source, Input::Port { .. })
                        || matches!(destination, Output::Stdin(_))
                        || physical::inspect(source, destination, instruction.span)?.is_some()
                }
                _ => false,
            };
            if !deferred {
                self.completed_nodes.insert(instruction.id);
            }
        }
        Ok(())
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
                    self.wait().await?;
                    let isolation = program
                        .transactions
                        .iter()
                        .find(|info| info.id == *transaction)
                        .ok_or_else(|| {
                            ExecutionError::new("transaction metadata is missing", node.span)
                        })?
                        .isolation;
                    let id = self.engine_mut()?.allocate()?;
                    self.engine_mut()?
                        .manager
                        .begin(id, isolation)
                        .map_err(|cause| {
                            ExecutionError::caused("cannot begin transaction", node.span, cause)
                        })?;
                    self.engine_mut()?.named.insert(*transaction, id);
                    let result = self.region(body, Some(id), program).await;
                    let result = match result {
                        Ok(()) => self
                            .quiesce(id)
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
                    let report = self.engine_mut()?.manager.checkpoint(id).map_err(|cause| {
                        ExecutionError::caused("checkpoint failed", node.span, cause)
                    })?;
                    self.engine_mut()?.committed(report);
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
                            let mut failure = ExecutionError::new(
                                "discarding the pending program epoch",
                                node.span,
                            );
                            self.cancel_transaction(id, &mut failure).await;
                            self.engine_mut()?
                                .manager
                                .rollback_pending(id)
                                .map_err(|cause| {
                                    ExecutionError::caused(
                                        "cannot restore checkpoint",
                                        node.span,
                                        cause,
                                    )
                                })?;
                        }
                        RollbackScope::Begin => {
                            if current.is_some() {
                                return Err(ExecutionError::new(
                                    "named rollback is not allowed inside a transaction",
                                    node.span,
                                ));
                            }
                            self.wait().await?;
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
        let peer = self.programs.values().find(|program| {
            program.is_running() && current.is_none_or(|id| id == program.transaction)
        });
        let shared = peer.and_then(Program::fork_shared);
        let baseline = peer.map(|program| program.baseline.clone());
        let owner = peer.map(|program| program.transaction);
        if self.programs.contains_key(&id) {
            return Err(ExecutionError::new(
                "program identity is already owned by this runtime",
                span,
            ));
        }
        let lease = self.admission.program(span)?;
        let task = lease.task("program.launch", self.cancel.clone());
        let transaction = match current {
            Some(id) => id,
            None if owner.is_some() => owner.unwrap(),
            None => {
                let id = self.engine_mut()?.allocate()?;
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
        let events = self
            .programs
            .values()
            .map(|program| {
                (
                    program.id,
                    (program.exit_signal.is_cancelled(), program.is_sealed()),
                )
            })
            .collect();
        let completed = self.completed_nodes.clone();
        let mut rejected = false;
        let engine = self.engine_mut()?;
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
                    let result = super::scheduler::admit_spawn(
                        &graph,
                        instruction,
                        &events,
                        &completed,
                        span,
                    );
                    rejected = result.is_err();
                    result
                },
            ))
            .await;
        if rejected {
            self.rejected_before_start = true;
        }
        let program = launched?;
        self.programs.insert(id, program);
        self.latest_processes.insert(id);
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
        if let Some(spec) = physical::inspect(source, destination, span)? {
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
                let reader = self.reader(instruction, source, transaction, span).await?;
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
                let target = local(location, false, span)?;
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
                if *port != ReadPort::Pid {
                    self.quiesce(owner).await?;
                }
                let bytes = self.programs.get(process).unwrap().scalar(*port)?;
                let snapshot = self.capture_bytes(&bytes, span).await?;
                let target = local(location, false, span)?;
                let transaction = self.connection_transaction(*process, current, span)?;
                let instruction = self.fresh_instruction()?;
                self.deferred.push(Deferred {
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
                }
                Ok(())
            }
            (_, Output::File { location, mode }) => {
                if let Some(id) = current {
                    self.quiesce(id).await?;
                }
                let transaction = self.transaction(current, span)?;
                let operation = self.fresh_instruction()?;
                let transfer = Transfer {
                    source: file_source(source, span)?,
                    destination: local(location, false, span)?,
                    mode: *mode,
                    pattern: match source {
                        Input::File { pattern, .. } => pattern.clone(),
                        _ => None,
                    },
                    span,
                };
                self.engine_mut()?
                    .manager
                    .start_statement(transaction)
                    .map_err(|cause| {
                        ExecutionError::caused("cannot begin statement view", span, cause)
                    })?;
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
                let id = self.engine_mut()?.allocate()?;
                self.engine_mut()?
                    .manager
                    .begin(id, Isolation::ReadCommitted)
                    .map_err(|cause| {
                        ExecutionError::caused("cannot begin physical output", span, cause)
                    })?;
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
                    let snapshot = self
                        .engine_mut()?
                        .manager
                        .read_snapshot(transaction, &path)
                        .map_err(|cause| {
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
        let lease = self.admission.flow(span)?;
        let buffer = self.options.limits.buffer_bytes;
        let cancel = self.cancel.clone();
        let managed = output.as_ref().map(|(_, target, _)| target.clone());
        let task = lease.task("physical.transfer", cancel.clone());
        let handle = tokio::spawn(task.run(async move {
            let _lease = lease;
            spec.run(source, capture, buffer, cancel).await?;
            Ok(FlowResult {
                origin: None,
                instruction: operation,
                transaction,
                span,
                output,
                basename,
            })
        }));
        self.flows.push(Flow {
            origin: self.active_origin.clone(),
            transaction,
            handle,
            physical: true,
            managed,
        });
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
        let lease = self.admission.flow(span)?;
        let buffer = self.options.limits.buffer_bytes;
        let cancel = self.cancel.clone();
        let task = lease.task("physical.input", cancel.clone());
        let handle = tokio::spawn(task.run(async move {
            let _lease = lease;
            physical::to_writer(source, sink, buffer, cancel, span, consumer_closed).await?;
            Ok(FlowResult {
                origin: None,
                instruction,
                transaction,
                span,
                output: None,
                basename: None,
            })
        }));
        self.flows.push(Flow {
            origin: self
                .active_origin
                .as_ref()
                .map(|origin| origin.for_operand(false)),
            transaction,
            handle,
            physical: true,
            managed: None,
        });
        Ok(())
    }

    async fn reap_physical(&mut self) -> Result<()> {
        let ready = self
            .implicit
            .iter()
            .copied()
            .filter(|id| self.is_ready(*id))
            .collect::<Vec<_>>();
        for transaction in ready {
            self.quiesce(transaction).await.map_err(|mut error| {
                error.transaction = Some(transaction);
                error
            })?;
            self.engine_mut()?
                .finish(transaction, Span::default())
                .map_err(|mut error| {
                    error.transaction = Some(transaction);
                    error
                })?;
            self.implicit.remove(&transaction);
        }
        Ok(())
    }

    fn transaction(&mut self, current: Option<TransactionId>, span: Span) -> Result<TransactionId> {
        if let Some(id) = current {
            return Ok(id);
        }
        let id = self.engine_mut()?.allocate()?;
        self.engine_mut()?
            .manager
            .begin(id, Isolation::Serializable)
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
                if *port != ReadPort::Pid {
                    self.quiesce(owner).await?;
                }
                let bytes = self.programs.get(process).unwrap().scalar(*port)?;
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
                let path = local(location, true, span)?;
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
        let lease = self.admission.flow(span)?;
        let instruction = self.fresh_instruction()?;
        let size = self.options.limits.buffer_bytes;
        let cancel = self.cancel.clone();
        let managed = output.as_ref().map(|(_, target, _)| target.clone());
        let task = lease.task("program.port", cancel.clone());
        let origin = self.active_origin.clone();
        let handle = tokio::spawn(task.run(async move {
            let _lease = lease;
            let mut bytes = vec![0; size];
            loop {
                let count = tokio::select! { () = cancel.cancelled() => return Err(ExecutionError::new("program flow cancelled", span)), read = source.read(&mut bytes) => read.map_err(|cause| ExecutionError::caused("cannot read program flow", span, cause).with_operand(origin.as_ref(), false))? };
                if count == 0 {
                    break;
                }
                tokio::select! { () = cancel.cancelled() => return Err(ExecutionError::new("program flow cancelled", span)), written = destination.write_all(&bytes[..count]) => written.map_err(|cause| ExecutionError::caused("cannot write program flow", span, cause).with_operand(origin.as_ref(), true))? };
            }
            destination.shutdown().await.map_err(|cause| {
                ExecutionError::caused("cannot close program flow", span, cause).with_operand(origin.as_ref(), true)
            })?;
            Ok(FlowResult {
                origin: None,
                instruction,
                transaction,
                span,
                output,
                basename: None,
            })
        }));
        self.flows.push(Flow {
            origin: self.active_origin.clone(),
            transaction,
            handle,
            physical: false,
            managed,
        });
        Ok(())
    }

    async fn quiesce(&mut self, transaction: TransactionId) -> Result<()> {
        let physical_output = self
            .flows
            .iter()
            .any(|flow| flow.transaction == transaction && flow.physical);
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
                () = self.cancel.cancelled() => {
                    let origin = flow.origin.clone();
                    self.flows.push(flow);
                    self.flows.extend(selected);
                    return Err(ExecutionError::new("execution cancelled while draining program IO", Span::default()).with_origin(origin.as_ref()));
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
                .join(&self.cancel)
                .await
                .map_err(|error| error.with_origin(program.origin.as_ref()))?;
        }
        let mut imported = BTreeSet::new();
        for id in ids {
            let engine = self.engine.as_mut().expect("initialized engine");
            let program = self.programs.get_mut(&id).unwrap();
            let cohort = program.cohort;
            let origin = program.origin.clone();
            let receipt = program
                .seal(&engine.assets, &self.cancel)
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
        for flow in completed {
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
                    origin: flow.origin.clone(),
                    instruction: flow.instruction,
                    transaction: flow.transaction,
                    source: snapshot,
                    target,
                    mode,
                    span: flow.span,
                    basename: flow.basename,
                });
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
        if physical_output {
            self.engine_mut()?
                .manager
                .start_statement(transaction)
                .map_err(|cause| {
                    ExecutionError::caused(
                        "cannot start completed physical publication",
                        Span::default(),
                        cause,
                    )
                })?;
        }
        assignments.sort_by_key(|assignment| assignment.instruction);
        for assignment in assignments {
            let transfer = Transfer {
                source: Source::Captured {
                    snapshot: assignment.source,
                    basename: assignment.basename,
                },
                destination: assignment.target,
                mode: assignment.mode,
                pattern: None,
                span: assignment.span,
            };
            self.engine_mut()?
                .transfer(assignment.instruction, &transfer, transaction)
                .map_err(|error| error.with_operand(assignment.origin.as_ref(), true))?;
        }
        Ok(())
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
    ) {
        for program in self
            .programs
            .values_mut()
            .filter(|program| program.transaction == transaction)
        {
            if let Err(error) = program.cancel().await {
                failure
                    .report
                    .help
                    .push(format!("program cleanup: {error}"));
            }
        }
        let mut remaining = Vec::new();
        for flow in std::mem::take(&mut self.flows) {
            if flow.transaction == transaction {
                flow.handle.abort();
                let _ = flow.handle.await;
            } else {
                remaining.push(flow);
            }
        }
        self.flows = remaining;
        self.deferred
            .retain(|assignment| assignment.transaction != transaction);
    }

    async fn cancel_programs(&mut self, failure: &mut ExecutionError) {
        for program in self.programs.values_mut() {
            if let Err(error) = program.cancel().await {
                failure
                    .report
                    .help
                    .push(format!("program cleanup: {error}"));
            }
        }
    }

    async fn cancel_flows(&mut self) {
        for flow in std::mem::take(&mut self.flows) {
            flow.handle.abort();
            let _ = flow.handle.await;
        }
        self.deferred.clear();
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

fn live_domain(program: &hir::Program, options: &Options) -> Result<Domain> {
    fn finite(region: &Region, roots: &mut Vec<PathBuf>) -> Result<Region> {
        let mut result = Region::default();
        for node in &region.instructions {
            if let InstructionKind::Transfer {
                source,
                destination,
            } = &node.kind
            {
                if let Some(spec) = physical::inspect(source, destination, node.span)? {
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
                    if let Source::Local(path) = file_source(source, node.span)? {
                        roots.push(path);
                    }
                    continue;
                }
                if let (Input::File { location, .. }, Output::Stdin(_)) = (source, destination)
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
                            let path = local(location, true, node.span)?;
                            roots.push(if path.is_dir() {
                                path
                            } else {
                                path.parent().unwrap_or(Path::new(".")).to_owned()
                            });
                        }
                    }
                }
                InstructionKind::Transfer {
                    source: Input::Port { .. },
                    destination: Output::File { location, .. },
                } => {
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
                            body: finite(body, roots)?,
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
        body: finite(&program.body, &mut roots)?,
        transactions: program.transactions.clone(),
    };
    let mut domain = Domain::resolve(&filtered, options)?;
    roots.extend(domain.roots);
    roots.sort_by_key(|root| root.components().count());
    domain.roots = roots.into_iter().fold(Vec::new(), |mut result, root| {
        if !result
            .iter()
            .any(|old| domain::relative_to(&root, old).is_some())
        {
            result.push(root);
        }
        result
    });
    Ok(domain)
}

#[cfg(test)]
#[path = "source_tests.rs"]
mod source_tests;
