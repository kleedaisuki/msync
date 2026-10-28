//! Linux filesystem resource views backed by frozen directory snapshots.
//!
//! Logical absolute paths are mounted over independent private copies. The host
//! tree is read-only, while `/tmp` and `/run` are disposable private filesystems.
//! This mediates managed filesystem resources, not arbitrary hostile syscalls:
//! in particular, read-only exposure is not a general Unix-socket IPC policy.
//! External TCP/IP is isolated by a network namespace. No native-launch fallback
//! exists when bubblewrap or the required namespaces are unavailable.
//!
//! Complete logical roots are nonoverlapping directories. Owned backing is
//! excluded by their snapshots or fenced separately when outside those roots;
//! selecting a smaller managed domain never grants access to runtime storage.
//! This adapter does not discover additional managed roots from IO.
//! The owned worker execs the target without changing its argv or PID. Kernel
//! SCM_CREDENTIALS translates that sender's PID into the parent namespace; the
//! worker's CLOEXEC control connection distinguishes exec failure from success.
//! Raw `Process` identities still name bubblewrap; the view's PID/scalar methods
//! and sealed exit metadata name the actual payload. Exit codes retain
//! bubblewrap's shell-style signal normalization, not a fabricated raw signal.

use std::collections::BTreeSet;
use std::ffi::OsString;
use std::fmt;
use std::fs::{self, File, OpenOptions};
use std::io::{self, Write};
use std::os::fd::{AsRawFd, FromRawFd};
use std::os::unix::fs::PermissionsExt;
use std::os::unix::net::UnixStream;
use std::os::unix::process::CommandExt;
use std::path::{Component, Path, PathBuf};
use std::process::{Command, ExitCode, Stdio};
use std::sync::OnceLock;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use same_file::Handle;
use tokio::io::AsyncReadExt;
use tokio::io::unix::AsyncFd;

use super::{ExitInfo, Process, ProcessConfig};
use crate::endpoint::{Backend, Location};
use crate::hir::ReadPort;
use crate::hir::effects::{Access, AccessKind, Effects, FileResource, Resource, Scope};
use crate::hir::graph::ProcessEffects;
use crate::transaction::filesystem::{FsError, InvocationRoot, Snapshot, SnapshotStore};

static CAPABILITY: OnceLock<PathBuf> = OnceLock::new();
static NEXT_VIEW: AtomicU64 = AtomicU64::new(0);
const RESERVED: &[&str] = &["/proc", "/dev", "/tmp", "/run", "/sys"];
const MAX_WORKER_ERROR: u64 = 8192;

/// Internal executable dispatch; embedders must explicitly supply a binary that
/// forwards this mode to `worker_entry`, not assume their current executable does.
pub const WORKER_FLAG: &str = "--internal-linux-view-worker";

#[derive(Clone, Debug)]
pub struct ViewError {
    pub phase: &'static str,
    pub path: PathBuf,
    pub cause: Arc<io::Error>,
    pub details: Option<String>,
}

impl ViewError {
    fn new(phase: &'static str, path: &Path, cause: io::Error) -> Self {
        Self {
            phase,
            path: path.to_owned(),
            cause: Arc::new(cause),
            details: None,
        }
    }

    fn invalid(path: &Path, message: &'static str) -> Self {
        Self::new(
            "validate resource view",
            path,
            io::Error::new(io::ErrorKind::InvalidInput, message),
        )
    }

    fn filesystem(phase: &'static str, failure: FsError) -> Self {
        Self::new(
            phase,
            &failure.path.clone(),
            io::Error::new(failure.cause.kind(), failure),
        )
    }
}

impl fmt::Display for ViewError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "Linux resource view {} at {}: {}",
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

/// A complete immutable version of an existing logical directory root.
/// All manager-enrolled roots visible to the child must be supplied, not merely
/// an argv-derived subset. Reading another omitted managed path through the
/// read-only host tree would not justify a disjoint resource-effect certificate.
#[derive(Clone, Debug)]
pub struct ManagedRoot {
    pub logical: PathBuf,
    pub snapshot: Snapshot,
}

/// Exact native argv; no shell interpretation or argument path rewriting occurs.
#[derive(Clone, Debug)]
pub struct ProgramSpec {
    pub executable: PathBuf,
    pub arguments: Vec<OsString>,
    pub cwd: PathBuf,
}

/// A successful capability check is cached for this executable/provider process.
/// Failure remains a capability error, never permission to execute unmediated.
#[derive(Clone, Debug)]
pub struct Provider {
    executable: PathBuf,
    worker: Option<PathBuf>,
    invocation: Option<InvocationRoot>,
}

impl Provider {
    pub fn probe() -> Result<Self, ViewError> {
        cached_probe(&CAPABILITY, probe).map(|executable| Self {
            executable,
            worker: None,
            invocation: None,
        })
    }

    pub fn with_worker(mut self, executable: impl AsRef<Path>) -> Result<Self, ViewError> {
        let executable = executable.as_ref();
        if !executable.is_absolute() {
            return Err(ViewError::invalid(
                executable,
                "the resource-view worker executable must be an explicit absolute path",
            ));
        }
        let executable = fs::canonicalize(executable)
            .map_err(|cause| ViewError::new("resolve owned worker", executable, cause))?;
        let metadata = fs::metadata(&executable)
            .map_err(|cause| ViewError::new("inspect owned worker", &executable, cause))?;
        if !metadata.is_file() || metadata.permissions().mode() & 0o111 == 0 {
            return Err(ViewError::invalid(
                &executable,
                "the owned worker must be an executable ordinary file implementing WORKER_FLAG",
            ));
        }
        self.worker = Some(executable);
        Ok(self)
    }

    /// Protect owned runtime backing independently of the selected logical roots.
    /// Preparation validates that its scratch directory belongs to this proof.
    pub fn with_invocation(mut self, invocation: InvocationRoot) -> Self {
        self.invocation = Some(invocation);
        self
    }

    pub fn prepare(
        &self,
        store: &SnapshotStore,
        scratch: &Path,
        roots: Vec<ManagedRoot>,
    ) -> Result<PreparedView, ViewError> {
        if self.worker.is_none() {
            return Err(ViewError::invalid(
                scratch,
                "configure the owned worker executable with Provider::with_worker before preparing a view",
            ));
        }
        validate_roots(scratch, &roots)?;
        let inferred = roots
            .iter()
            .map(|root| root.snapshot.invocation_for_path(scratch))
            .collect::<Result<Vec<_>, _>>()
            .map_err(|failure| ViewError::filesystem("verify private backing", failure))?
            .into_iter()
            .flatten()
            .next();
        let invocation = self.invocation.clone().or(inferred);
        if let Some(invocation) = &invocation {
            let contained = invocation
                .contains_private(scratch)
                .map_err(|failure| ViewError::filesystem("verify private backing", failure))?;
            if !contained {
                return Err(ViewError::invalid(
                    scratch,
                    "scratch is outside the supplied owned invocation",
                ));
            }
            if invocation
                .contains_private(self.worker.as_ref().expect("worker is configured"))
                .map_err(|failure| ViewError::filesystem("verify worker isolation", failure))?
            {
                return Err(ViewError::invalid(
                    scratch,
                    "the worker executable must be outside private runtime backing",
                ));
            }
        }
        let private_mask = invocation
            .as_ref()
            .filter(|invocation| {
                !roots
                    .iter()
                    .any(|root| invocation.path().starts_with(&root.logical))
            })
            .map(|invocation| invocation.path().to_owned());
        let workspace = Workspace::create(scratch, invocation)?;
        let mut mounts = Vec::with_capacity(roots.len());
        for (index, root) in roots.into_iter().enumerate() {
            let private = workspace.path.join(format!("root-{index}"));
            let mut publication = store
                .prepare(&root.snapshot, &private)
                .map_err(|failure| ViewError::filesystem("prepare frozen root", failure))?;
            publication
                .publish()
                .map_err(|failure| ViewError::filesystem("materialize frozen root", failure))?;
            publication
                .finalize()
                .map_err(|failure| ViewError::filesystem("finalize private root", failure))?;
            mounts.push(Mount {
                logical: root.logical,
                private,
            });
        }
        let effects = resource_effects(&mounts, &workspace.path)?;
        Ok(PreparedView {
            provider: self.clone(),
            workspace: Arc::new(workspace),
            mounts,
            effects,
            private_mask,
            cohort: Arc::new(Mutex::new(Cohort::default())),
        })
    }
}

#[derive(Debug, Clone)]
struct Mount {
    logical: PathBuf,
    private: PathBuf,
}

/// Only private file data exists at this stage; no child has been launched.
#[derive(Clone)]
pub struct PreparedView {
    provider: Provider,
    workspace: Arc<Workspace>,
    mounts: Vec<Mount>,
    effects: ProcessEffects,
    private_mask: Option<PathBuf>,
    cohort: Arc<Mutex<Cohort>>,
}

#[derive(Default)]
struct Cohort {
    pending: BTreeSet<u32>,
    exported: bool,
}

impl PreparedView {
    /// Join all cohort payloads before exporting any fork of this shared view.
    pub fn fork_shared(&self) -> Self {
        self.clone()
    }

    fn cleanup(self) -> Result<(), ViewError> {
        match Arc::try_unwrap(self.workspace) {
            Ok(workspace) => workspace.cleanup(),
            Err(_) => Ok(()),
        }
    }

    pub fn effects(&self) -> &ProcessEffects {
        &self.effects
    }

    pub fn root(&self) -> &Path {
        &self.workspace.path
    }

    fn command(
        &self,
        spec: &ProgramSpec,
        status: File,
        control: UnixStream,
    ) -> Result<Command, ViewError> {
        if let Some(invocation) = &self.workspace.invocation {
            invocation
                .verify()
                .map_err(|failure| ViewError::filesystem("verify private backing", failure))?;
        }
        validate_program(spec, &self.mounts)?;
        let mut command = base_command(&self.provider.executable);
        for mount in &self.mounts {
            checked_directory(&mount.logical)?;
            command
                .arg("--bind")
                .arg(&mount.private)
                .arg(&mount.logical);
        }
        if let Some(path) = &self.private_mask {
            // Root overlays already hide their excluded invocation. An omitted
            // root instead needs an exact inaccessible, read-only mount; hiding
            // every .temp directory would also hide ordinary user data.
            command
                .args(["--perms", "000", "--tmpfs"])
                .arg(path)
                .arg("--remount-ro")
                .arg(path);
        }
        command.arg("--chdir").arg(&spec.cwd);
        command
            .arg("--json-status-fd")
            .arg(status.as_raw_fd().to_string());
        command
            .arg("--")
            .arg(
                self.provider
                    .worker
                    .as_ref()
                    .expect("prepared worker configuration"),
            )
            .arg(WORKER_FLAG)
            .arg(control.as_raw_fd().to_string())
            .arg(&spec.executable)
            .args(&spec.arguments);
        // SAFETY: Only fcntl runs between fork/exec. Owning the file in this
        // closure keeps it valid through spawn; bubblewrap closes it in the
        // target and reports an exit record only after exec succeeded.
        unsafe {
            command.pre_exec(move || {
                for fd in [status.as_raw_fd(), control.as_raw_fd()] {
                    if libc::fcntl(fd, libc::F_SETFD, 0) == -1 {
                        return Err(io::Error::last_os_error());
                    }
                }
                Ok(())
            });
        }
        Ok(command)
    }

    /// Launch owns both the private resource view and the real process lifetime.
    /// Output spools must not be placed inside this view's owned directory.
    pub fn launch(
        self,
        spec: &ProgramSpec,
        config: ProcessConfig,
    ) -> Result<RunningView, ViewError> {
        let mut cohort = self.cohort.lock().unwrap();
        if cohort.exported {
            return Err(ViewError::invalid(
                &self.workspace.path,
                "a sealed shared epoch cannot launch another payload",
            ));
        }
        tokio::runtime::Handle::try_current().map_err(|cause| {
            ViewError::new(
                "configure worker control",
                &self.workspace.path,
                io::Error::other(cause),
            )
        })?;
        if config.scratch_root.starts_with(&self.workspace.path) {
            return Err(ViewError::invalid(
                &config.scratch_root,
                "process spools must be outside the resource view being sealed",
            ));
        }
        let status = self.workspace.path.join(format!(
            "status-{}.jsonl",
            NEXT_VIEW.fetch_add(1, Ordering::Relaxed)
        ));
        let status_file = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&status)
            .map_err(|cause| ViewError::new("create launch status", &status, cause))?;
        let (parent, worker) = UnixStream::pair().map_err(|cause| {
            ViewError::new("create worker control", &self.workspace.path, cause)
        })?;
        enable_credentials(&parent).map_err(|cause| {
            ViewError::new("authenticate worker control", &self.workspace.path, cause)
        })?;
        parent.set_nonblocking(true).map_err(|cause| {
            ViewError::new("prepare worker control", &self.workspace.path, cause)
        })?;
        // SAFETY: UnixStream owns this stable descriptor until AsyncFd drops or
        // transfers it via into_inner; no code closes/replaces the descriptor.
        let control = unsafe { AsyncFd::register(parent) }.map_err(|cause| {
            ViewError::new(
                "register worker control",
                &self.workspace.path,
                cause.into(),
            )
        })?;
        let command = self.command(spec, status_file, worker)?;
        let process = Process::spawn_prepared(command, config)
            .map_err(|cause| ViewError::new("launch", &spec.executable, io::Error::other(cause)))?;
        cohort.pending.insert(process.pid());
        drop(cohort);
        Ok(RunningView {
            prepared: self,
            process,
            status,
            control: Some(control),
            identity: None,
            executable: spec.executable.clone(),
        })
    }

    /// Prepared views have no live child; cleanup remains explicit so
    /// native permission errors cannot silently erase recovery evidence.
    pub fn discard(self) -> Result<(), ViewError> {
        self.cleanup()
    }
}

/// Dropping requests process cancellation but deliberately retains private data.
/// Call `seal` to join physically and export, or `cancel` to join and discard.
pub struct RunningView {
    prepared: PreparedView,
    process: Process,
    status: PathBuf,
    control: Option<AsyncFd<UnixStream>>,
    identity: Option<ProgramIdentity>,
    executable: PathBuf,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct ProgramIdentity {
    host: u32,
    namespace: u32,
}

impl RunningView {
    pub fn fork_shared(&self) -> PreparedView {
        self.prepared.fork_shared()
    }

    /// A shared backing tree is exportable only after every payload is joined.
    pub async fn join(&mut self) -> Result<(), ViewError> {
        self.process.wait().await.map_err(|cause| {
            ViewError::new(
                "join shared payload",
                &self.executable,
                io::Error::other(cause),
            )
        })?;
        self.prepared
            .cohort
            .lock()
            .unwrap()
            .pending
            .remove(&self.process.pid());
        Ok(())
    }
    /// A child-owned close notification, not a request to cancel its siblings.
    pub fn exit_signal(&self) -> tokio_util::sync::CancellationToken {
        let mut receiver = self.process.finished.clone();
        let signal = tokio_util::sync::CancellationToken::new();
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

    /// Low-level wrapper ownership API. `Process::pid` here identifies bubblewrap;
    /// use `program_pid` or `scalar` for DSL-visible program identities.
    pub fn process_mut(&mut self) -> &mut Process {
        &mut self.process
    }

    pub fn effects(&self) -> &ProcessEffects {
        &self.prepared.effects
    }

    pub async fn program_pid(&mut self) -> Result<u32, ViewError> {
        Ok(self.identity().await?.host)
    }

    pub async fn namespace_pid(&mut self) -> Result<u32, ViewError> {
        Ok(self.identity().await?.namespace)
    }

    pub async fn scalar(&mut self, port: ReadPort) -> Result<Vec<u8>, ViewError> {
        if matches!(port, ReadPort::Stdout | ReadPort::Stderr) {
            return Err(ViewError::invalid(
                &self.executable,
                "stdout/stderr are consuming streams, not scalar ports",
            ));
        }
        let identity = self.identity().await?;
        if port == ReadPort::Pid {
            return Ok(format!("{}\n", identity.host).into_bytes());
        }
        let mut exit = self.process.wait().await.map_err(|cause| {
            ViewError::new(
                "join program scalar",
                &self.executable,
                io::Error::other(cause),
            )
        })?;
        exit.pid = identity.host;
        program_scalar(&exit, identity.namespace, self.process.pid(), port)
            .map_err(|cause| ViewError::new("read program scalar", &self.executable, cause))
    }

    async fn identity(&mut self) -> Result<ProgramIdentity, ViewError> {
        if let Some(identity) = self.identity {
            return Ok(identity);
        }
        let control = self.control.take().ok_or_else(|| {
            ViewError::new(
                "program identity",
                &self.executable,
                io::Error::new(
                    io::ErrorKind::BrokenPipe,
                    "the worker identity channel has already failed",
                ),
            )
        })?;
        match receive_worker(control).await {
            Ok(identity) => {
                self.identity = Some(identity);
                Ok(identity)
            }
            Err(StartFailure { cause, mut details }) => {
                let _ = self.process.cancel_and_wait().await;
                if details.is_none()
                    && let Some(stderr) = self.process.take_stderr()
                {
                    let mut bytes = Vec::new();
                    let _ = stderr.take(16 * 1024).read_to_end(&mut bytes).await;
                    details = Some(String::from_utf8_lossy(&bytes).into_owned());
                }
                let mut failure = ViewError::new(
                    "namespace setup or executable launch",
                    &self.executable,
                    cause,
                );
                failure.details = details;
                Err(failure)
            }
        }
    }

    /// Cancel, physically join, and discard. Payload metadata is returned only
    /// if readiness was already acknowledged; otherwise Interrupted reports
    /// cancellation without inventing a payload PID from the monitor identity.
    pub async fn cancel(mut self) -> Result<ExitInfo, ViewError> {
        let mut exit = self.process.cancel_and_wait().await.map_err(|cause| {
            ViewError::new(
                "cancel and join",
                &self.prepared.workspace.path,
                io::Error::other(cause),
            )
        })?;
        self.prepared
            .cohort
            .lock()
            .unwrap()
            .pending
            .remove(&self.process.pid());
        self.prepared.cleanup()?;
        let identity = self.identity.ok_or_else(|| {
            ViewError::new(
                "cancel before program acknowledgement",
                &self.executable,
                io::Error::new(
                    io::ErrorKind::Interrupted,
                    "the namespace was cancelled before a payload PID was acknowledged",
                ),
            )
        })?;
        exit.pid = identity.host;
        Ok(exit)
    }

    /// Cancellation retains ownership until physical join and private discard;
    /// callers must not drop a consuming seal future to simulate cancellation.
    pub async fn seal_cancellable(
        mut self,
        store: &SnapshotStore,
        cancel: &tokio_util::sync::CancellationToken,
    ) -> Result<SealedView, ViewError> {
        tokio::select! {
            biased;
            () = cancel.cancelled() => {
                let path = self.executable.clone();
                self.cancel().await?;
                return Err(ViewError::new("seal cancelled view", &path,
                    io::Error::new(io::ErrorKind::Interrupted, "cancelled program views are discarded")));
            }
            completed = self.process.wait() => {
                completed.map_err(|cause| ViewError::new("join before seal", &self.executable, io::Error::other(cause)))?;
            }
        }
        if cancel.is_cancelled() {
            let path = self.executable.clone();
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

    /// Export happens only after the namespace leader, descendants, and physical
    /// output drains finish. A nonzero program exit is returned as program data.
    pub async fn seal(mut self, store: &SnapshotStore) -> Result<SealedView, ViewError> {
        let mut exit = self.process.wait().await.map_err(|cause| {
            ViewError::new(
                "join before seal",
                &self.prepared.workspace.path,
                io::Error::other(cause),
            )
        })?;
        let export = {
            let mut cohort = self.prepared.cohort.lock().unwrap();
            cohort.pending.remove(&self.process.pid());
            if !cohort.pending.is_empty() {
                return Err(ViewError::new(
                    "seal shared view",
                    &self.executable,
                    io::Error::new(
                        io::ErrorKind::WouldBlock,
                        "join every shared payload before exporting its epoch",
                    ),
                ));
            }
            let export = !cohort.exported;
            cohort.exported = true;
            export
        };
        let status = fs::read_to_string(&self.status)
            .map_err(|cause| ViewError::new("read launch status", &self.status, cause))?;
        if exit.cancelled {
            self.prepared.cleanup()?;
            return Err(ViewError::new(
                "seal cancelled view",
                &self.status,
                io::Error::new(
                    io::ErrorKind::Interrupted,
                    "cancelled program views are discarded, never exported as a successful transaction version",
                ),
            ));
        }
        // Bubblewrap emits exit-code only after successful exec, unlike the
        // early child-pid record. Keep setup failure distinct from program data.
        // https://github.com/containers/bubblewrap/blob/v0.9.0/bubblewrap.c
        if !status.lines().any(|line| line.contains("\"exit-code\"")) {
            let mut failure = ViewError::new(
                "namespace setup or executable launch",
                &self.prepared.workspace.path,
                io::Error::other(format!(
                    "bubblewrap exited {} before reporting an executed program",
                    exit.exit_code
                )),
            );
            if let Some(stderr) = self.process.take_stderr() {
                let mut bytes = Vec::new();
                let _ = stderr.take(16 * 1024).read_to_end(&mut bytes).await;
                failure.details = Some(String::from_utf8_lossy(&bytes).into_owned());
            }
            return Err(failure);
        }
        let identity = self.identity().await?;
        let supervisor_pid = self.process.pid();
        exit.pid = identity.host;
        let mut roots = Vec::with_capacity(self.prepared.mounts.len());
        for mount in &self.prepared.mounts {
            if !export {
                break;
            }
            let snapshot = store
                .capture(&mount.private)
                .map_err(|failure| ViewError::filesystem("export managed root", failure))?;
            roots.push(ManagedRoot {
                logical: mount.logical.clone(),
                snapshot,
            });
        }
        let effects = self.prepared.effects.clone();
        self.prepared.cleanup()?;
        Ok(SealedView {
            roots,
            exit,
            effects,
            process: self.process,
            namespace_pid: identity.namespace,
            supervisor_pid,
            executable: self.executable,
        })
    }
}

pub struct SealedView {
    pub roots: Vec<ManagedRoot>,
    pub exit: ExitInfo,
    pub effects: ProcessEffects,
    /// Retains any untaken output spool readers and replayable scalar ports.
    pub process: Process,
    pub namespace_pid: u32,
    pub supervisor_pid: u32,
    executable: PathBuf,
}

impl SealedView {
    pub fn scalar(&self, port: ReadPort) -> Result<Vec<u8>, ViewError> {
        program_scalar(&self.exit, self.namespace_pid, self.supervisor_pid, port)
            .map_err(|cause| ViewError::new("read sealed program scalar", &self.executable, cause))
    }
}

fn program_scalar(
    exit: &ExitInfo,
    namespace_pid: u32,
    supervisor_pid: u32,
    port: ReadPort,
) -> io::Result<Vec<u8>> {
    match port {
        ReadPort::Pid => Ok(format!("{}\n", exit.pid).into_bytes()),
        ReadPort::ExitCode => Ok(format!("{}\n", exit.exit_code).into_bytes()),
        ReadPort::Meta => Ok(format!("{{\"pid\":{},\"namespace_pid\":{},\"supervisor_pid\":{},\"exit_code\":{},\"success\":{},\"cancelled\":{},\"stdout_bytes\":{},\"stderr_bytes\":{},\"exit_status_encoding\":\"bubblewrap-shell\"}}\n",
            exit.pid, namespace_pid, supervisor_pid, exit.exit_code, exit.success, exit.cancelled, exit.stdout_bytes, exit.stderr_bytes).into_bytes()),
        _ => Err(io::Error::new(io::ErrorKind::InvalidInput, "stdout/stderr are consuming streams, not scalar ports")),
    }
}

fn cached_probe(
    cache: &OnceLock<PathBuf>,
    perform: impl FnOnce() -> Result<PathBuf, ViewError>,
) -> Result<PathBuf, ViewError> {
    if let Some(executable) = cache.get() {
        return Ok(executable.clone());
    }
    let executable = perform()?;
    Ok(cache.get_or_init(|| executable).clone())
}

struct StartFailure {
    cause: io::Error,
    details: Option<String>,
}

impl From<io::Error> for StartFailure {
    fn from(cause: io::Error) -> Self {
        Self {
            cause,
            details: None,
        }
    }
}

async fn receive_worker(control: AsyncFd<UnixStream>) -> Result<ProgramIdentity, StartFailure> {
    let host = loop {
        let mut readable = control.readable().await?;
        match readable.try_io(|socket| receive_host_pid(socket.get_ref())) {
            Ok(Err(cause)) if cause.kind() == io::ErrorKind::Interrupted => continue,
            Ok(result) => break result?,
            Err(_) => continue,
        }
    };
    let mut control = tokio::net::UnixStream::from_std(control.into_inner())?;
    let mut namespace = [0; 4];
    control.read_exact(&mut namespace).await?;
    let mut failure = Vec::new();
    control
        .take(MAX_WORKER_ERROR + 6)
        .read_to_end(&mut failure)
        .await?;
    if failure.is_empty() {
        let namespace = u32::from_le_bytes(namespace);
        if namespace == 0 {
            return Err(io::Error::new(
                io::ErrorKind::InvalidData,
                "worker readiness has no usable namespace PID",
            )
            .into());
        }
        return Ok(ProgramIdentity { host, namespace });
    }
    if failure.len() < 5 || failure[0] != b'E' || failure.len() as u64 > MAX_WORKER_ERROR + 5 {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "invalid bounded worker exec result",
        )
        .into());
    }
    let errno = i32::from_le_bytes(
        failure[1..5]
            .try_into()
            .expect("checked exec result header"),
    );
    let cause = if errno > 0 {
        io::Error::from_raw_os_error(errno)
    } else {
        io::Error::other("the worker could not execute the target program")
    };
    Err(StartFailure {
        cause,
        details: Some(String::from_utf8_lossy(&failure[5..]).into_owned()),
    })
}

fn enable_credentials(socket: &UnixStream) -> io::Result<()> {
    let enabled: libc::c_int = 1;
    // SAFETY: Live AF_UNIX socket and correctly sized integer option buffer.
    if unsafe {
        libc::setsockopt(
            socket.as_raw_fd(),
            libc::SOL_SOCKET,
            libc::SO_PASSCRED,
            (&enabled as *const libc::c_int).cast(),
            std::mem::size_of_val(&enabled) as libc::socklen_t,
        )
    } == -1
    {
        return Err(io::Error::last_os_error());
    }
    Ok(())
}

fn receive_host_pid(socket: &UnixStream) -> io::Result<u32> {
    let mut tag = [0u8; 1];
    let mut ancillary = [0usize; 8];
    let mut buffer = libc::iovec {
        iov_base: tag.as_mut_ptr().cast(),
        iov_len: tag.len(),
    };
    // SAFETY: Zero is a valid empty msghdr; all supplied buffers remain live.
    let mut message: libc::msghdr = unsafe { std::mem::zeroed() };
    message.msg_iov = &mut buffer;
    message.msg_iovlen = 1;
    message.msg_control = ancillary.as_mut_ptr().cast();
    message.msg_controllen = std::mem::size_of_val(&ancillary);
    // SO_PASSCRED yields the sender's PID translated into our namespace, unlike
    // SO_PEERCRED on an inherited socketpair (which identifies its creator).
    let count = unsafe { libc::recvmsg(socket.as_raw_fd(), &mut message, libc::MSG_DONTWAIT) };
    if count < 0 {
        return Err(io::Error::last_os_error());
    }
    if count != 1 || tag[0] != b'R' || message.msg_flags & libc::MSG_CTRUNC != 0 {
        return Err(io::Error::new(
            io::ErrorKind::BrokenPipe,
            "owned worker did not report a complete credentialled readiness frame",
        ));
    }
    // SAFETY: recvmsg initialized this bounded ancillary region. Header lengths
    // are checked before reading its credential payload.
    unsafe {
        let mut header = libc::CMSG_FIRSTHDR(&message);
        while !header.is_null() {
            if (*header).cmsg_level == libc::SOL_SOCKET
                && (*header).cmsg_type == libc::SCM_CREDENTIALS
                && (*header).cmsg_len
                    >= libc::CMSG_LEN(std::mem::size_of::<libc::ucred>() as u32) as usize
            {
                let credentials = libc::CMSG_DATA(header)
                    .cast::<libc::ucred>()
                    .read_unaligned();
                return u32::try_from(credentials.pid)
                    .ok()
                    .filter(|pid| *pid > 0)
                    .ok_or_else(|| {
                        io::Error::new(
                            io::ErrorKind::InvalidData,
                            "worker readiness had no usable host PID",
                        )
                    });
            }
            header = libc::CMSG_NXTHDR(&message, header);
        }
    }
    Err(io::Error::new(
        io::ErrorKind::InvalidData,
        "kernel SCM_CREDENTIALS are required to identify the payload process",
    ))
}

/// Internal mode for an explicitly configured msync worker executable. Successful
/// execution never returns: the target inherits exactly argv, env, stdio and PID.
pub fn worker_entry(arguments: impl Iterator<Item = OsString>) -> ExitCode {
    match worker_exec(arguments) {
        Ok(never) => match never {},
        Err(cause) => {
            eprintln!("msync Linux resource worker: {cause}");
            ExitCode::FAILURE
        }
    }
}

fn worker_exec(
    mut arguments: impl Iterator<Item = OsString>,
) -> io::Result<std::convert::Infallible> {
    let descriptor = arguments
        .next()
        .and_then(|text| text.to_str().and_then(|text| text.parse::<i32>().ok()))
        .filter(|descriptor| *descriptor > 2)
        .ok_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "owned worker requires a private control descriptor",
            )
        })?;
    let executable = arguments.next().ok_or_else(|| {
        io::Error::new(
            io::ErrorKind::InvalidInput,
            "owned worker requires an executable",
        )
    })?;
    let mut control = worker_control(descriptor)?;
    let mut ready = [b'R', 0, 0, 0, 0];
    ready[1..].copy_from_slice(&std::process::id().to_le_bytes());
    control.write_all(&ready)?;
    let cause = Command::new(&executable).args(arguments).exec();
    let mut failure = Vec::with_capacity(128);
    failure.push(b'E');
    failure.extend_from_slice(&cause.raw_os_error().unwrap_or(0).to_le_bytes());
    let detail = format!("execute {}: {cause}", executable.to_string_lossy());
    failure.extend_from_slice(&detail.as_bytes()[..detail.len().min(MAX_WORKER_ERROR as usize)]);
    let _ = control.write_all(&failure);
    Err(cause)
}

fn worker_control(descriptor: i32) -> io::Result<UnixStream> {
    let mut domain: libc::c_int = 0;
    let mut length = std::mem::size_of_val(&domain) as libc::socklen_t;
    // SAFETY: Validate the inherited descriptor before taking ownership. It is
    // private to this internal mode, not one of stdin/stdout/stderr.
    if unsafe {
        libc::getsockopt(
            descriptor,
            libc::SOL_SOCKET,
            libc::SO_DOMAIN,
            (&mut domain as *mut libc::c_int).cast(),
            &mut length,
        )
    } == -1
    {
        return Err(io::Error::last_os_error());
    }
    if domain != libc::AF_UNIX {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "owned worker control must be a Unix socket",
        ));
    }
    let mut kind: libc::c_int = 0;
    length = std::mem::size_of_val(&kind) as libc::socklen_t;
    if unsafe {
        libc::getsockopt(
            descriptor,
            libc::SOL_SOCKET,
            libc::SO_TYPE,
            (&mut kind as *mut libc::c_int).cast(),
            &mut length,
        )
    } == -1
    {
        return Err(io::Error::last_os_error());
    }
    if kind != libc::SOCK_STREAM {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "owned worker control must be a stream socket",
        ));
    }
    // The connection closes automatically only when target exec succeeds; an
    // exec failure retains it long enough to transmit the native error frame.
    if unsafe { libc::fcntl(descriptor, libc::F_SETFD, libc::FD_CLOEXEC) } == -1 {
        return Err(io::Error::last_os_error());
    }
    // SAFETY: The validated inherited descriptor has no Rust owner in this
    // newly executed worker process. Ownership transfers exactly once here.
    Ok(unsafe { UnixStream::from_raw_fd(descriptor) })
}

fn base_command(executable: &Path) -> Command {
    let mut command = Command::new(executable);
    command.args([
        "--ro-bind",
        "/",
        "/",
        "--unshare-user",
        "--unshare-pid",
        "--unshare-ipc",
        "--unshare-net",
        "--die-with-parent",
        "--new-session",
        "--proc",
        "/proc",
        "--dev",
        "/dev",
        "--tmpfs",
        "/tmp",
        "--tmpfs",
        "/run",
        "--setenv",
        "TMPDIR",
        "/tmp",
    ]);
    command
}

fn probe() -> Result<PathBuf, ViewError> {
    let executable = std::env::var_os("PATH").into_iter().flat_map(|paths| std::env::split_paths(&paths).collect::<Vec<_>>())
        .map(|path| path.join("bwrap")).find(|path| path.is_file())
        .ok_or_else(|| ViewError::new("capability probe", Path::new("bwrap"), io::Error::new(io::ErrorKind::NotFound,
            "install bubblewrap and enable unprivileged user/mount/PID/network namespaces; native execution is not a fallback")))?;
    let executable = fs::canonicalize(&executable)
        .map_err(|cause| ViewError::new("resolve provider", &executable, cause))?;
    let output = base_command(&executable)
        .args(["--json-status-fd", "1", "--", "/bin/true"])
        .stdin(Stdio::null())
        .output()
        .map_err(|cause| ViewError::new("capability probe", &executable, cause))?;
    if !output.status.success() {
        let mut failure = ViewError::new(
            "capability probe",
            &executable,
            io::Error::new(
                io::ErrorKind::Unsupported,
                format!("required namespaces unavailable ({})", output.status),
            ),
        );
        failure.details = Some(format!(
            "{}; provision the host's bubblewrap/user-namespace policy explicitly; execution was not attempted unmediated",
            String::from_utf8_lossy(&output.stderr)
        ));
        return Err(failure);
    }
    if !String::from_utf8_lossy(&output.stdout).contains("\"exit-code\"") {
        return Err(ViewError::new(
            "capability probe",
            &executable,
            io::Error::new(
                io::ErrorKind::Unsupported,
                "bubblewrap must support --json-status-fd and confirm successful exec before a managed version can be exported",
            ),
        ));
    }
    Ok(executable)
}

fn validate_roots(scratch: &Path, roots: &[ManagedRoot]) -> Result<(), ViewError> {
    checked_directory(scratch)?;
    if roots.is_empty() {
        return Err(ViewError::invalid(
            scratch,
            "enroll at least one immutable managed directory",
        ));
    }
    for (index, root) in roots.iter().enumerate() {
        checked_directory(&root.logical)?;
        if !root.snapshot.is_directory() {
            return Err(ViewError::invalid(
                &root.logical,
                "managed roots require a complete directory snapshot",
            ));
        }
        if RESERVED
            .iter()
            .any(|path| paths_overlap(&root.logical, Path::new(path)))
            || root.logical.parent().is_none()
        {
            return Err(ViewError::invalid(
                &root.logical,
                "managed roots must not cover namespace implementation mounts or the filesystem root",
            ));
        }
        if aliases_overlap(scratch, &root.logical)?
            && !root
                .snapshot
                .excludes_private_path(scratch)
                .map_err(|failure| {
                    ViewError::filesystem("verify private backing exclusion", failure)
                })?
        {
            return Err(ViewError::invalid(
                &root.logical,
                "a managed root may contain scratch only when its immutable snapshot excludes that exact owned invocation",
            ));
        }
        for previous in &roots[..index] {
            if aliases_overlap(&previous.logical, &root.logical)? {
                return Err(ViewError::invalid(
                    &root.logical,
                    "managed directory roots must not overlap or alias",
                ));
            }
        }
    }
    Ok(())
}

fn validate_program(spec: &ProgramSpec, mounts: &[Mount]) -> Result<(), ViewError> {
    if !spec.executable.is_absolute() {
        return Err(ViewError::invalid(
            &spec.executable,
            "resolve the executable to an absolute native path before constructing a resource view command",
        ));
    }
    checked_directory(&spec.cwd)?;
    if let Some(mount) = mounts
        .iter()
        .find(|mount| spec.cwd.starts_with(&mount.logical))
    {
        let suffix = spec
            .cwd
            .strip_prefix(&mount.logical)
            .expect("checked prefix");
        let physical = if suffix.as_os_str().is_empty() {
            mount.private.clone()
        } else {
            mount.private.join(suffix)
        };
        checked_directory(&physical)?;
    }
    Ok(())
}

fn checked_directory(path: &Path) -> Result<(), ViewError> {
    if !path.is_absolute()
        || path
            .components()
            .any(|part| matches!(part, Component::ParentDir))
    {
        return Err(ViewError::invalid(
            path,
            "use an absolute path without parent traversal",
        ));
    }
    for ancestor in path.ancestors() {
        let metadata = fs::symlink_metadata(ancestor)
            .map_err(|cause| ViewError::new("inspect directory", ancestor, cause))?;
        if !metadata.is_dir() || metadata.file_type().is_symlink() {
            return Err(ViewError::invalid(
                ancestor,
                "directory roots and their ancestors must be real directories, not symbolic links",
            ));
        }
    }
    Ok(())
}

fn paths_overlap(left: &Path, right: &Path) -> bool {
    left.starts_with(right) || right.starts_with(left)
}

fn aliases_overlap(left: &Path, right: &Path) -> Result<bool, ViewError> {
    if paths_overlap(left, right) {
        return Ok(true);
    }
    let left_id = Handle::from_path(left)
        .map_err(|cause| ViewError::new("identify managed root", left, cause))?;
    let right_id = Handle::from_path(right)
        .map_err(|cause| ViewError::new("identify managed root", right, cause))?;
    for ancestor in left.ancestors() {
        if Handle::from_path(ancestor)
            .map_err(|cause| ViewError::new("identify root ancestor", ancestor, cause))?
            == right_id
        {
            return Ok(true);
        }
    }
    for ancestor in right.ancestors() {
        if Handle::from_path(ancestor)
            .map_err(|cause| ViewError::new("identify root ancestor", ancestor, cause))?
            == left_id
        {
            return Ok(true);
        }
    }
    Ok(false)
}

fn resource_effects(mounts: &[Mount], identity: &Path) -> Result<ProcessEffects, ViewError> {
    let mut footprint = Effects::default();
    for mount in mounts {
        let path = mount
            .logical
            .to_str()
            .ok_or_else(|| ViewError::invalid(&mount.logical, "managed DSL paths must be UTF-8"))?
            .to_owned();
        let resource = Resource::File(FileResource::new(
            Location {
                backend: Backend::Local,
                path,
            },
            Scope::Tree,
        ));
        footprint.accesses.push(Access {
            resource: resource.clone(),
            kind: AccessKind::Read,
        });
        footprint.accesses.push(Access {
            resource,
            kind: AccessKind::Write,
        });
    }
    ProcessEffects::mediated(
        footprint,
        "linux-bubblewrap-frozen-roots",
        identity.to_string_lossy(),
    )
    .map_err(|cause| {
        ViewError::new(
            "certify resource footprint",
            identity,
            io::Error::other(cause),
        )
    })
}

struct Workspace {
    path: PathBuf,
    identity: Handle,
    invocation: Option<InvocationRoot>,
}

impl Workspace {
    fn create(scratch: &Path, invocation: Option<InvocationRoot>) -> Result<Self, ViewError> {
        for _ in 0..128 {
            let path = scratch.join(format!(
                "view-linux-{}-{}",
                std::process::id(),
                NEXT_VIEW.fetch_add(1, Ordering::Relaxed)
            ));
            match fs::create_dir(&path) {
                Ok(()) => {
                    fs::set_permissions(&path, fs::Permissions::from_mode(0o700))
                        .map_err(|cause| ViewError::new("protect private view", &path, cause))?;
                    let identity = Handle::from_path(&path)
                        .map_err(|cause| ViewError::new("identify private view", &path, cause))?;
                    return Ok(Self {
                        path,
                        identity,
                        invocation,
                    });
                }
                Err(cause) if cause.kind() == io::ErrorKind::AlreadyExists => continue,
                Err(cause) => return Err(ViewError::new("create private view", &path, cause)),
            }
        }
        Err(ViewError::new(
            "create private view",
            scratch,
            io::Error::new(io::ErrorKind::AlreadyExists, "private view names exhausted"),
        ))
    }

    fn cleanup(self) -> Result<(), ViewError> {
        checked_directory(&self.path)?;
        let identity = Handle::from_path(&self.path)
            .map_err(|cause| ViewError::new("identify cleanup root", &self.path, cause))?;
        if identity != self.identity {
            return Err(ViewError::invalid(
                &self.path,
                "private view ownership changed; retained data must not be deleted",
            ));
        }
        writable_directories(&self.path)?;
        fs::remove_dir_all(&self.path)
            .map_err(|cause| ViewError::new("cleanup sealed resource view", &self.path, cause))
    }
}

fn writable_directories(path: &Path) -> Result<(), ViewError> {
    let metadata = fs::symlink_metadata(path)
        .map_err(|cause| ViewError::new("inspect cleanup entry", path, cause))?;
    if !metadata.is_dir() || metadata.file_type().is_symlink() {
        return Ok(());
    }
    fs::set_permissions(
        path,
        fs::Permissions::from_mode(metadata.permissions().mode() | 0o700),
    )
    .map_err(|cause| ViewError::new("prepare private directory cleanup", path, cause))?;
    for entry in fs::read_dir(path)
        .map_err(|cause| ViewError::new("enumerate private directory cleanup", path, cause))?
    {
        let entry =
            entry.map_err(|cause| ViewError::new("read private cleanup entry", path, cause))?;
        writable_directories(&entry.path())?;
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn failed_capability_probes_do_not_poison_a_repaired_repl_environment() {
        let cache = OnceLock::new();
        let failed = cached_probe(&cache, || {
            Err(ViewError::invalid(
                Path::new("bwrap"),
                "temporarily unavailable",
            ))
        });
        assert!(failed.is_err());
        assert!(cache.get().is_none());
        let repaired = cached_probe(&cache, || Ok(PathBuf::from("/usr/bin/bwrap"))).unwrap();
        assert_eq!(repaired, Path::new("/usr/bin/bwrap"));
        let reused = cached_probe(&cache, || panic!("successful probes must be cached")).unwrap();
        assert_eq!(reused, repaired);
    }
}
