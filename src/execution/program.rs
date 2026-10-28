//! A program space owns its native resource view independently of connected IO.

use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::pin::Pin;

use tokio::io::{AsyncRead, AsyncWrite};

use crate::endpoint::{Backend, Location};
use crate::hir::{Argument, ProcessId, ReadPort};
use crate::process::{ExitInfo, ProcessConfig, SpoolReader};
use crate::syntax::Span;
use crate::transaction::filesystem::{Snapshot, SnapshotStore};

use super::domain;
use super::error::{ExecutionError, Result};

#[cfg(target_os = "linux")]
use crate::process::view_linux as view;
#[cfg(windows)]
use crate::process::view_windows as view;

pub(super) type Reader = Pin<Box<dyn AsyncRead + Send>>;
pub(super) type Writer = Pin<Box<dyn AsyncWrite + Send>>;
pub(super) type SharedView = (ProcessId, view::PreparedView);

pub(super) struct Program {
    pub id: ProcessId,
    pub cohort: ProcessId,
    pub transaction: crate::hir::TransactionId,
    pub span: Span,
    pub origin: Option<crate::source::InstructionOrigin>,
    pub baseline: Vec<(PathBuf, Snapshot)>,
    pub stdin: Option<Writer>,
    pub stdout: Option<SpoolReader>,
    pub stderr: Option<SpoolReader>,
    pub pid: u32,
    pub effects: crate::hir::graph::ProcessEffects,
    pub exit_signal: tokio_util::sync::CancellationToken,
    running: Option<view::RunningView>,
    completed: Option<Receipt>,
    lease: Option<super::scheduler::Lease>,
}

pub(super) struct Receipt {
    pub roots: Vec<(PathBuf, Snapshot)>,
    pub exit: ExitInfo,
    pub meta: Vec<u8>,
}

pub(super) struct Launch<'a> {
    pub id: ProcessId,
    pub transaction: crate::hir::TransactionId,
    pub span: Span,
    pub origin: Option<crate::source::InstructionOrigin>,
    pub executable: &'a Location,
    pub arguments: &'a [Argument],
    pub cwd: &'a Path,
    pub worker: &'a Path,
    pub scratch: &'a Path,
    pub config: ProcessConfig,
    pub lease: super::scheduler::Lease,
}

impl Program {
    #[cfg(test)]
    pub(super) fn exited_fixture(id: ProcessId, transaction: crate::hir::TransactionId) -> Self {
        let exit_signal = tokio_util::sync::CancellationToken::new();
        exit_signal.cancel();
        Self {
            id,
            cohort: id,
            transaction,
            span: Span::default(),
            origin: None,
            baseline: Vec::new(),
            stdin: None,
            stdout: None,
            stderr: None,
            pid: 1,
            effects: crate::hir::graph::ProcessEffects::mediated(
                crate::hir::effects::Effects::default(),
                "test-only",
                "signalled-exit",
            )
            .unwrap(),
            exit_signal,
            running: None,
            completed: None,
            lease: None,
        }
    }

    pub async fn launch(
        launch: Launch<'_>,
        assets: &SnapshotStore,
        roots: Vec<(PathBuf, Snapshot)>,
        shared: Option<(ProcessId, view::PreparedView)>,
        admit: impl FnOnce(ProcessId, &crate::hir::graph::ProcessEffects) -> Result<()>,
    ) -> Result<Self> {
        let executable = local(launch.executable, launch.span)?;
        let arguments = launch
            .arguments
            .iter()
            .map(|argument| match argument {
                Argument::Text(text) => Ok(OsString::from(text)),
                Argument::Path(path) => local(path, launch.span).map(PathBuf::into_os_string),
            })
            .collect::<Result<Vec<_>>>()?;
        let cohort = shared.as_ref().map_or(launch.id, |(cohort, _)| *cohort);
        let prepared = if let Some((_, prepared)) = shared {
            prepared
        } else {
            let provider = view::Provider::probe()
                .and_then(|provider| provider.with_worker(launch.worker))
                .map_err(|cause| {
                    ExecutionError::caused(
                        "program resource-view capability is unavailable",
                        launch.span,
                        cause,
                    )
                })?;
            let managed = roots
                .iter()
                .map(|(logical, snapshot)| view::ManagedRoot {
                    logical: logical.clone(),
                    snapshot: snapshot.clone(),
                })
                .collect();
            #[cfg(target_os = "linux")]
            let prepared = provider.prepare(assets, launch.scratch, managed);
            #[cfg(windows)]
            let prepared = {
                let _ = assets;
                provider.prepare(launch.scratch, managed)
            };
            prepared.map_err(|cause| {
                ExecutionError::caused(
                    "cannot prepare the program's immutable resource view",
                    launch.span,
                    cause,
                )
            })?
        };
        let effects = prepared.effects().clone();
        if let Err(mut failure) = admit(cohort, &effects) {
            let path = prepared.root().to_owned();
            if let Err(cause) = prepared.discard() {
                failure.retained_paths.push(path);
                failure
                    .report
                    .help
                    .push(format!("prepared-view cleanup failed: {cause}"));
            }
            return Err(failure);
        }
        let spec = view::ProgramSpec {
            executable,
            arguments,
            cwd: launch.cwd.to_owned(),
        };
        #[cfg(target_os = "linux")]
        let running = prepared.launch(&spec, launch.config);
        #[cfg(windows)]
        let running = prepared.launch(&spec, launch.config).await;
        let mut running = running.map_err(|cause| {
            ExecutionError::caused("cannot launch the mediated program", launch.span, cause)
        })?;
        #[cfg(target_os = "linux")]
        let pid = running.program_pid().await.map_err(|cause| {
            ExecutionError::caused(
                "cannot acknowledge the actual program PID",
                launch.span,
                cause,
            )
        })?;
        #[cfg(windows)]
        let pid = running.pid();
        #[cfg(target_os = "linux")]
        let (stdin, stdout, stderr) = {
            let process = running.process_mut();
            (
                process.take_stdin().map(|pipe| Box::pin(pipe) as Writer),
                process.take_stdout(),
                process.take_stderr(),
            )
        };
        #[cfg(windows)]
        let (stdin, stdout, stderr) = (
            running.take_stdin().map(|pipe| Box::pin(pipe) as Writer),
            running.take_stdout(),
            running.take_stderr(),
        );
        let exit_signal = running.exit_signal();
        Ok(Self {
            origin: launch.origin,
            id: launch.id,
            cohort,
            transaction: launch.transaction,
            span: launch.span,
            baseline: roots,
            stdin,
            stdout,
            stderr,
            pid,
            effects,
            exit_signal,
            running: Some(running),
            completed: None,
            lease: Some(launch.lease),
        })
    }

    pub fn take_reader(&mut self, port: ReadPort) -> Result<Reader> {
        let reader = match port {
            ReadPort::Stdout => self.stdout.take(),
            ReadPort::Stderr => self.stderr.take(),
            _ => {
                return Err(ExecutionError::new(
                    "this program member is a scalar, not a consuming stream",
                    self.span,
                ));
            }
        }
        .ok_or_else(|| {
            ExecutionError::new("the program stream already has a consumer", self.span)
        })?;
        Ok(Box::pin(reader))
    }

    pub fn close_unused_stdin(&mut self) {
        self.stdin.take();
    }

    pub fn fork_shared(&self) -> Option<(ProcessId, view::PreparedView)> {
        self.running
            .as_ref()
            .map(|running| (self.cohort, running.fork_shared()))
    }

    /// Join leaders and native drains for every peer before a shared export.
    pub async fn join(&mut self, cancel: &tokio_util::sync::CancellationToken) -> Result<()> {
        let Some(running) = self.running.as_mut() else {
            return Ok(());
        };
        tokio::select! {
            biased;
            () = cancel.cancelled() => Err(ExecutionError::cancelled("execution cancelled while joining program cohort", self.span)),
            joined = running.join() => joined.map_err(|cause| ExecutionError::caused("cannot join shared program cohort", self.span, cause)),
        }
    }

    pub async fn seal(
        &mut self,
        assets: &SnapshotStore,
        cancel: &tokio_util::sync::CancellationToken,
    ) -> Result<Option<&Receipt>> {
        let Some(running) = self.running.take() else {
            return Ok(None);
        };
        let sealed = running
            .seal_cancellable(assets, cancel)
            .await
            .map_err(|cause| {
                ExecutionError::caused(
                    "cannot seal the completed program resource view",
                    self.span,
                    cause,
                )
            })?;
        #[cfg(target_os = "linux")]
        let meta = sealed.scalar(ReadPort::Meta).map_err(|cause| {
            ExecutionError::caused("cannot read completed program metadata", self.span, cause)
        })?;
        #[cfg(windows)]
        let meta = exit_json(&sealed.exit);
        self.completed = Some(Receipt {
            roots: sealed
                .roots
                .into_iter()
                .map(|root| (root.logical, root.snapshot))
                .collect(),
            exit: sealed.exit,
            meta,
        });
        self.lease.take();
        Ok(self.completed.as_ref())
    }

    pub fn scalar(&self, port: ReadPort) -> Result<Vec<u8>> {
        if port == ReadPort::Pid {
            return Ok(format!("{}\n", self.pid).into_bytes());
        }
        let completed = self.completed.as_ref().ok_or_else(|| {
            ExecutionError::new("program completion has not been joined", self.span)
        })?;
        match port {
            ReadPort::ExitCode => Ok(format!("{}\n", completed.exit.exit_code).into_bytes()),
            ReadPort::Meta => Ok(completed.meta.clone()),
            _ => Err(ExecutionError::new(
                "this member is a consuming program stream",
                self.span,
            )),
        }
    }

    pub async fn read_scalar(
        &mut self,
        port: ReadPort,
        cancel: &tokio_util::sync::CancellationToken,
    ) -> Result<Vec<u8>> {
        if port == ReadPort::Pid || self.completed.is_some() {
            return self.scalar(port);
        }
        let running = self.running.as_mut().ok_or_else(|| {
            ExecutionError::new("program status has no retained native receipt", self.span)
        })?;
        tokio::select! {
            () = cancel.cancelled() => Err(ExecutionError::cancelled("program status observation was cancelled", self.span)),
            result = running.scalar(port) => result.map_err(|cause| ExecutionError::caused("cannot observe native program status", self.span, cause)),
        }
    }

    pub async fn cancel(&mut self) -> Result<()> {
        self.stdin.take();
        if let Some(running) = self.running.take() {
            running.cancel().await.map_err(|cause| {
                ExecutionError::caused(
                    "cannot cancel and physically join the program",
                    self.span,
                    cause,
                )
            })?;
        }
        self.lease.take();
        Ok(())
    }

    pub fn is_running(&self) -> bool {
        self.running.is_some()
    }

    pub fn is_sealed(&self) -> bool {
        self.completed.is_some()
    }
}

fn local(location: &Location, span: Span) -> Result<PathBuf> {
    if !matches!(
        location.backend,
        Backend::Local | Backend::Path | Backend::Env
    ) {
        return Err(ExecutionError::new(
            "this program endpoint requires a remote resource-view provider",
            span,
        )
        .help("Native local programs are never substituted for remote execution."));
    }
    let path = crate::runtime::local_path(location, true)
        .map_err(|message| ExecutionError::new(message, span))?
        .ok_or_else(|| {
            ExecutionError::new("a native executable or argv path was expected", span)
        })?;
    domain::normalize(&path, span)
}

#[cfg(windows)]
fn exit_json(exit: &ExitInfo) -> Vec<u8> {
    format!("{{\"pid\":{},\"exit_code\":{},\"success\":{},\"cancelled\":{},\"stdout_bytes\":{},\"stderr_bytes\":{}}}\n",
        exit.pid, exit.exit_code, exit.success, exit.cancelled, exit.stdout_bytes, exit.stderr_bytes).into_bytes()
}
