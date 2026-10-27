//! The boxed worker is an IO bridge, not a shell or a privileged path broker.

use std::ffi::OsString;
use std::fs;
use std::io;
use std::os::windows::io::AsRawHandle;
use std::path::{Component, Path, PathBuf, Prefix};
use std::process::{Command, ExitCode};
use std::sync::Arc;

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::windows::named_pipe::ClientOptions;
use tokio::sync::{Mutex, oneshot};
use tokio_util::sync::CancellationToken;
use windows_sys::Win32::System::Pipes::GetNamedPipeServerProcessId;

use super::wire::{Decode, Encode};
use super::{ProgramSpec, native, trees, wire};
use crate::process::{ExitInfo, Process, ProcessConfig};
use crate::transaction::filesystem::SnapshotStore;

pub(super) fn entry(args: impl Iterator<Item = OsString>) -> ExitCode {
    let args = args.collect::<Vec<_>>();
    let result = tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .map_err(io::Error::other)
        .and_then(|runtime| runtime.block_on(run(&args)));
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(failure) => {
            eprintln!("msync boxed worker: {failure}");
            ExitCode::from(125)
        }
    }
}

async fn run(args: &[OsString]) -> io::Result<()> {
    if args.len() != 4 {
        return Err(wire::invalid(
            "worker expects pipe, owned box, nonce and parent PID",
        ));
    }
    let name = args[1]
        .to_str()
        .ok_or_else(|| wire::invalid("box name must be UTF-8"))?;
    native::owned_name(name)?;
    let nonce = args[2]
        .to_str()
        .filter(|value| value.len() == 32 && value.bytes().all(|byte| byte.is_ascii_hexdigit()))
        .ok_or_else(|| wire::invalid("worker nonce is malformed"))?;
    let parent = args[3]
        .to_str()
        .and_then(|value| value.parse::<u32>().ok())
        .ok_or_else(|| wire::invalid("parent PID is malformed"))?;
    let client = ClientOptions::new().open(&args[0])?;
    let mut actual = 0;
    if unsafe { GetNamedPipeServerProcessId(client.as_raw_handle(), &mut actual) } == 0
        || actual != parent
    {
        return Err(io::Error::new(
            io::ErrorKind::PermissionDenied,
            "control server is not the expected parent",
        ));
    }
    let (mut source, mut sink) = tokio::io::split(client);
    let api = match native::Api::detect().and_then(|api| {
        api.belongs(std::process::id(), name)?;
        Ok(api)
    }) {
        Ok(api) => api,
        Err(failure) => {
            send_failure(&mut sink, &failure).await?;
            return Err(failure);
        }
    };
    wire::send(&mut sink, wire::HELLO, &wire::hello(nonce)).await?;
    let initialized = initialize(&mut source, &api, name, nonce).await;
    let (spec, config, roots, _scratch) = match initialized {
        Ok(initialized) => initialized,
        Err(failure) => {
            send_failure(&mut sink, &failure).await?;
            return Err(failure);
        }
    };
    let mut command = Command::new(&spec.executable);
    command.args(&spec.arguments).current_dir(&spec.cwd);
    let mut process = match Process::spawn_native(command, config.clone()) {
        Ok(process) => process,
        Err(failure) => {
            let failure = io::Error::other(failure);
            send_failure(&mut sink, &failure).await?;
            return Err(failure);
        }
    };
    let mut body = Encode::default();
    body.integer(process.pid() as u64);
    wire::send(&mut sink, wire::STARTED, &body.0).await?;
    let sink = Arc::new(Mutex::new(sink));
    let stdout = process.take_stdout().expect("native stdout is owned");
    let stderr = process.take_stderr().expect("native stderr is owned");
    let out = tokio::spawn(forward(
        stdout,
        sink.clone(),
        wire::STDOUT,
        config.buffer_bytes,
    ));
    let err = tokio::spawn(forward(
        stderr,
        sink.clone(),
        wire::STDERR,
        config.buffer_bytes,
    ));
    let cancel = CancellationToken::new();
    let read_cancel = cancel.clone();
    let mut stdin = process.take_stdin();
    let (control, exported) = oneshot::channel();
    tokio::spawn(async move {
        loop {
            let next = wire::receive(&mut source).await;
            match next {
                Ok((wire::STDIN, bytes)) => {
                    if let Some(writer) = &mut stdin
                        && writer.write_all(&bytes).await.is_err()
                    {
                        stdin = None;
                    }
                }
                Ok((wire::CLOSE_STDIN, bytes)) if bytes.is_empty() => {
                    stdin.take();
                }
                Ok((wire::EXPORT, bytes)) if bytes.is_empty() => {
                    let _ = control.send(true);
                    return;
                }
                Ok((wire::RELEASE, bytes)) if bytes.is_empty() => {
                    let _ = control.send(false);
                    return;
                }
                Ok((wire::CANCEL, bytes)) if bytes.is_empty() => {
                    read_cancel.cancel();
                    return;
                }
                _ => {
                    read_cancel.cancel();
                    return;
                }
            }
        }
    });
    let result = tokio::select! {
        result = process.wait() => result,
        () = cancel.cancelled() => process.cancel_and_wait().await,
    };
    let result = async {
        let exit = result.map_err(io::Error::other)?;
        out.await.map_err(io::Error::other)??;
        err.await.map_err(io::Error::other)??;
        wire::send(&mut *sink.lock().await, wire::EXIT, &encode_exit(&exit)).await?;
        if exit.cancelled {
            wire::send(&mut *sink.lock().await, wire::COMPLETE, &[]).await?;
            return Ok(());
        }
        if !exported.await.map_err(io::Error::other)? {
            return wire::send(&mut *sink.lock().await, wire::RELEASE, &[]).await;
        }
        let mut store = SnapshotStore::new(&config.scratch_root).map_err(io::Error::other)?;
        for root in roots {
            let snapshot = store.capture(&root).map_err(io::Error::other)?;
            trees::send_snapshot(
                &mut *sink.lock().await,
                &root,
                &snapshot,
                config.buffer_bytes,
            )
            .await?;
        }
        store.cleanup().map_err(io::Error::other)?;
        wire::send(&mut *sink.lock().await, wire::COMPLETE, &[]).await
    }
    .await;
    if let Err(failure) = &result {
        let _ = send_failure(&mut *sink.lock().await, failure).await;
    }
    result
}

async fn initialize(
    source: &mut (impl tokio::io::AsyncRead + Unpin),
    api: &Arc<native::Api>,
    name: &str,
    nonce: &str,
) -> io::Result<(ProgramSpec, ProcessConfig, Vec<PathBuf>, PrivateScratch)> {
    let (kind, bytes) = wire::receive(source).await?;
    if kind != wire::SPEC {
        return Err(wire::invalid("worker expected a program specification"));
    }
    let mut body = Decode::new(&bytes);
    let executable = body.path()?;
    let cwd = body.path()?;
    let count = body.integer()?;
    if count > 65536 {
        return Err(wire::invalid("program argument count exceeds limit"));
    }
    let arguments = (0..count)
        .map(|_| body.text())
        .collect::<io::Result<Vec<_>>>()?;
    let buffer = body.integer()?;
    let quota = body.integer()?;
    body.finish()?;
    if buffer == 0 || buffer > 1024 * 1024 || !executable.is_absolute() || !cwd.is_absolute() {
        return Err(wire::invalid("invalid program paths or buffer limits"));
    }
    let mut roots: Vec<PathBuf> = Vec::new();
    loop {
        let (kind, bytes) = wire::receive(source).await?;
        if kind == wire::SEEDED && bytes.is_empty() {
            break;
        }
        if !matches!(kind, wire::ROOT | wire::ROOT_REFERENCE) || roots.len() >= 4096 {
            return Err(wire::invalid("invalid or excessive managed roots"));
        }
        let mut body = Decode::new(&bytes);
        let root = body.path()?;
        body.finish()?;
        if !root.is_absolute()
            || root.file_name().is_none()
            || roots
                .iter()
                .any(|old| root.starts_with(old) || old.starts_with(&root))
        {
            return Err(wire::invalid("managed roots must be absolute and disjoint"));
        }
        if kind == wire::ROOT_REFERENCE {
            if !fs::symlink_metadata(&root)?.is_dir() {
                return Err(wire::invalid(
                    "shared managed root must be an existing directory",
                ));
            }
            roots.push(root);
            continue;
        }
        match fs::symlink_metadata(&root) {
            Err(error) if error.kind() == io::ErrorKind::NotFound => {}
            Err(error) => return Err(error),
            Ok(_) => {
                return Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    format!(
                        "WriteFilePath did not hide live host root {}",
                        root.display()
                    ),
                ));
            }
        }
        trees::receive_tree(source, &root).await?;
        roots.push(root);
    }
    let mut contained = false;
    for root in &roots {
        contained = trees::contains_directory(root, &cwd)?;
        if contained {
            break;
        }
    }
    if !contained {
        return Err(wire::invalid(
            "program cwd is outside its managed namespace",
        ));
    }
    let scratch = PrivateScratch::create(api, name, nonce, &cwd, &roots)?;
    let mut config = ProcessConfig::new(&scratch.path);
    config.buffer_bytes = buffer as usize;
    config.max_output_bytes = (quota != u64::MAX).then_some(quota);
    Ok((
        ProgramSpec {
            executable,
            arguments,
            cwd,
        },
        config,
        roots,
        scratch,
    ))
}

/// A logical sibling of every enrolled directory, physically owned by the box.
/// This avoids needing an exclusion token when capturing a managed CWD.
struct PrivateScratch {
    path: PathBuf,
    identity: same_file::Handle,
    api: Arc<native::Api>,
    name: String,
}

impl PrivateScratch {
    fn create(
        api: &Arc<native::Api>,
        name: &str,
        nonce: &str,
        cwd: &Path,
        roots: &[PathBuf],
    ) -> io::Result<Self> {
        let path = scratch_path(cwd, roots, nonce, std::process::id())?;
        api.belongs(std::process::id(), name)?;
        for key in [
            "OpenFilePath",
            "OpenFilePathEx",
            "NormalFilePath",
            "ReadFilePath",
        ] {
            if !api.values(name, key)?.is_empty() {
                return Err(io::Error::new(
                    io::ErrorKind::PermissionDenied,
                    format!("worker scratch refuses effective {key} overrides"),
                ));
            }
        }
        // A single create (not create_dir_all) cannot traverse an attacker-chosen
        // intermediate directory. The authenticated box's verified effective
        // no-bypass policy supplies copy-on-write; verify the actual handle
        // before trusting the newly created directory for storage or cleanup.
        fs::create_dir(&path)?;
        let identity = api.require_boxed_directory(name, &path)?;
        Ok(Self {
            path,
            identity,
            api: api.clone(),
            name: name.to_owned(),
        })
    }
}

impl Drop for PrivateScratch {
    fn drop(&mut self) {
        // Retain the original handle generation and reauthenticate the current
        // path before recursive cleanup. A replacement is left to parent box GC.
        match self.api.require_boxed_directory(&self.name, &self.path) {
            Ok(current) if current == self.identity => {
                if let Err(failure) = fs::remove_dir_all(&self.path) {
                    eprintln!(
                        "msync boxed scratch cleanup {}: {failure}",
                        self.path.display()
                    );
                }
            }
            Ok(_) => eprintln!(
                "msync boxed scratch cleanup refused replaced directory {}",
                self.path.display()
            ),
            Err(failure) => eprintln!(
                "msync boxed scratch cleanup verification {}: {failure}",
                self.path.display()
            ),
        }
    }
}

fn scratch_path(cwd: &Path, roots: &[PathBuf], nonce: &str, pid: u32) -> io::Result<PathBuf> {
    if nonce.len() != 32 || !nonce.bytes().all(|byte| byte.is_ascii_hexdigit()) || pid == 0 {
        return Err(wire::invalid("invalid private scratch identity"));
    }
    let mut components = cwd.components();
    let drive = match components.next() {
        Some(Component::Prefix(prefix)) => match prefix.kind() {
            Prefix::Disk(drive) | Prefix::VerbatimDisk(drive) => drive,
            _ => return Err(wire::invalid("worker scratch requires a local DOS volume")),
        },
        _ => return Err(wire::invalid("worker scratch requires an absolute CWD")),
    };
    if components.next() != Some(Component::RootDir) {
        return Err(wire::invalid("worker scratch requires an absolute CWD"));
    }
    let path = PathBuf::from(format!(
        "{}:\\msync-worker-{nonce}-{pid}",
        char::from(drive)
    ));
    // The candidate has one component below the volume root. Reject an enrolled
    // volume or its exact ancestor, including case/verbatim aliases.
    for root in roots {
        let normal = native::configuration_path(root)?;
        let normal = normal.to_string_lossy();
        let candidate = path.to_string_lossy();
        let normal = normal.trim_end_matches('\\');
        if candidate.eq_ignore_ascii_case(normal)
            || candidate
                .get(..normal.len())
                .is_some_and(|prefix| prefix.eq_ignore_ascii_case(normal))
                && candidate.as_bytes().get(normal.len()) == Some(&b'\\')
            || normal
                .get(..candidate.len())
                .is_some_and(|prefix| prefix.eq_ignore_ascii_case(&candidate))
                && normal.as_bytes().get(candidate.len()) == Some(&b'\\')
        {
            return Err(wire::invalid(
                "worker scratch overlaps an enrolled resource root",
            ));
        }
    }
    Ok(path)
}

async fn forward(
    mut source: crate::process::SpoolReader,
    sink: Arc<Mutex<impl tokio::io::AsyncWrite + Unpin>>,
    kind: u8,
    buffer: usize,
) -> io::Result<()> {
    let mut bytes = vec![0; buffer];
    loop {
        let count = source.read(&mut bytes).await?;
        if count == 0 {
            return Ok(());
        }
        wire::send(&mut *sink.lock().await, kind, &bytes[..count]).await?;
    }
}

fn encode_exit(exit: &ExitInfo) -> Vec<u8> {
    let mut body = Encode::default();
    for value in [
        exit.pid as u64,
        exit.exit_code as u32 as u64,
        u64::from(exit.success),
        u64::from(exit.cancelled),
        exit.stdout_bytes,
        exit.stderr_bytes,
    ] {
        body.integer(value);
    }
    body.0
}

pub(super) fn decode_exit(bytes: &[u8]) -> io::Result<ExitInfo> {
    let mut body = Decode::new(bytes);
    let pid = u32::try_from(body.integer()?).map_err(|_| wire::invalid("invalid PID"))?;
    let code = u32::try_from(body.integer()?).map_err(|_| wire::invalid("invalid exit code"))?;
    let success = boolean(body.integer()?)?;
    let cancelled = boolean(body.integer()?)?;
    let stdout_bytes = body.integer()?;
    let stderr_bytes = body.integer()?;
    body.finish()?;
    Ok(ExitInfo {
        pid,
        exit_code: code as i32,
        success,
        cancelled,
        stdout_bytes,
        stderr_bytes,
    })
}

fn boolean(value: u64) -> io::Result<bool> {
    match value {
        0 => Ok(false),
        1 => Ok(true),
        _ => Err(wire::invalid("invalid boolean")),
    }
}

async fn send_failure(
    sink: &mut (impl tokio::io::AsyncWrite + Unpin),
    failure: &io::Error,
) -> io::Result<()> {
    let mut body = Encode::default();
    let mut current: Option<&(dyn std::error::Error + 'static)> = Some(failure);
    let mut kind = failure.kind();
    let mut raw = None;
    while let Some(error) = current {
        if let Some(error) = error.downcast_ref::<io::Error>() {
            if kind == io::ErrorKind::Other {
                kind = error.kind();
            }
            raw = raw.or(error.raw_os_error());
        }
        current = error.source();
    }
    body.integer(raw.map_or(u64::MAX, |code| code as u32 as u64));
    body.integer(error_kind(kind));
    body.text(&OsString::from(failure.to_string()))?;
    wire::send(sink, wire::FAILURE, &body.0).await
}

pub(super) fn decode_failure(bytes: &[u8]) -> io::Result<(io::Error, String)> {
    let mut body = Decode::new(bytes);
    let raw = body.integer()?;
    let kind = match body.integer()? {
        0 => io::ErrorKind::Other,
        1 => io::ErrorKind::PermissionDenied,
        2 => io::ErrorKind::NotFound,
        3 => io::ErrorKind::StorageFull,
        4 => io::ErrorKind::InvalidData,
        5 => io::ErrorKind::Interrupted,
        6 => io::ErrorKind::BrokenPipe,
        7 => io::ErrorKind::TimedOut,
        8 => io::ErrorKind::Unsupported,
        9 => io::ErrorKind::InvalidInput,
        _ => return Err(wire::invalid("unknown worker error kind")),
    };
    let text = body.text()?.to_string_lossy().into_owned();
    body.finish()?;
    let error = if raw == u64::MAX {
        io::Error::new(kind, text.clone())
    } else {
        io::Error::from_raw_os_error(
            u32::try_from(raw).map_err(|_| wire::invalid("native error exceeds DWORD"))? as i32,
        )
    };
    Ok((error, text))
}

fn error_kind(kind: io::ErrorKind) -> u64 {
    match kind {
        io::ErrorKind::PermissionDenied => 1,
        io::ErrorKind::NotFound => 2,
        io::ErrorKind::StorageFull => 3,
        io::ErrorKind::InvalidData => 4,
        io::ErrorKind::Interrupted => 5,
        io::ErrorKind::BrokenPipe => 6,
        io::ErrorKind::TimedOut => 7,
        io::ErrorKind::Unsupported => 8,
        io::ErrorKind::InvalidInput => 9,
        _ => 0,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn scratch_is_a_disjoint_drive_child_not_a_managed_cwd_descendant() {
        let nonce = "0123456789abcdef0123456789abcdef";
        for cwd in [r"D:\project\work", r"\\?\D:\project\work"] {
            let scratch = scratch_path(Path::new(cwd), &[PathBuf::from(cwd)], nonce, 42).unwrap();
            assert_eq!(
                scratch,
                PathBuf::from(format!(r"D:\msync-worker-{nonce}-42"))
            );
            assert_eq!(scratch.parent(), Some(Path::new(r"D:\")));
        }
        for root in [
            r"D:\".to_owned(),
            format!(r"\\?\d:\MSYNC-WORKER-{nonce}-42"),
            format!(r"D:\msync-worker-{nonce}-42\nested"),
        ] {
            assert!(
                scratch_path(Path::new(r"D:\work"), &[PathBuf::from(root)], nonce, 42).is_err()
            );
        }
        for cwd in [
            r"D:relative",
            r"relative",
            r"\\server\share\work",
            r"\\?\GLOBALROOT\Device\HarddiskVolume1\work",
        ] {
            assert!(scratch_path(Path::new(cwd), &[], nonce, 42).is_err());
        }
        for nonce in ["", "../../escape", "z123456789abcdef0123456789abcdef"] {
            assert!(scratch_path(Path::new(r"D:\work"), &[], nonce, 42).is_err());
        }
        assert!(scratch_path(Path::new(r"D:\work"), &[], nonce, 0).is_err());
    }

    #[tokio::test]
    async fn remote_failures_retain_native_codes_and_domain_error_kinds() {
        for error in [
            io::Error::from_raw_os_error(5),
            io::Error::new(io::ErrorKind::StorageFull, "quota exhausted"),
        ] {
            let raw = error.raw_os_error();
            let kind = error.kind();
            let (mut sink, mut source) = tokio::io::duplex(4096);
            send_failure(&mut sink, &error).await.unwrap();
            let (frame, body) = wire::receive(&mut source).await.unwrap();
            assert_eq!(frame, wire::FAILURE);
            let (decoded, text) = decode_failure(&body).unwrap();
            assert_eq!(decoded.raw_os_error(), raw);
            assert_eq!(decoded.kind(), kind);
            assert!(!text.is_empty());
        }
    }
}
