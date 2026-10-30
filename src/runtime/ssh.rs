//! Foreground authentication leases for later metadata and background data clients.

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::future::Future;
#[cfg(unix)]
use std::path::Path;
use std::path::PathBuf;
#[cfg(unix)]
use std::process::Stdio;
use std::process::{Child, Command};
#[cfg(unix)]
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex};
#[cfg(unix)]
use std::time::Duration;

use tokio_util::sync::CancellationToken;

use crate::compiler::{Backend, Program};

thread_local! {
    static CONTEXT: RefCell<Option<SshContext>> = const { RefCell::new(None) };
}
tokio::task_local! {
    static ASYNC_CONTEXT: SshContext;
}
#[cfg(unix)]
static NEXT: AtomicU64 = AtomicU64::new(0);

/// Owns authentication sessions, never passwords or user-owned master processes.
#[derive(Clone, Default)]
pub struct SshContext {
    inner: Arc<Mutex<Sessions>>,
    cancel: Option<CancellationToken>,
    #[cfg(any(windows, test))]
    graphical: Option<PathBuf>,
    #[cfg(windows)]
    graphical_cancel: Option<Arc<super::windows_auth::CancellationEvent>>,
    #[cfg(any(windows, test))]
    label: Option<String>,
}

#[derive(Default)]
struct Sessions {
    entries: HashMap<Backend, Session>,
    directory: Option<PathBuf>,
}

#[cfg_attr(not(unix), allow(dead_code))]
enum Session {
    Existing,
    Owned { child: Child, socket: PathBuf },
}

impl Drop for Sessions {
    fn drop(&mut self) {
        for session in self.entries.values_mut() {
            if let Session::Owned { child, .. } = session {
                stop(child);
            }
        }
        if let Some(directory) = &self.directory {
            let _ = fs::remove_dir_all(directory);
        }
    }
}

pub fn current() -> Option<SshContext> {
    ASYNC_CONTEXT
        .try_with(Clone::clone)
        .ok()
        .or_else(|| CONTEXT.with(|slot| slot.borrow().clone()))
}

pub fn with_context<T>(context: Option<&SshContext>, action: impl FnOnce() -> T) -> T {
    struct Restore(Option<SshContext>);
    impl Drop for Restore {
        fn drop(&mut self) {
            CONTEXT.with(|slot| *slot.borrow_mut() = self.0.take());
        }
    }
    let previous = CONTEXT.with(|slot| slot.replace(context.cloned()));
    let _restore = Restore(previous);
    action()
}

impl SshContext {
    pub fn for_submission(&self, cancel: CancellationToken) -> Self {
        Self {
            inner: Arc::clone(&self.inner),
            cancel: Some(cancel),
            #[cfg(any(windows, test))]
            graphical: None,
            #[cfg(windows)]
            graphical_cancel: None,
            #[cfg(any(windows, test))]
            label: self.label.clone(),
        }
    }

    /// Give native authentication windows a trusted task label, never credentials.
    pub fn with_label(&self, label: &str) -> Self {
        #[allow(unused_mut)]
        let mut context = self.clone();
        #[cfg(any(windows, test))]
        {
            context.label = Some(label.to_owned());
        }
        #[cfg(not(any(windows, test)))]
        let _ = label;
        context
    }

    #[cfg(any(windows, test))]
    pub fn with_graphical_auth(&self, executable: PathBuf) -> std::io::Result<Self> {
        let mut context = self.clone();
        #[cfg(windows)]
        {
            let cancel = context.cancel.clone().ok_or_else(|| {
                std::io::Error::new(
                    std::io::ErrorKind::InvalidInput,
                    "graphical authentication requires a submission cancellation token",
                )
            })?;
            context.graphical_cancel = Some(Arc::new(super::windows_auth::CancellationEvent::new(
                cancel,
            )?));
        }
        context.graphical = Some(executable);
        Ok(context)
    }

    pub fn cancellation(&self) -> Option<CancellationToken> {
        self.cancel.clone()
    }

    pub async fn scope<F: Future>(&self, future: F) -> F::Output {
        ASYNC_CONTEXT.scope(self.clone(), future).await
    }

    pub fn ensure_program(
        &self,
        program: &Program,
        cancel: &CancellationToken,
    ) -> Result<(), String> {
        let mut seen = HashSet::new();
        for operation in &program.operations {
            for backend in [&operation.source.backend, &operation.destination.backend] {
                if seen.insert(backend) {
                    self.ensure(backend, cancel)?;
                }
            }
        }
        Ok(())
    }

    /// Try to reserve safe background authentication, without making optional
    /// control-socket storage a requirement for ordinary foreground transfers.
    pub fn ensure_program_for_repl(
        &self,
        program: &Program,
        cancel: &CancellationToken,
    ) -> Result<Option<String>, String> {
        let mut seen = HashSet::new();
        let mut foreground = None;
        for operation in &program.operations {
            for backend in [&operation.source.backend, &operation.destination.backend] {
                if seen.insert(backend) {
                    let unavailable = self.ensure_optional(backend, cancel)?;
                    foreground = foreground.or(unavailable);
                }
            }
        }
        Ok(foreground)
    }

    /// Called with the terminal editor suspended, before any background client starts.
    pub fn ensure(&self, backend: &Backend, cancel: &CancellationToken) -> Result<(), String> {
        match self.ensure_optional(backend, cancel)? {
            Some(reason) => Err(reason),
            None => Ok(()),
        }
    }

    fn ensure_optional(
        &self,
        backend: &Backend,
        cancel: &CancellationToken,
    ) -> Result<Option<String>, String> {
        if !matches!(backend, Backend::Ssh { .. }) {
            return Ok(None);
        }
        if cancel.is_cancelled() {
            return Err("SSH authentication cancelled".into());
        }
        #[cfg(not(unix))]
        return Ok(None);
        #[cfg(unix)]
        self.ensure_unix(backend, cancel)
    }

    #[cfg(unix)]
    fn ensure_unix(
        &self,
        backend: &Backend,
        cancel: &CancellationToken,
    ) -> Result<Option<String>, String> {
        let mut sessions = self
            .inner
            .lock()
            .map_err(|_| "SSH session registry is poisoned")?;
        if let Some(Session::Owned { child, socket }) = sessions.entries.get_mut(backend)
            && child
                .try_wait()
                .map_err(|error| format!("cannot inspect SSH session: {error}"))?
                .is_none()
            && check_master(backend, Some(socket), cancel)?
        {
            return Ok(None);
        }
        if let Some(Session::Owned { mut child, .. }) = sessions.entries.remove(backend) {
            stop(&mut child);
        }
        if check_master(backend, None, cancel)? {
            sessions.entries.insert(backend.clone(), Session::Existing);
            return Ok(None);
        }
        let new_directory = sessions.directory.is_none();
        let directory = match &sessions.directory {
            Some(directory) => directory.clone(),
            None => match private_directory() {
                Ok(directory) => directory,
                Err(reason) => return Ok(Some(reason)),
            },
        };
        let socket = directory.join(format!("s{}", NEXT.fetch_add(1, Ordering::Relaxed)));
        if socket.as_os_str().len() > 95 {
            if new_directory {
                let _ = fs::remove_dir(&directory);
            }
            return Ok(Some(format!(
                "SSH control socket path is too long: {}; background authentication requires a shorter writable workspace",
                socket.display()
            )));
        }
        if new_directory {
            sessions.directory = Some(directory);
        }
        let mut command = client(backend);
        command
            .args([
                "-M",
                "-N",
                "-o",
                "ControlPersist=no",
                "-o",
                "ClearAllForwardings=yes",
            ])
            .arg("-S")
            .arg(&socket);
        destination(&mut command, backend);
        let mut child = command
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::inherit())
            .spawn()
            .map_err(|error| format!("cannot start SSH authentication: {error}"))?;
        loop {
            if cancel.is_cancelled() {
                stop(&mut child);
                return Err("SSH authentication cancelled; owned client closed".into());
            }
            match child.try_wait() {
                Ok(Some(status)) => {
                    return Err(format!(
                        "SSH authentication ended ({status}); inspect the native SSH diagnostic above"
                    ));
                }
                Err(error) => {
                    stop(&mut child);
                    return Err(format!("cannot wait for SSH authentication: {error}"));
                }
                Ok(None) => {}
            }
            match check_master(backend, Some(&socket), cancel) {
                Ok(true) => break,
                Ok(false) => std::thread::sleep(Duration::from_millis(50)),
                Err(error) => {
                    stop(&mut child);
                    return Err(error);
                }
            }
        }
        sessions
            .entries
            .insert(backend.clone(), Session::Owned { child, socket });
        Ok(None)
    }

    #[cfg(any(windows, test))]
    fn configure_graphical(&self, command: &mut Command, backend: &Backend) {
        let Some(executable) = &self.graphical else {
            return;
        };
        let external = std::env::var_os("SSH_ASKPASS").filter(|value| !value.is_empty());
        #[cfg(windows)]
        if let Some(cancel) = &self.graphical_cancel {
            command.env("MSYNC_SSH_CANCEL_EVENT", cancel.name());
        }
        graphical_command(
            command,
            executable,
            external.as_deref(),
            backend,
            self.label.as_deref(),
        );
    }

    pub(super) fn configure_scp(&self, command: &mut Command, backend: &Backend) {
        #[cfg(windows)]
        self.configure_graphical(command, backend);
        self.configure_client(command, backend, true);
    }

    pub(super) fn configure(&self, command: &mut Command, backend: &Backend) {
        #[cfg(windows)]
        self.configure_graphical(command, backend);
        self.configure_client(command, backend, false);
    }

    fn configure_client(&self, command: &mut Command, backend: &Backend, scp: bool) {
        let Ok(sessions) = self.inner.lock() else {
            return;
        };
        let Some(session) = sessions.entries.get(backend) else {
            return;
        };
        // Only authenticated leases suppress fallback prompts. Initial native
        // authentication remains interactive and owns the foreground terminal.
        command.args(["-o", "BatchMode=yes"]);
        if let Session::Owned { socket, .. } = session {
            if scp {
                command
                    .arg("-o")
                    .arg(format!("ControlPath={}", socket.display()));
            } else {
                command.arg("-S").arg(socket);
            }
        }
    }
}

fn stop(child: &mut Child) {
    if child.try_wait().is_ok_and(|status| status.is_some()) {
        return;
    }
    #[cfg(unix)]
    {
        // OpenSSH restores its controlling terminal on SIGTERM. SIGKILL alone
        // can leave echo disabled when programmatic cancellation interrupts auth.
        // SAFETY: the live owned Child supplies this process ID.
        unsafe {
            libc::kill(child.id() as libc::pid_t, libc::SIGTERM);
        }
        for _ in 0..25 {
            if child.try_wait().is_ok_and(|status| status.is_some()) {
                return;
            }
            std::thread::sleep(std::time::Duration::from_millis(10));
        }
    }
    let _ = child.kill();
    let _ = child.wait();
}

#[cfg(any(windows, test))]
fn graphical_command(
    command: &mut Command,
    executable: &std::path::Path,
    external: Option<&std::ffi::OsStr>,
    backend: &Backend,
    label: Option<&str>,
) {
    let Backend::Ssh { host, user, port } = backend else {
        return;
    };
    command.env("SSH_ASKPASS_REQUIRE", "force");
    command.env("MSYNC_SSH_ALIAS", host);
    command.env("MSYNC_SSH_TASK", label.unwrap_or("Current msync transfer"));
    match user {
        Some(user) => {
            command.env("MSYNC_SSH_USER", user);
        }
        None => {
            command.env_remove("MSYNC_SSH_USER");
        }
    }
    match port {
        Some(port) => {
            command.env("MSYNC_SSH_PORT", port.to_string());
        }
        None => {
            command.env_remove("MSYNC_SSH_PORT");
        }
    }
    if let Some(external) = external {
        command.env("SSH_ASKPASS", external);
        command.env_remove("MSYNC_ASKPASS_INTERNAL");
    } else {
        command.env("SSH_ASKPASS", executable);
        command.env("MSYNC_ASKPASS_INTERNAL", "msync-native-askpass-v1");
    }
}

#[cfg(unix)]
fn private_directory() -> Result<PathBuf, String> {
    use std::os::unix::fs::DirBuilderExt;
    let parent = std::env::current_dir()
        .map_err(|error| error.to_string())?
        .join(".temp");
    fs::create_dir_all(&parent)
        .map_err(|error| format!("cannot create SSH workspace {}: {error}", parent.display()))?;
    loop {
        let directory = parent.join(format!(
            "ssh-{}-{}",
            std::process::id(),
            NEXT.fetch_add(1, Ordering::Relaxed)
        ));
        match fs::DirBuilder::new().mode(0o700).create(&directory) {
            Ok(()) => return Ok(directory),
            Err(error) if error.kind() == std::io::ErrorKind::AlreadyExists => continue,
            Err(error) => {
                return Err(format!(
                    "cannot create private SSH workspace {}: {error}",
                    directory.display()
                ));
            }
        }
    }
}

#[cfg(unix)]
fn client(backend: &Backend) -> Command {
    let mut command = super::ssh_client("ssh");
    if let Backend::Ssh {
        port: Some(port), ..
    } = backend
    {
        command.arg("-p").arg(port.to_string());
    }
    command
}

#[cfg(unix)]
fn destination(command: &mut Command, backend: &Backend) {
    if let Backend::Ssh { host, user, .. } = backend {
        command
            .arg("--")
            .arg(super::ssh_host(host, user.as_deref()));
    }
}

#[cfg(unix)]
fn check_master(
    backend: &Backend,
    socket: Option<&Path>,
    cancel: &CancellationToken,
) -> Result<bool, String> {
    let mut command = client(backend);
    command.args(["-O", "check"]);
    if let Some(socket) = socket {
        command.arg("-S").arg(socket);
    }
    destination(&mut command, backend);
    let mut child = command
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|error| format!("cannot inspect SSH master: {error}"))?;
    loop {
        if cancel.is_cancelled() {
            stop(&mut child);
            return Err("SSH authentication cancelled".into());
        }
        match child.try_wait() {
            Ok(Some(status)) => return Ok(status.success()),
            Ok(None) => std::thread::sleep(Duration::from_millis(10)),
            Err(error) => {
                stop(&mut child);
                return Err(format!("cannot inspect SSH master: {error}"));
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn graphical_auth_is_child_scoped_and_preserves_configured_helpers() {
        use std::ffi::OsStr;
        let backend = Backend::Ssh {
            host: "Production_Box".into(),
            user: None,
            port: None,
        };
        let mut command = Command::new("ssh");
        graphical_command(
            &mut command,
            std::path::Path::new("msync.exe"),
            None,
            &backend,
            Some("sync.msync"),
        );
        let environment: std::collections::BTreeMap<_, _> = command.get_envs().collect();
        assert_eq!(
            environment.get(OsStr::new("SSH_ASKPASS_REQUIRE")),
            Some(&Some(OsStr::new("force")))
        );
        assert_eq!(
            environment.get(OsStr::new("SSH_ASKPASS")),
            Some(&Some(OsStr::new("msync.exe")))
        );
        assert_eq!(
            environment.get(OsStr::new("MSYNC_SSH_ALIAS")),
            Some(&Some(OsStr::new("Production_Box")))
        );
        assert_eq!(environment.get(OsStr::new("MSYNC_SSH_USER")), Some(&None));
        assert_eq!(environment.get(OsStr::new("MSYNC_SSH_PORT")), Some(&None));
        assert_eq!(
            environment.get(OsStr::new("MSYNC_SSH_TASK")),
            Some(&Some(OsStr::new("sync.msync")))
        );
        let mut external = Command::new("scp");
        graphical_command(
            &mut external,
            std::path::Path::new("msync.exe"),
            Some(OsStr::new("existing-helper.exe")),
            &backend,
            None,
        );
        let environment: std::collections::BTreeMap<_, _> = external.get_envs().collect();
        assert_eq!(
            environment.get(OsStr::new("SSH_ASKPASS")),
            Some(&Some(OsStr::new("existing-helper.exe")))
        );
        assert_eq!(
            environment.get(OsStr::new("MSYNC_ASKPASS_INTERNAL")),
            Some(&None)
        );
    }

    #[test]
    fn graphical_context_is_opt_in_and_retains_only_trusted_metadata() {
        let submission = SshContext::default()
            .for_submission(CancellationToken::new())
            .with_graphical_auth("msync.exe".into())
            .unwrap()
            .with_label("script.msync");
        assert_eq!(
            submission.graphical.as_deref(),
            Some(std::path::Path::new("msync.exe"))
        );
        assert_eq!(submission.label.as_deref(), Some("script.msync"));
        let untouched = Command::new("ssh");
        assert_eq!(untouched.get_envs().count(), 0);
        let mut configured = Command::new("ssh");
        submission.configure_graphical(
            &mut configured,
            &Backend::Ssh {
                host: "alias".into(),
                user: Some("alice".into()),
                port: Some(2222),
            },
        );
        let environment: std::collections::BTreeMap<_, _> = configured.get_envs().collect();
        assert_eq!(
            environment.get(std::ffi::OsStr::new("MSYNC_SSH_USER")),
            Some(&Some(std::ffi::OsStr::new("alice")))
        );
        assert_eq!(
            environment.get(std::ffi::OsStr::new("MSYNC_SSH_PORT")),
            Some(&Some(std::ffi::OsStr::new("2222")))
        );
    }

    #[test]
    fn submission_cancellation_is_scoped_without_owning_passwords() {
        let shared = SshContext::default();
        let first = shared.for_submission(CancellationToken::new());
        let second = shared.for_submission(CancellationToken::new());
        first.cancellation().unwrap().cancel();
        assert!(first.cancellation().unwrap().is_cancelled());
        assert!(!second.cancellation().unwrap().is_cancelled());
        assert!(Arc::ptr_eq(&first.inner, &second.inner));
        assert!(current().is_none());
        with_context(Some(&second), || {
            assert!(!current().unwrap().cancellation().unwrap().is_cancelled());
            let _ = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
                with_context(Some(&first), || panic!("restore scoped context"));
            }));
            assert!(!current().unwrap().cancellation().unwrap().is_cancelled());
        });
        assert!(current().is_none());
    }

    #[tokio::test]
    async fn asynchronous_scope_retains_submission_context_across_yield() {
        let context = SshContext::default().for_submission(CancellationToken::new());
        context
            .scope(async {
                tokio::task::yield_now().await;
                assert!(Arc::ptr_eq(&current().unwrap().inner, &context.inner));
            })
            .await;
        assert!(current().is_none());
    }
}
