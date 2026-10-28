//! Native Windows filesystem views using an explicitly installed Sandboxie backend.
//!
//! Only a fresh owned box is configured. Complete frozen managed roots are seeded
//! through logical paths; no physical Sandboxie drive/tombstone layout is assumed.
//! Planning must not call runtime `prepare`, which creates private view data.
//! This is a managed-filesystem boundary, not universal undo for external services.

#[path = "windows_view/hive.rs"]
mod hive;
#[path = "windows_view/native.rs"]
mod native;
#[path = "windows_view/trees.rs"]
mod trees;
#[path = "windows_view/wire.rs"]
mod wire;
#[path = "windows_view/worker.rs"]
mod worker;

use std::collections::BTreeSet;
use std::ffi::OsString;
use std::fmt;
use std::fs;
use std::io;
use std::os::windows::io::AsRawHandle;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use same_file::Handle;
use tokio::io::{AsyncReadExt, AsyncWriteExt, DuplexStream};
use tokio::net::windows::named_pipe::ServerOptions;
use tokio::sync::{mpsc, watch};
use tokio_util::sync::CancellationToken;
use windows_sys::Win32::System::Pipes::GetNamedPipeClientProcessId;

use super::{ExitInfo, OutputBudget, ProcessConfig, ProcessError, Spool, SpoolReader, Workspace};
use crate::endpoint::{Backend, Location};
use crate::hir::ReadPort;
use crate::hir::effects::{Access, AccessKind, Effects, FileResource, Resource, Scope};
use crate::hir::graph::ProcessEffects;
use crate::transaction::filesystem::{InvocationRoot, Snapshot, SnapshotStore};
use wire::{Decode, Encode};

pub const WORKER_FLAG: &str = "--internal-windows-view-worker";

#[derive(Clone, Debug)]
pub struct ViewError {
    pub phase: &'static str,
    pub path: PathBuf,
    pub cause: Arc<io::Error>,
    pub details: Option<String>,
}

impl ViewError {
    fn new(phase: &'static str, path: &Path, cause: impl Into<io::Error>) -> Self {
        Self {
            phase,
            path: path.to_owned(),
            cause: Arc::new(cause.into()),
            details: None,
        }
    }
}

impl fmt::Display for ViewError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "Windows resource view {} at {}: {}",
            self.phase,
            self.path.display(),
            self.cause
        )?;
        if let Some(details) = &self.details {
            write!(f, "; {details}")?;
        }
        Ok(())
    }
}

impl std::error::Error for ViewError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(self.cause.as_ref())
    }
}

#[derive(Clone, Debug)]
pub struct ManagedRoot {
    pub logical: PathBuf,
    pub snapshot: Snapshot,
}

#[derive(Clone, Debug)]
pub struct ProgramSpec {
    pub executable: PathBuf,
    pub arguments: Vec<OsString>,
    pub cwd: PathBuf,
}

#[derive(Clone)]
pub struct Provider {
    api: Arc<native::Api>,
    worker: Option<PathBuf>,
}

impl Provider {
    pub fn from_installation(root: impl AsRef<Path>) -> Result<Self, ViewError> {
        native::Api::from_installation(root.as_ref())
            .map(|api| Self { api, worker: None })
            .map_err(|cause| ViewError::new("capability probe", root.as_ref(), cause))
    }

    pub fn probe() -> Result<Self, ViewError> {
        native::Api::detect()
            .map(|api| Self { api, worker: None })
            .map_err(|cause| ViewError::new("capability probe", Path::new("SbieDll.dll"), cause))
    }

    pub fn with_worker(mut self, path: impl AsRef<Path>) -> Result<Self, ViewError> {
        let path = path.as_ref();
        let canonical = trees::safe_path(path)
            .map_err(|cause| ViewError::new("validate worker", path, cause))?;
        if !canonical.is_file() {
            return Err(ViewError::new(
                "validate worker",
                path,
                wire::invalid("worker must be an ordinary executable"),
            ));
        }
        self.worker = Some(canonical);
        Ok(self)
    }

    /// Supply every manager-enrolled root visible to this program, not an argv-
    /// derived subset. An omitted mutable host read cannot prove independence.
    pub fn prepare(
        &self,
        scratch: &Path,
        roots: Vec<ManagedRoot>,
    ) -> Result<PreparedView, ViewError> {
        let worker = self.worker.clone().ok_or_else(|| ViewError::new("configure worker", scratch,
            io::Error::new(io::ErrorKind::InvalidInput, "call Provider::with_worker with an msync executable supporting the internal worker dispatch")))?;
        let scratch = trees::safe_path(scratch)
            .map_err(|cause| ViewError::new("validate scratch", scratch, cause))?;
        let roots = validate_roots(&scratch, &worker, roots)
            .map_err(|cause| ViewError::new("validate roots", &scratch, cause))?;
        let owned = OwnedBox::create(self.api.clone(), &scratch, &roots)?;
        let effects = effects(&roots, &owned.name)
            .map_err(|cause| ViewError::new("certify footprint", &scratch, cause))?;
        Ok(PreparedView {
            owned: Arc::new(owned),
            worker,
            roots,
            effects,
        })
    }
}

#[must_use = "launch this private view or explicitly discard it"]
#[derive(Clone)]
pub struct PreparedView {
    owned: Arc<OwnedBox>,
    worker: PathBuf,
    roots: Vec<ManagedRoot>,
    effects: ProcessEffects,
}

impl PreparedView {
    /// Fork only within one epoch; every payload must join before any export.
    pub fn fork_shared(&self) -> Self {
        self.clone()
    }

    fn cleanup_shared(&self) -> Result<(), ViewError> {
        if Arc::strong_count(&self.owned) == 1 {
            self.owned.cleanup_ref()?;
        }
        Ok(())
    }

    pub fn box_name(&self) -> &str {
        &self.owned.name
    }

    pub fn revalidate(&self) -> Result<(), ViewError> {
        self.owned.verify(&self.roots)
    }

    pub fn effects(&self) -> &ProcessEffects {
        &self.effects
    }
    pub fn root(&self) -> &Path {
        &self.owned.path
    }

    pub fn discard(self) -> Result<(), ViewError> {
        self.cleanup_shared()
    }

    pub async fn launch(
        self,
        spec: &ProgramSpec,
        config: ProcessConfig,
    ) -> Result<RunningView, ViewError> {
        let initialization = self.owned.initialization.lock().await;
        if self.owned.exported.load(Ordering::Acquire) {
            return Err(ViewError::new(
                "launch shared epoch",
                &self.owned.path,
                wire::invalid("a sealed shared epoch cannot launch another payload"),
            ));
        }
        self.revalidate()?;
        let spool_root = trees::safe_path(&config.scratch_root)
            .map_err(|cause| ViewError::new("validate spool root", &config.scratch_root, cause))?;
        let backing_is_owned = self
            .owned
            .invocation
            .as_ref()
            .map(|invocation| invocation.contains_private(&spool_root))
            .transpose()
            .map_err(|cause| {
                ViewError::new(
                    "verify spool ownership",
                    &spool_root,
                    io::Error::other(cause),
                )
            })?
            .unwrap_or(false);
        if self.roots.iter().any(|root| {
            (spool_root.starts_with(&root.logical) || root.logical.starts_with(&spool_root))
                && !backing_is_owned
        }) || spool_root.starts_with(&self.owned.path)
        {
            return Err(ViewError::new(
                "validate spool isolation",
                &spool_root,
                wire::invalid(
                    "parent output spools must be disjoint from managed roots and box files",
                ),
            ));
        }
        config.validate().map_err(|cause| {
            ViewError::new(
                "configure buffers",
                &config.scratch_root,
                io::Error::other(cause),
            )
        })?;
        if config.buffer_bytes > 1024 * 1024
            || !spec.executable.is_absolute()
            || !spec.cwd.is_absolute()
        {
            return Err(ViewError::new(
                "validate program",
                &spec.executable,
                wire::invalid("use absolute executable/cwd and a buffer no larger than one MiB"),
            ));
        }
        let mut contained = false;
        for root in &self.roots {
            contained = trees::contains_directory(&root.logical, &spec.cwd).map_err(|cause| {
                ViewError::new("validate program cwd identity", &spec.cwd, cause)
            })?;
            if contained {
                break;
            }
        }
        if !contained {
            return Err(ViewError::new(
                "validate program cwd",
                &spec.cwd,
                wire::invalid("program cwd must belong to the complete managed resource view"),
            ));
        }
        let launch_nonce = native::nonce()
            .map_err(|cause| ViewError::new("create launch nonce", &self.owned.path, cause))?;
        let pipe = format!(r"\\.\pipe\msync_{launch_nonce}");
        self.owned.set(
            "OpenPipePath",
            OsString::from(format!(r"\Device\NamedPipe\msync_{launch_nonce}")).as_os_str(),
            true,
        )?;
        let server = ServerOptions::new()
            .first_pipe_instance(true)
            .reject_remote_clients(true)
            .max_instances(1)
            .create(&pipe)
            .map_err(|cause| ViewError::new("create control pipe", &self.owned.path, cause))?;
        let args = [
            WORKER_FLAG.to_owned(),
            pipe,
            self.owned.name.clone(),
            self.owned.nonce.clone(),
            std::process::id().to_string(),
        ];
        let worker = self
            .owned
            .api
            .launch(&self.owned.name, &self.worker, &args, &self.owned.path)
            .map_err(|cause| ViewError::new("launch boxed worker", &self.worker, cause))?;
        self.owned.workers.lock().unwrap().insert(worker.pid);
        tokio::time::timeout(std::time::Duration::from_secs(30), server.connect())
            .await
            .map_err(|_| {
                ViewError::new(
                    "authenticate worker",
                    &self.owned.path,
                    io::Error::new(io::ErrorKind::TimedOut, "boxed worker did not connect"),
                )
            })?
            .map_err(|cause| ViewError::new("connect worker", &self.owned.path, cause))?;
        let mut peer = 0;
        if unsafe { GetNamedPipeClientProcessId(server.as_raw_handle(), &mut peer) } == 0
            || peer != worker.pid
        {
            return Err(ViewError::new(
                "authenticate worker PID",
                &self.owned.path,
                io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    "control client is not the launched worker",
                ),
            ));
        }
        self.owned
            .api
            .belongs(peer, &self.owned.name)
            .map_err(|cause| ViewError::new("authenticate box", &self.owned.path, cause))?;
        let (mut source, mut sink) = tokio::io::split(server);
        let (kind, nonce) = wire::receive(&mut source)
            .await
            .map_err(|cause| ViewError::new("worker handshake", &self.owned.path, cause))?;
        if kind == wire::FAILURE {
            return Err(remote_error(&nonce, &self.worker));
        }
        if kind != wire::HELLO || nonce != wire::hello(&self.owned.nonce) {
            return Err(ViewError::new(
                "worker handshake",
                &self.owned.path,
                wire::invalid(
                    "invalid worker nonce or protocol version; use the same msync release for the worker",
                ),
            ));
        }
        let mut message = Encode::default();
        message
            .text(spec.executable.as_os_str())
            .and_then(|()| message.text(spec.cwd.as_os_str()))
            .map_err(|cause| ViewError::new("encode program", &spec.executable, cause))?;
        message.integer(spec.arguments.len() as u64);
        for arg in &spec.arguments {
            message
                .text(arg)
                .map_err(|cause| ViewError::new("encode argv", &spec.executable, cause))?;
        }
        message.integer(config.buffer_bytes as u64);
        message.integer(config.max_output_bytes.unwrap_or(u64::MAX));
        wire::send(&mut sink, wire::SPEC, &message.0)
            .await
            .map_err(|cause| ViewError::new("send program", &self.owned.path, cause))?;
        for root in &self.roots {
            if self.owned.seeded.load(Ordering::Acquire) {
                let mut body = Encode::default();
                body.text(root.logical.as_os_str())
                    .map_err(|cause| ViewError::new("encode shared root", &root.logical, cause))?;
                wire::send(&mut sink, wire::ROOT_REFERENCE, &body.0)
                    .await
                    .map_err(|cause| ViewError::new("attach shared root", &root.logical, cause))?;
                continue;
            }
            trees::send_snapshot(
                &mut sink,
                &root.logical,
                &root.snapshot,
                config.buffer_bytes,
            )
            .await
            .map_err(|cause| ViewError::new("seed frozen root", &root.logical, cause))?;
        }
        wire::send(&mut sink, wire::SEEDED, &[])
            .await
            .map_err(|cause| ViewError::new("finish seed", &self.owned.path, cause))?;
        let (kind, body) = wire::receive(&mut source)
            .await
            .map_err(|cause| ViewError::new("launch payload", &spec.executable, cause))?;
        if kind == wire::FAILURE {
            return Err(remote_error(&body, &spec.executable));
        }
        let mut body = Decode::new(&body);
        let pid = body
            .integer()
            .and_then(|pid| {
                u32::try_from(pid).map_err(|_| wire::invalid("payload PID exceeds DWORD"))
            })
            .map_err(|cause| ViewError::new("payload PID", &spec.executable, cause))?;
        body.finish()
            .map_err(|cause| ViewError::new("payload PID", &spec.executable, cause))?;
        if kind != wire::STARTED || pid == 0 {
            return Err(ViewError::new(
                "payload launch",
                &spec.executable,
                wire::invalid("missing payload launch receipt"),
            ));
        }
        self.owned.seeded.store(true, Ordering::Release);
        drop(initialization);
        // The authenticated worker reports its native Child PID. Querying by PID
        // here would race a valid fast exit and could inspect a recycled PID.
        let workspace = Arc::new(Workspace::create(&config.scratch_root).map_err(|cause| {
            ViewError::new(
                "create spool",
                &config.scratch_root,
                io::Error::other(cause),
            )
        })?);
        let stdout = Spool::create(workspace.clone(), "stdout", config.buffer_bytes)
            .map_err(process_error)?;
        let stderr =
            Spool::create(workspace, "stderr", config.buffer_bytes).map_err(process_error)?;
        let (input, mut input_source) = tokio::io::duplex(config.buffer_bytes);
        let (commands, mut command_source) = mpsc::channel::<(u8, Vec<u8>)>(2);
        let cancel = CancellationToken::new();
        let writer_cancel = cancel.clone();
        let buffer = config.buffer_bytes;
        tokio::spawn(async move {
            let mut bytes = vec![0; buffer];
            let mut input_open = true;
            loop {
                let next = tokio::select! {
                    () = writer_cancel.cancelled() => Some((wire::CANCEL, Vec::new())),
                    command = command_source.recv() => command,
                    read = input_source.read(&mut bytes), if input_open => match read {
                        Ok(0) => { input_open = false; Some((wire::CLOSE_STDIN, Vec::new())) },
                        Ok(count) => Some((wire::STDIN, bytes[..count].to_vec())),
                        Err(_) => Some((wire::CANCEL, Vec::new())),
                    },
                };
                let Some((kind, bytes)) = next else { break };
                if wire::send(&mut sink, kind, &bytes).await.is_err()
                    || matches!(kind, wire::CANCEL | wire::EXPORT | wire::RELEASE)
                {
                    break;
                }
            }
        });
        let (exit_sender, exit) = watch::channel(None);
        let (done_sender, done) = watch::channel(None);
        let export = self.owned.path.join(format!("export-{launch_nonce}"));
        fs::create_dir(&export)
            .map_err(|cause| ViewError::new("create export root", &export, cause))?;
        let logical = self
            .roots
            .iter()
            .map(|root| root.logical.clone())
            .collect::<Vec<_>>();
        let read_cancel = cancel.clone();
        let reader_path = self.owned.path.clone();
        let budget = OutputBudget {
            used: 0.into(),
            limit: config.max_output_bytes,
        };
        tokio::spawn(async move {
            let result = read_worker(
                &mut source,
                (pid, stdout.writer, stderr.writer),
                &logical,
                &export,
                &exit_sender,
                &budget,
                &reader_path,
            )
            .await;
            if let Err(failure) = &result {
                let _ = exit_sender.send(Some(Err(failure.clone())));
                read_cancel.cancel();
            }
            let _ = done_sender.send(Some(result));
        });
        Ok(RunningView {
            prepared: self,
            worker,
            pid,
            stdin: Some(input),
            stdout: Some(stdout.reader),
            stderr: Some(stderr.reader),
            commands,
            cancel,
            exit,
            done,
            released: false,
        })
    }
}

pub struct RunningView {
    prepared: PreparedView,
    worker: native::NativeProcess,
    pid: u32,
    stdin: Option<DuplexStream>,
    stdout: Option<SpoolReader>,
    stderr: Option<SpoolReader>,
    commands: mpsc::Sender<(u8, Vec<u8>)>,
    cancel: CancellationToken,
    exit: watch::Receiver<Option<Result<ExitInfo, ViewError>>>,
    done: watch::Receiver<Option<Result<Vec<PathBuf>, ViewError>>>,
    released: bool,
}

impl RunningView {
    pub fn fork_shared(&self) -> PreparedView {
        self.prepared.fork_shared()
    }

    pub async fn join(&mut self) -> Result<(), ViewError> {
        self.wait().await.map(|_| ())
    }

    fn quiesce_payloads(&self) -> Result<(), ViewError> {
        let workers = self
            .prepared
            .owned
            .workers
            .lock()
            .unwrap()
            .iter()
            .copied()
            .collect::<Vec<_>>();
        self.prepared
            .owned
            .api
            .quiesce_except_many(&self.prepared.owned.name, &workers)
            .map_err(|cause| {
                ViewError::new("join shared box payloads", &self.prepared.owned.path, cause)
            })
    }

    fn release(&mut self) -> Result<(), ViewError> {
        self.prepared
            .owned
            .workers
            .lock()
            .unwrap()
            .remove(&self.worker.pid);
        self.prepared.cleanup_shared()?;
        self.released = true;
        Ok(())
    }
    /// Signal only this consumer's completion to its connected input flow.
    pub fn exit_signal(&self) -> CancellationToken {
        let mut receiver = self.exit.clone();
        let signal = CancellationToken::new();
        let completed = signal.clone();
        tokio::spawn(async move {
            while receiver.borrow().is_none() {
                if receiver.changed().await.is_err() {
                    break;
                }
            }
            completed.cancel();
        });
        signal
    }

    pub fn pid(&self) -> u32 {
        self.pid
    }
    pub fn worker_pid(&self) -> u32 {
        self.worker.pid
    }
    pub fn effects(&self) -> &ProcessEffects {
        &self.prepared.effects
    }
    pub fn take_stdin(&mut self) -> Option<DuplexStream> {
        self.stdin.take()
    }
    pub fn take_stdout(&mut self) -> Option<SpoolReader> {
        self.stdout.take()
    }
    pub fn take_stderr(&mut self) -> Option<SpoolReader> {
        self.stderr.take()
    }

    pub async fn wait(&mut self) -> Result<ExitInfo, ViewError> {
        self.stdin.take();
        let exit = receive_watch(&mut self.exit, &self.prepared.owned.path).await?;
        self.prepared
            .owned
            .joined
            .lock()
            .unwrap()
            .insert(self.worker.pid);
        Ok(exit)
    }

    pub async fn scalar(&mut self, port: ReadPort) -> Result<Vec<u8>, ViewError> {
        if port == ReadPort::Pid {
            return Ok(format!("{}\n", self.pid).into_bytes());
        }
        let exit = self.wait().await?;
        match port {
            ReadPort::ExitCode => Ok(format!("{}\n", exit.exit_code).into_bytes()),
            ReadPort::Meta => Ok(exit.metadata()),
            _ => Err(ViewError::new(
                "read scalar",
                &self.prepared.owned.path,
                wire::invalid("stdout/stderr require their unique stream reader"),
            )),
        }
    }

    pub async fn cancel(mut self) -> Result<ExitInfo, ViewError> {
        self.cancel.cancel();
        // Break a full payload stdin pipe independently of control-channel
        // backpressure. The worker remains alive to drain and report native IO.
        self.quiesce_payloads()?;
        let result = self.wait().await.map(|mut exit| {
            exit.cancelled = true;
            exit.success = false;
            exit
        });
        let completed = receive_watch(&mut self.done, &self.prepared.owned.path)
            .await
            .and_then(|roots| {
                if roots.is_empty() {
                    Ok(())
                } else {
                    Err(ViewError::new(
                        "discard cancelled view",
                        &self.prepared.owned.path,
                        wire::invalid("cancelled worker exported managed roots"),
                    ))
                }
            });
        self.worker
            .wait()
            .await
            .map_err(|cause| ViewError::new("join worker", &self.prepared.owned.path, cause))?;
        self.release()?;
        result.and_then(|exit| completed.map(|()| exit))
    }

    /// Join/discard a cancelled view without dropping its owning seal future.
    pub async fn seal_cancellable(
        mut self,
        store: &SnapshotStore,
        cancel: &CancellationToken,
    ) -> Result<SealedView, ViewError> {
        tokio::select! {
            biased;
            () = cancel.cancelled() => {
                let path = self.prepared.owned.path.clone();
                self.cancel().await?;
                return Err(ViewError::new("seal cancelled view", &path,
                    io::Error::new(io::ErrorKind::Interrupted, "cancelled program views are discarded")));
            }
            completed = self.wait() => { completed?; }
        }
        if cancel.is_cancelled() {
            let path = self.prepared.owned.path.clone();
            self.cancel().await?;
            return Err(ViewError::new(
                "seal cancelled view",
                &path,
                io::Error::new(
                    io::ErrorKind::Interrupted,
                    "cancelled program views are discarded",
                ),
            ));
        }
        self.seal(store).await
    }

    pub async fn seal(mut self, store: &SnapshotStore) -> Result<SealedView, ViewError> {
        let exit = self.wait().await?;
        let initialization = self.prepared.owned.initialization.lock().await;
        let workers = self.prepared.owned.workers.lock().unwrap().clone();
        if !workers.is_subset(&self.prepared.owned.joined.lock().unwrap()) {
            return Err(ViewError::new(
                "seal shared view",
                &self.prepared.owned.path,
                io::Error::new(
                    io::ErrorKind::WouldBlock,
                    "join every shared payload before exporting its epoch",
                ),
            ));
        }
        let export = !self.prepared.owned.exported.swap(true, Ordering::AcqRel);
        drop(initialization);
        if exit.cancelled {
            return Err(ViewError::new(
                "seal cancelled view",
                &self.prepared.owned.path,
                io::Error::new(
                    io::ErrorKind::Interrupted,
                    "cancelled views cannot be committed",
                ),
            ));
        }
        self.quiesce_payloads()?;
        self.commands
            .send((
                if export { wire::EXPORT } else { wire::RELEASE },
                Vec::new(),
            ))
            .await
            .map_err(|cause| {
                ViewError::new(
                    "request export",
                    &self.prepared.owned.path,
                    io::Error::other(cause),
                )
            })?;
        let paths = receive_watch(&mut self.done, &self.prepared.owned.path).await?;
        self.worker
            .wait()
            .await
            .map_err(|cause| ViewError::new("join worker", &self.prepared.owned.path, cause))?;
        let mut roots = Vec::new();
        for (logical, physical) in self.prepared.roots.iter().zip(paths) {
            let snapshot = store.capture(&physical).map_err(|cause| {
                ViewError::new("capture export", &physical, io::Error::other(cause))
            })?;
            roots.push(ManagedRoot {
                logical: logical.logical.clone(),
                snapshot,
            });
        }
        self.release()?;
        Ok(SealedView {
            roots,
            exit,
            effects: self.prepared.effects.clone(),
            stdout: self.stdout.take(),
            stderr: self.stderr.take(),
        })
    }
}

impl Drop for RunningView {
    fn drop(&mut self) {
        self.cancel.cancel();
        if !self.released
            && self.prepared.owned.section_owned.load(Ordering::Acquire)
            && !self.prepared.owned.cleaned.load(Ordering::Acquire)
        {
            let _ = self.prepared.owned.api.kill_all(&self.prepared.owned.name);
        }
    }
}

pub struct SealedView {
    pub roots: Vec<ManagedRoot>,
    pub exit: ExitInfo,
    pub effects: ProcessEffects,
    pub stdout: Option<SpoolReader>,
    pub stderr: Option<SpoolReader>,
}

async fn receive_watch<T: Clone>(
    source: &mut watch::Receiver<Option<Result<T, ViewError>>>,
    path: &Path,
) -> Result<T, ViewError> {
    loop {
        if let Some(result) = source.borrow().clone() {
            return result;
        }
        source.changed().await.map_err(|cause| {
            ViewError::new("join control reader", path, io::Error::other(cause))
        })?;
    }
}

fn process_error(cause: ProcessError) -> ViewError {
    let path = cause.path.clone().unwrap_or_default();
    ViewError::new("output spool", &path, io::Error::other(cause))
}

fn remote_error(bytes: &[u8], path: &Path) -> ViewError {
    match worker::decode_failure(bytes) {
        Ok((cause, details)) => {
            let mut failure = ViewError::new("boxed worker", path, cause);
            failure.details = Some(details);
            failure
        }
        Err(cause) => ViewError::new("decode worker failure", path, cause),
    }
}

async fn read_worker(
    source: &mut (impl tokio::io::AsyncRead + Unpin),
    spools: (u32, super::SpoolWriter, super::SpoolWriter),
    roots: &[PathBuf],
    export: &Path,
    exit: &watch::Sender<Option<Result<ExitInfo, ViewError>>>,
    budget: &OutputBudget,
    path: &Path,
) -> Result<Vec<PathBuf>, ViewError> {
    let (pid, mut stdout, mut stderr) = spools;
    let result = async {
        let mut exported = Vec::new();
        let mut exited = None;
        loop {
            let (kind, bytes) = wire::receive(source).await?;
            match kind {
                wire::STDOUT | wire::STDERR => {
                    if exited.is_some() {
                        return Err(wire::invalid(
                            "payload output followed its physical exit receipt",
                        ));
                    }
                    budget.reserve(bytes.len() as u64)?;
                    let spool = if kind == wire::STDOUT {
                        &mut stdout
                    } else {
                        &mut stderr
                    };
                    spool.file.write_all(&bytes).await?;
                    spool.file.flush().await?;
                    spool.publish(bytes.len() as u64);
                }
                wire::EXIT => {
                    if exited.is_some() {
                        return Err(wire::invalid("duplicate payload exit receipt"));
                    }
                    let info = worker::decode_exit(&bytes)?;
                    let out = stdout.state.lock().unwrap_or_else(|error| error.into_inner()).written;
                    let err = stderr.state.lock().unwrap_or_else(|error| error.into_inner()).written;
                    if info.pid != pid || info.stdout_bytes != out || info.stderr_bytes != err {
                        return Err(wire::invalid("payload receipt does not match its PID and physically forwarded byte counts"));
                    }
                    exited = Some(info.cancelled);
                    stdout.complete(Ok(()));
                    stderr.complete(Ok(()));
                    let _ = exit.send(Some(Ok(info)));
                }
                wire::ROOT => {
                    if exited != Some(false) {
                        return Err(wire::invalid(
                            "export requires a joined non-cancelled payload",
                        ));
                    }
                    let mut body = Decode::new(&bytes);
                    let logical = body.path()?;
                    body.finish()?;
                    if roots.get(exported.len()) != Some(&logical) {
                        return Err(wire::invalid("export root differs from enrolled namespace"));
                    }
                    let target = export.join(format!("root-{}", exported.len()));
                    trees::receive_tree(source, &target).await?;
                    exported.push(target);
                }
                wire::COMPLETE if bytes.is_empty() => {
                    if exited.is_none() || (exited == Some(false) && exported.len() != roots.len())
                    {
                        return Err(wire::invalid(
                            "worker completed without the full managed namespace",
                        ));
                    }
                    return Ok(exported);
                }
                wire::RELEASE if bytes.is_empty() && exited == Some(false) && exported.is_empty() => {
                    return Ok(exported);
                }
                wire::FAILURE => return Err(io::Error::other(remote_error(&bytes, path))),
                _ => return Err(wire::invalid("unexpected worker output frame")),
            }
        }
    }
    .await;
    result.map_err(|cause| ViewError::new("receive worker output", path, cause))
}

struct OwnedBox {
    api: Arc<native::Api>,
    name: String,
    nonce: String,
    path: PathBuf,
    identity: Handle,
    cleaned: AtomicBool,
    section_owned: AtomicBool,
    invocation: Option<InvocationRoot>,
    seeded: AtomicBool,
    exported: AtomicBool,
    initialization: tokio::sync::Mutex<()>,
    workers: Mutex<BTreeSet<u32>>,
    joined: Mutex<BTreeSet<u32>>,
}

impl OwnedBox {
    fn create(
        api: Arc<native::Api>,
        scratch: &Path,
        roots: &[ManagedRoot],
    ) -> Result<Self, ViewError> {
        let nonce = native::nonce()
            .map_err(|cause| ViewError::new("create session nonce", scratch, cause))?;
        let name = format!("msync_{nonce}");
        let path = scratch.join(&name);
        fs::create_dir(&path).map_err(|cause| ViewError::new("create owned box", &path, cause))?;
        let identity = Handle::from_path(&path)
            .map_err(|cause| ViewError::new("identify owned box", &path, cause))?;
        let invocation = roots
            .iter()
            .map(|root| root.snapshot.invocation_for_path(scratch))
            .collect::<Result<Vec<_>, _>>()
            .map_err(|cause| {
                ViewError::new("verify private backing", scratch, io::Error::other(cause))
            })?
            .into_iter()
            .flatten()
            .next();
        let owned = Self {
            api,
            name,
            nonce,
            path,
            identity,
            cleaned: AtomicBool::new(false),
            section_owned: AtomicBool::new(false),
            invocation,
            seeded: AtomicBool::new(false),
            exported: AtomicBool::new(false),
            initialization: tokio::sync::Mutex::new(()),
            workers: Mutex::new(BTreeSet::new()),
            joined: Mutex::new(BTreeSet::new()),
        };
        fs::create_dir(owned.path.join("sandbox"))
            .map_err(|cause| ViewError::new("create owned file root", &owned.path, cause))?;
        if !owned
            .api
            .values_flags(&owned.name, "Enabled", 0x5000_0000)
            .map_err(|cause| ViewError::new("check fresh section", &owned.path, cause))?
            .is_empty()
        {
            return Err(ViewError::new(
                "create owned section",
                &owned.path,
                io::Error::new(
                    io::ErrorKind::AlreadyExists,
                    "owned box name already exists",
                ),
            ));
        }
        owned.section_owned.store(true, Ordering::Release);
        let settings = [
            ("Enabled", "y"),
            ("AutoRecover", "n"),
            ("AutoDelete", "n"),
            ("UseFileDeleteV2", "y"),
            ("BlockNetworkFiles", "y"),
        ];
        for (key, value) in settings {
            owned.set(key, Path::new(value).as_os_str(), false)?;
        }
        owned.set_directory("FileRootPath", &owned.path.join("sandbox"), false)?;
        owned.set(
            "OpenPipePath",
            OsString::from(format!(r"\Device\NamedPipe\msync_{}", owned.nonce)).as_os_str(),
            false,
        )?;
        owned.set(
            "ClosedFilePath",
            Path::new(r"\Device\Afd*").as_os_str(),
            true,
        )?;
        for root in roots {
            owned.set_directory("WriteFilePath", &root.logical, true)?;
        }
        owned.verify(roots)?;
        Ok(owned)
    }

    fn verify(&self, roots: &[ManagedRoot]) -> Result<(), ViewError> {
        for key in [
            "OpenFilePath",
            "OpenFilePathEx",
            "NormalFilePath",
            "ReadFilePath",
        ] {
            let values = self.api.values(&self.name, key).map_err(|cause| {
                ViewError::new("read effective access rules", &self.path, cause)
            })?;
            reject_override(key, &values).map_err(|cause| {
                ViewError::new("verify inherited access policy", &self.path, cause)
            })?;
        }
        for root in roots {
            self.require_directory("WriteFilePath", &root.logical)?;
        }
        self.require("Enabled", std::ffi::OsStr::new("y"))?;
        self.require_directory("FileRootPath", &self.path.join("sandbox"))?;
        let expected = Handle::from_path(self.path.join("sandbox"))
            .map_err(|cause| ViewError::new("identify private file root", &self.path, cause))?;
        let actual = self
            .api
            .box_path(&self.name)
            .map_err(|cause| ViewError::new("query driver file root", &self.path, cause))?;
        for ancestor in actual.ancestors() {
            match Handle::from_path(ancestor) {
                Ok(handle) if handle == expected => return Ok(()),
                Ok(_) => {}
                Err(cause) if cause.kind() == io::ErrorKind::NotFound => {}
                Err(cause) => {
                    return Err(ViewError::new(
                        "verify driver root identity",
                        &actual,
                        cause,
                    ));
                }
            }
        }
        Err(ViewError::new(
            "verify driver root identity",
            &actual,
            io::Error::new(
                io::ErrorKind::PermissionDenied,
                "driver file root is outside the exclusively owned private directory",
            ),
        ))
    }

    fn set(&self, key: &str, value: &std::ffi::OsStr, append: bool) -> Result<(), ViewError> {
        self.api
            .configure(&self.name, key, Some(value), append)
            .map_err(|cause| ViewError::new("configure owned box", &self.path, cause))?;
        self.require(key, value)
    }

    fn set_directory(&self, key: &str, value: &Path, append: bool) -> Result<(), ViewError> {
        let encoded = native::configuration_path(value).map_err(|cause| {
            ViewError::new("serialize Sandboxie directory policy", value, cause)
        })?;
        self.api
            .configure(&self.name, key, Some(&encoded), append)
            .map_err(|cause| ViewError::new("configure owned path", &self.path, cause))?;
        self.require_directory(key, value)
    }

    fn require_directory(&self, key: &str, expected: &Path) -> Result<(), ViewError> {
        let readback = self.readback(key)?;
        if directory_matches(expected, &readback.effective)
            .map_err(|cause| ViewError::new("verify effective path identity", expected, cause))?
        {
            return Ok(());
        }
        Err(readback.mismatch(key, expected.as_os_str(), &self.path))
    }

    fn readback(&self, key: &str) -> Result<PolicyReadback, ViewError> {
        let effective = self
            .api
            .values(&self.name, key)
            .map_err(|cause| ViewError::new("read effective policy", &self.path, cause))?;
        let raw = self
            .api
            .values_flags(&self.name, key, 0x2000_0000)
            .map_err(|cause| ViewError::new("read unexpanded policy", &self.path, cause))?;
        Ok(PolicyReadback { raw, effective })
    }

    fn require(&self, key: &str, value: &std::ffi::OsStr) -> Result<(), ViewError> {
        let readback = self.readback(key)?;
        if !readback
            .effective
            .iter()
            .any(|actual| actual.eq_ignore_ascii_case(&value.to_string_lossy()))
        {
            return Err(readback.mismatch(key, value, &self.path));
        }
        Ok(())
    }

    fn cleanup_ref(&self) -> Result<(), ViewError> {
        if self.cleaned.load(Ordering::Acquire) {
            return Ok(());
        }
        if self.section_owned.load(Ordering::Acquire) {
            self.api
                .quiesce_except(&self.name, None)
                .map_err(|cause| ViewError::new("quiesce cleanup", &self.path, cause))?;
            self.api
                .wait_hive_unmounted(&self.name)
                .map_err(|cause| ViewError::new("join registry hive", &self.path, cause))?;
        }
        let identity = Handle::from_path(&self.path)
            .map_err(|cause| ViewError::new("identify cleanup", &self.path, cause))?;
        if identity != self.identity {
            return Err(ViewError::new(
                "cleanup ownership",
                &self.path,
                io::Error::other("owned box path identity changed; retain it"),
            ));
        }
        if self.section_owned.load(Ordering::Acquire) {
            self.api
                .configure(&self.name, "*", None, false)
                .map_err(|cause| ViewError::new("remove owned section", &self.path, cause))?;
            if !self
                .api
                .values_flags(&self.name, "Enabled", 0x5000_0000)
                .map_err(|cause| ViewError::new("verify section removal", &self.path, cause))?
                .is_empty()
            {
                return Err(ViewError::new(
                    "verify section removal",
                    &self.path,
                    io::Error::other(
                        "owned configuration section remains; retain the private files",
                    ),
                ));
            }
            self.section_owned.store(false, Ordering::Release);
        }
        fs::remove_dir_all(&self.path)
            .map_err(|cause| ViewError::new("cleanup private box files", &self.path, cause))?;
        self.cleaned.store(true, Ordering::Release);
        Ok(())
    }
}

struct PolicyReadback {
    raw: Vec<String>,
    effective: Vec<String>,
}

impl PolicyReadback {
    fn mismatch(&self, key: &str, expected: &std::ffi::OsStr, owner: &Path) -> ViewError {
        ViewError::new(
            "verify effective policy",
            owner,
            io::Error::other(format!(
                "Sandboxie setting {key} mismatch: expected={expected:?}; effective(flags=0x00000000)={:?}; raw(flags=CONF_GET_NO_EXPAND/0x20000000)={:?}",
                self.effective, self.raw
            )),
        )
    }
}

fn directory_matches(expected: &Path, effective: &[String]) -> io::Result<bool> {
    let expected = Handle::from_path(expected)?;
    for value in effective {
        let Ok(path) = native::path_from_native(value) else {
            continue;
        };
        if Handle::from_path(path).is_ok_and(|actual| actual == expected) {
            return Ok(true);
        }
    }
    Ok(false)
}

impl Drop for OwnedBox {
    fn drop(&mut self) {
        if self.section_owned.load(Ordering::Acquire) && !self.cleaned.load(Ordering::Acquire) {
            let _ = self.api.kill_all(&self.name);
        }
    }
}

fn reject_override(key: &str, values: &[String]) -> io::Result<()> {
    if values.is_empty() {
        return Ok(());
    }
    Err(io::Error::new(
        io::ErrorKind::PermissionDenied,
        format!(
            "effective {key} rules can bypass a frozen managed namespace; use a dedicated policy without inherited direct/read-through overrides: {}",
            values.join("; ")
        ),
    ))
}

fn validate_roots(
    scratch: &Path,
    worker: &Path,
    roots: Vec<ManagedRoot>,
) -> io::Result<Vec<ManagedRoot>> {
    let mut checked: Vec<ManagedRoot> = Vec::new();
    for root in roots {
        let logical = trees::safe_path(&root.logical)?;
        if !logical.is_dir() || !root.snapshot.is_directory() {
            return Err(wire::invalid(
                "managed program roots must be existing directories with complete directory snapshots",
            ));
        }
        let private_backing = root
            .snapshot
            .excludes_private_path(scratch)
            .map_err(io::Error::other)?;
        if ((logical.starts_with(scratch) || scratch.starts_with(&logical)) && !private_backing)
            || worker.starts_with(&logical)
        {
            return Err(wire::invalid(
                "managed roots, private scratch, and trusted worker must be disjoint",
            ));
        }
        if checked
            .iter()
            .any(|other| logical.starts_with(&other.logical) || other.logical.starts_with(&logical))
        {
            return Err(wire::invalid("managed program roots must be disjoint"));
        }
        checked.push(ManagedRoot {
            logical,
            snapshot: root.snapshot,
        });
    }
    if checked.is_empty() {
        return Err(wire::invalid(
            "a process view requires the complete managed resource domain",
        ));
    }
    Ok(checked)
}

fn effects(roots: &[ManagedRoot], name: &str) -> io::Result<ProcessEffects> {
    let mut effects = Effects::default();
    for root in roots {
        let resource = Resource::File(FileResource::new(
            Location {
                backend: Backend::Local,
                path: root.logical.to_string_lossy().into_owned(),
            },
            Scope::Tree,
        ));
        for kind in [AccessKind::Read, AccessKind::Write] {
            effects.accesses.push(Access {
                resource: resource.clone(),
                kind,
            });
        }
    }
    ProcessEffects::mediated(effects, "windows-sandboxie-frozen-roots", name)
        .map_err(io::Error::other)
}

/// Internal trusted entrypoint: verifies both pipe-server PID and current box
/// membership before accepting any resource tree or executable specification.
pub fn worker_entry(args: impl Iterator<Item = OsString>) -> std::process::ExitCode {
    worker::entry(args)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn inherited_read_through_rules_never_certify_a_frozen_namespace() {
        for key in [
            "OpenFilePath",
            "OpenFilePathEx",
            "NormalFilePath",
            "ReadFilePath",
        ] {
            assert!(reject_override(key, &[]).is_ok());
            for rule in ["*", r"C:\project\*", r"python.exe,C:\project\keep"] {
                let failure = reject_override(key, &[rule.to_owned()]).unwrap_err();
                assert_eq!(failure.kind(), io::ErrorKind::PermissionDenied);
                assert!(failure.to_string().contains(rule));
            }
        }
    }

    #[test]
    fn expanded_nt_paths_are_verified_by_identity_not_dos_text() {
        use std::os::windows::ffi::OsStringExt;
        use std::os::windows::fs::OpenOptionsExt;
        use windows_sys::Win32::Storage::FileSystem::{
            FILE_FLAG_BACKUP_SEMANTICS, GetFinalPathNameByHandleW, VOLUME_NAME_NT,
        };

        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        let fixture = tempfile::Builder::new()
            .prefix("config-identity-")
            .tempdir_in(root)
            .unwrap();
        let expected = fixture.path().join("配置-🌟");
        let other = fixture.path().join("other");
        fs::create_dir(&expected).unwrap();
        fs::create_dir(&other).unwrap();
        let handle = fs::OpenOptions::new()
            .read(true)
            .custom_flags(FILE_FLAG_BACKUP_SEMANTICS)
            .open(&expected)
            .unwrap();
        let mut native = vec![0u16; 32768];
        // This is the same NT device spelling returned by driver Conf_Expand.
        let count = unsafe {
            GetFinalPathNameByHandleW(
                handle.as_raw_handle(),
                native.as_mut_ptr(),
                native.len() as u32,
                VOLUME_NAME_NT,
            )
        } as usize;
        assert!(count > 0 && count < native.len());
        let nt = OsString::from_wide(&native[..count]).into_string().unwrap();
        assert!(nt.starts_with(r"\Device\"));
        assert_ne!(nt, expected.to_string_lossy());
        assert!(directory_matches(&expected, std::slice::from_ref(&nt)).unwrap());
        assert!(!directory_matches(&other, &[nt]).unwrap());
        assert!(!directory_matches(&expected, &["relative-or-malformed".into()]).unwrap());
    }

    #[test]
    fn policy_mismatch_reports_expected_raw_expanded_values_and_query_flags() {
        let readback = PolicyReadback {
            raw: vec![r"D:\configured".into()],
            effective: vec![r"\Device\HarddiskVolume9\configured".into()],
        };
        let error = readback.mismatch(
            "FileRootPath",
            std::ffi::OsStr::new(r"D:\expected"),
            Path::new(r"D:\owner"),
        );
        let message = error.to_string();
        for expected in [
            "FileRootPath",
            "expected",
            "configured",
            "HarddiskVolume9",
            "0x00000000",
            "CONF_GET_NO_EXPAND",
            "0x20000000",
        ] {
            assert!(message.contains(expected), "{message}");
        }
    }

    #[test]
    fn directory_configuration_uses_sandboxie_dos_syntax_not_win32_verbatim_syntax() {
        for (input, expected) in [
            (r"D:\project\目录", r"D:\project\目录"),
            (r"D:/project/目录", r"D:\project\目录"),
            (r"\\?\D:\project\目录", r"D:\project\目录"),
            (r"\\server\share\project", r"\\server\share\project"),
            (r"\\?\UNC\server\share\project", r"\\server\share\project"),
        ] {
            assert_eq!(
                native::configuration_path(Path::new(input)).unwrap(),
                std::ffi::OsStr::new(expected),
                "{input}"
            );
        }
        for input in [
            r"D:relative",
            r"relative",
            r"\\.\pipe\control",
            r"\\?\GLOBALROOT\Device\HarddiskVolume3\project",
            r"\\?\Volume{00000000-0000-0000-0000-000000000000}\project",
            r"\\?\D:\project\..\outside",
            r"\\?\D:\project\trailing.\child",
            r"\\?\D:\project\trailing \child",
            r"\\?\D:\project/child",
        ] {
            assert!(
                native::configuration_path(Path::new(input)).is_err(),
                "{input}"
            );
        }
    }

    #[test]
    fn malformed_mup_translation_is_never_reinterpreted_as_a_dos_alias() {
        let actual = native::path_from_native(r"\Device\Mup\?\D:\project").unwrap();
        assert_eq!(actual, Path::new(r"\\?\GLOBALROOT\Device\Mup\?\D:\project"));
        assert_ne!(actual, Path::new(r"\\?\D:\project"));
    }
}
