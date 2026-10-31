//! Structured frontend and runtime ownership at the human-facing boundary.

use super::*;

use msync::compiler::structured::Session;
use msync::execution::{Options as ExecutionOptions, RuntimeSession};
use msync::hir::{Argument, Input, Instruction, InstructionKind, Output};
use msync::pipeline;
use msync::source::LocatedReport;
use std::collections::{BTreeMap, BTreeSet};
use std::sync::Arc;

mod unit;
use unit::ExecutionUnit;
mod jobs;
use jobs::Job;

#[derive(Clone, Copy)]
enum JoinMode {
    Wait,
    Stop,
    Kill,
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
    progress_enabled: bool,
    progress_settings: msync::config::ProgressSettings,
    ssh: msync::runtime::ssh::SshContext,
    last: Option<LastIr>,
}

struct LastIr {
    portable: Result<msync::ir::Structured, String>,
    context: CompileContext,
    bound: Option<msync::ir::Structured>,
}

impl State {
    pub fn read_input(
        &mut self,
        input: &mut tokio::sync::mpsc::Receiver<Result<editor::InputEvent, String>>,
        printer: &mut editor::NoticePrinter,
        color: Color,
    ) -> Result<editor::InputEvent, String> {
        enum Wake {
            Input(Option<Result<editor::InputEvent, String>>),
            Work,
        }
        let mut notice_error = None;
        loop {
            if !self.active() {
                let event = input
                    .blocking_recv()
                    .ok_or_else(|| "input reader exited without a result".to_owned())?;
                return notice_error.map_or(event, Err);
            }
            let executor = self.executor.as_ref().expect("active execution runtime");
            let runtime = self.runtime.as_ref().expect("active execution session");
            let mut wait = msync::telemetry::span("repl.wait");
            let wake = executor.block_on(async {
                tokio::select! {
                    event = input.recv() => Wake::Input(event),
                    _ = runtime.wait_ready() => Wake::Work,
                }
            });
            wait.attribute(
                "cause",
                match &wake {
                    Wake::Input(_) => "input",
                    Wake::Work => "completion",
                },
            );
            drop(wait);
            match wake {
                Wake::Input(event) => {
                    let event =
                        event.ok_or_else(|| "input reader exited without a result".to_owned())?;
                    return notice_error.map_or(event, Err);
                }
                Wake::Work => {
                    self.poll(color);
                    if let Err(error) = self.flush_idle_notices(printer, color) {
                        notice_error.get_or_insert(error);
                    }
                }
            }
        }
    }

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
        self.reconcile_status("completed");
    }

    fn reconcile_status(&mut self, status: &str) {
        let pending = self
            .runtime
            .as_ref()
            .map(RuntimeSession::work_ids)
            .unwrap_or_default();
        let mut retained = Vec::new();
        for mut job in std::mem::take(&mut self.jobs) {
            job.retain(&pending);
            if job.is_empty() {
                self.notices.push(format!("job {}: {status}", job.id));
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
            self.errors.push(error);
            self.reconcile_status("settled after a background failure");
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

    fn flush_idle_notices(
        &mut self,
        printer: &mut editor::NoticePrinter,
        color: Color,
    ) -> Result<(), String> {
        let had_errors = !self.errors.is_empty();
        for error in std::mem::take(&mut self.errors) {
            print_error(&error, color);
        }
        if had_errors {
            printer.refresh()?;
        }
        for message in std::mem::take(&mut self.notices) {
            printer.print(format!(
                "{}\n",
                presentation::human(message.trim_end(), color.stdout())
            ))?;
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
        if report.assignments != 0 {
            self.notices.push(format!(
                "session progress: {} assignment(s), {} file(s), {} bytes",
                report.assignments, report.assigned_files, report.assigned_bytes
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
        let lowered = pipeline::compile_relocatable_named(
            &mut candidate,
            input.text,
            input.name,
            &input.context,
            &planning,
        )
        .map_err(|reports| located(reports, options.color, "frontend"))?;
        let last = if incremental
            && command == "run"
            && !lowered.compilation.program.body.instructions.is_empty()
        {
            let portable = lowered
                .clone()
                .into_object()
                .map_err(|error| error.to_string())
                .and_then(msync::ir::Structured::relocatable);
            Some(LastIr {
                portable,
                context: input.context.clone(),
                bound: None,
            })
        } else {
            None
        };
        if let Some(output) = &options.output {
            let module = lowered
                .into_object()
                .map_err(|cause| object_error(cause, input.name, presentation::Stage::Compile))?;
            let bytes = module
                .encode()
                .map_err(|cause| object_error(cause, input.name, presentation::Stage::Compile))?;
            write_object(output, input.name, &bytes)?;
            self.compiler = candidate;
            return Ok(());
        }
        if command == "check" {
            self.compiler = candidate;
            return print_text(&format!(
                "{}\n",
                presentation::paint("ok", "1;32", options.color.stdout())
            ));
        }
        if matches!(command, "compile" | "plan") || dry_run {
            let compiled = ExecutionUnit::from(lowered.compilation);
            print_plan(&compiled, options.color)?;
            for relocation in &lowered.relocations {
                print_text(&format!(
                    "relocation: #{} {:?} = {:?} + {:?}\n",
                    relocation.instruction.0,
                    relocation.slot,
                    relocation.path.anchor,
                    relocation.path.suffix
                ))?;
            }
            self.compiler = candidate;
            if let Some(last) = last {
                self.last = Some(last);
            }
            return Ok(());
        }
        let compiled = {
            let mut trace = msync::telemetry::span("contextualize");
            let result = msync::context::contextualize_source(
                &lowered,
                &msync::context::Context::new(&input.context),
                planning,
            );
            trace.finish_result(&result);
            ExecutionUnit::from(result.map_err(|cause| link_error(cause, input.name))?)
        };
        if let Some(mut last) = last {
            last.bound = last
                .portable
                .as_ref()
                .ok()
                .and_then(|portable| portable.bind(&last.context).ok());
            self.last = Some(last);
        }
        self.execute_unit(compiled, candidate, &input.context, options, incremental)
    }

    pub fn submit_object(
        &mut self,
        input: ObjectUnit<'_>,
        command: &str,
        dry_run: bool,
        options: &Args,
        incremental: bool,
    ) -> CliResult {
        let mut trace = msync::telemetry::span("submission");
        trace.attribute("source.kind", "object");
        trace.attribute("source.path", input.name);
        trace.attribute("command", command);
        let result = self.submit_object_inner(input, command, dry_run, options, incremental);
        trace.finish_result(&result);
        result
    }

    fn submit_object_inner(
        &mut self,
        input: ObjectUnit<'_>,
        command: &str,
        dry_run: bool,
        options: &Args,
        incremental: bool,
    ) -> CliResult {
        let module = {
            let mut load = msync::telemetry::span("load_object");
            let result = msync::object::ObjectModule::decode(input.bytes);
            load.finish_result(&result);
            result.map_err(|cause| object_error(cause, input.name, presentation::Stage::Load))?
        };
        if let Some(output) = &options.output {
            let bytes = module
                .encode()
                .map_err(|cause| object_error(cause, input.name, presentation::Stage::Compile))?;
            return write_object(output, input.name, &bytes);
        }
        if command == "check" {
            return print_text(&format!(
                "{}\n",
                presentation::paint("ok", "1;32", options.color.stdout())
            ));
        }
        let mut candidate = self.compiler.clone();
        let compiled = {
            let mut link = msync::telemetry::span("contextualize");
            let result = msync::context::contextualize(
                &module,
                &msync::context::Context::new(&input.context),
                &mut candidate,
            );
            link.finish_result(&result);
            ExecutionUnit::from(result.map_err(|cause| link_error(cause, input.name))?)
        };
        if matches!(command, "compile" | "plan") || dry_run {
            print_plan(&compiled, options.color)?;
            if command == "run" {
                self.compiler = candidate;
            }
            return Ok(());
        }
        self.execute_unit(compiled, candidate, &input.context, options, incremental)
    }

    pub fn export_ir(&self, file: &str, relocatable: bool) -> CliResult {
        let last = self
            .last
            .as_ref()
            .ok_or("no transfer to export; submit or load a transfer first")?;
        let portable = last.portable.as_ref().map_err(Clone::clone)?;
        let ir = if relocatable {
            portable.clone()
        } else {
            match &last.bound {
                Some(bound) => bound.clone(),
                None => portable.bind(&last.context)?,
            }
        };
        artifact_cli::export(&msync::ir::Artifact::Structured(Box::new(ir)), file)
    }

    pub fn submit_ir(
        &mut self,
        ir: &msync::ir::Structured,
        context: &CompileContext,
        options: &Args,
        incremental: bool,
    ) -> CliResult {
        let mut candidate = self.compiler.clone();
        let compiled = ExecutionUnit::from(ir.contextualize(context, &mut candidate)?);
        if options.command == "plan-ir" || options.dry_run {
            return print_plan(&compiled, options.color);
        }
        self.execute_unit(
            compiled,
            candidate,
            ir.context.as_ref().unwrap_or(context),
            options,
            incremental,
        )
    }

    fn execute_unit(
        &mut self,
        compiled: ExecutionUnit,
        candidate: Session,
        context: &CompileContext,
        options: &Args,
        incremental: bool,
    ) -> CliResult {
        self.compiler = candidate;
        if compiled.program.body.instructions.is_empty() {
            return Ok(());
        }
        let compiled = Arc::new(compiled);
        let parent = msync::telemetry::context();
        let mut execution = msync::telemetry::linked_span("execute", parent.as_ref());
        execution.expect_completion();
        let execution_context = execution.context();
        let initialized = msync::telemetry::with_context(execution_context.as_ref(), || {
            self.ensure_runtime(context, options)
        });
        if initialized.is_err() {
            execution.finish_result(&initialized);
            return initialized;
        }
        let reporter = if options.trace.as_deref() == Some("-") {
            None
        } else {
            crate::progress::Reporter::start_with(options.color, options.settings.progress.clone())
                .map_err(|cause| {
                    Issue::new("cannot start execution progress").field("cause", cause.to_string())
                })?
        };
        let started = std::time::Instant::now();
        let runtime = self
            .runtime
            .as_mut()
            .expect("initialized execution session");
        let previous = runtime.work_ids();
        runtime.set_origins(compiled.origins.clone());
        runtime.set_progress(reporter.as_ref().map(crate::progress::Reporter::handle));
        let cancel = runtime.cancellation();
        let executor = self.executor.as_ref().expect("initialized async runtime");
        let lease = incremental && io::stdin().is_terminal() && io::stdout().is_terminal();
        let authentication = match auth::Submission::structured(
            &compiled.program,
            &self.ssh,
            cancel.clone(),
            lease,
            &compiled.source.name,
        ) {
            Ok(authentication) => authentication,
            Err(error) => {
                if cancel.is_cancelled() {
                    executor
                        .block_on(runtime.cancel_active())
                        .map_err(|cleanup| {
                            execution_error(cleanup, Some(&compiled), options.color)
                        })?;
                }
                return Err(error);
            }
        };
        let incremental = incremental && authentication.foreground_reason().is_none();
        if let Some(reason) = authentication.foreground_reason() {
            print_status(
                "SSH plan: running in foreground; Ctrl-C cancels authentication and transfer",
                options.color,
            )?;
            print_text(&format!("  reason: {}\n", presentation::escape(reason)))?;
        }
        let outcome = authentication.run(|| {
            executor.block_on(contextual(execution_context.clone(), async {
            let operation = async {
                if incremental {
                    runtime.submit(&compiled.program).await
                } else {
                    runtime.execute(&compiled.program).await
                }
            };
            tokio::pin!(operation);
            tokio::select! {
                result = &mut operation => result,
                _ = tokio::signal::ctrl_c() => {
                    msync::telemetry::event("cancellation.requested", &[("cause", "ctrl_c")], &[]);
                    cancel.cancel();
                    if options.trace.as_deref() != Some("-") {
                        interrupt_notice(options.color);
                    }
                    operation.await
                }
            }
        }))
        });
        runtime.set_progress(None);
        let added = runtime
            .work_ids()
            .difference(&previous)
            .copied()
            .collect::<BTreeSet<_>>();
        let partial = outcome.is_err().then(|| runtime.take_report());
        let job = if incremental && !added.is_empty() {
            let sources = jobs::RuntimeSources {
                instructions: runtime.work_instructions(),
                programs: runtime.work_processes(),
            };
            let admission = jobs::register_with_sources(
                &mut self.jobs,
                &mut self.next_job,
                added,
                &compiled,
                &sources,
            );
            let id = admission.id;
            for merged in &admission.merged {
                self.notices.push(format!(
                    "job {merged}: joined job {id} through program ports"
                ));
            }
            if outcome.is_err() {
                self.notices.push(format!(
                    "job {id}: retained after partial submission; use /jobs, /wait, /stop or /kill"
                ));
            }
            Some(admission)
        } else {
            None
        };
        self.reconcile();
        if let Some(report) = partial {
            self.queue_progress(report);
        }
        let observer = match reporter {
            Some(reporter) => reporter.finish().map_err(|cause| {
                Issue::new("cannot finish execution progress").field("cause", cause.to_string())
            }),
            None => Ok(()),
        };
        execution.finish_result(&outcome);
        let mut report = outcome.map_err(|error| {
            msync::telemetry::with_context(execution_context.as_ref(), || {
                execution_error(error, Some(&compiled), options.color)
            })
        })?;
        observer?;
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
        if let Some(admission) = job {
            let activity = if admission.created {
                "running in background"
            } else {
                "additional background work attached"
            };
            print_status(
                &format!(
                    "job {}: {activity}; use /jobs, /wait or /stop",
                    admission.id
                ),
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
                    "done: {} assignment(s), {} file(s), {} bytes; {:.1} ms{}",
                    report.assignments,
                    report.assigned_files,
                    report.assigned_bytes,
                    started.elapsed().as_secs_f64() * 1000.0,
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
        execution.max_output_bytes = options.spool_limit;
        execution.fallback_scratch_root = options.settings.execution.state_dir.clone();
        let runtime = RuntimeSession::new(&execution)
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
        self.progress_enabled = options.trace.as_deref() != Some("-");
        self.progress_settings = options.settings.progress.clone();
        Ok(())
    }

    pub fn wait(&mut self, stop: bool, color: Color) -> CliResult {
        self.join(if stop { JoinMode::Stop } else { JoinMode::Wait }, color)
    }

    pub fn force_stop(&mut self, color: Color) -> CliResult {
        self.join(JoinMode::Kill, color)
    }

    fn join(&mut self, mode: JoinMode, color: Color) -> CliResult {
        self.reap(color)?;
        if !self.active() {
            return print_status(
                "jobs: no active jobs; enter a transfer or /load FILE",
                color,
            );
        }
        if matches!(mode, JoinMode::Kill) {
            print_status(
                "force stop: terminating owned processes; remote children may survive; effects may be partial",
                color,
            )?;
        }
        let reporter = if self.progress_enabled {
            crate::progress::Reporter::start_with(color, self.progress_settings.clone()).map_err(
                |cause| {
                    Issue::new("cannot start execution progress").field("cause", cause.to_string())
                },
            )?
        } else {
            None
        };
        let show_notice = self.progress_enabled;
        let (Some(runtime), Some(executor)) = (&mut self.runtime, &self.executor) else {
            return print_status("jobs: no active jobs", color);
        };
        runtime.set_progress(reporter.as_ref().map(crate::progress::Reporter::handle));
        let cancel = runtime.cancellation();
        let result = dispatch_located(
            executor,
            match mode {
                JoinMode::Wait => "wait",
                JoinMode::Stop => "stop",
                JoinMode::Kill => "force_stop",
            },
            color,
            &self.jobs,
            async {
                match mode {
                    JoinMode::Stop => return runtime.cancel_active().await,
                    JoinMode::Kill => return runtime.force_stop_active().await,
                    JoinMode::Wait => {}
                }
                let operation = runtime.wait();
                tokio::pin!(operation);
                tokio::select! {
                    result = &mut operation => result,
                    _ = tokio::signal::ctrl_c() => {
                        msync::telemetry::event("cancellation.requested", &[("cause", "ctrl_c")], &[]);
                        cancel.cancel();
                        if show_notice {
                            interrupt_notice(color);
                        }
                        operation.await
                    }
                }
            },
        );
        runtime.set_progress(None);
        if let Some(reporter) = reporter {
            reporter.finish().map_err(|cause| {
                Issue::new("cannot finish execution progress").field("cause", cause.to_string())
            })?;
        }
        if result.is_err() {
            let _ = executor.block_on(runtime.cancel_active());
        }
        for job in std::mem::take(&mut self.jobs) {
            let status = match (mode, result.is_ok()) {
                (JoinMode::Kill, _) => "force-stopped; external effects may be partial",
                (JoinMode::Stop, _) => "stopped; external programs drained to their own exit",
                (JoinMode::Wait, true) => "completed",
                (JoinMode::Wait, false) => "stopped after execution failure",
            };
            print_status(&format!("job {}: {status}", job.id), color)?;
        }
        self.progress();
        self.flush_notices(color)?;
        result
    }

    pub fn shutdown(&mut self, color: Color) -> CliResult {
        if let (Some(mut runtime), Some(executor)) = (self.runtime.take(), &self.executor) {
            let result =
                dispatch_located(executor, "shutdown", color, &self.jobs, runtime.shutdown());
            self.queue_progress(runtime.take_report());
            let flushed = self.flush_notices(color);
            return result.and(flushed);
        }
        Ok(())
    }

    pub fn finish(&mut self, stop: bool, color: Color) -> CliResult {
        if stop {
            let result = self.shutdown(color);
            for job in std::mem::take(&mut self.jobs) {
                print_status(&format!("job {}: closed", job.id), color)?;
            }
            return result;
        }
        let joined = if self.active() {
            self.wait(false, color)
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

fn interrupt_notice(color: Color) {
    if !io::stderr().is_terminal() {
        return;
    }
    // Reporting a closed terminal must not detach a child that still owns effects.
    let _ = writeln!(
        anstream::AutoStream::new(io::stderr().lock(), anstream::ColorChoice::Always),
        "\r{}",
        presentation::human(
            "interrupt: draining started work; no later statement will start",
            color.stderr(),
        )
    );
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
        let source = jobs::source(jobs, error.context().instruction);
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

pub(super) fn located(reports: Vec<LocatedReport>, color: Color, phase: &'static str) -> CliError {
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

fn object_error(
    cause: msync::object::ObjectError,
    name: &str,
    stage: presentation::Stage,
) -> CliError {
    Issue::new(cause.message)
        .stage(stage)
        .field("object", name)
        .help(cause.help)
        .into()
}

fn link_error(cause: msync::context::ContextError, name: &str) -> CliError {
    let mut issue = Issue::new(cause.message)
        .stage(presentation::Stage::Evaluate)
        .field("module", name);
    if let Some(instruction) = cause.instruction {
        issue = issue.field("operation", format!("#{}", instruction.0));
    }
    if let Some(site) = cause.site {
        issue = issue.field("required by", source_location(&site));
    }
    for help in cause.help {
        issue = issue.help(help);
    }
    issue.into()
}

fn execution_error(
    error: msync::execution::ExecutionError,
    source: Option<&ExecutionUnit>,
    color: Color,
) -> CliError {
    let cancelled = error.is_cancelled();
    let presented = present_execution_error(error, source, color);
    if cancelled {
        presented.cancelled()
    } else {
        presented
    }
}

fn present_execution_error(
    error: msync::execution::ExecutionError,
    source: Option<&ExecutionUnit>,
    color: Color,
) -> CliError {
    use msync::execution::DiagnosticLocation;

    let context = error.context();
    let phase = context.phase.as_str();
    let stage = presentation::Stage::from(context.phase);
    let cause = error.native_cause().map(ToString::to_string);
    let site = runtime_site(&error, source, context);
    record_runtime_context(context, site.as_ref());
    msync::telemetry::record_error(phase, &error);
    for failure in &error.compensation {
        msync::telemetry::record_error("compensation", failure);
    }
    for path in &error.retained_paths {
        let path = path.to_string_lossy();
        msync::telemetry::event("retained_path", &[("phase", phase), ("path", &path)], &[]);
    }
    let mut report = (*error.report).clone();
    for failure in &error.compensation {
        report.help.push(format!("Compensation: {failure}"));
    }
    if !error.retained_paths.is_empty() {
        report
            .help
            .push(format!("Retained paths: {:?}", error.retained_paths));
    }
    if context.location == DiagnosticLocation::Primary {
        let mut primary = report.clone();
        if let Some(cause) = &cause {
            primary.help.push(format!("Cause: {cause}"));
        }
        let located = match &error.origin {
            Some(origin) => Some(origin.report(primary, None)),
            None if primary.diagnostic.span.end > primary.diagnostic.span.start => {
                source.map(|compiled| compiled.locate(primary))
            }
            None => None,
        };
        if let Some(located) = located {
            record_diagnostic(&located, phase);
            return CliError::Presented(located.render_with_title(stage.heading(), color.stderr()));
        }
    }

    record_unlocated_runtime(&report, context, site.as_ref());
    let mut issue = Issue::new(report.diagnostic.message)
        .stage(stage)
        .field("phase", phase)
        .field("action", context.action);
    if let Some(backend) = context.backend {
        issue = issue.field("backend", backend);
    }
    if let Some(instruction) = context.instruction {
        issue = issue.field("operation", format!("#{}", instruction.0));
    }
    if let Some(site) = site {
        issue = issue.field("required by", source_location(&site));
    }
    if context.location == DiagnosticLocation::Context
        && let Some(origin) = &error.origin
        && origin.definition != origin.site
    {
        issue = issue.field("expanded from", source_location(&origin.definition));
    }
    if let Some(cause) = cause {
        issue = issue.field("cause", cause);
    }
    for help in report.help {
        issue = issue.help(help);
    }
    issue.into()
}

fn runtime_site(
    error: &msync::execution::ExecutionError,
    source: Option<&ExecutionUnit>,
    context: msync::execution::DiagnosticContext,
) -> Option<msync::source::SourceSpan> {
    if context.location == msync::execution::DiagnosticLocation::None {
        return None;
    }
    error
        .origin
        .as_ref()
        .map(|origin| origin.site.clone())
        .or_else(|| {
            context
                .instruction
                .and_then(|id| source?.origins.get(&id).map(|origin| origin.site.clone()))
        })
}

fn source_location(site: &msync::source::SourceSpan) -> String {
    let text = &site.source.text;
    let mut start = site.span.start.min(text.len());
    while !text.is_char_boundary(start) {
        start -= 1;
    }
    let prefix = &text[..start];
    let line = prefix.bytes().filter(|byte| *byte == b'\n').count() + 1;
    let row = prefix.rsplit('\n').next().unwrap_or_default();
    format!("{}:{line}:{}", site.source.name, row.chars().count() + 1)
}

fn record_runtime_context(
    context: msync::execution::DiagnosticContext,
    site: Option<&msync::source::SourceSpan>,
) {
    use msync::execution::{DiagnosticCategory, DiagnosticLocation};
    let category = match context.category {
        DiagnosticCategory::Operation => "operation",
        DiagnosticCategory::Capability => "capability",
        DiagnosticCategory::Cleanup => "cleanup",
    };
    let location = match context.location {
        DiagnosticLocation::None => "none",
        DiagnosticLocation::Primary => "primary",
        DiagnosticLocation::Context => "context",
    };
    let mut attributes = vec![
        ("phase", context.phase.as_str()),
        ("category", category),
        ("location.role", location),
        ("action", context.action),
    ];
    if let Some(backend) = context.backend {
        attributes.push(("backend", backend));
    }
    if let Some(site) = site {
        attributes.push(("source.name", site.source.name.as_ref()));
    }
    let metrics = context
        .instruction
        .map(|id| ("instruction.id", id.0 as u64));
    msync::telemetry::event("diagnostic.context", &attributes, metrics.as_slice());
}

fn record_unlocated_runtime(
    report: &Report,
    context: msync::execution::DiagnosticContext,
    site: Option<&msync::source::SourceSpan>,
) {
    let phase = context.phase.as_str();
    let mut attributes = vec![
        ("phase", phase),
        ("diagnostic.message", report.diagnostic.message.as_str()),
    ];
    let mut metrics = Vec::new();
    if let Some(site) = site {
        attributes.push(("source.name", site.source.name.as_ref()));
        metrics.extend([
            ("span.start", site.span.start as u64),
            ("span.end", site.span.end as u64),
        ]);
    }
    msync::telemetry::event("diagnostic", &attributes, &metrics);
    for help in &report.help {
        msync::telemetry::event("diagnostic.help", &[("phase", phase), ("help", help)], &[]);
    }
}

fn print_plan(plan: &ExecutionUnit, color: Color) -> CliResult {
    print_status(
        "Static IR preview — ordered basic blocks and dependency graphs; no endpoint inspection or helper processes",
        color,
    )?;
    let nodes = plan
        .graph
        .nodes
        .iter()
        .map(|node| (node.instruction, node))
        .collect();
    for block in plan.program.blocks() {
        print_text(&format!("block {}:\n", block.id.0))?;
        print_block(block.instructions, &nodes, color, plan)?;
        for candidate in msync::hir::rewrite::adjacent_overwrites_in(block) {
            print_text(&format!(
                "guarded candidate: {:04} -> {:04}; retain original operations until this block's runtime guards authorize elimination\n",
                candidate.first.0, candidate.last.0
            ))?;
        }
    }
    for obligation in &plan.obligations {
        print_text(&format!("evaluation obligation: {obligation:?}\n"))?;
    }
    print_status(
        "Runtime obligations: finish the preceding block, evaluate only the next block against current filesystem state, validate publication and resource capacity, then schedule its graph",
        color,
    )
}

fn print_block(
    instructions: &[Instruction],
    nodes: &BTreeMap<msync::hir::InstructionId, &msync::hir::graph::Node>,
    color: Color,
    plan: &ExecutionUnit,
) -> CliResult {
    let depth = 1;
    for node in instructions {
        let Some(graph_node) = nodes.get(&node.id) else {
            return Err(
                Issue::new("compiled instruction is missing its dependency graph node")
                    .stage(presentation::Stage::Compile)
                    .field("operation", format!("#{}", node.id.0))
                    .into(),
            );
        };
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
                print_text(&format!(
                    "{}  type: {} -> {}; file kinds: unknown until runtime\n",
                    "  ".repeat(depth),
                    input_type(source),
                    output_type(destination),
                ))?;
                print_origin(node.id, depth, plan)?;
                print_effects(&graph_node.effects, depth)?;
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
            InstructionKind::Control(control) => {
                format!("control {} (evaluated at dispatch)", control.name())
            }
            InstructionKind::Barrier { kind } => format!("barrier {kind:?}"),
            InstructionKind::Await {
                process: Some(process),
            } => {
                format!("acquire program {}", process.0)
            }
            InstructionKind::Await { process: None } => "acquire pending work".into(),
        };
        print_text(&format!(
            "{prefix}{}\n",
            msync::highlight::highlight(&presentation::escape(&text), color.stdout())
        ))?;
        print_origin(node.id, depth, plan)?;
        print_effects(&graph_node.effects, depth)?;
    }
    let members = instructions
        .iter()
        .map(|node| node.id)
        .collect::<BTreeSet<_>>();
    for edge in plan
        .graph
        .dependencies
        .iter()
        .filter(|edge| members.contains(&edge.before) && members.contains(&edge.after))
    {
        print_text(&format!(
            "{}needs: {} -> {} ({:?})\n",
            "  ".repeat(depth + 1),
            edge.before.0,
            edge.after.0,
            edge.wait
        ))?;
    }
    Ok(())
}

fn print_origin(id: msync::hir::InstructionId, depth: usize, plan: &ExecutionUnit) -> CliResult {
    let Some(origin) = plan.origins.get(&id) else {
        return Ok(());
    };
    for (label, site) in [("source", &origin.site), ("definition", &origin.definition)] {
        if label == "definition" && origin.site == origin.definition {
            continue;
        }
        print_text(&format!(
            "{}  {label}: {} bytes {}..{}\n",
            "  ".repeat(depth),
            presentation::escape(&site.source.name),
            site.span.start,
            site.span.end
        ))?;
    }
    Ok(())
}

fn print_effects(effects: &msync::hir::effects::Effects, depth: usize) -> CliResult {
    use msync::hir::effects::AccessKind;

    let count = |kind| {
        effects
            .accesses
            .iter()
            .filter(|access| access.kind == kind)
            .count()
    };
    print_text(&format!(
        "{}  effects: {} read, {} write, {} consume; external: {}\n",
        "  ".repeat(depth),
        count(AccessKind::Read),
        count(AccessKind::Write),
        count(AccessKind::Consume),
        if effects.unknown_external {
            "unknown"
        } else {
            "none"
        },
    ))
}

fn input_type(input: &Input) -> &'static str {
    match input {
        Input::File { .. } => "path",
        Input::Literal(_) => "UTF-8 literal",
        Input::Port { port, .. } => match port {
            msync::hir::ReadPort::Stdout | msync::hir::ReadPort::Stderr => "program byte stream",
            _ => "program scalar",
        },
    }
}

fn output_type(output: &Output) -> &'static str {
    match output {
        Output::File { .. } => "path",
        Output::Stdin(_) => "program stdin",
    }
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

#[cfg(test)]
mod diagnostic_tests {
    use super::*;

    fn compilation(text: &str) -> ExecutionUnit {
        pipeline::compile_named(
            &mut Session::new(),
            text,
            "code.msync",
            &CompileContext {
                pwd: std::path::PathBuf::from(env!("CARGO_MANIFEST_DIR")),
                source_file: None,
            },
            &Default::default(),
        )
        .unwrap()
        .into()
    }

    fn failure(report: Report) -> msync::execution::ExecutionError {
        msync::execution::ExecutionError {
            origin: None,
            report: Box::new(report),
            cause: Some(Box::new(std::io::Error::from(std::io::ErrorKind::NotFound))),
            compensation: Vec::new(),
            retained_paths: Vec::new(),
        }
    }

    #[test]
    fn an_unlocated_runtime_error_never_uses_the_first_source_line() {
        let compiled = compilation("macro unused(src, des):\n    src -> des\n");
        let output = execution_error(
            failure(Report::new(
                "runtime storage unavailable",
                syntax::Span::default(),
            )),
            Some(&compiled),
            Color::Never,
        )
        .render(Color::Never);
        assert!(output.starts_with("execution error:"), "{output}");
        assert!(output.contains("phase: execute"), "{output}");
        assert!(output.contains("cause:"), "{output}");
        assert!(!output.contains("-->"), "{output}");
        assert!(!output.contains("macro unused"), "{output}");
        assert!(!output.contains('^'), "{output}");
    }

    #[test]
    fn an_operand_failure_keeps_its_token_but_has_an_execution_heading() {
        let compiled = compilation("missing -> output\n");
        let mut error = failure(Report::new(
            "source does not exist",
            syntax::Span { start: 0, end: 7 },
        ));
        error.origin = compiled
            .origins
            .values()
            .next()
            .cloned()
            .map(|origin| Box::new(origin.for_operand(false)));
        let output = execution_error(error, Some(&compiled), Color::Never).render(Color::Never);
        assert!(output.starts_with("execution error:"), "{output}");
        assert!(output.contains("--> code.msync:1:1"), "{output}");
        assert!(output.contains("^^^^^^^"), "{output}");
        assert_eq!(output.matches("error:").count(), 1, "{output}");
        assert!(!output.contains('\x1b'));
    }

    #[test]
    fn frontend_diagnostics_keep_the_existing_source_presentation() {
        let source = msync::source::Source::new("code.msync", "unknown::input -> output\n");
        let output = located(
            vec![LocatedReport::new(
                source,
                Report::new("unknown namespace", syntax::Span { start: 0, end: 7 }),
            )],
            Color::Never,
            "frontend",
        )
        .render(Color::Never);
        assert!(output.starts_with("error:"), "{output}");
        assert!(output.contains("--> code.msync:1:1"), "{output}");
    }

    #[test]
    fn contextual_locations_clamp_external_utf8_offsets_without_a_fake_caret() {
        let source = msync::source::Source::new("可莉.msync", "可莉\nabc");
        assert_eq!(
            source_location(&source.at(syntax::Span { start: 1, end: 1 })),
            "可莉.msync:1:1"
        );
        assert_eq!(
            source_location(&source.at(syntax::Span {
                start: usize::MAX,
                end: usize::MAX
            })),
            "可莉.msync:2:4"
        );
    }
}
