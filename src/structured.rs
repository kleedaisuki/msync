//! Structured frontend and runtime ownership at the human-facing boundary.

use super::*;

use msync::compiler::structured::Session;
use msync::execution::{Options as ExecutionOptions, RuntimeSession};
use msync::hir::{Argument, Input, InstructionKind, Output, Region};
use msync::pipeline::{self, Compilation};
use msync::preparation::PreparedProgram;
use msync::source::LocatedReport;
use std::collections::BTreeSet;

struct Job {
    id: u64,
    work: BTreeSet<msync::hir::TransactionId>,
    source: Compilation,
}

#[derive(Default)]
pub(super) struct State {
    pub compiler: Session,
    runtime: Option<RuntimeSession>,
    executor: Option<tokio::runtime::Runtime>,
    jobs: Vec<Job>,
    next_job: u64,
    notices: Vec<String>,
    errors: Vec<CliError>,
}

impl State {
    pub fn active(&self) -> bool {
        self.runtime.as_ref().is_some_and(RuntimeSession::has_work)
    }

    pub fn jobs(&self, color: Color) -> CliResult {
        if self.jobs.is_empty() {
            return print_status(
                "jobs: no active jobs; enter a transfer or /load FILE",
                color,
            );
        }
        for job in &self.jobs {
            print_status(&format!("job {}: running", job.id), color)?;
        }
        Ok(())
    }

    fn reconcile(&mut self) {
        let pending = self
            .runtime
            .as_ref()
            .map(RuntimeSession::work_ids)
            .unwrap_or_default();
        let mut retained = Vec::new();
        for mut job in std::mem::take(&mut self.jobs) {
            job.work.retain(|id| pending.contains(id));
            if job.work.is_empty() {
                self.notices.push(format!("job {}: completed", job.id));
            } else {
                retained.push(job);
            }
        }
        self.jobs = retained;
    }

    pub fn reap(&mut self, color: Color) -> CliResult {
        self.poll(color);
        self.flush_notices(color)
    }

    pub fn poll(&mut self, color: Color) {
        if self.jobs.is_empty() && !self.active() {
            return;
        }
        let (Some(runtime), Some(executor)) = (&mut self.runtime, &self.executor) else {
            return;
        };
        if !runtime.has_ready_work() {
            return;
        }
        if let Err(error) =
            dispatch_located(executor, "poll", color, &self.jobs, runtime.poll_ready())
        {
            let _ = executor.block_on(runtime.cancel_active());
            for job in std::mem::take(&mut self.jobs) {
                self.notices
                    .push(format!("job {}: stopped after execution failure", job.id));
            }
            self.errors.push(error);
            self.progress();
            return;
        }
        self.reconcile();
        self.progress();
    }

    fn flush_notices(&mut self, color: Color) -> CliResult {
        for message in std::mem::take(&mut self.notices) {
            print_status(&message, color)?;
        }
        for error in std::mem::take(&mut self.errors) {
            print_error(&error, color);
        }
        Ok(())
    }

    fn progress(&mut self) {
        let Some(runtime) = &mut self.runtime else {
            return;
        };
        let report = runtime.take_report();
        self.queue_progress(report);
    }

    fn queue_progress(&mut self, report: msync::execution::ExecutionReport) {
        for issue in report.cleanup.into_iter().filter(|issue| !issue.resolved) {
            self.notices.push(format!(
                "warning: {}; retained paths: {:?}",
                issue.cause, issue.retained_paths
            ));
        }
        if report.assignments != 0 || report.checkpoints != 0 || report.rollbacks != 0 {
            self.notices.push(format!(
                    "session progress: {} assignment(s), {} file(s), {} bytes; {} checkpoint(s), {} rollback(s)",
                    report.assignments,
                    report.assigned_files,
                    report.assigned_bytes,
                    report.checkpoints,
                    report.rollbacks
                ));
        }
    }

    pub fn submit(
        &mut self,
        input: SourceUnit<'_>,
        command: &str,
        dry_run: bool,
        options: &Args,
        incremental: bool,
    ) -> CliResult {
        let mut trace = msync::telemetry::span("submission");
        input.annotate(&mut trace);
        trace.attribute("command", command);
        trace.attribute("dry_run", dry_run.to_string());
        let result = self.submit_inner(input, command, dry_run, options, incremental);
        trace.finish_result(&result);
        result
    }

    fn submit_inner(
        &mut self,
        input: SourceUnit<'_>,
        command: &str,
        dry_run: bool,
        options: &Args,
        incremental: bool,
    ) -> CliResult {
        let mut candidate = self.compiler.clone();
        let planning = self
            .runtime
            .as_ref()
            .map(RuntimeSession::planning_context)
            .unwrap_or_default();
        let compiled = pipeline::compile_named(
            &mut candidate,
            input.text,
            input.name,
            &input.context,
            &planning,
        )
        .map_err(|reports| located(reports, options.color, "frontend"))?;
        if command == "check" {
            self.compiler = candidate;
            return print_text(&format!(
                "{}\n",
                presentation::paint("ok", "1;32", options.color.stdout())
            ));
        }
        if compiled.program.body.instructions.is_empty() && command == "run" && !dry_run {
            self.compiler = candidate;
            return Ok(());
        }
        let prepared = {
            let mut trace = msync::telemetry::span("prepare");
            let result = msync::preparation::prepare_with_context_detailed(
                &compiled.program,
                !dry_run,
                planning,
            )
            .map_err(|reports| {
                located(
                    reports
                        .into_iter()
                        .map(|report| compiled.preparation_report(report))
                        .collect(),
                    options.color,
                    "prepare",
                )
            });
            trace.finish_result(&result);
            result?
        };
        if matches!(command, "compile" | "plan") || dry_run {
            print_plan(&prepared, options.color)?;
            self.compiler = candidate;
            return Ok(());
        }
        let parent = msync::telemetry::context();
        let mut execution = msync::telemetry::linked_span("execute", parent.as_ref());
        execution.expect_completion();
        let execution_context = execution.context();
        let initialized = msync::telemetry::with_context(execution_context.as_ref(), || {
            self.ensure_runtime(&input.context, options)
        });
        if initialized.is_err() {
            execution.finish_result(&initialized);
            return initialized;
        }
        self.compiler = candidate;
        let runtime = self
            .runtime
            .as_mut()
            .expect("initialized execution session");
        let previous = runtime.work_ids();
        runtime.set_origins(compiled.origins.clone());
        let cancel = runtime.cancellation();
        let executor = self.executor.as_ref().expect("initialized async runtime");
        let outcome = executor.block_on(contextual(execution_context.clone(), async {
            let operation = async {
                if incremental {
                    runtime.submit_prepared(&prepared).await
                } else {
                    runtime.execute_prepared(&prepared).await
                }
            };
            tokio::pin!(operation);
            tokio::select! {
                result = &mut operation => result,
                _ = tokio::signal::ctrl_c() => {
                    msync::telemetry::event("cancellation.requested", &[("cause", "ctrl_c")], &[]);
                    cancel.cancel();
                    operation.await
                }
            }
        }));
        execution.finish_result(&outcome);
        let mut report = outcome.map_err(|error| {
            msync::telemetry::with_context(execution_context.as_ref(), || {
                execution_error(error, Some(&compiled), options.color)
            })
        })?;
        for issue in std::mem::take(&mut report.cleanup)
            .into_iter()
            .filter(|issue| !issue.resolved)
        {
            print_status(
                &format!(
                    "warning: {}; retained paths: {:?}",
                    issue.cause, issue.retained_paths
                ),
                options.color,
            )?;
        }
        self.reconcile();
        self.flush_notices(options.color)?;
        let pending = self
            .runtime
            .as_ref()
            .expect("initialized execution session")
            .work_ids();
        let added = pending
            .difference(&previous)
            .copied()
            .collect::<BTreeSet<_>>();
        if incremental && !added.is_empty() {
            self.next_job += 1;
            let id = self.next_job;
            self.jobs.push(Job {
                id,
                work: added,
                source: compiled,
            });
            print_status(
                &format!("job {id}: running in background; use /jobs, /wait or /stop"),
                options.color,
            )?;
            self.queue_progress(report);
            return self.flush_notices(options.color);
        }
        if !compiled.program.body.instructions.is_empty() {
            if self.active() && report.assignments == 0 {
                return print_status(
                    "submitted: existing jobs remain active; use /jobs, /wait or /stop",
                    options.color,
                );
            }
            print_status(
                &format!(
                    "done: {} assignment(s), {} file(s), {} bytes; {} checkpoint(s), {} rollback(s){}",
                    report.assignments,
                    report.assigned_files,
                    report.assigned_bytes,
                    report.checkpoints,
                    report.rollbacks,
                    if self.active() {
                        "; program jobs remain active; use /wait or /stop"
                    } else {
                        ""
                    },
                ),
                options.color,
            )?;
        }
        Ok(())
    }

    fn ensure_runtime(&mut self, context: &CompileContext, options: &Args) -> CliResult {
        if self.runtime.is_some() {
            return Ok(());
        }
        let mut execution = ExecutionOptions::new(context.pwd.join(".temp"));
        execution.limits = options.limits.clone();
        let mut runtime = RuntimeSession::new(&execution)
            .map_err(|error| execution_error(error, None, options.color))?;
        runtime
            .set_worker(env::current_exe().map_err(|error| {
                msync::telemetry::io_error("locate_worker", &error);
                Issue::new("cannot locate the program worker").field("cause", error.to_string())
            })?)
            .map_err(|error| execution_error(error, None, options.color))?;
        let executor = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .enable_all()
            .build()
            .map_err(|error| {
                msync::telemetry::io_error("runtime_init", &error);
                Issue::new("cannot start the execution runtime").field("cause", error.to_string())
            })?;
        self.runtime = Some(runtime);
        self.executor = Some(executor);
        Ok(())
    }

    pub fn wait(&mut self, stop: bool, color: Color) -> CliResult {
        self.reap(color)?;
        if !self.active() {
            return print_status(
                "jobs: no active jobs; enter a transfer or /load FILE",
                color,
            );
        }
        let (Some(runtime), Some(executor)) = (&mut self.runtime, &self.executor) else {
            return print_status("jobs: no active jobs", color);
        };
        let cancel = runtime.cancellation();
        let result = dispatch_located(
            executor,
            if stop { "stop" } else { "wait" },
            color,
            &self.jobs,
            async {
                if stop {
                    return runtime.cancel_active().await;
                }
                let operation = runtime.wait();
                tokio::pin!(operation);
                tokio::select! {
                    result = &mut operation => result,
                    _ = tokio::signal::ctrl_c() => {
                        msync::telemetry::event("cancellation.requested", &[("cause", "ctrl_c")], &[]);
                        cancel.cancel();
                        operation.await
                    }
                }
            },
        );
        if result.is_err() {
            let _ = executor.block_on(runtime.cancel_active());
        }
        for job in std::mem::take(&mut self.jobs) {
            let status = if stop {
                "stopped"
            } else if result.is_ok() {
                "completed"
            } else {
                "stopped after execution failure"
            };
            print_status(&format!("job {}: {status}", job.id), color)?;
        }
        self.progress();
        self.flush_notices(color)?;
        result
    }

    pub fn shutdown(&mut self, color: Color) -> CliResult {
        if let (Some(mut runtime), Some(executor)) = (self.runtime.take(), &self.executor) {
            dispatch(executor, "shutdown", color, runtime.shutdown())?;
        }
        Ok(())
    }

    pub fn finish(&mut self, stop: bool, color: Color) -> CliResult {
        let joined = if self.active() {
            self.wait(stop, color)
        } else {
            Ok(())
        };
        let closed = self.shutdown(color);
        joined.and(closed)
    }
}

impl Drop for State {
    fn drop(&mut self) {
        if let Err(error) = self.shutdown(Color::Never) {
            print_error(&error, Color::Never);
        }
    }
}

// Attach the retained dispatch context for each poll, never across suspension.
async fn contextual<F: std::future::Future>(
    context: Option<msync::telemetry::TraceContext>,
    future: F,
) -> F::Output {
    let mut future = std::pin::pin!(future);
    std::future::poll_fn(|cx| {
        msync::telemetry::with_context(context.as_ref(), || future.as_mut().poll(cx))
    })
    .await
}

fn dispatch<T>(
    executor: &tokio::runtime::Runtime,
    phase: &'static str,
    color: Color,
    future: impl std::future::Future<Output = Result<T, msync::execution::ExecutionError>>,
) -> CliResult<T> {
    dispatch_located(executor, phase, color, &[], future)
}

fn dispatch_located<T>(
    executor: &tokio::runtime::Runtime,
    phase: &'static str,
    color: Color,
    jobs: &[Job],
    future: impl std::future::Future<Output = Result<T, msync::execution::ExecutionError>>,
) -> CliResult<T> {
    let parent = msync::telemetry::context();
    let mut trace = msync::telemetry::linked_span("execute", parent.as_ref());
    trace.attribute("phase", phase);
    trace.expect_completion();
    let context = trace.context();
    let result = executor.block_on(contextual(context.clone(), future));
    trace.finish_result(&result);
    result.map_err(|error| {
        let source = error
            .transaction
            .and_then(|id| jobs.iter().find(|job| job.work.contains(&id)))
            .or_else(|| (jobs.len() == 1).then(|| &jobs[0]))
            .map(|job| &job.source);
        msync::telemetry::with_context(context.as_ref(), || execution_error(error, source, color))
    })
}

fn record_diagnostic(report: &LocatedReport, phase: &'static str) {
    let diagnostic = &report.report.diagnostic;
    msync::telemetry::event(
        "diagnostic",
        &[
            ("phase", phase),
            ("source.name", &report.source.name),
            ("diagnostic.message", &diagnostic.message),
        ],
        &[
            ("span.start", diagnostic.span.start as u64),
            ("span.end", diagnostic.span.end as u64),
        ],
    );
    for label in &report.report.labels {
        record_label(&report.source.name, label.span, &label.message, phase);
    }
    for label in &report.related {
        record_label(
            &label.site.source.name,
            label.site.span,
            &label.message,
            phase,
        );
    }
    for help in &report.report.help {
        msync::telemetry::event("diagnostic.help", &[("phase", phase), ("help", help)], &[]);
    }
}

fn record_label(source: &str, span: syntax::Span, message: &str, phase: &'static str) {
    msync::telemetry::event(
        "diagnostic.label",
        &[
            ("phase", phase),
            ("source.name", source),
            ("label.message", message),
        ],
        &[
            ("span.start", span.start as u64),
            ("span.end", span.end as u64),
        ],
    );
}

fn located(reports: Vec<LocatedReport>, color: Color, phase: &'static str) -> CliError {
    for report in &reports {
        record_diagnostic(report, phase);
    }
    CliError::Presented(
        reports
            .into_iter()
            .map(|report| report.render(color.stderr()))
            .collect::<Vec<_>>()
            .join("\n"),
    )
}

fn execution_error(
    error: msync::execution::ExecutionError,
    source: Option<&Compilation>,
    color: Color,
) -> CliError {
    msync::telemetry::record_error("execute", &error);
    for failure in &error.recovery {
        msync::telemetry::record_error("recovery", failure);
    }
    for failure in &error.compensation {
        msync::telemetry::record_error("compensation", failure);
    }
    for path in &error.retained_paths {
        let path = path.to_string_lossy();
        msync::telemetry::event(
            "retained_path",
            &[("phase", "execute"), ("path", &path)],
            &[],
        );
    }
    let mut report = (*error.report).clone();
    if let Some(cause) = error.cause {
        report.help.push(format!("Cause: {cause}"));
    }
    for failure in error.recovery {
        report.help.push(format!("Recovery: {failure}"));
    }
    for failure in error.compensation {
        report.help.push(format!("Compensation: {failure}"));
    }
    if !error.retained_paths.is_empty() {
        report
            .help
            .push(format!("Retained paths: {:?}", error.retained_paths));
    }
    if let Some(origin) = error.origin {
        return located(vec![origin.report(report, None)], color, "execute");
    }
    match source {
        Some(compiled) => located(vec![compiled.locate(report)], color, "execute"),
        None => {
            record_diagnostic(
                &LocatedReport::new(msync::source::Source::new("<runtime>", ""), report.clone()),
                "execute",
            );
            Issue::new(report.diagnostic.message)
                .help(report.help.join("\n"))
                .into()
        }
    }
}

fn print_plan(plan: &PreparedProgram, color: Color) -> CliResult {
    print_status(
        "Metadata preview only — Plan (compile); no file payloads or user programs executed",
        color,
    )?;
    print_region(&plan.program.body, 0, color, plan)?;
    for edge in &plan.graph.dependencies {
        print_text(&format!(
            "  needs: {} -> {} ({:?})\n",
            edge.before.0, edge.after.0, edge.wait
        ))?;
    }
    for rewrite in &plan.rewrites {
        let reason = match &rewrite.kind {
            msync::preparation::RewriteKind::Forward { producer, .. } => {
                format!("read forwarded from {}", producer.0)
            }
            msync::preparation::RewriteKind::DeadWrite { overwritten_by } => {
                format!("eliminated; overwritten by {}", overwritten_by.0)
            }
            msync::preparation::RewriteKind::RedundantWrite { same_as } => {
                format!("eliminated; same stable file value as {}", same_as.0)
            }
            msync::preparation::RewriteKind::CoalesceCreation { overwritten_by } => {
                format!(
                    "coalesced creation into adjacent file assignment {}",
                    overwritten_by.0
                )
            }
        };
        print_text(&format!(
            "  optimized: {} {reason}\n",
            rewrite.instruction.0
        ))?;
    }
    if !plan.is_execution_ready() {
        print_status(
            "Runtime refinement required: types, stream cohorts or verified process-resource views",
            color,
        )?;
    }
    Ok(())
}

fn print_region(region: &Region, depth: usize, color: Color, plan: &PreparedProgram) -> CliResult {
    for node in &region.instructions {
        let prefix = format!("{}{:04} ", "  ".repeat(depth), node.id.0);
        let text = match &node.kind {
            InstructionKind::Transfer {
                source,
                destination,
            } => {
                print_text(&format!("{prefix}transfer\n"))?;
                for (field, text) in [
                    ("from", input_text(source)),
                    ("  to", output_text(destination)),
                ] {
                    print_text(&format!(
                        "{}  {field}: {}\n",
                        "  ".repeat(depth),
                        msync::highlight::highlight(&presentation::escape(&text), color.stdout())
                    ))?;
                }
                if let Some(kinds) = plan.facts.get(&node.id) {
                    print_text(&format!(
                        "{}  type: {:?} -> {:?}\n",
                        "  ".repeat(depth),
                        kinds.source,
                        kinds.destination
                    ))?;
                }
                continue;
            }
            InstructionKind::Spawn {
                process,
                executable,
                arguments,
                ..
            } => format!(
                "program {} => {}({})",
                process.0,
                endpoint_text(executable),
                arguments
                    .iter()
                    .map(|argument| match argument {
                        Argument::Text(text) => format!("{text:?}"),
                        Argument::Path(path) => endpoint_text(path),
                    })
                    .collect::<Vec<_>>()
                    .join(", ")
            ),
            InstructionKind::Transaction { transaction, body } => {
                let name = plan
                    .program
                    .transactions
                    .iter()
                    .find(|info| info.id == *transaction)
                    .map(|info| format!("{} ({})", info.name, info.isolation.as_str()))
                    .unwrap_or_else(|| format!("#{}", transaction.0));
                print_text(&format!(
                    "{prefix}transaction {}\n",
                    presentation::escape(&name)
                ))?;
                print_region(body, depth + 1, color, plan)?;
                continue;
            }
            InstructionKind::Commit { transaction } => format!("commit {}", transaction.0),
            InstructionKind::Rollback { transaction, scope } => {
                format!("rollback {} {scope:?}", transaction.0)
            }
        };
        print_text(&format!(
            "{prefix}{}\n",
            msync::highlight::highlight(&presentation::escape(&text), color.stdout())
        ))?;
    }
    Ok(())
}

fn input_text(input: &Input) -> String {
    match input {
        Input::File { location, pattern } => format!(
            "{}{}",
            endpoint_text(location),
            pattern
                .as_ref()
                .map(|pattern| format!("/<{pattern}>"))
                .unwrap_or_default()
        ),
        Input::Literal(text) => format!("literal UTF-8 ({} bytes)", text.len()),
        Input::Port { process, port } => format!("program {}::{port:?}", process.0),
    }
}

fn output_text(output: &Output) -> String {
    match output {
        Output::File { location, mode } => format!("{} [{mode:?}]", endpoint_text(location)),
        Output::Stdin(process) => format!("program {}::stdin", process.0),
    }
}

fn endpoint_text(location: &msync::endpoint::Location) -> String {
    let path = if matches!(
        location.backend,
        msync::endpoint::Backend::Local
            | msync::endpoint::Backend::Path
            | msync::endpoint::Backend::Env
    ) {
        location.path.replace('\\', "/")
    } else {
        location.path.clone()
    };
    format!("{}::{path:?}", location.backend)
}
