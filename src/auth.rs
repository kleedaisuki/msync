use super::{CliResult, Issue};
use msync::compiler::{Backend, Program};
use msync::runtime::ssh::{self, SshContext};
use std::io::{self, IsTerminal};
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
    pub(super) fn new(program: &Program, live: bool, sessions: &SshContext) -> CliResult<Self> {
        Self::start(
            program,
            live,
            sessions,
            io::stdin().is_terminal() && io::stdout().is_terminal(),
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
        let cancel = CancellationToken::new();
        let done = CancellationToken::new();
        let remote = live
            && program.operations.iter().any(|operation| {
                matches!(operation.source.backend, Backend::Ssh { .. })
                    || matches!(operation.destination.backend, Backend::Ssh { .. })
            });
        let mut scope = Self {
            context: None,
            cancel,
            done,
            watcher: None,
            foreground_reason: None,
        };
        if !remote {
            return Ok(scope);
        }
        #[allow(unused_mut)]
        let mut context = sessions.for_submission(scope.cancel.clone());
        #[cfg(windows)]
        if lease {
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
        scope.watcher = Some(watch(scope.cancel.clone(), scope.done.clone())?);
        if lease {
            scope.foreground_reason = context.ensure_program_for_repl(program, &scope.cancel).map_err(|cause| {
                Issue::new("SSH authentication did not complete")
                    .field("cause", cause)
                    .help("Retry the statement to authenticate again; Ctrl-C cancels a pending request.")
            })?;
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
