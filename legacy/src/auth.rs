use super::CliResult;
#[cfg(windows)]
use super::Issue;
use msync::compiler::{Backend, Program};
use msync::runtime::ssh::{self, SshContext};
use tokio_util::sync::CancellationToken;

/// Submission metadata and cancellation belong to the existing execution runtime.
pub(super) struct Submission {
    context: Option<SshContext>,
    cancel: CancellationToken,
}

impl Submission {
    pub(super) fn structured(
        program: &msync::hir::Program,
        sessions: &SshContext,
        cancel: CancellationToken,
        interactive: bool,
        label: &str,
    ) -> CliResult<Self> {
        let graphical = cfg!(windows)
            && interactive
            && (msync::hir::walk::backends(&program.body)
                .into_iter()
                .any(|backend| matches!(backend, Backend::Ssh { .. }))
                || msync::hir::walk::instructions(&program.body)
                    .into_iter()
                    .any(may_use_dynamic_endpoint));
        Self::start(&sessions.with_label(label), true, cancel, graphical)
    }

    pub(super) fn foreground(_: &Program, live: bool, sessions: &SshContext) -> CliResult<Self> {
        Self::start(sessions, live, CancellationToken::new(), false)
    }

    fn start(
        sessions: &SshContext,
        live: bool,
        cancel: CancellationToken,
        graphical: bool,
    ) -> CliResult<Self> {
        if !live {
            return Ok(Self {
                context: None,
                cancel,
            });
        }
        #[allow(unused_mut)]
        let mut context = sessions
            .for_submission(cancel.clone())
            .with_native_clients();
        #[cfg(windows)]
        if graphical {
            context = context
                .with_graphical_auth(std::env::current_exe().map_err(|cause| {
                    Issue::new("cannot locate the SSH authentication helper")
                        .field("cause", cause.to_string())
                })?)
                .map_err(|cause| {
                    Issue::new("cannot prepare graphical SSH authentication")
                        .field("cause", cause.to_string())
                })?;
        }
        #[cfg(not(windows))]
        let _ = graphical;
        Ok(Self {
            context: Some(context),
            cancel,
        })
    }

    pub(super) fn with_label(mut self, label: &str) -> Self {
        self.context = self.context.take().map(|context| context.with_label(label));
        self
    }

    pub(super) fn run<T>(&self, action: impl FnOnce() -> T) -> T {
        ssh::with_context(self.context.as_ref(), action)
    }

    pub(super) fn cancellation(&self) -> CancellationToken {
        self.cancel.clone()
    }
}

fn may_use_dynamic_endpoint(node: &msync::hir::Instruction) -> bool {
    use msync::hir::{InstructionKind, control::ControlInstruction as Control};
    matches!(
        &node.kind,
        InstructionKind::Control(
            Control::For { .. }
                | Control::Loop { .. }
                | Control::Transfer { .. }
                | Control::RangeTransfer { .. }
                | Control::Spawn { .. }
        )
    )
}
