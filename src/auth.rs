use super::{CliResult, Issue};
use msync::compiler::{Backend, Program};
use msync::runtime::ssh::{self, SshContext};
use std::sync::mpsc;
use std::thread::{self, JoinHandle};
use tokio_util::sync::CancellationToken;

/// One foreground authentication/preparation/execution cancellation lifetime.
pub(super) struct Submission {
    context: Option<SshContext>,
    cancel: CancellationToken,
    done: CancellationToken,
    watcher: Option<JoinHandle<()>>,
    foreground_reason: Option<String>,
}

impl Submission {
    pub(super) fn structured(
        program: &msync::hir::Program,
        sessions: &SshContext,
        cancel: CancellationToken,
        lease: bool,
        label: &str,
    ) -> CliResult<Self> {
        use msync::hir::{Input, InstructionKind, Output};
        let mut required = Vec::new();
        for node in &program.body.instructions {
            let locations = match &node.kind {
                InstructionKind::Transfer {
                    source,
                    destination,
                } => {
                    let source = match source {
                        Input::File { location, .. } => Some(location),
                        _ => None,
                    };
                    let destination = match destination {
                        Output::File { location, .. } => Some(location),
                        _ => None,
                    };
                    source.into_iter().chain(destination).collect::<Vec<_>>()
                }
                InstructionKind::Spawn { executable, .. } => vec![executable],
                _ => Vec::new(),
            };
            for location in locations {
                required.push(&location.backend);
            }
        }
        let referenced = msync::hir::walk::backends(&program.body);
        let reuse = msync::hir::walk::instructions(&program.body)
            .into_iter()
            .any(reuses_transport);
        Self::start_with_cancel(
            &required,
            &referenced,
            true,
            &sessions.with_label(label),
            lease,
            cancel,
            reuse,
        )
    }

    pub(super) fn foreground(
        program: &Program,
        live: bool,
        sessions: &SshContext,
    ) -> CliResult<Self> {
        Self::start(program, live, sessions, false)
    }

    fn start(program: &Program, live: bool, sessions: &SshContext, lease: bool) -> CliResult<Self> {
        let backends: Vec<_> = program
            .operations
            .iter()
            .flat_map(|operation| [&operation.source.backend, &operation.destination.backend])
            .collect();
        Self::start_with_cancel(
            &backends,
            &backends,
            live,
            sessions,
            lease,
            CancellationToken::new(),
            true,
        )
    }

    fn start_with_cancel(
        required: &[&Backend],
        referenced: &[&Backend],
        live: bool,
        sessions: &SshContext,
        lease: bool,
        cancel: CancellationToken,
        reuse: bool,
    ) -> CliResult<Self> {
        let done = CancellationToken::new();
        let remote = live
            && referenced
                .iter()
                .any(|backend| matches!(backend, Backend::Ssh { .. }));
        let mut scope = Self {
            context: None,
            cancel,
            done,
            watcher: None,
            foreground_reason: None,
        };
        if !live {
            return Ok(scope);
        }
        #[allow(unused_mut)]
        let mut context = sessions.for_submission(scope.cancel.clone());
        #[cfg(windows)]
        if lease && (remote || reuse) {
            context = context
                .with_graphical_auth(std::env::current_exe().map_err(|error| {
                    Issue::new("cannot locate the SSH authentication helper")
                        .field("cause", error.to_string())
                })?)
                .map_err(|error| {
                    Issue::new("cannot prepare graphical SSH authentication")
                        .field("cause", error.to_string())
                })?;
        }
        if remote || reuse {
            scope.watcher = Some(watch(scope.cancel.clone(), scope.done.clone())?);
        }
        if lease {
            scope.foreground_reason = context.ensure_backends(required.iter().copied(), &scope.cancel).map_err(|cause| {
                Issue::new("SSH authentication did not complete")
                    .field("cause", cause)
                    .help("Retry the statement to authenticate again; Ctrl-C cancels a pending request.")
            })?;
        }
        if reuse {
            context = context.with_foreground_reuse();
        }
        scope.context = Some(context);
        Ok(scope)
    }

    pub(super) fn with_label(mut self, label: &str) -> Self {
        self.context = self.context.take().map(|context| context.with_label(label));
        self
    }

    pub(super) fn run<T>(&self, action: impl FnOnce() -> T) -> T {
        ssh::with_context(self.context.as_ref(), action)
    }

    pub(super) fn foreground_reason(&self) -> Option<&str> {
        self.foreground_reason.as_deref()
    }

    pub(super) fn cancellation(&self) -> CancellationToken {
        self.cancel.clone()
    }
}

fn reuses_transport(node: &msync::hir::Instruction) -> bool {
    use msync::hir::{Input, InstructionKind, Output, control::ControlInstruction as Control};
    match &node.kind {
        InstructionKind::Transfer {
            source,
            destination,
        } => {
            matches!(source, Input::File { location, .. } if matches!(location.backend, Backend::Ssh { .. }))
                || matches!(destination, Output::File { location, .. } if matches!(location.backend, Backend::Ssh { .. }))
        }
        InstructionKind::Spawn { executable, .. } => {
            matches!(executable.backend, Backend::Ssh { .. })
        }
        InstructionKind::Control(
            Control::For { .. }
            | Control::Loop { .. }
            | Control::Transfer { .. }
            | Control::RangeTransfer { .. }
            | Control::Spawn { .. },
        ) => true,
        _ => false,
    }
}

impl Drop for Submission {
    fn drop(&mut self) {
        self.done.cancel();
        if let Some(watcher) = self.watcher.take() {
            let _ = watcher.join();
        }
    }
}

fn watch(cancel: CancellationToken, done: CancellationToken) -> CliResult<JoinHandle<()>> {
    let (ready, initialized) = mpsc::sync_channel(1);
    let watcher = thread::Builder::new()
        .name("msync-auth-interrupt".into())
        .spawn(move || {
            let runtime = match tokio::runtime::Builder::new_current_thread()
                .enable_all()
                .build()
            {
                Ok(runtime) => runtime,
                Err(error) => {
                    let _ = ready.send(Err(error.to_string()));
                    return;
                }
            };
            runtime.block_on(async move {
                #[cfg(unix)]
                let interrupt =
                    tokio::signal::unix::signal(tokio::signal::unix::SignalKind::interrupt());
                #[cfg(windows)]
                let interrupt = tokio::signal::windows::ctrl_c();
                let mut interrupt = match interrupt {
                    Ok(interrupt) => interrupt,
                    Err(error) => {
                        let _ = ready.send(Err(error.to_string()));
                        return;
                    }
                };
                let _ = ready.send(Ok(()));
                tokio::select! {
                    _ = interrupt.recv() => cancel.cancel(),
                    _ = done.cancelled() => {},
                }
            });
        })
        .map_err(|error| format!("cannot start SSH interrupt watcher: {error}"))?;
    initialized
        .recv()
        .map_err(|_| "SSH interrupt watcher exited during initialization")?
        .map_err(|cause| Issue::new("cannot prepare SSH cancellation").field("cause", cause))?;
    Ok(watcher)
}
