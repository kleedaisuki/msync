//! Owned asynchronous program spaces and their byte/scalar ports.
//!
//! Native children execute against the real filesystem, not a transactional view.
//! Output is drained immediately into private spools, so a process can finish
//! even when only its exit code is observed. Internal memory is buffer-bounded;
//! disk use is bounded only when `max_output_bytes` is explicitly configured.

mod tree;

use std::fmt;
use std::fs::{self, OpenOptions};
use std::io;
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::process::{Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
use std::task::{Context, Poll, Waker};

use tokio::io::{AsyncRead, AsyncReadExt, AsyncWriteExt, ReadBuf};
use tokio::process::{Child, ChildStdin};
use tokio::sync::watch;
use tokio_util::sync::CancellationToken;

use crate::hir::ReadPort;

#[derive(Clone, Debug)]
pub struct ProcessConfig {
    /// Caller-owned project scratch directory, normally `<project>/.temp`.
    pub scratch_root: PathBuf,
    /// One buffer per output drain, excluding OS pipe and filesystem caches.
    pub buffer_bytes: usize,
    /// Combined stdout/stderr spool quota. None leaves limits to the filesystem.
    pub max_output_bytes: Option<u64>,
}

impl ProcessConfig {
    pub fn new(scratch_root: impl Into<PathBuf>) -> Self {
        Self {
            scratch_root: scratch_root.into(),
            buffer_bytes: 64 * 1024,
            max_output_bytes: None,
        }
    }

    /// Conservative internal payload capacity: two drains plus the two Tokio
    /// writer and reader buffers. Caller-owned read buffers and OS caches differ.
    pub fn payload_capacity(&self) -> usize {
        self.buffer_bytes.saturating_mul(6)
    }

    fn validate(&self) -> Result<(), ProcessError> {
        if !self.scratch_root.is_absolute() || self.buffer_bytes == 0 {
            return Err(ProcessError::new(
                "configure",
                None,
                Some(self.scratch_root.clone()),
                io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "process scratch root must be absolute and buffer size must be positive",
                ),
            ));
        }
        Ok(())
    }
}

/// Native cause, process identity and phase remain available for diagnostics.
#[derive(Clone, Debug)]
pub struct ProcessError {
    pub phase: &'static str,
    pub pid: Option<u32>,
    pub path: Option<PathBuf>,
    pub cause: Arc<io::Error>,
}

impl ProcessError {
    fn new(phase: &'static str, pid: Option<u32>, path: Option<PathBuf>, cause: io::Error) -> Self {
        Self {
            phase,
            pid,
            path,
            cause: Arc::new(cause),
        }
    }

    fn reader_error(&self) -> io::Error {
        io::Error::new(self.cause.kind(), self.clone())
    }
}

impl fmt::Display for ProcessError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(f, "process {} failed", self.phase)?;
        if let Some(pid) = self.pid {
            write!(f, " (pid {pid})")?;
        }
        if let Some(path) = &self.path {
            write!(f, " at {}", path.display())?;
        }
        write!(f, ": {}", self.cause)
    }
}

impl std::error::Error for ProcessError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        Some(self.cause.as_ref())
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ProcessExitStatus {
    pub pid: u32,
    /// A Unix signal is represented as its negative number, not a shell guess.
    pub exit_code: i32,
    pub success: bool,
    pub cancelled: bool,
}

impl ProcessExitStatus {
    fn new(pid: u32, status: &std::process::ExitStatus, cancelled: bool) -> Self {
        let exit_code = status.code().unwrap_or({
            #[cfg(unix)]
            {
                use std::os::unix::process::ExitStatusExt;
                -status.signal().unwrap_or(1)
            }
            #[cfg(not(unix))]
            -1
        });
        Self {
            pid,
            exit_code,
            success: status.success(),
            cancelled,
        }
    }
}

#[derive(Clone, Debug, PartialEq, Eq)]
pub struct ExitInfo {
    pub pid: u32,
    /// A Unix signal is represented as its negative number, not a shell guess.
    pub exit_code: i32,
    pub success: bool,
    pub cancelled: bool,
    pub stdout_bytes: u64,
    pub stderr_bytes: u64,
}

impl ExitInfo {
    fn metadata(&self) -> Vec<u8> {
        format!(
            "{{\"pid\":{},\"exit_code\":{},\"success\":{},\"cancelled\":{},\"stdout_bytes\":{},\"stderr_bytes\":{}}}\n",
            self.pid,
            self.exit_code,
            self.success,
            self.cancelled,
            self.stdout_bytes,
            self.stderr_bytes
        )
        .into_bytes()
    }
}

/// The session joins children at barriers. Dropping this handle never kills a
/// child; its independent worker retains wait ownership and drains both outputs.
pub struct Process {
    pid: u32,
    stdin: Option<ChildStdin>,
    stdout: Option<SpoolReader>,
    stderr: Option<SpoolReader>,
    cancel: CancellationToken,
    exited: CancellationToken,
    status: watch::Receiver<Option<Result<ProcessExitStatus, ProcessError>>>,
    finished: watch::Receiver<Option<Result<ExitInfo, ProcessError>>>,
}

struct ExitObserver {
    signal: CancellationToken,
    status: watch::Sender<Option<Result<ProcessExitStatus, ProcessError>>>,
}

impl Process {
    /// Launch an exact argv natively. No shell or path rewriting is
    /// inserted. The command's environment/cwd are retained; stdio is owned here.
    pub fn spawn_native(command: Command, config: ProcessConfig) -> Result<Self, ProcessError> {
        Self::spawn_prepared(command, config)
    }

    /// Shared launch implementation for execution-layer adapters.
    pub(crate) fn spawn_prepared(
        mut command: Command,
        config: ProcessConfig,
    ) -> Result<Self, ProcessError> {
        config.validate()?;
        tokio::runtime::Handle::try_current()
            .map_err(|cause| ProcessError::new("configure", None, None, io::Error::other(cause)))?;
        let workspace = Arc::new(Workspace::create(&config.scratch_root)?);
        let stdout = Spool::create(workspace.clone(), "stdout", config.buffer_bytes)?;
        let stderr = Spool::create(workspace, "stderr", config.buffer_bytes)?;
        command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped());
        tree::configure(&mut command);
        let mut command = tokio::process::Command::from(command);
        command.kill_on_drop(false);
        let mut child = command
            .spawn()
            .map_err(|cause| ProcessError::new("launch", None, None, cause))?;
        let pid = child.id().ok_or_else(|| {
            ProcessError::new("launch", None, None, io::Error::other("child has no PID"))
        })?;
        // Tree membership supports force-stop only; an unavailable optional job
        // must not kill a command that has already started doing external work.
        let tree = tree::Tree::attach(&child, pid).ok();
        let stdin = child.stdin.take();
        // All three handles are guaranteed by our piped stdio configuration.
        let child_stdout = child.stdout.take().expect("piped stdout");
        let child_stderr = child.stderr.take().expect("piped stderr");
        let cancel = CancellationToken::new();
        let budget = Arc::new(OutputBudget {
            used: AtomicU64::new(0),
            limit: config.max_output_bytes,
        });
        let stdout_reader = stdout.reader;
        let stderr_reader = stderr.reader;
        let out = tokio::spawn(drain(
            child_stdout,
            stdout.writer,
            config.buffer_bytes,
            budget.clone(),
            cancel.clone(),
            pid,
        ));
        let err = tokio::spawn(drain(
            child_stderr,
            stderr.writer,
            config.buffer_bytes,
            budget,
            cancel.clone(),
            pid,
        ));
        let (finished, receiver) = watch::channel(None);
        let (status, status_receiver) = watch::channel(None);
        let worker_cancel = cancel.clone();
        let exited = CancellationToken::new();
        let worker_exited = ExitObserver {
            signal: exited.clone(),
            status,
        };
        tokio::spawn(async move {
            let result = finish(child, tree, pid, out, err, worker_cancel, &worker_exited).await;
            let _ = finished.send(Some(result));
            worker_exited.signal.cancel();
        });
        Ok(Self {
            pid,
            stdin,
            stdout: Some(stdout_reader),
            stderr: Some(stderr_reader),
            cancel,
            exited,
            status: status_receiver,
            finished: receiver,
        })
    }

    pub fn pid(&self) -> u32 {
        self.pid
    }

    pub fn take_stdin(&mut self) -> Option<ChildStdin> {
        self.stdin.take()
    }

    pub fn close_stdin(&mut self) {
        self.stdin.take();
    }

    pub fn take_stdout(&mut self) -> Option<SpoolReader> {
        self.stdout.take()
    }

    pub fn take_stderr(&mut self) -> Option<SpoolReader> {
        self.stderr.take()
    }

    pub fn is_finished(&self) -> bool {
        self.finished.borrow().is_some()
    }

    pub fn exit_signal(&self) -> CancellationToken {
        self.exited.clone()
    }

    /// Observe the root's exit without waiting for inherited output pipes to
    /// close. Unlike `completion`, this carries no final output-byte counts.
    pub fn exit_status(
        &self,
    ) -> impl std::future::Future<Output = Result<ProcessExitStatus, ProcessError>> + Send + 'static
    {
        let mut status = self.status.clone();
        let pid = self.pid;
        async move {
            loop {
                let result = status.borrow().clone();
                if let Some(result) = result {
                    return result;
                }
                status.changed().await.map_err(|cause| {
                    ProcessError::new("observe exit", Some(pid), None, io::Error::other(cause))
                })?;
            }
        }
    }

    /// Observe the retained completion without borrowing the program owner.
    /// Creating this future neither waits nor closes stdin.
    pub fn completion(
        &self,
    ) -> impl std::future::Future<Output = Result<ExitInfo, ProcessError>> + Send + 'static {
        let mut finished = self.finished.clone();
        let pid = self.pid;
        async move {
            loop {
                let result = finished.borrow().clone();
                if let Some(result) = result {
                    return result;
                }
                finished.changed().await.map_err(|cause| {
                    ProcessError::new("join", Some(pid), None, io::Error::other(cause))
                })?;
            }
        }
    }

    /// Close any untaken stdin, then await the leader, descendants and both
    /// physical output drains. A nonzero program exit is data, not an I/O error.
    pub async fn wait(&mut self) -> Result<ExitInfo, ProcessError> {
        self.close_stdin();
        loop {
            if let Some(result) = self.finished.borrow().as_ref() {
                return result.clone();
            }
            self.finished.changed().await.map_err(|cause| {
                ProcessError::new("join", Some(self.pid), None, io::Error::other(cause))
            })?;
        }
    }

    /// Explicit destructive stop, never used for ordinary interpreter cancellation.
    pub async fn force_stop_and_wait(&mut self) -> Result<ExitInfo, ProcessError> {
        self.cancel.cancel();
        self.wait().await
    }

    /// Explicit destructive compatibility operation; prefer `force_stop_and_wait`.
    pub async fn cancel_and_wait(&mut self) -> Result<ExitInfo, ProcessError> {
        self.force_stop_and_wait().await
    }

    /// Scalar ports are replayable UTF-8. Stream ports must be taken exactly once
    /// through `take_stdout`/`take_stderr`; requesting one here is a type error.
    pub async fn scalar(&mut self, port: ReadPort) -> Result<Vec<u8>, ProcessError> {
        if port == ReadPort::Pid {
            return Ok(format!("{}\n", self.pid).into_bytes());
        }
        if matches!(port, ReadPort::Stdout | ReadPort::Stderr) {
            return Err(ProcessError::new(
                "read scalar",
                Some(self.pid),
                None,
                io::Error::new(
                    io::ErrorKind::InvalidInput,
                    "this port is a consuming byte stream",
                ),
            ));
        }
        if port == ReadPort::ExitCode {
            self.close_stdin();
            let status = self.exit_status().await?;
            return Ok(format!("{}\n", status.exit_code).into_bytes());
        }
        let exit = self.wait().await?;
        Ok(match port {
            ReadPort::Meta => exit.metadata(),
            _ => unreachable!("ports checked above"),
        })
    }
}

async fn finish(
    mut child: Child,
    tree: Option<tree::Tree>,
    pid: u32,
    out: tokio::task::JoinHandle<Result<u64, ProcessError>>,
    err: tokio::task::JoinHandle<Result<u64, ProcessError>>,
    cancel: CancellationToken,
    exited: &ExitObserver,
) -> Result<ExitInfo, ProcessError> {
    let status = tokio::select! {
        result = child.wait() => result,
        () = cancel.cancelled() => {
            if let Some(tree) = &tree { tree.terminate(); }
            let _ = child.start_kill();
            child.wait().await
        }
    }
    .map_err(|cause| ProcessError::new("wait", Some(pid), None, cause));
    let observed = status
        .as_ref()
        .map(|status| ProcessExitStatus::new(pid, status, cancel.is_cancelled()))
        .map_err(Clone::clone);
    let _ = exited.status.send_replace(Some(observed));
    exited.signal.cancel();
    let mut drains = std::pin::pin!(async { tokio::join!(out, err) });
    let (out, err) = tokio::select! {
        result = &mut drains => result,
        () = cancel.cancelled() => {
            if let Some(tree) = &tree { tree.terminate(); }
            drains.await
        }
    };
    drop(tree);
    let stdout_bytes = join_drain(out, pid)?;
    let stderr_bytes = join_drain(err, pid)?;
    let status = ProcessExitStatus::new(pid, &status?, cancel.is_cancelled());
    Ok(ExitInfo {
        pid,
        exit_code: status.exit_code,
        success: status.success,
        cancelled: status.cancelled,
        stdout_bytes,
        stderr_bytes,
    })
}

fn join_drain(
    result: Result<Result<u64, ProcessError>, tokio::task::JoinError>,
    pid: u32,
) -> Result<u64, ProcessError> {
    result.map_err(|cause| {
        ProcessError::new("drain worker", Some(pid), None, io::Error::other(cause))
    })?
}

static WORKSPACE_ID: AtomicU64 = AtomicU64::new(0);

struct Workspace {
    path: PathBuf,
}

impl Workspace {
    fn create(root: &Path) -> Result<Self, ProcessError> {
        fs::create_dir_all(root).map_err(|cause| {
            ProcessError::new("create scratch root", None, Some(root.to_owned()), cause)
        })?;
        for _ in 0..128 {
            let path = root.join(format!(
                "process-{}-{}",
                std::process::id(),
                WORKSPACE_ID.fetch_add(1, Ordering::Relaxed)
            ));
            match fs::create_dir(&path) {
                Ok(()) => return Ok(Self { path }),
                Err(cause) if cause.kind() == io::ErrorKind::AlreadyExists => continue,
                Err(cause) => {
                    return Err(ProcessError::new("create scratch", None, Some(path), cause));
                }
            }
        }
        Err(ProcessError::new(
            "create scratch",
            None,
            Some(root.to_owned()),
            io::Error::new(
                io::ErrorKind::AlreadyExists,
                "cannot allocate a private process workspace",
            ),
        ))
    }
}

impl Drop for Workspace {
    fn drop(&mut self) {
        // Only this exclusively created directory is removed; no caller path is
        // recursively cleaned. The last spool file handle drops before this Arc.
        let _ = fs::remove_dir_all(&self.path);
    }
}

#[derive(Default)]
struct SpoolState {
    written: u64,
    done: Option<Result<(), ProcessError>>,
    reader: Option<Waker>,
}

struct Spool {
    writer: SpoolWriter,
    reader: SpoolReader,
}

struct SpoolWriter {
    file: tokio::fs::File,
    state: Arc<Mutex<SpoolState>>,
    path: PathBuf,
    _workspace: Arc<Workspace>,
}

impl SpoolWriter {
    fn publish(&self, bytes: u64) {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.written += bytes;
        if let Some(waker) = state.reader.take() {
            waker.wake();
        }
    }

    fn complete(&self, result: Result<(), ProcessError>) {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        state.done = Some(result);
        if let Some(waker) = state.reader.take() {
            waker.wake();
        }
    }
}

impl Drop for SpoolWriter {
    fn drop(&mut self) {
        let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
        if state.done.is_none() {
            state.done = Some(Err(ProcessError::new(
                "drain output",
                None,
                Some(self.path.clone()),
                io::Error::new(
                    io::ErrorKind::BrokenPipe,
                    "output drain ended before completion",
                ),
            )));
            if let Some(waker) = state.reader.take() {
                waker.wake();
            }
        }
    }
}

impl Spool {
    fn create(
        workspace: Arc<Workspace>,
        name: &str,
        buffer_bytes: usize,
    ) -> Result<Self, ProcessError> {
        let path = workspace.path.join(name);
        let make_error =
            |cause| ProcessError::new("create output spool", None, Some(path.clone()), cause);
        let writer = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(&path)
            .map_err(make_error)?;
        let reader = fs::File::open(&path).map_err(make_error)?;
        let state = Arc::new(Mutex::new(SpoolState::default()));
        let mut writer = tokio::fs::File::from_std(writer);
        writer.set_max_buf_size(buffer_bytes);
        let mut reader = tokio::fs::File::from_std(reader);
        reader.set_max_buf_size(buffer_bytes);
        Ok(Self {
            writer: SpoolWriter {
                file: writer,
                state: state.clone(),
                path,
                _workspace: workspace.clone(),
            },
            reader: SpoolReader {
                file: reader,
                state,
                position: 0,
                _workspace: workspace,
            },
        })
    }
}

/// A single-owner tail reader. EOF means the child pipe physically closed and
/// all preceding writes reached the spool, not merely a temporarily empty file.
pub struct SpoolReader {
    file: tokio::fs::File,
    state: Arc<Mutex<SpoolState>>,
    position: u64,
    _workspace: Arc<Workspace>,
}

impl AsyncRead for SpoolReader {
    fn poll_read(
        mut self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        if buffer.remaining() == 0 {
            return Poll::Ready(Ok(()));
        }
        let available = {
            let mut state = self.state.lock().unwrap_or_else(|error| error.into_inner());
            if self.position == state.written {
                if let Some(result) = &state.done {
                    return Poll::Ready(
                        result.as_ref().map_err(ProcessError::reader_error).copied(),
                    );
                }
                if !state
                    .reader
                    .as_ref()
                    .is_some_and(|waker| waker.will_wake(cx.waker()))
                {
                    state.reader = Some(cx.waker().clone());
                }
                return Poll::Pending;
            }
            (state.written - self.position).min(buffer.remaining() as u64) as usize
        };
        let mut part = ReadBuf::new(buffer.initialize_unfilled_to(available));
        match Pin::new(&mut self.file).poll_read(cx, &mut part) {
            Poll::Ready(Ok(())) => {
                let count = part.filled().len();
                if count == 0 {
                    return Poll::Ready(Err(io::Error::new(
                        io::ErrorKind::UnexpectedEof,
                        "process spool ended before its published bytes",
                    )));
                }
                self.position += count as u64;
                buffer.advance(count);
                Poll::Ready(Ok(()))
            }
            result => result,
        }
    }
}

struct OutputBudget {
    used: AtomicU64,
    limit: Option<u64>,
}

impl OutputBudget {
    fn reserve(&self, bytes: u64) -> io::Result<()> {
        self.used
            .try_update(Ordering::Relaxed, Ordering::Relaxed, |used| {
                used.checked_add(bytes)
                    .filter(|next| self.limit.is_none_or(|limit| *next <= limit))
            })
            .map(|_| ())
            .map_err(|_| {
                io::Error::new(
                    io::ErrorKind::StorageFull,
                    "combined stdout/stderr spool quota exceeded",
                )
            })
    }
}

async fn drain(
    mut source: impl AsyncRead + Unpin,
    mut spool: SpoolWriter,
    buffer_bytes: usize,
    budget: Arc<OutputBudget>,
    _cancel: CancellationToken,
    pid: u32,
) -> Result<u64, ProcessError> {
    let result = drain_inner(&mut source, &mut spool, buffer_bytes, &budget).await;
    match result {
        Ok(bytes) => {
            spool.complete(Ok(()));
            Ok(bytes)
        }
        Err(cause) => {
            let error =
                ProcessError::new("drain output", Some(pid), Some(spool.path.clone()), cause);
            spool.complete(Err(error.clone()));
            // A failed/quota-limited spool is not permission to kill a child.
            // Keep draining into a bounded sink so its own exit remains possible.
            let mut buffer = vec![0; buffer_bytes];
            loop {
                match source.read(&mut buffer).await {
                    Ok(0) | Err(_) => break,
                    Ok(_) => {}
                }
            }
            Err(error)
        }
    }
}

async fn drain_inner(
    source: &mut (impl AsyncRead + Unpin),
    spool: &mut SpoolWriter,
    buffer_bytes: usize,
    budget: &OutputBudget,
) -> io::Result<u64> {
    let mut buffer = vec![0; buffer_bytes];
    let mut bytes = 0;
    loop {
        let count = source.read(&mut buffer).await?;
        if count == 0 {
            return Ok(bytes);
        }
        budget.reserve(count as u64)?;
        spool.file.write_all(&buffer[..count]).await?;
        // Tokio File can enqueue a blocking write; flush makes published bytes
        // available before waking a tail reader on the other file handle.
        spool.file.flush().await?;
        spool.publish(count as u64);
        bytes += count as u64;
    }
}
