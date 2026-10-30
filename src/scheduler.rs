//! Typed planning and bounded execution of file and stream dependency graphs.

use std::collections::{BTreeMap, VecDeque};

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
    /// Aggregate transfer payload-buffer capacity, not process RSS.
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

/// One preparation's unoptimized, resolved transfers and disposable execution plan.
/// Persist `program`, not the filesystem observations/proofs embedded in `plan`.
#[derive(Clone, Debug)]
pub struct Prepared {
    pub program: Program,
    pub plan: Plan,
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
    prepare_snapshot_spanned(program, live, operands).map(|prepared| prepared.plan)
}

pub fn prepare_snapshot_spanned(
    program: &Program,
    live: bool,
    operands: &[(Span, Span)],
) -> Result<Prepared, RuntimeError> {
    let mut trace = crate::telemetry::span("prepare");
    let mut observations = Observations::default();
    let result = prepare_inner(program, live, operands, &mut observations);
    trace.finish_result(&result);
    trace.metric("inspection.local_probes", observations.local_probes);
    trace.metric("inspection.remote_paths", observations.remote_paths);
    trace.metric("inspection.remote_groups", observations.remote_groups);
    trace.metric("inspection.cache_hits", observations.cache_hits);
    trace.metric("inspection.leaf_probes", observations.native.leaf_probes);
    trace.metric(
        "inspection.ancestor_probes",
        observations.native.ancestor_probes,
    );
    trace.metric(
        "inspection.ancestor_hits",
        observations.native.ancestor_hits,
    );
    result
}

fn prepare_inner(
    program: &Program,
    live: bool,
    operands: &[(Span, Span)],
    observations: &mut Observations,
) -> Result<Prepared, RuntimeError> {
    let mut resolved = program.clone();
    let mut kinds = Vec::with_capacity(resolved.operations.len());
    let mut failures = Vec::new();
    let mut resolution_errors = Vec::with_capacity(resolved.operations.len());
    for operation in &mut resolved.operations {
        let result = observations
            .resolve(&mut operation.source, true)
            .map_err(|message| (false, message))
            .and_then(|()| {
                observations
                    .resolve(&mut operation.destination, false)
                    .map_err(|message| (true, message))
            });
        if result.is_ok() {
            observations.observe(&operation.source, true, live);
            observations.observe(&operation.destination, false, live);
        }
        resolution_errors.push(result.err());
    }
    observations.complete();
    for (index, operation) in resolved.operations.iter_mut().enumerate() {
        let result = match resolution_errors[index].take() {
            Some(failure) => Err(failure),
            None => inspect_operation(operation, observations),
        };
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
                        message: {
                            let location = if destination {
                                &operation.destination
                            } else {
                                &operation.source
                            };
                            let role = if destination { "Destination" } else { "Source" };
                            format!(
                                "{message}\n{role} endpoint: {} {}",
                                location.backend, location.path
                            )
                        },
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
                return Err(error(
                    format!(
                        "source does not exist and no preceding plan operation produces it\nResolved source: {} {}\nCorrect the source path or namespace binding, or create this source before execution.",
                        node.operation.source.backend, node.operation.source.path
                    ),
                    pair.0,
                ));
            }
        }
        validate_operation(&node.operation, node.kinds, pair)?;
    }
    Ok(Prepared {
        program: resolved,
        plan,
    })
}

#[derive(Default)]
struct Observations {
    native: runtime::Inspection,
    resolved: std::collections::HashMap<
        (crate::compiler::Location, bool),
        Result<crate::compiler::Location, String>,
    >,
    classes: std::collections::HashMap<crate::compiler::Location, Observation>,
    remote: std::collections::HashMap<Backend, Vec<crate::compiler::Location>>,
    local_probes: u64,
    remote_paths: u64,
    remote_groups: u64,
    cache_hits: u64,
}

enum Observation {
    Queued,
    Ready(Result<FileClass, String>),
}

impl Observations {
    fn resolve(
        &mut self,
        location: &mut crate::compiler::Location,
        source: bool,
    ) -> Result<(), String> {
        let key = (location.clone(), source);
        if let Some(result) = self.resolved.get(&key) {
            self.cache_hits += 1;
            *location = result.clone()?;
            return Ok(());
        }
        let result = snapshot_local(location, source).map(|()| location.clone());
        self.resolved.insert(key, result.clone());
        result.map(|_| ())
    }

    fn observe(&mut self, location: &crate::compiler::Location, source: bool, live: bool) {
        if self.classes.contains_key(location) {
            self.cache_hits += 1;
            return;
        }
        let observation = if live && matches!(location.backend, Backend::Wsl | Backend::Ssh { .. })
        {
            self.remote
                .entry(location.backend.clone())
                .or_default()
                .push(location.clone());
            self.remote_paths += 1;
            Observation::Queued
        } else {
            self.local_probes += 1;
            Observation::Ready(self.native.inspect(location, source))
        };
        self.classes.insert(location.clone(), observation);
    }

    fn complete(&mut self) {
        let mut groups: Vec<_> = std::mem::take(&mut self.remote).into_iter().collect();
        groups.sort_by_key(|(backend, _)| backend.to_string());
        for (backend, locations) in groups {
            self.remote_groups += 1;
            let paths: Vec<_> = locations
                .iter()
                .map(|location| location.path.clone())
                .collect();
            let results = match runtime::inspect_locations_live(&backend, &paths) {
                Ok(results) => results,
                Err(message) => vec![Err(message); paths.len()],
            };
            for (location, result) in locations.into_iter().zip(results) {
                self.classes.insert(location, Observation::Ready(result));
            }
        }
    }

    fn class(&self, location: &crate::compiler::Location) -> Result<FileClass, String> {
        match self.classes.get(location) {
            Some(Observation::Ready(result)) => result.clone(),
            _ => Err("metadata observation did not complete; no execution was started".into()),
        }
    }
}

fn inspect_operation(
    operation: &CopyOp,
    observations: &Observations,
) -> Result<OperationKinds, (bool, String)> {
    Ok(OperationKinds {
        source: observations
            .class(&operation.source)
            .map_err(|message| (false, message))?,
        destination: observations
            .class(&operation.destination)
            .map_err(|message| (true, message))?,
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
    trace.expect_completion();
    let ssh = runtime::ssh::current();
    let execution = async {
        limits
            .validate()
            .map_err(|message| error(message, first_span(plan)))?;
        validate_graph(plan)?;
        validate_execution_kinds(plan)?;
        schedule(plan, limits, cancel, trace.context()).await
    };
    let result = match ssh {
        Some(context) => context.scope(execution).await,
        None => execution.await,
    };
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

fn validate_execution_kinds(plan: &Plan) -> Result<(), RuntimeError> {
    for node in &plan.nodes {
        let produced = |resource: &crate::plan::Resource| {
            plan.nodes[..node.id].iter().any(|producer| {
                !matches!(
                    producer.kinds.destination,
                    FileClass::Stream | FileClass::Unknown
                ) && producer
                    .effects
                    .writes
                    .iter()
                    .any(|write| write.overlaps(resource))
            })
        };
        let source = node.kinds.source != FileClass::Unknown
            || node.effects.reads.first().is_some_and(produced);
        let destination = node.kinds.destination != FileClass::Unknown
            || node.effects.writes.first().is_some_and(produced);
        if !source || !destination {
            return Err(error("execution requires inspected endpoint kinds; prepare the program live before executing (offline plans are previews)".into(), node.operation.span));
        }
    }
    Ok(())
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

#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, PartialOrd, Ord)]
struct Demand {
    copies: usize,
    streams: usize,
    bytes: usize,
}

impl Demand {
    fn fits(self, available: Self) -> bool {
        self.copies <= available.copies
            && self.streams <= available.streams
            && self.bytes <= available.bytes
    }
}

#[derive(Default)]
struct Ready {
    classes: BTreeMap<Demand, VecDeque<(usize, usize)>>,
    sequence: usize,
}

impl Ready {
    fn push(&mut self, group: usize, demand: Demand) {
        self.classes
            .entry(demand)
            .or_default()
            .push_back((self.sequence, group));
        self.sequence += 1;
    }

    fn pop_fitting(&mut self, available: Demand) -> Option<usize> {
        // Compare class heads to preserve FIFO among eligible groups, without
        // visiting every queued member of a saturated resource class.
        let demand = self
            .classes
            .iter()
            .filter(|(demand, _)| demand.fits(available))
            .min_by_key(|(_, queue)| queue.front().unwrap().0)
            .map(|(demand, _)| *demand)?;
        let queue = self.classes.get_mut(&demand).unwrap();
        let (_, group) = queue.pop_front().unwrap();
        if queue.is_empty() {
            self.classes.remove(&demand);
        }
        Some(group)
    }
}

struct Scheduler<'a> {
    plan: &'a Plan,
    limits: &'a ResourceLimits,
    cancel: CancellationToken,
    parent: Option<crate::telemetry::TraceContext>,
    remaining: Vec<usize>,
    dependents: Vec<Vec<usize>>,
    ready: Ready,
    tasks: JoinSet<Completion>,
    reports: Vec<Option<ExecutionReport>>,
    costs: Vec<usize>,
    groups: Vec<Vec<usize>>,
    owners: Vec<usize>,
    demands: Vec<Demand>,
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
        let mut dependents = vec![Vec::new(); plan.nodes.len()];
        let mut costs = Vec::with_capacity(plan.nodes.len());
        for node in &plan.nodes {
            for dependency in &node.dependencies {
                dependents[*dependency].push(node.id);
            }
            costs.push(buffer_cost(&node.operation, node.kinds, limits)?);
        }
        let (groups, owners) = execution_groups(plan)?;
        let mut remaining = Vec::with_capacity(groups.len());
        let mut demands = Vec::with_capacity(groups.len());
        let mut ready = Ready::default();
        for group in &groups {
            let streams = group
                .iter()
                .filter(|id| is_stream(plan.nodes[**id].kinds))
                .count();
            let bytes = group
                .iter()
                .try_fold(0_usize, |total, id| total.checked_add(costs[*id]))
                .ok_or_else(|| {
                    error(
                        "pipeline buffer reservation overflow".into(),
                        first_span(plan),
                    )
                })?;
            let demand = Demand {
                copies: group.len() - streams,
                streams,
                bytes,
            };
            if !demand.fits(Demand {
                copies: limits.jobs,
                streams: limits.max_streams,
                bytes: limits.memory_bytes,
            }) {
                let span = group
                    .first()
                    .map(|id| plan.nodes[*id].operation.span)
                    .unwrap_or_default();
                return Err(error(
                    format!(
                        "pipeline must co-start {streams} streams and reserve {bytes} buffer bytes; increase --max-streams/--memory-limit or reduce --buffer-size"
                    ),
                    span,
                ));
            }
            let pending = group
                .iter()
                .map(|id| plan.nodes[*id].dependencies.len())
                .sum();
            remaining.push(pending);
            demands.push(demand);
        }
        let mut initial: Vec<_> = (0..groups.len()).filter(|id| remaining[*id] == 0).collect();
        initial.sort_unstable_by_key(|id| groups[*id].iter().min().copied().unwrap());
        for id in initial {
            ready.push(id, demands[id]);
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
            demands,
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
        while let Some(group) = self.ready.pop_fitting(Demand {
            copies: self.limits.jobs - self.copies,
            streams: self.limits.stream_slots() - self.streams,
            bytes: self.limits.memory_bytes - self.buffer_bytes,
        }) {
            for index in 0..self.groups[group].len() {
                self.launch(self.groups[group][index]);
            }
        }
    }

    fn launch(&mut self, id: usize) {
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
        let kinds = self.plan.nodes[id].kinds;
        let parent = self.parent.clone();
        let cancel = self.cancel.clone();
        let buffer = self.limits.buffer_bytes;
        let ssh = runtime::ssh::current();
        self.tasks.spawn(async move {
            let execution = run_node(operation, kinds, buffer, cancel, parent);
            let result = match ssh {
                Some(context) => context.scope(execution).await,
                None => execution.await,
            };
            (id, result)
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
            let group = self.owners[*next];
            self.remaining[group] -= 1;
            if self.remaining[group] == 0 {
                self.ready.push(group, self.demands[group]);
            }
        }
    }

    fn is_stream(&self, id: usize) -> bool {
        is_stream(self.plan.nodes[id].kinds)
    }

    fn fail(&mut self, error: RuntimeError) {
        if self.failure.is_some() {
            return;
        }
        self.failure = Some(error);
        self.cancel.cancel();
    }

    fn finish(&mut self) -> Result<ExecutionSummary, RuntimeError> {
        if let Some(error) = self.failure.take() {
            return Err(error);
        }
        if self.summary.completed != self.plan.nodes.len() {
            return Err(error(
                "plan could not make progress".into(),
                first_span(self.plan),
            ));
        }
        for report in self.reports.iter_mut().filter_map(Option::take) {
            self.summary.report.copied_files += report.copied_files;
            self.summary.report.created_directories += report.created_directories;
            self.summary.report.transferred_bytes += report.transferred_bytes;
            self.summary.report.actions.extend(report.actions);
        }
        Ok(std::mem::take(&mut self.summary))
    }
}

impl Drop for Scheduler<'_> {
    fn drop(&mut self) {
        self.cancel.cancel();
    }
}

fn is_stream(kinds: OperationKinds) -> bool {
    kinds.source == FileClass::Stream || kinds.destination == FileClass::Stream
}

fn buffer_cost(
    operation: &CopyOp,
    kinds: OperationKinds,
    limits: &ResourceLimits,
) -> Result<usize, RuntimeError> {
    let stream = kinds.source == FileClass::Stream || kinds.destination == FileClass::Stream;
    let remote = [&operation.source, &operation.destination]
        .iter()
        .filter(|location| stream && matches!(location.backend, Backend::Wsl | Backend::Ssh { .. }))
        .count();
    let remote_frame = usize::from(
        stream
            && matches!(
                operation.destination.backend,
                Backend::Wsl | Backend::Ssh { .. }
            ),
    );
    let bytes = limits
        .buffer_bytes
        .checked_mul(1 + remote + remote_frame)
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
    kinds: OperationKinds,
    buffer: usize,
    cancel: CancellationToken,
    parent: Option<crate::telemetry::TraceContext>,
) -> Result<(ExecutionReport, u64), RuntimeError> {
    let mut trace = crate::telemetry::linked_span("task", parent.as_ref());
    trace.expect_completion();
    trace.attribute_lazy("source.backend", || operation.source.backend.to_string());
    trace.attribute_lazy("source.path", || operation.source.path.clone());
    trace.attribute_lazy("destination.backend", || {
        operation.destination.backend.to_string()
    });
    trace.attribute_lazy("destination.path", || operation.destination.path.clone());
    let requested_cancel = cancel.clone();
    let stream = kinds.source == FileClass::Stream || kinds.destination == FileClass::Stream;
    let mut flow_end = None;
    let result = if stream {
        crate::io::transfer_known(&operation, kinds, buffer, cancel, trace.context())
            .await
            .map(|flow| {
                flow_end = Some(flow.end);
                (
                    ExecutionReport {
                        copied_files: usize::from(
                            flow.end == crate::io::FlowEnd::SourceClosed
                                && !matches!(
                                    kinds.destination,
                                    FileClass::Stream | FileClass::Unknown
                                ),
                        ),
                        transferred_bytes: flow.bytes,
                        actions: vec![format!(
                            "{} -> {} (flow: {} bytes, {:?})",
                            operation.source, operation.destination, flow.bytes, flow.end
                        )],
                        ..ExecutionReport::default()
                    },
                    flow.bytes,
                )
            })
    } else {
        let context = trace.context();
        let ssh = runtime::ssh::current();
        let span = operation.span;
        tokio::task::spawn_blocking(move || {
            runtime::ssh::with_context(ssh.as_ref(), || {
                crate::telemetry::with_context(context.as_ref(), || {
                    runtime::execute_copy_cancellable(&operation, buffer, cancel)
                })
            })
        })
        .await
        .map_err(|failure| error(format!("copy worker failed: {failure}"), span))
        .and_then(|result| result)
        .map(|report| (report, 0))
    };
    trace.finish_result(&result);
    if flow_end == Some(crate::io::FlowEnd::Cancelled)
        || (requested_cancel.is_cancelled() && result.is_err())
    {
        trace.cancelled();
    }
    if let Err(failure) = &result {
        trace.record_error("execute", failure);
    }
    if let Ok((report, bytes)) = &result {
        trace.metric("bytes.transferred", report.transferred_bytes.max(*bytes));
    }
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

#[cfg(test)]
mod admission_tests {
    use super::{Demand, Ready};
    use proptest::prelude::*;

    proptest! {
        #[test]
        fn demand_classes_match_oldest_fitting_fifo(
            events in prop::collection::vec((any::<bool>(), 0_usize..8, 0_usize..8, 0_usize..32), 0..500)
        ) {
            let mut ready = Ready::default();
            let mut reference = Vec::new();
            for (id, (enqueue, copies, streams, bytes)) in events.into_iter().enumerate() {
                let demand = Demand { copies, streams, bytes };
                if enqueue {
                    ready.push(id, demand);
                    reference.push((id, demand));
                    continue;
                }
                let expected = reference.iter().position(|(_, cost)| cost.fits(demand))
                    .map(|index| reference.remove(index).0);
                prop_assert_eq!(ready.pop_fitting(demand), expected);
                prop_assert!(ready.classes.values().all(|queue| !queue.is_empty()));
            }
            let unlimited = Demand { copies: usize::MAX, streams: usize::MAX, bytes: usize::MAX };
            for (id, _) in reference {
                prop_assert_eq!(ready.pop_fitting(unlimited), Some(id));
            }
            prop_assert!(ready.classes.is_empty());
        }
    }

    #[test]
    fn cohort_waits_for_every_external_dependency_edge_and_enqueues_once() {
        use super::{ResourceLimits, Scheduler, validate_graph};
        use crate::compiler::Compiler;
        use crate::plan::{FileClass, OperationKinds, Plan};
        use crate::runtime::ExecutionReport;
        use crate::syntax;
        use tokio_util::sync::CancellationToken;

        let program = Compiler::new()
            .compile(
                &syntax::parse(
                    "input0 -> output0\ninput1 -> output1\ninput2 -> output2\ninput3 -> output3\n",
                )
                .unwrap(),
            )
            .unwrap();
        let kinds = OperationKinds {
            source: FileClass::Regular,
            destination: FileClass::Missing,
        };
        let mut plan = Plan::build(&program, &[kinds; 4]);
        plan.nodes[2].dependencies = vec![0];
        plan.nodes[3].dependencies = vec![0, 1];
        plan.stream_groups = vec![vec![2, 3]];
        validate_graph(&plan).unwrap();
        let limits = ResourceLimits::default();
        let mut scheduler = Scheduler::new(&plan, &limits, CancellationToken::new(), None).unwrap();
        let capacity = Demand {
            copies: limits.jobs,
            streams: limits.max_streams,
            bytes: limits.memory_bytes,
        };
        assert_eq!(
            scheduler.ready.pop_fitting(capacity),
            Some(scheduler.owners[0])
        );
        assert_eq!(
            scheduler.ready.pop_fitting(capacity),
            Some(scheduler.owners[1])
        );
        assert_eq!(scheduler.ready.pop_fitting(capacity), None);
        scheduler.copies = 2;
        scheduler.buffer_bytes = scheduler.costs[0] + scheduler.costs[1];
        scheduler.complete(0, Ok((ExecutionReport::default(), 0)));
        assert_eq!(scheduler.remaining[scheduler.owners[2]], 1);
        assert_eq!(scheduler.ready.pop_fitting(capacity), None);
        scheduler.complete(1, Ok((ExecutionReport::default(), 0)));
        assert_eq!(
            scheduler.ready.pop_fitting(capacity),
            Some(scheduler.owners[2])
        );
        assert_eq!(scheduler.ready.pop_fitting(capacity), None);
    }

    #[test]
    fn blocked_large_cohort_does_not_hide_a_smaller_or_other_lane_cohort() {
        let mut ready = Ready::default();
        ready.push(
            0,
            Demand {
                copies: 1,
                streams: 2,
                bytes: 4,
            },
        );
        ready.push(
            1,
            Demand {
                copies: 1,
                streams: 0,
                bytes: 1,
            },
        );
        ready.push(
            2,
            Demand {
                copies: 0,
                streams: 1,
                bytes: 1,
            },
        );
        ready.push(
            3,
            Demand {
                copies: 1,
                streams: 0,
                bytes: 1,
            },
        );
        let available = Demand {
            copies: 1,
            streams: 1,
            bytes: 2,
        };
        assert_eq!(ready.pop_fitting(available), Some(1));
        assert_eq!(ready.pop_fitting(available), Some(2));
        assert_eq!(ready.pop_fitting(available), Some(3));
        assert_eq!(ready.pop_fitting(available), None);
        assert_eq!(
            ready.pop_fitting(Demand {
                copies: 1,
                streams: 2,
                bytes: 4
            }),
            Some(0)
        );
    }
}
