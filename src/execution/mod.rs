//! Ordered basic-block interpretation with statement-local atomic publication.
//!
//! Compilation is pure. Filesystem observations belong to the currently admitted
//! block, and external children observe the real filesystem at their launch.

mod assignment;
mod blocks;
mod control_fs;
mod domain;
mod endpoint;
mod error;
mod fifo;
mod flow;
pub mod live;
mod ordinary;
mod path_policy;
mod program;
pub mod progress;
mod scheduler;
mod trace;
mod transfer;
mod wsl_source;

pub use error::{
    DiagnosticCategory, DiagnosticContext, DiagnosticLocation, DiagnosticPhase, ExecutionError,
};
pub use live::RuntimeSession;

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord)]
pub enum WorkId {
    Program(crate::hir::ProcessId),
    Flow {
        submission: u64,
        instruction: crate::hir::InstructionId,
    },
}

use crate::scheduler::ResourceLimits;
use crate::syntax::Span;
use error::Result;
use std::path::{Path, PathBuf};
use tokio_util::sync::CancellationToken;

#[derive(Clone, Debug)]
pub struct Options {
    pub scratch_root: PathBuf,
    /// Optional stable state root when the requested cwd cannot host private data.
    pub fallback_scratch_root: Option<PathBuf>,
    pub limits: ResourceLimits,
    pub cancel: CancellationToken,
    pub max_output_bytes: Option<u64>,
    pub(crate) shared_invocation: Option<crate::filesystem::InvocationRoot>,
}

impl Options {
    pub fn new(scratch_root: impl Into<PathBuf>) -> Self {
        Self {
            scratch_root: scratch_root.into(),
            fallback_scratch_root: None,
            limits: ResourceLimits::default(),
            cancel: CancellationToken::new(),
            max_output_bytes: None,
            shared_invocation: None,
        }
    }

    fn check(&self, span: Span) -> Result<()> {
        if self.cancel.is_cancelled() {
            return Err(ExecutionError::cancelled(
                "execution cancelled before the next statement",
                span,
            ));
        }
        Ok(())
    }
}

#[derive(Debug, Default)]
pub struct ExecutionReport {
    pub assignments: usize,
    pub assigned_files: usize,
    pub assigned_bytes: u64,
    pub published_roots: usize,
    pub cleanup: Vec<CleanupIssue>,
}

#[derive(Debug)]
pub struct CleanupIssue {
    pub cause: Box<dyn std::error::Error + Send + Sync>,
    pub retained_paths: Vec<PathBuf>,
    pub resolved: bool,
}

pub(super) fn merge(target: &mut ExecutionReport, source: ExecutionReport) {
    target.assignments += source.assignments;
    target.assigned_files += source.assigned_files;
    target.assigned_bytes = target.assigned_bytes.saturating_add(source.assigned_bytes);
    target.published_roots += source.published_roots;
    target.cleanup.extend(source.cleanup);
}

/// Read-only admission check for existing local or remote recovery state.
/// Missing and non-directory roots stay cold; present or inaccessible registries
/// require normal runtime recovery, including its ownership checks and diagnostics.
pub fn recovery_required(scratch_root: &Path, fallback: Option<&Path>) -> bool {
    root_may_need_recovery(scratch_root) || fallback.is_some_and(root_may_need_recovery)
}

fn root_may_need_recovery(scratch_root: &Path) -> bool {
    crate::filesystem::recovery::may_need_recovery(scratch_root)
        || endpoint::may_need_recovery(scratch_root)
}

/// Blocking convenience entry point for finite documents. Async callers use a session.
pub fn execute(program: &crate::hir::Program, options: &Options) -> Result<ExecutionReport> {
    let mut session = RuntimeSession::new(options)?;
    tokio::runtime::Builder::new_multi_thread()
        .worker_threads(options.limits.jobs.min(4))
        .enable_all()
        .build()
        .map_err(|cause| {
            ExecutionError::caused("cannot start execution runtime", Span::default(), cause)
        })?
        .block_on(async {
            let result = session.execute(program).await;
            let cleanup = session.shutdown().await;
            match (result, cleanup) {
                (Ok(report), Ok(())) => Ok(report),
                (Err(error), Ok(())) => Err(error),
                (Ok(mut report), Err(error)) => {
                    report.cleanup.push(CleanupIssue {
                        retained_paths: error.retained_paths.clone(),
                        cause: Box::new(error),
                        resolved: false,
                    });
                    Ok(report)
                }
                (Err(mut error), Err(cleanup)) => {
                    error.report.help.push(format!("cleanup: {cleanup}"));
                    error.retained_paths.extend(cleanup.retained_paths);
                    Err(error)
                }
            }
        })
}
