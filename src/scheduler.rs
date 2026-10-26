//! Typed planning and bounded execution of file and stream dependency graphs.

use std::collections::VecDeque;

use tokio::task::JoinSet;
use tokio_util::sync::CancellationToken;

use crate::compiler::{Backend, CopyOp, Program};
use crate::plan::{FileClass, OperationKinds, Plan};
use crate::runtime::{self, ExecutionReport, RuntimeError};
use crate::syntax::Span;

#[derive(Clone, Debug)]
pub struct ResourceLimits {
    /// Maximum simultaneous ordinary copy operations.
    pub jobs: usize,
    /// Separate background-flow slots, so idle streams do not occupy copy workers.
    pub max_streams: usize,
    pub buffer_bytes: usize,
    /// Aggregate application-owned stream buffer capacity, not process RSS.
    pub memory_bytes: usize,
}

impl Default for ResourceLimits {
    fn default() -> Self {
        Self {
            jobs: 4,
            max_streams: 16,
            buffer_bytes: 64 * 1024,
            memory_bytes: 64 * 1024 * 1024,
        }
    }
}

impl ResourceLimits {
    pub fn validate(&self) -> Result<(), String> {
        if self.jobs == 0 || self.max_streams == 0 || self.buffer_bytes == 0 {
            return Err("jobs, max-streams and buffer-size must be positive".into());
        }
        if self.memory_bytes < self.buffer_bytes {
            return Err("memory-limit must admit at least one buffer-size".into());
        }
        Ok(())
    }

    fn stream_slots(&self) -> usize {
        self.max_streams.min(self.memory_bytes / self.buffer_bytes)
    }
}

#[derive(Clone, Debug, Default)]
pub struct ExecutionSummary {
    pub report: ExecutionReport,
    pub completed: usize,
    pub stream_bytes: u64,
    pub max_active_copies: usize,
    pub max_active_streams: usize,
    pub peak_buffer_bytes: usize,
}

/// Resolve local execution context once, inspect types without opening stream data,
/// then optimize effects. Offline preparation never launches a remote client.
pub fn prepare(program: &Program, live: bool) -> Result<Plan, RuntimeError> {
    prepare_spanned(program, live, &[])
}

pub fn prepare_spanned(
    program: &Program,
    live: bool,
    operands: &[(Span, Span)],
) -> Result<Plan, RuntimeError> {
    let mut trace = crate::telemetry::span("prepare");
    let result = prepare_inner(program, live, operands);
    trace.finish_result(&result);
    result
}

fn prepare_inner(
    program: &Program,
    live: bool,
    operands: &[(Span, Span)],
) -> Result<Plan, RuntimeError> {
    let mut resolved = program.clone();
    let mut kinds = Vec::with_capacity(resolved.operations.len());
    let mut failures = Vec::new();
    for (index, operation) in resolved.operations.iter_mut().enumerate() {
        let result = inspect_operation(operation, live);
        match result {
            Ok(kind) => kinds.push(kind),
            Err((destination, message)) => {
                kinds.push(OperationKinds {
                    source: FileClass::Unknown,
                    destination: FileClass::Unknown,
                });
                let pair = operands
                    .get(index)
                    .copied()
                    .unwrap_or((operation.span, operation.span));
                failures.push((
                    index,
                    RuntimeError {
                        message,
                        span: if destination { pair.1 } else { pair.0 },
                    },
                ));
            }
        }
    }
    let plan = Plan::build(&resolved, &kinds);
    validate_graph(&plan)?;
    if let Some((_, failure)) = failures
        .into_iter()
        .find(|(index, _)| plan.nodes.iter().any(|node| node.original_index == *index))
    {
        return Err(failure);
    }
    for node in &plan.nodes {
        let pair = operands
            .get(node.original_index)
            .copied()
            .unwrap_or((node.operation.span, node.operation.span));
        if live && node.kinds.source == FileClass::Missing {
            let produced = node.effects.reads.first().is_some_and(|read| {
                plan.nodes[..node.id].iter().any(|producer| {
                    producer
                        .effects
                        .writes
                        .iter()
                        .any(|write| write.overlaps(read))
                })
            });
            if !produced {
                return Err(error("source does not exist and no preceding plan operation produces it; correct the source path or create it before execution".into(), pair.0));
            }
        }
        validate_operation(&node.operation, node.kinds, pair)?;
    }
    Ok(plan)
}

fn inspect_operation(operation: &mut CopyOp, live: bool) -> Result<OperationKinds, (bool, String)> {
    snapshot_local(&mut operation.source, true).map_err(|message| (false, message))?;
    snapshot_local(&mut operation.destination, false).map_err(|message| (true, message))?;
    let inspect = if live {
        runtime::inspect_location_live
    } else {
        runtime::inspect_location
    };
    Ok(OperationKinds {
        source: inspect(&operation.source, true).map_err(|message| (false, message))?,
        destination: inspect(&operation.destination, false).map_err(|message| (true, message))?,
    })
}

fn snapshot_local(location: &mut crate::compiler::Location, source: bool) -> Result<(), String> {
    if let Some(path) = runtime::local_path(location, source)? {
        let trailing = location.path.ends_with('/');
        let absolute = std::path::absolute(path).map_err(|error| error.to_string())?;
        location.backend = Backend::Local;
        location.path = absolute
            .into_os_string()
            .into_string()
            .map_err(|_| "resolved path is not valid Unicode")?;
        if trailing && !location.path.ends_with('/') {
            location.path.push('/');
        }
    }
    Ok(())
}

fn validate_operation(
    operation: &CopyOp,
    kinds: OperationKinds,
    operands: (Span, Span),
) -> Result<(), RuntimeError> {
    let message = if matches!(
        operation.destination.backend,
        Backend::Http { .. } | Backend::Path
    ) {
        Some((
            operands.1,
            "HTTP(S) and PATH endpoints are read-only; choose a writable destination",
        ))
    } else if kinds.destination == FileClass::Directory
        && !operation.destination.path.ends_with('/')
    {
        Some((
            operands.1,
            "destination is an existing directory; append '/' to use its container semantics or choose a new exact target",
        ))
    } else if kinds.source == FileClass::Directory && kinds.destination == FileClass::Stream {
        Some((
            operands.0,
            "a directory cannot be written to a pipe or socket; select an individual file instead",
        ))
    } else if kinds.source == FileClass::Stream && operation.pattern.is_some() {
        Some((
            operands.0,
            "a pipe or socket cannot enumerate a directory selector",
        ))
    } else if kinds.destination == FileClass::Stream
        && operation.destination.path.ends_with('/')
        && !matches!(operation.destination.backend, Backend::Tcp { .. })
    {
        Some((
            operands.1,
            "a pipe or socket is an exact endpoint, not a directory container; remove the trailing '/'",
        ))
    } else {
        None
    };
    match message {
        Some((span, message)) => Err(RuntimeError {
            message: message.into(),
            span,
        }),
        None => Ok(()),
    }
}

pub fn execute_plan(
    plan: &Plan,
    limits: &ResourceLimits,
) -> Result<ExecutionSummary, RuntimeError> {
    execute_plan_cancellable(plan, limits, CancellationToken::new())
}

pub fn execute_plan_cancellable(
    plan: &Plan,
    limits: &ResourceLimits,
    cancel: CancellationToken,
) -> Result<ExecutionSummary, RuntimeError> {
    if tokio::runtime::Handle::try_current().is_ok() {
        return Err(error(
            "synchronous execution cannot run inside Tokio; await execute_plan_async instead"
                .into(),
            first_span(plan),
        ));
    }
    limits
        .validate()
        .map_err(|message| error(message, first_span(plan)))?;
    let runtime = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .max_blocking_threads(limits.jobs.saturating_add(limits.max_streams).max(1))
        .build()
        .map_err(|failure| error(failure.to_string(), first_span(plan)))?;
    runtime.block_on(async {
        let signal = tokio::spawn({
            let cancel = cancel.clone();
            async move {
                if tokio::signal::ctrl_c().await.is_ok() {
                    cancel.cancel();
                }
            }
        });
        let result = execute_plan_async(plan, limits, cancel).await;
        signal.abort();
        result
    })
}

pub async fn execute_plan_async(
    plan: &Plan,
    limits: &ResourceLimits,
    cancel: CancellationToken,
) -> Result<ExecutionSummary, RuntimeError> {
    let parent = crate::telemetry::context();
    let mut trace = crate::telemetry::linked_span("schedule", parent.as_ref());
    let result = async {
        limits
            .validate()
            .map_err(|message| error(message, first_span(plan)))?;
        validate_graph(plan)?;
        schedule(plan, limits, cancel, trace.context()).await
    }
    .await;
    trace.finish_result(&result);
    if let Ok(summary) = &result {
        trace.metric("completed", summary.completed as u64);
        trace.metric("stream_bytes", summary.stream_bytes);
        trace.metric("max_in_flight_copies", summary.max_active_copies as u64);
        trace.metric("max_in_flight_streams", summary.max_active_streams as u64);
        trace.metric("peak_buffer_bytes", summary.peak_buffer_bytes as u64);
    }
    result
}

fn validate_graph(plan: &Plan) -> Result<(), RuntimeError> {
    if let Some(issue) = plan.issues.first() {
        let span = issue
            .nodes
            .first()
            .and_then(|id| plan.nodes.get(*id))
            .map(|node| node.operation.span)
            .unwrap_or_else(|| first_span(plan));
        return Err(error(issue.message.clone(), span));
    }
    for (index, node) in plan.nodes.iter().enumerate() {
        if node.id != index
            || node
                .dependencies
                .iter()
                .any(|dependency| *dependency >= index)
        {
            return Err(error(
                "plan is not a dense, acyclic dependency graph".into(),
                node.operation.span,
            ));
        }
    }
    let (groups, owners) = execution_groups(plan)?;
    let mut pending = vec![0_usize; groups.len()];
    let mut followers = vec![Vec::new(); groups.len()];
    let mut seen = std::collections::HashSet::new();
    for node in &plan.nodes {
        for dependency in &node.dependencies {
            let pair = (owners[*dependency], owners[node.id]);
            if pair.0 == pair.1 {
                return Err(error(
                    "a co-start pipeline depends on its own completion; use a distinct output file"
                        .into(),
                    node.operation.span,
                ));
            }
            if seen.insert(pair) {
                pending[pair.1] += 1;
                followers[pair.0].push(pair.1);
            }
        }
    }
    let mut ready: VecDeque<_> = pending
        .iter()
        .enumerate()
        .filter_map(|(id, count)| (*count == 0).then_some(id))
        .collect();
    let mut completed = 0;
    while let Some(id) = ready.pop_front() {
        completed += 1;
        for next in &followers[id] {
            pending[*next] -= 1;
            if pending[*next] == 0 {
                ready.push_back(*next);
            }
        }
    }
    if completed != groups.len() {
        return Err(error("co-start pipelines have a cyclic completion dependency; split the pipeline or use separate output files".into(), first_span(plan)));
    }
    Ok(())
}

fn execution_groups(plan: &Plan) -> Result<(Vec<Vec<usize>>, Vec<usize>), RuntimeError> {
    let mut groups = plan.stream_groups.clone();
    let mut owners = vec![usize::MAX; plan.nodes.len()];
    for (index, group) in groups.iter().enumerate() {
        if group.is_empty() {
            return Err(error(
                "an empty co-start group is invalid".into(),
                first_span(plan),
            ));
        }
        for member in group {
            let Some(owner) = owners.get_mut(*member) else {
                return Err(error(
                    "co-start group has an invalid node ID".into(),
                    first_span(plan),
                ));
            };
            if *owner != usize::MAX {
                return Err(error(
                    "co-start groups must not overlap".into(),
                    first_span(plan),
                ));
            }
            *owner = index;
        }
    }
    for (id, owner) in owners.iter_mut().enumerate() {
        if *owner != usize::MAX {
            continue;
        }
        *owner = groups.len();
        groups.push(vec![id]);
    }
    Ok((groups, owners))
}

type NodeResult = Result<(ExecutionReport, u64), RuntimeError>;
type Completion = (usize, NodeResult);

struct Scheduler<'a> {
    plan: &'a Plan,
    limits: &'a ResourceLimits,
    cancel: CancellationToken,
    parent: Option<crate::telemetry::TraceContext>,
    remaining: Vec<usize>,
    dependents: Vec<Vec<usize>>,
    ready: VecDeque<usize>,
    tasks: JoinSet<Completion>,
    reports: Vec<Option<ExecutionReport>>,
    costs: Vec<usize>,
    groups: Vec<Vec<usize>>,
    owners: Vec<usize>,
    started: Vec<bool>,
    copies: usize,
    streams: usize,
    buffer_bytes: usize,
    summary: ExecutionSummary,
    failure: Option<RuntimeError>,
}

impl<'a> Scheduler<'a> {
    fn new(
        plan: &'a Plan,
        limits: &'a ResourceLimits,
        cancel: CancellationToken,
        parent: Option<crate::telemetry::TraceContext>,
    ) -> Result<Self, RuntimeError> {
        let remaining: Vec<_> = plan
            .nodes
            .iter()
            .map(|node| node.dependencies.len())
            .collect();
        let ready = remaining
            .iter()
            .enumerate()
            .filter_map(|(id, count)| (*count == 0).then_some(id))
            .collect();
        let mut dependents = vec![Vec::new(); plan.nodes.len()];
        let mut costs = Vec::with_capacity(plan.nodes.len());
        for node in &plan.nodes {
            for dependency in &node.dependencies {
                dependents[*dependency].push(node.id);
            }
            costs.push(buffer_cost(&node.operation, node.kinds, limits)?);
        }
        let (groups, owners) = execution_groups(plan)?;
        for group in &groups {
            let stream_count = group.iter().filter(|id| costs[**id] > 0).count();
            let bytes = group
                .iter()
                .try_fold(0_usize, |total, id| total.checked_add(costs[*id]))
                .ok_or_else(|| {
                    error(
                        "pipeline buffer reservation overflow".into(),
                        first_span(plan),
                    )
                })?;
            if stream_count > limits.max_streams
                || group.len() - stream_count > limits.jobs
                || bytes > limits.memory_bytes
            {
                let span = group
                    .first()
                    .map(|id| plan.nodes[*id].operation.span)
                    .unwrap_or_default();
                return Err(error(
                    format!(
                        "pipeline must co-start {stream_count} streams and reserve {bytes} buffer bytes; increase --max-streams/--memory-limit or reduce --buffer-size"
                    ),
                    span,
                ));
            }
        }
        Ok(Self {
            plan,
            limits,
            cancel: cancel.child_token(),
            parent,
            remaining,
            dependents,
            ready,
            tasks: JoinSet::new(),
            reports: vec![None; plan.nodes.len()],
            costs,
            groups,
            owners,
            started: vec![false; plan.nodes.len()],
            copies: 0,
            streams: 0,
            buffer_bytes: 0,
            summary: ExecutionSummary::default(),
            failure: None,
        })
    }

    async fn run(mut self) -> Result<ExecutionSummary, RuntimeError> {
        loop {
            self.observe_cancel();
            if self.failure.is_none() {
                self.launch_ready();
            }
            if self.tasks.is_empty() {
                break;
            }
            let joined = if self.failure.is_some() {
                self.tasks.join_next().await
            } else {
                tokio::select! {
                    result = self.tasks.join_next() => result,
                    () = self.cancel.cancelled() => continue,
                }
            };
            if let Some(joined) = joined {
                self.accept(joined);
            }
        }
        self.observe_cancel();
        self.finish()
    }

    fn observe_cancel(&mut self) {
        if self.cancel.is_cancelled() && self.failure.is_none() {
            self.fail(error(
                "execution cancelled; active streams closed and no new operations started".into(),
                first_span(self.plan),
            ));
        }
    }

    fn launch_ready(&mut self) {
        for _ in 0..self.ready.len() {
            let id = self.ready.pop_front().unwrap();
            if self.started[id] {
                continue;
            }
            let group = self.owners[id];
            if !self.can_start(group) {
                self.ready.push_back(id);
                continue;
            }
            for member in self.groups[group].clone() {
                self.launch(member);
            }
        }
    }

    fn can_start(&self, group: usize) -> bool {
        let members = &self.groups[group];
        if members
            .iter()
            .any(|id| self.remaining[*id] != 0 || self.started[*id])
        {
            return false;
        }
        let streams = members.iter().filter(|id| self.is_stream(**id)).count();
        let copies = members.len() - streams;
        let bytes: usize = members.iter().map(|id| self.costs[*id]).sum();
        self.streams + streams <= self.limits.stream_slots()
            && self.copies + copies <= self.limits.jobs
            && bytes <= self.limits.memory_bytes - self.buffer_bytes
    }

    fn launch(&mut self, id: usize) {
        self.started[id] = true;
        let stream = self.is_stream(id);
        if stream {
            self.streams += 1;
        } else {
            self.copies += 1;
        }
        self.buffer_bytes += self.costs[id];
        self.summary.max_active_copies = self.summary.max_active_copies.max(self.copies);
        self.summary.max_active_streams = self.summary.max_active_streams.max(self.streams);
        self.summary.peak_buffer_bytes = self.summary.peak_buffer_bytes.max(self.buffer_bytes);
        let operation = self.plan.nodes[id].operation.clone();
        let parent = self.parent.clone();
        let cancel = self.cancel.clone();
        let buffer = self.limits.buffer_bytes;
        self.tasks.spawn(async move {
            (
                id,
                run_node(operation, stream, buffer, cancel, parent).await,
            )
        });
    }

    fn accept(&mut self, joined: Result<Completion, tokio::task::JoinError>) {
        match joined {
            Ok((id, result)) => self.complete(id, result),
            Err(failure) => self.fail(error(
                format!("execution task failed: {failure}"),
                first_span(self.plan),
            )),
        }
    }

    fn complete(&mut self, id: usize, result: NodeResult) {
        if self.is_stream(id) {
            self.streams -= 1;
        } else {
            self.copies -= 1;
        }
        self.buffer_bytes -= self.costs[id];
        let (report, bytes) = match result {
            Ok(value) => value,
            Err(error) => {
                self.fail(error);
                return;
            }
        };
        self.reports[id] = Some(report);
        self.summary.completed += 1;
        self.summary.stream_bytes = self.summary.stream_bytes.saturating_add(bytes);
        if self.failure.is_some() || self.cancel.is_cancelled() {
            return;
        }
        for next in &self.dependents[id] {
            self.remaining[*next] -= 1;
            if self.remaining[*next] == 0 {
                self.ready.push_back(*next);
            }
        }
    }

    fn is_stream(&self, id: usize) -> bool {
        let kinds = self.plan.nodes[id].kinds;
        kinds.source == FileClass::Stream || kinds.destination == FileClass::Stream
    }

    fn fail(&mut self, error: RuntimeError) {
        if self.failure.is_some() {
            return;
        }
        self.failure = Some(error);
        self.cancel.cancel();
    }

    fn finish(mut self) -> Result<ExecutionSummary, RuntimeError> {
        if let Some(error) = self.failure {
            return Err(error);
        }
        if self.summary.completed != self.plan.nodes.len() {
            return Err(error(
                "plan could not make progress".into(),
                first_span(self.plan),
            ));
        }
        for report in self.reports.into_iter().flatten() {
            self.summary.report.copied_files += report.copied_files;
            self.summary.report.created_directories += report.created_directories;
            self.summary.report.actions.extend(report.actions);
        }
        Ok(self.summary)
    }
}

fn buffer_cost(
    operation: &CopyOp,
    kinds: OperationKinds,
    limits: &ResourceLimits,
) -> Result<usize, RuntimeError> {
    if kinds.source != FileClass::Stream && kinds.destination != FileClass::Stream {
        return Ok(0);
    }
    let remote = [&operation.source, &operation.destination]
        .iter()
        .filter(|location| matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }))
        .count();
    let bytes = limits
        .buffer_bytes
        .checked_mul(1 + remote)
        .ok_or_else(|| error("flow buffer reservation overflow".into(), operation.span))?;
    if bytes > limits.memory_bytes {
        return Err(error("memory-limit cannot admit this flow's local and remote buffers; increase --memory-limit or lower --buffer-size".into(), operation.span));
    }
    Ok(bytes)
}

async fn schedule(
    plan: &Plan,
    limits: &ResourceLimits,
    cancel: CancellationToken,
    parent: Option<crate::telemetry::TraceContext>,
) -> Result<ExecutionSummary, RuntimeError> {
    Scheduler::new(plan, limits, cancel, parent)?.run().await
}

async fn run_node(
    operation: CopyOp,
    stream: bool,
    buffer: usize,
    cancel: CancellationToken,
    parent: Option<crate::telemetry::TraceContext>,
) -> Result<(ExecutionReport, u64), RuntimeError> {
    let mut trace = crate::telemetry::linked_span("task", parent.as_ref());
    let result = if stream {
        crate::streams::transfer(&operation, buffer, cancel)
            .await
            .map(|flow| {
                (
                    ExecutionReport {
                        actions: vec![format!(
                            "{} -> {} (stream: {} bytes, {:?})",
                            operation.source, operation.destination, flow.bytes, flow.end
                        )],
                        ..ExecutionReport::default()
                    },
                    flow.bytes,
                )
            })
    } else {
        let context = trace.context();
        let span = operation.span;
        tokio::task::spawn_blocking(move || {
            crate::telemetry::with_context(context.as_ref(), || runtime::execute_copy(&operation))
        })
        .await
        .map_err(|failure| error(format!("copy worker failed: {failure}"), span))
        .and_then(|result| result)
        .map(|report| (report, 0))
    };
    trace.finish_result(&result);
    result
}

fn first_span(plan: &Plan) -> Span {
    plan.nodes
        .first()
        .map(|node| node.operation.span)
        .unwrap_or_default()
}
fn error(message: String, span: Span) -> RuntimeError {
    RuntimeError { message, span }
}
