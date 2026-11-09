//! Native program ownership and typed byte/scalar ports.
//!
//! Programs observe the real filesystem when launched. Their side effects are
//! neither snapshotted nor replayed; synchronization belongs to IR barriers.

use std::ffi::OsString;
use std::future::Future;
use std::io;
use std::path::{Path, PathBuf};
use std::pin::Pin;
use std::process::Command;
use std::task::{Context, Poll, ready};

use tokio::io::{AsyncRead, AsyncWrite, ReadBuf};
use tokio_util::sync::CancellationToken;

use crate::endpoint::{Backend, Location};
use crate::hir::{Argument, ReadPort};
use crate::process::{ExitInfo, Process, ProcessConfig, ProcessError, SpoolReader};
use crate::syntax::Span;

use super::error::{ExecutionError, Result};

pub(super) type Reader = Pin<Box<dyn AsyncRead + Send>>;
pub(super) type Writer = Pin<Box<dyn AsyncWrite + Send>>;

type Completion = Pin<Box<dyn Future<Output = std::result::Result<Vec<u8>, ProcessError>> + Send>>;

enum ScalarState {
    Pending(Completion),
    Ready(Vec<u8>),
    Failed(ProcessError),
}

struct ScalarReader {
    state: ScalarState,
    offset: usize,
}

impl AsyncRead for ScalarReader {
    fn poll_read(
        self: Pin<&mut Self>,
        cx: &mut Context<'_>,
        buffer: &mut ReadBuf<'_>,
    ) -> Poll<io::Result<()>> {
        let this = self.get_mut();
        if buffer.remaining() == 0 {
            return Poll::Ready(Ok(()));
        }
        if let ScalarState::Pending(completion) = &mut this.state {
            this.state = match ready!(completion.as_mut().poll(cx)) {
                Ok(bytes) => ScalarState::Ready(bytes),
                Err(cause) => ScalarState::Failed(cause),
            };
        }
        match &this.state {
            ScalarState::Ready(bytes) => {
                let count = buffer.remaining().min(bytes.len() - this.offset);
                buffer.put_slice(&bytes[this.offset..this.offset + count]);
                this.offset += count;
                Poll::Ready(Ok(()))
            }
            ScalarState::Failed(cause) => Poll::Ready(Err(io::Error::other(cause.clone()))),
            ScalarState::Pending(_) => unreachable!("pending completion returned before reading"),
        }
    }
}

pub(super) struct Program {
    pub span: Span,
    pub origin: Option<crate::source::InstructionOrigin>,
    pub stdin: Option<Writer>,
    pub stdout: Option<SpoolReader>,
    pub stderr: Option<SpoolReader>,
    pub pid: u32,
    process: Process,
    completed: Option<std::result::Result<ExitInfo, ProcessError>>,
}

pub(super) struct Launch<'a> {
    pub span: Span,
    pub origin: Option<crate::source::InstructionOrigin>,
    pub executable: &'a Location,
    pub arguments: &'a [Argument],
    pub cwd: &'a Path,
    pub config: ProcessConfig,
}

impl Program {
    pub async fn launch(launch: Launch<'_>) -> Result<Self> {
        let command = command(launch.executable, launch.arguments, launch.cwd, launch.span)
            .map_err(|error| error.with_origin(launch.origin.as_ref()))?;
        let mut process = Process::spawn_native(command, launch.config).map_err(|cause| {
            ExecutionError::caused("cannot launch the external program", launch.span, cause)
                .with_origin(launch.origin.as_ref())
        })?;
        Ok(Self {
            span: launch.span,
            origin: launch.origin,
            stdin: process.take_stdin().map(|pipe| Box::pin(pipe) as Writer),
            stdout: process.take_stdout(),
            stderr: process.take_stderr(),
            pid: process.pid(),
            process,
            completed: None,
        })
    }

    pub fn take_reader(&mut self, port: ReadPort) -> Result<Reader> {
        let reader = match port {
            ReadPort::Stdout => self.stdout.take(),
            ReadPort::Stderr => self.stderr.take(),
            _ => {
                return Err(self.error("this program member is a scalar, not a consuming stream"));
            }
        }
        .ok_or_else(|| self.error("the program stream already has a consumer"))?;
        Ok(Box::pin(reader))
    }

    /// Enqueue a scalar observation without blocking independent graph nodes.
    /// Each reader owns a completion subscription, not a borrow of this program.
    pub fn scalar_reader(&self, port: ReadPort) -> Result<Reader> {
        if port == ReadPort::Pid {
            return Ok(Box::pin(io::Cursor::new(
                format!("{}\n", self.pid).into_bytes(),
            )));
        }
        if matches!(port, ReadPort::Stdout | ReadPort::Stderr) {
            return Err(self.error("this program member is a consuming stream, not a scalar"));
        }
        let completion: Completion = match port {
            ReadPort::ExitCode => {
                let status = self.process.exit_status();
                Box::pin(async move { Ok(format!("{}\n", status.await?.exit_code).into_bytes()) })
            }
            ReadPort::Meta => {
                let completion = self.process.completion();
                Box::pin(async move { Ok(exit_json(&completion.await?)) })
            }
            _ => unreachable!("non-completion scalar ports were handled above"),
        };
        Ok(Box::pin(ScalarReader {
            state: ScalarState::Pending(completion),
            offset: 0,
        }))
    }

    pub fn take_stdin(&mut self) -> Result<Writer> {
        self.stdin
            .take()
            .ok_or_else(|| self.error("the program stdin already has a producer or is closed"))
    }

    pub fn close_unused_stdin(&mut self) {
        self.stdin.take();
    }

    pub fn exit_signal(&self) -> CancellationToken {
        self.process.exit_signal()
    }

    /// Await natural exit and both output drains. Nonzero exit is observable data.
    pub async fn wait(&mut self) -> Result<&ExitInfo> {
        self.close_unused_stdin();
        if self.completed.is_none() {
            self.completed = Some(self.process.wait().await);
        }
        self.completed
            .as_ref()
            .expect("completion was retained")
            .as_ref()
            .map_err(|cause| {
                ExecutionError::caused("cannot join the external program", self.span, cause.clone())
                    .with_origin(self.origin.as_ref())
            })
    }
    pub async fn force_stop(&mut self) -> Result<()> {
        self.close_unused_stdin();
        if self.completed.is_none() {
            self.completed = Some(self.process.force_stop_and_wait().await);
        }
        self.completed
            .as_ref()
            .expect("completion was retained")
            .as_ref()
            .map(|_| ())
            .map_err(|cause| {
                ExecutionError::caused(
                    "cannot forcibly stop the external program",
                    self.span,
                    cause.clone(),
                )
                .with_origin(self.origin.as_ref())
            })
    }

    pub fn is_finished(&self) -> bool {
        self.completed.is_some() || self.process.is_finished()
    }

    pub fn completion_signal(&self) -> CancellationToken {
        self.process.completion_signal()
    }

    pub fn is_running(&self) -> bool {
        !self.is_finished()
    }

    pub fn receipt(&self) -> Option<&std::result::Result<ExitInfo, ProcessError>> {
        self.completed.as_ref()
    }

    fn error(&self, message: &str) -> ExecutionError {
        ExecutionError::new(message, self.span).with_origin(self.origin.as_ref())
    }
}

fn command(
    executable: &Location,
    arguments: &[Argument],
    cwd: &Path,
    span: Span,
) -> Result<Command> {
    let mut command = match &executable.backend {
        Backend::Local | Backend::Path | Backend::Env => {
            let executable = local(executable, cwd, span)?;
            let mut command = Command::new(executable);
            for argument in arguments {
                command.arg(match argument {
                    Argument::Text(text) => OsString::from(text),
                    Argument::Path(path) => local(path, cwd, span)?.into_os_string(),
                });
            }
            command
        }
        Backend::Wsl | Backend::Ssh { .. } => remote_command(executable, arguments, span)?,
        _ => {
            return Err(ExecutionError::new(
                "this endpoint cannot launch an external program",
                span,
            ));
        }
    };
    command.current_dir(cwd);
    Ok(command)
}

fn local(location: &Location, cwd: &Path, span: Span) -> Result<PathBuf> {
    if location.backend == Backend::Path {
        let name = Path::new(&location.path);
        if name.components().count() != 1 || location.path.contains(['/', '\\']) {
            return Err(ExecutionError::new(
                "a PATH executable must be a single file name",
                span,
            ));
        }
        // Use native executable search at spawn, including the optional .exe
        // suffix on Windows. No cached existence check is authoritative.
        return Ok(name.to_owned());
    }
    if !matches!(location.backend, Backend::Local | Backend::Env) {
        return Err(ExecutionError::new(
            "a native argv path must name a local filesystem endpoint",
            span,
        )
        .help("Use a text argument for a path interpreted by the program itself."));
    }
    let path = crate::runtime::local_path(location, true)
        .map_err(|message| ExecutionError::new(message, span))?
        .ok_or_else(|| ExecutionError::new("a local executable or argv path was expected", span))?;
    Ok(if path.is_absolute() {
        path
    } else {
        cwd.join(path)
    })
}

fn remote_command(executable: &Location, arguments: &[Argument], span: Span) -> Result<Command> {
    let mut script = format!("exec {}", shell_path(&executable.path));
    for argument in arguments {
        script.push(' ');
        match argument {
            Argument::Text(text) => script.push_str(&shell_quote(text)),
            Argument::Path(path) if path.backend == executable.backend => {
                script.push_str(&shell_path(&path.path));
            }
            Argument::Path(_) => {
                return Err(ExecutionError::new(
                    "a remote argv path must belong to the executable's endpoint",
                    span,
                )
                .help(
                    "Use a text argument when the remote program interprets another path syntax.",
                ));
            }
        }
    }
    crate::runtime::remote_command_builder(&executable.backend, &script, &[])
        .map_err(|message| ExecutionError::new(message, span))
}

fn shell_path(path: &str) -> String {
    if path == "~" {
        return "\"$HOME\"".into();
    }
    if let Some(suffix) = path.strip_prefix("~/") {
        return format!("\"$HOME\"/{}", shell_quote(suffix));
    }
    shell_quote(path)
}

fn shell_quote(text: &str) -> String {
    format!("'{}'", text.replace('\'', "'\\''"))
}

fn exit_json(exit: &ExitInfo) -> Vec<u8> {
    format!(
        "{{\"pid\":{},\"exit_code\":{},\"success\":{},\"cancelled\":{},\"stdout_bytes\":{},\"stderr_bytes\":{}}}\n",
        exit.pid,
        exit.exit_code,
        exit.success,
        exit.cancelled,
        exit.stdout_bytes,
        exit.stderr_bytes
    )
    .into_bytes()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn native_argv_preserves_literal_and_path_types() {
        let cwd = std::env::current_dir().unwrap();
        let command = command(
            &Location {
                backend: Backend::Path,
                path: "some-tool".into(),
            },
            &[
                Argument::Text("$HOME ; 'not shell'".into()),
                Argument::Path(Location {
                    backend: Backend::Local,
                    path: "not-yet-created".into(),
                }),
            ],
            &cwd,
            Span::default(),
        )
        .unwrap();
        assert_eq!(command.get_program(), "some-tool");
        let args = command.get_args().collect::<Vec<_>>();
        assert_eq!(args[0], "$HOME ; 'not shell'");
        assert_eq!(args[1], cwd.join("not-yet-created").as_os_str());
        assert_eq!(command.get_current_dir(), Some(cwd.as_path()));
    }

    #[test]
    fn remote_home_expansion_does_not_evaluate_argument_text() {
        assert_eq!(shell_path("~/a b/$(no)"), "\"$HOME\"/'a b/$(no)'");
        assert_eq!(shell_quote("$(no)'"), "'$(no)'\\'''");
    }

    #[test]
    fn mismatched_remote_path_is_rejected_without_launch() {
        let error = remote_command(
            &Location {
                backend: Backend::Wsl,
                path: "/bin/cat".into(),
            },
            &[Argument::Path(Location {
                backend: Backend::Local,
                path: "file".into(),
            })],
            Span { start: 4, end: 12 },
        )
        .unwrap_err();
        assert!(error.to_string().contains("executable's endpoint"));
    }

    fn scratch() -> tempfile::TempDir {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        std::fs::create_dir_all(&root).unwrap();
        tempfile::Builder::new()
            .prefix("native-program-")
            .tempdir_in(root)
            .unwrap()
    }

    fn shell(script: &str) -> (Location, Vec<Argument>) {
        let (path, prefix): (&str, &[&str]) = if cfg!(windows) {
            ("cmd.exe", &["/D", "/C"])
        } else {
            ("sh", &["-c"])
        };
        let arguments = prefix
            .iter()
            .copied()
            .chain(std::iter::once(script))
            .map(|text| Argument::Text(text.to_owned()))
            .collect();
        (
            Location {
                backend: Backend::Path,
                path: path.into(),
            },
            arguments,
        )
    }

    #[tokio::test]
    async fn nonzero_exit_keeps_both_streams_available_after_wait() {
        use tokio::io::AsyncReadExt;

        let workspace = scratch();
        let script = if cfg!(windows) {
            "echo native-output& echo native-error 1>&2& exit /B 7"
        } else {
            "printf native-output; printf native-error >&2; exit 7"
        };
        let (executable, arguments) = shell(script);
        let mut program = Program::launch(Launch {
            span: Span::default(),
            origin: None,
            executable: &executable,
            arguments: &arguments,
            cwd: workspace.path(),
            config: ProcessConfig::new(workspace.path()),
        })
        .await
        .unwrap();
        assert_eq!(program.wait().await.unwrap().exit_code, 7);
        let mut exit_code = Vec::new();
        program
            .scalar_reader(ReadPort::ExitCode)
            .unwrap()
            .read_to_end(&mut exit_code)
            .await
            .unwrap();
        assert_eq!(exit_code, b"7\n");
        assert!(program.exit_signal().is_cancelled());
        for (port, expected) in [
            (ReadPort::Stdout, "native-output"),
            (ReadPort::Stderr, "native-error"),
        ] {
            let mut output = Vec::new();
            program
                .take_reader(port)
                .unwrap()
                .read_to_end(&mut output)
                .await
                .unwrap();
            assert!(String::from_utf8(output).unwrap().contains(expected));
            assert!(program.take_reader(port).is_err());
        }
    }

    #[tokio::test]
    async fn launched_program_accepts_stdin_before_await() {
        use tokio::io::{AsyncReadExt, AsyncWriteExt};

        let workspace = scratch();
        let script = if cfg!(windows) { "findstr ." } else { "cat" };
        let (executable, arguments) = shell(script);
        let mut program = Program::launch(Launch {
            span: Span::default(),
            origin: None,
            executable: &executable,
            arguments: &arguments,
            cwd: workspace.path(),
            config: ProcessConfig::new(workspace.path()),
        })
        .await
        .unwrap();
        let mut stdin = program.take_stdin().unwrap();
        assert!(program.take_stdin().is_err());
        stdin.write_all(b"feed-before-await\n").await.unwrap();
        stdin.shutdown().await.unwrap();
        drop(stdin);
        assert!(program.wait().await.unwrap().success);
        let mut output = Vec::new();
        program
            .take_reader(ReadPort::Stdout)
            .unwrap()
            .read_to_end(&mut output)
            .await
            .unwrap();
        assert!(
            String::from_utf8(output)
                .unwrap()
                .contains("feed-before-await")
        );
    }
    #[tokio::test]
    async fn a_drain_failure_is_settled_without_truncating_the_child() {
        let workspace = scratch();
        let script = if cfg!(windows) {
            "(for /L %i in (1,1,10000) do @echo payload)& echo done>child-finished"
        } else {
            "i=0; while [ \"$i\" -lt 10000 ]; do printf payload; i=$((i+1)); done; printf done > child-finished"
        };
        let (executable, arguments) = shell(script);
        let mut config = ProcessConfig::new(workspace.path());
        config.max_output_bytes = Some(0);
        let mut program = Program::launch(Launch {
            span: Span::default(),
            origin: None,
            executable: &executable,
            arguments: &arguments,
            cwd: workspace.path(),
            config,
        })
        .await
        .unwrap();
        let result = tokio::time::timeout(std::time::Duration::from_secs(10), program.wait())
            .await
            .expect("failed drains must still consume child output to EOF");
        assert!(result.is_err());
        assert!(workspace.path().join("child-finished").is_file());
        assert!(program.is_finished());
        assert!(program.receipt().is_some_and(std::result::Result::is_err));
    }

    #[tokio::test]
    async fn deferred_exit_code_does_not_block_an_independent_marker_write() {
        use tokio::io::AsyncReadExt;

        let workspace = scratch();
        let release = ReleaseMarker(workspace.path().join("release-marker"));
        let executable = Location {
            backend: Backend::Local,
            path: std::env::current_exe()
                .unwrap()
                .to_string_lossy()
                .into_owned(),
        };
        let arguments = [
            "--exact",
            "execution::program::tests::deferred_exit_child",
            "--ignored",
            "--nocapture",
        ]
        .into_iter()
        .map(|text| Argument::Text(text.into()))
        .collect::<Vec<_>>();
        let mut program = Program::launch(Launch {
            span: Span::default(),
            origin: None,
            executable: &executable,
            arguments: &arguments,
            cwd: workspace.path(),
            config: ProcessConfig::new(workspace.path()),
        })
        .await
        .unwrap();
        let mut scalar = program.scalar_reader(ReadPort::ExitCode).unwrap();
        let read = crate::runtime::ssh::spawn(async move {
            let mut bytes = Vec::new();
            scalar.read_to_end(&mut bytes).await.unwrap();
            bytes
        });
        tokio::time::timeout(std::time::Duration::from_secs(10), async {
            while !workspace.path().join("child-ready").is_file() {
                tokio::time::sleep(std::time::Duration::from_millis(2)).await;
            }
        })
        .await
        .expect("native fixture did not acknowledge startup before the scalar phase");
        let pending_before_marker = !read.is_finished();
        release.open();
        let bytes = tokio::time::timeout(std::time::Duration::from_secs(10), read)
            .await
            .expect("exit-code subscription did not settle after releasing the child")
            .unwrap();
        let exit = tokio::time::timeout(std::time::Duration::from_secs(10), program.wait())
            .await
            .expect("root exit was observed, but physical stdout/stderr drains did not settle")
            .unwrap();
        assert!(
            pending_before_marker,
            "the child must remain gated while its scalar is pending"
        );
        assert!(workspace.path().join("release-marker").is_file());
        assert_eq!(bytes, b"7\n");
        assert_eq!(exit.exit_code, 7);
        let mut replay = Vec::new();
        let mut scalar = program.scalar_reader(ReadPort::ExitCode).unwrap();
        tokio::time::timeout(
            std::time::Duration::from_secs(10),
            scalar.read_to_end(&mut replay),
        )
        .await
        .expect("a late exit-code subscription lost the retained result")
        .unwrap();
        assert_eq!(replay, bytes);
    }

    struct ReleaseMarker(PathBuf);

    impl ReleaseMarker {
        fn open(&self) {
            std::fs::write(&self.0, b"continue").unwrap();
        }
    }

    impl Drop for ReleaseMarker {
        fn drop(&mut self) {
            let _ = std::fs::write(&self.0, b"continue");
        }
    }

    #[test]
    #[ignore = "bounded native child fixture; invoked only by the deferred-scalar journey"]
    fn deferred_exit_child() {
        std::fs::write("child-ready", b"ready").unwrap();
        let deadline = std::time::Instant::now() + std::time::Duration::from_secs(20);
        while !Path::new("release-marker").is_file() {
            if std::time::Instant::now() >= deadline {
                eprintln!("deferred-scalar fixture reached its own release deadline");
                std::process::exit(124);
            }
            std::thread::sleep(std::time::Duration::from_millis(2));
        }
        std::process::exit(7);
    }
}
