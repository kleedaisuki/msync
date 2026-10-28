//! The ordinary statement owner has no version store or retained transaction.

use super::*;

pub(super) struct Completed {
    pub report: ExecutionReport,
    pub remaining: hir::Program,
}

pub(super) fn merge(target: &mut ExecutionReport, source: ExecutionReport) {
    target.assignments += source.assignments;
    target.assigned_files += source.assigned_files;
    target.assigned_bytes = target.assigned_bytes.saturating_add(source.assigned_bytes);
    target.checkpoints += source.checkpoints;
    target.rollbacks += source.rollbacks;
    target.published_roots += source.published_roots;
    target.cleanup.extend(source.cleanup);
}

impl RuntimeSession {
    pub(super) fn observe_phase(&self, phase: super::super::progress::Phase) {
        if let Some(progress) = &self.observer {
            let mut event = progress.snapshot().event;
            event.phase = phase;
            progress.update(event);
        }
    }

    pub(super) fn observe_instruction(&self, node: &Instruction) {
        let Some(progress) = &self.observer else {
            return;
        };
        let mut event = super::super::progress::Event::new(match &node.kind {
            InstructionKind::Commit { .. } | InstructionKind::Rollback { .. } => {
                super::super::progress::Phase::Publish
            }
            _ => super::super::progress::Phase::Execute,
        });
        event.instruction = Some(node.id);
        if let InstructionKind::Transfer {
            source,
            destination,
        } = &node.kind
        {
            event.phase = super::super::progress::Phase::Acquire;
            event.source = Some(match source {
                Input::File { location, .. } => format!("{}::{}", location.backend, location.path),
                Input::Literal(_) => "<literal>".into(),
                Input::Port { process, port } => format!("program {}::{port:?}", process.0),
            });
            event.destination = Some(match destination {
                Output::File { location, .. } => format!("{}::{}", location.backend, location.path),
                Output::Stdin(process) => format!("program {}::stdin", process.0),
            });
        }
        progress.update(event);
    }

    pub(super) fn prepare_runtime(&mut self, program: &hir::Program) -> Result<Arc<hir::Program>> {
        self.observe(super::super::progress::Event::new(
            super::super::progress::Phase::Inspect,
        ));
        let mut trace = crate::telemetry::span("prepare");
        let result = crate::preparation::observe_runtime(program, self.planning_context());
        trace.finish_result(&result);
        let (resolved, context) = result.map_err(|reports| {
            let mut reports = reports.into_iter();
            let first = reports
                .next()
                .expect("preparation failure has a diagnostic");
            let mut error = ExecutionError::new(
                first.report.diagnostic.message.clone(),
                first.report.diagnostic.span,
            );
            error.report = first.report;
            if let Some(id) = first.instruction {
                error = error.with_operand(
                    self.origins.get(&id),
                    first.operand == crate::preparation::Operand::Destination,
                );
            }
            error
                .report
                .help
                .extend(reports.map(|report| report.report.diagnostic.message));
            error
        })?;
        // Interpreted host observations belong to this dispatch. Keep every
        // original instruction when an optimized rewrite has no pinned lease.
        self.planning = context;
        Ok(resolved)
    }

    pub(super) fn observe(&self, event: super::super::progress::Event) {
        if let Some(progress) = &self.observer {
            progress.update(event);
        }
    }

    pub(super) async fn run_ordinary(&mut self, program: &hir::Program) -> Result<Completed> {
        domain::validate(program)?;
        let mut options = self.options.clone();
        options.cancel = self.cancel.clone();
        let origins = self.origins.clone();
        let program = program.clone();
        let progress = self.observer.clone();
        let context = crate::telemetry::context();
        // A blocking filesystem call cannot be aborted safely. Its owner and
        // journal remain joined even after Ctrl-C requests cancellation.
        let cancel_on_drop = self.cancel.clone().drop_guard();
        self.ordinary_task = Some(tokio::task::spawn_blocking(move || {
            crate::telemetry::with_context(context.as_ref(), || {
                ordinary_submission(program, &options, &origins, progress)
            })
        }));
        let result = self.join_ordinary().await;
        cancel_on_drop.disarm();
        result
    }

    async fn join_ordinary(&mut self) -> Result<Completed> {
        let joined = self
            .ordinary_task
            .as_mut()
            .expect("ordinary worker is owned")
            .await;
        self.ordinary_task.take();
        joined.map_err(|cause| {
            ExecutionError::caused("ordinary assignment worker failed", Span::default(), cause)
        })?
    }

    pub(super) async fn settle_ordinary(&mut self, cancelling: bool) -> Result<()> {
        if self.ordinary_task.is_none() {
            return Ok(());
        }
        let result = self.join_ordinary().await;
        self.cancel = self.options.cancel.child_token();
        match result {
            Ok(completed) => {
                merge(&mut self.ordinary_report, completed.report);
                Ok(())
            }
            Err(error) if cancelling && error.is_cancelled() => Ok(()),
            Err(error) => Err(error),
        }
    }
}

fn ordinary_submission(
    program: hir::Program,
    options: &Options,
    origins: &crate::source::Origins,
    progress: Option<super::super::progress::Progress>,
) -> Result<Completed> {
    let mut owner = super::super::ordinary::Ordinary::new(options);
    owner.set_progress(progress);
    let mut report = ExecutionReport::default();
    let mut remaining = program.clone();
    let mut completed = 0;
    let mut failure = None;
    for node in &program.body.instructions {
        owner.set_origin(origins.get(&node.id).cloned());
        match owner.execute(node) {
            Ok(super::super::ordinary::Attempt::Completed(result)) => {
                merge(&mut report, result);
                completed += 1;
            }
            Ok(super::super::ordinary::Attempt::Unsupported) => break,
            Err(error) => {
                failure = Some(error.with_origin(origins.get(&node.id)));
                break;
            }
        }
    }
    let cleanup = owner.close();
    if let Some(mut failure) = failure {
        if report.assignments != 0 {
            failure.report.help.push(format!("{} earlier ordinary assignment(s) remain committed; this was not a script-wide transaction.", report.assignments));
        }
        for issue in cleanup {
            failure.retained_paths.extend(issue.retained_paths);
            failure
                .report
                .help
                .push(format!("cleanup: {}", issue.cause));
        }
        return Err(failure);
    }
    report.cleanup.extend(cleanup);
    // A type guard may discover a physical endpoint. Resume only the suffix;
    // earlier successful IO is never replayed through the managed interpreter.
    remaining.body.instructions.drain(..completed);
    Ok(Completed { report, remaining })
}

#[cfg(test)]
#[path = "ordinary_runtime_tests.rs"]
mod tests;
