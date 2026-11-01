//! Foreground authentication leases for later metadata and background data clients.

use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::fs;
use std::future::Future;
use std::path::{Path, PathBuf};
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
    native_clients: bool,
    #[cfg(unix)]
    foreground: Option<Arc<Mutex<HashSet<Backend>>>>,
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
    directory: Option<ControlDirectory>,
}

struct ControlDirectory {
    path: PathBuf,
    identity: same_file::Handle,
}

impl ControlDirectory {
    fn verify(&self) -> std::io::Result<()> {
        let metadata = fs::symlink_metadata(&self.path)?;
        if !metadata.is_dir() || same_file::Handle::from_path(&self.path)? != self.identity {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidInput,
                format!(
                    "SSH control directory identity changed: {}",
                    self.path.display()
                ),
            ));
        }
        Ok(())
    }

    fn remove(&self) -> std::io::Result<()> {
        self.verify()?;
        fs::remove_dir_all(&self.path)
    }
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
        if let Some(directory) = &self.directory
            && let Err(error) = directory.remove()
        {
            crate::telemetry::io_error("ssh.control.cleanup", &error);
        }
    }
}

pub fn current() -> Option<SshContext> {
    ASYNC_CONTEXT
        .try_with(Clone::clone)
        .ok()
        .or_else(|| CONTEXT.with(|slot| slot.borrow().clone()))
}

/// Builds one native SSH exec channel without acquiring an application master.
/// The caller owns streaming pipes and process cancellation. Native SSH config
/// and authentication remain authoritative, including user-configured sharing.
pub(crate) fn single_command(
    backend: &Backend,
    script: &str,
    args: &[&str],
) -> Result<Command, String> {
    if !matches!(backend, Backend::Ssh { .. }) {
        return Err("a single SSH channel requires an SSH endpoint".into());
    }
    let mut command = client(backend);
    command.arg("-T");
    if let Some(context) = current() {
        context.note_native_endpoint(backend);
        #[cfg(windows)]
        context.configure_graphical(&mut command, backend);
    }
    destination(&mut command, backend);
    command.arg(format!(
        "sh -c {} sh {}",
        super::quote(script),
        args.iter()
            .map(|arg| super::quote(arg))
            .collect::<Vec<_>>()
            .join(" ")
    ));
    Ok(command)
}

/// Describes the selected client policy, not a measured authentication count.
/// Reading the registry must never authenticate, probe a master or create a lease.
pub(crate) fn client_span(name: &'static str, backend: &Backend) -> crate::telemetry::LinkedSpan {
    let mut span = crate::telemetry::linked_span(name, crate::telemetry::context().as_ref());
    if !span.is_recording() {
        return span;
    }
    span.attribute("backend", super::backend_kind(backend));
    if let Backend::Ssh { host, user, port } = backend {
        span.attribute("endpoint.host", host);
        if let Some(user) = user {
            span.attribute("endpoint.user", user);
        }
        if let Some(port) = port {
            span.metric("endpoint.port", u64::from(*port));
        }
    }
    refresh_client_strategy(&mut span, backend);
    span
}

/// Refresh after command configuration, which may lazily admit the first lease.
pub(crate) fn refresh_client_strategy(span: &mut crate::telemetry::LinkedSpan, backend: &Backend) {
    if span.is_recording() && matches!(backend, Backend::Ssh { .. }) {
        span.attribute(
            "ssh.session.strategy",
            client_strategy(current().as_ref(), backend),
        );
    }
}

fn client_strategy(context: Option<&SshContext>, backend: &Backend) -> &'static str {
    #[cfg(windows)]
    {
        let _ = (context, backend);
        "native_per_process"
    }
    #[cfg(not(windows))]
    {
        let Some(context) = context else {
            return "native_config";
        };
        if context.native_clients {
            return "native_config";
        }
        let Ok(sessions) = context.inner.lock() else {
            return "registry_unavailable";
        };
        match sessions.entries.get(backend) {
            Some(Session::Existing) => "external_mux",
            Some(Session::Owned { .. }) => "owned_mux",
            None => "native_config",
        }
    }
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

/// Capture before spawning: Tokio task locals do not inherit into child tasks.
pub fn scope_current<F: Future>(future: F) -> impl Future<Output = F::Output> {
    let context = current();
    async move {
        match context {
            Some(context) => context.scope(future).await,
            None => future.await,
        }
    }
}

pub(crate) fn spawn<F>(future: F) -> tokio::task::JoinHandle<F::Output>
where
    F: Future + Send + 'static,
    F::Output: Send + 'static,
{
    tokio::spawn(scope_current(future))
}

pub(crate) fn blocking<F, T>(action: F) -> impl FnOnce() -> T + Send + 'static
where
    F: FnOnce() -> T + Send + 'static,
    T: Send + 'static,
{
    let context = current();
    move || with_context(context.as_ref(), action)
}

pub(crate) fn spawn_blocking<F, T>(action: F) -> tokio::task::JoinHandle<T>
where
    F: FnOnce() -> T + Send + 'static,
    T: Send + 'static,
{
    tokio::task::spawn_blocking(blocking(action))
}

impl SshContext {
    pub fn for_submission(&self, cancel: CancellationToken) -> Self {
        Self {
            inner: Arc::clone(&self.inner),
            cancel: Some(cancel),
            native_clients: self.native_clients,
            #[cfg(unix)]
            foreground: None,
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

    /// Native clients own authentication; selected Unix work retains the terminal.
    pub fn with_native_clients(mut self) -> Self {
        self.native_clients = true;
        #[cfg(unix)]
        {
            self.foreground = Some(Arc::default());
        }
        self
    }

    /// Explicit opt-in for callers that need an application-owned control master.
    pub fn with_foreground_reuse(mut self) -> Self {
        self.native_clients = false;
        #[cfg(unix)]
        {
            self.foreground = Some(Arc::default());
        }
        self
    }

    /// Marks actual selected work before it can race with foreground handoff.
    /// This does not contact the endpoint or create an authentication session.
    pub fn note_native_endpoint(&self, backend: &Backend) {
        #[cfg(unix)]
        if matches!(backend, Backend::Ssh { .. })
            && let Some(foreground) = &self.foreground
        {
            foreground
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .insert(backend.clone());
        }
        #[cfg(not(unix))]
        let _ = backend;
    }

    pub fn prepare_backends(&self, backends: &[Backend]) -> Result<(), String> {
        if self.native_clients {
            for backend in backends {
                self.note_native_endpoint(backend);
            }
            return Ok(());
        }
        #[cfg(unix)]
        for backend in backends {
            self.reuse_foreground(backend)?;
        }
        Ok(())
    }

    /// Only endpoints actually admitted without a safe lease require a local join.
    pub fn requires_foreground(&self) -> bool {
        #[cfg(unix)]
        {
            let Some(foreground) = &self.foreground else {
                return false;
            };
            let Ok(admitted) = foreground.lock() else {
                return true;
            };
            if self.native_clients {
                return !admitted.is_empty();
            }
            let Ok(sessions) = self.inner.lock() else {
                return true;
            };
            admitted
                .iter()
                .any(|backend| !sessions.entries.contains_key(backend))
        }
        #[cfg(not(unix))]
        false
    }

    pub(crate) fn may_require_foreground(&self) -> bool {
        #[cfg(unix)]
        return self.foreground.is_some();
        #[cfg(not(unix))]
        false
    }

    /// Transport-owned control directories are runtime resources, not payload.
    pub(crate) fn excludes_directory(&self, path: &Path) -> std::io::Result<bool> {
        let sessions = self
            .inner
            .lock()
            .map_err(|_| std::io::Error::other("SSH session registry is poisoned"))?;
        let Some(directory) = &sessions.directory else {
            return Ok(false);
        };
        directory.verify()?;
        Ok(same_file::Handle::from_path(path)? == directory.identity)
    }

    pub(crate) fn exclude_from(
        &self,
        store: &mut crate::filesystem::SnapshotStore,
    ) -> std::io::Result<()> {
        let sessions = self
            .inner
            .lock()
            .map_err(|_| std::io::Error::other("SSH session registry is poisoned"))?;
        if let Some(directory) = &sessions.directory {
            store
                .exclude_directory_identity(&directory.path, &directory.identity)
                .map_err(|cause| std::io::Error::new(cause.cause.kind(), cause))?;
        }
        Ok(())
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
        self.ensure_backends(
            program
                .operations
                .iter()
                .flat_map(|operation| [&operation.source.backend, &operation.destination.backend]),
            cancel,
        )
    }

    pub fn ensure_backends<'a>(
        &self,
        backends: impl IntoIterator<Item = &'a Backend>,
        cancel: &CancellationToken,
    ) -> Result<Option<String>, String> {
        let mut seen = HashSet::new();
        let mut foreground = None;
        for backend in backends {
            if seen.insert(backend) {
                let unavailable = self.ensure_optional(backend, cancel)?;
                foreground = foreground.or(unavailable);
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
        {
            let mut trace = crate::telemetry::span("ssh.session.admit");
            trace.expect_completion();
            if let Backend::Ssh { host, user, port } = backend {
                trace.attribute_lazy("endpoint.host", || host.clone());
                if let Some(user) = user {
                    trace.attribute_lazy("endpoint.user", || user.clone());
                }
                if let Some(port) = port {
                    trace.metric("endpoint.port", *port as u64);
                }
            }
            let result = self.ensure_unix(backend, cancel);
            trace.finish_result(&result);
            if let Ok(Some(reason)) = &result {
                trace.event(
                    "ssh.session.foreground_fallback",
                    &[("reason", reason)],
                    &[],
                );
            }
            result
        }
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
        if let Some(directory) = &sessions.directory {
            directory.verify().map_err(|cause| cause.to_string())?;
        }
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
        let created = if sessions.directory.is_none() {
            match private_directory() {
                Ok(directory) => Some(directory),
                Err(reason) => return Ok(Some(reason)),
            }
        } else {
            None
        };
        let directory = sessions
            .directory
            .as_ref()
            .or(created.as_ref())
            .expect("owned SSH directory")
            .path
            .clone();
        let socket = directory.join(format!("s{}", NEXT.fetch_add(1, Ordering::Relaxed)));
        if socket.as_os_str().len() > 95 {
            if let Some(created) = created {
                let _ = created.remove();
            }
            return Ok(Some(format!(
                "SSH control socket path is too long: {}; background authentication requires a shorter writable workspace",
                socket.display()
            )));
        }
        if let Some(created) = created {
            sessions.directory = Some(created);
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

    pub(super) fn configure(&self, command: &mut Command, backend: &Backend) -> Result<(), String> {
        #[cfg(windows)]
        self.configure_graphical(command, backend);
        if self.native_clients {
            self.note_native_endpoint(backend);
            return Ok(());
        }
        #[cfg(unix)]
        self.reuse_foreground(backend)?;
        let Ok(sessions) = self.inner.lock() else {
            return Err("SSH session registry is poisoned".into());
        };
        let Some(session) = sessions.entries.get(backend) else {
            return Ok(());
        };
        if let Some(directory) = &sessions.directory {
            directory.verify().map_err(|cause| cause.to_string())?;
        }
        // Only authenticated leases suppress fallback prompts. Initial native
        // authentication remains interactive and owns the foreground terminal.
        command.args(["-o", "BatchMode=yes"]);
        if let Session::Owned { socket, .. } = session {
            command.arg("-S").arg(socket);
        }
        Ok(())
    }

    #[cfg(unix)]
    fn reuse_foreground(&self, backend: &Backend) -> Result<(), String> {
        if !matches!(backend, Backend::Ssh { .. }) {
            return Ok(());
        }
        let Some(foreground) = &self.foreground else {
            return Ok(());
        };
        let mut admitted = foreground
            .lock()
            .map_err(|_| "SSH admission registry is poisoned")?;
        if admitted.contains(backend) {
            return Ok(());
        }
        let cancel = self.cancel.clone().unwrap_or_default();
        // Storage remains optional for an ordinary foreground client. Failure to
        // reserve a safe socket retains its native authentication path.
        self.ensure_optional(backend, &cancel)?;
        admitted.insert(backend.clone());
        Ok(())
    }
}

pub(super) fn stop(child: &mut Child) {
    if child.try_wait().is_ok_and(|status| status.is_some()) {
        return;
    }
    #[cfg(unix)]
    {
        let ready = super::ChildWait::new(child);
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
            ready.pause(child);
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
fn private_directory() -> Result<ControlDirectory, String> {
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
            Ok(()) => {
                let identity = same_file::Handle::from_path(&directory).map_err(|cause| {
                    format!(
                        "cannot identify SSH workspace {}: {cause}",
                        directory.display()
                    )
                })?;
                return Ok(ControlDirectory {
                    path: directory,
                    identity,
                });
            }
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
    let ready = super::ChildWait::new(&child);
    loop {
        if cancel.is_cancelled() {
            stop(&mut child);
            return Err("SSH authentication cancelled".into());
        }
        match child.try_wait() {
            Ok(Some(status)) => return Ok(status.success()),
            Ok(None) => ready.pause(&child),
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

    fn private_context(path: &std::path::Path) -> SshContext {
        let context = SshContext::default();
        context.inner.lock().unwrap().directory = Some(ControlDirectory {
            path: path.to_owned(),
            identity: same_file::Handle::from_path(path).unwrap(),
        });
        context
    }

    #[test]
    fn single_channel_keeps_native_configuration_and_skips_master_admission() {
        let backend = Backend::Ssh {
            host: "Production_Box".into(),
            user: Some("fixture".into()),
            port: Some(2222),
        };
        let context = SshContext::default().with_foreground_reuse();
        context
            .inner
            .lock()
            .unwrap()
            .entries
            .insert(backend.clone(), Session::Existing);
        let command = with_context(Some(&context), || {
            single_command(&backend, "printf '%s' \"$1\"", &["space ' and newline\n"]).unwrap()
        });
        assert_eq!(command.get_program(), "ssh");
        let args: Vec<_> = command.get_args().collect();
        let config = std::env::var_os("MSYNC_SSH_CONFIG");
        let offset = if let Some(config) = config {
            assert_eq!(args[0], "-F");
            assert_eq!(args[1], config);
            2
        } else {
            0
        };
        assert_eq!(
            args[offset..offset + 5],
            ["-p", "2222", "-T", "--", "fixture@Production_Box"]
        );
        assert_eq!(
            args[offset + 5],
            concat!(
                r#"sh -c 'printf '\''%s'\'' "$1"' sh 'space '\'' and newline"#,
                "\n'"
            )
        );
        assert_eq!(args.len(), offset + 6);
        assert!(context.inner.lock().unwrap().directory.is_none());
        assert_eq!(context.inner.lock().unwrap().entries.len(), 1);
    }

    #[test]
    fn single_channel_preserves_ipv6_and_rejects_non_ssh_backends() {
        let command = single_command(
            &Backend::Ssh {
                host: "2001:db8::1".into(),
                user: None,
                port: None,
            },
            "true",
            &[],
        )
        .unwrap();
        let args: Vec<_> = command.get_args().collect();
        assert_eq!(args[args.len() - 3], "--");
        assert_eq!(args[args.len() - 2], "[2001:db8::1]");
        assert!(single_command(&Backend::Local, "true", &[]).is_err());
    }

    #[test]
    fn native_clients_track_only_selected_endpoints_without_application_leases() {
        let context = SshContext::default()
            .for_submission(CancellationToken::new())
            .with_native_clients();
        let backend = Backend::Ssh {
            host: "not-contacted-for-admission".into(),
            user: None,
            port: None,
        };
        assert!(!context.requires_foreground());
        context.note_native_endpoint(&Backend::Local);
        assert!(!context.requires_foreground());
        context
            .prepare_backends(std::slice::from_ref(&backend))
            .unwrap();
        #[cfg(unix)]
        assert!(context.requires_foreground());
        #[cfg(not(unix))]
        assert!(!context.requires_foreground());
        assert!(context.inner.lock().unwrap().entries.is_empty());
        assert!(context.inner.lock().unwrap().directory.is_none());
        let mut command = Command::new("ssh");
        context.configure(&mut command, &backend).unwrap();
        assert_eq!(command.get_args().count(), 0);
        let next = context
            .for_submission(CancellationToken::new())
            .with_native_clients();
        assert!(!next.requires_foreground());
    }

    #[test]
    fn native_clients_do_not_inject_prior_opt_in_master_configuration() {
        let backend = Backend::Ssh {
            host: "native-client".into(),
            user: None,
            port: None,
        };
        let context = SshContext::default().with_native_clients();
        context
            .inner
            .lock()
            .unwrap()
            .entries
            .insert(backend.clone(), Session::Existing);
        let mut command = Command::new("ssh");
        context.configure(&mut command, &backend).unwrap();
        assert_eq!(command.get_args().count(), 0);
        #[cfg(unix)]
        assert!(context.requires_foreground());
        #[cfg(not(windows))]
        assert_eq!(client_strategy(Some(&context), &backend), "native_config");
    }

    #[test]
    fn client_strategy_observes_configuration_without_admitting_an_endpoint() {
        let context = SshContext::default();
        let backend = Backend::Ssh {
            host: "Production_Box".into(),
            user: Some("fixture".into()),
            port: Some(2222),
        };
        #[cfg(windows)]
        let direct = "native_per_process";
        #[cfg(not(windows))]
        let direct = "native_config";
        assert_eq!(client_strategy(None, &backend), direct);
        assert_eq!(client_strategy(Some(&context), &backend), direct);
        assert!(context.inner.lock().unwrap().entries.is_empty());
        context
            .inner
            .lock()
            .unwrap()
            .entries
            .insert(backend.clone(), Session::Existing);
        #[cfg(not(windows))]
        assert_eq!(client_strategy(Some(&context), &backend), "external_mux");
        #[cfg(windows)]
        assert_eq!(client_strategy(Some(&context), &backend), direct);
    }

    #[test]
    fn disabled_client_span_does_not_admit_unknown_remote_hosts() {
        let context = SshContext::default();
        with_context(Some(&context), || {
            drop(client_span(
                "ssh.test.disabled",
                &Backend::Ssh {
                    host: "unselected-host-with-no-network".into(),
                    user: None,
                    port: None,
                },
            ));
        });
        assert!(context.inner.lock().unwrap().entries.is_empty());
        assert!(context.inner.lock().unwrap().directory.is_none());
    }

    #[test]
    fn snapshot_exclusion_is_owned_identity_not_a_directory_name() {
        let parent = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&parent).unwrap();
        let root = tempfile::tempdir_in(parent).unwrap();
        let source = root.path().join("source");
        let owned = source.join("ssh-same");
        let user = source.join("nested/ssh-same");
        fs::create_dir_all(&owned).unwrap();
        fs::create_dir_all(&user).unwrap();
        fs::write(owned.join("control"), b"private").unwrap();
        fs::write(user.join("data"), b"user bytes").unwrap();
        let context = private_context(&owned);
        assert!(context.excludes_directory(&owned).unwrap());
        assert!(!context.excludes_directory(&user).unwrap());
        assert!(!SshContext::default().excludes_directory(&owned).unwrap());
        let mut store = crate::filesystem::SnapshotStore::new(&root.path().join("store")).unwrap();
        context.exclude_from(&mut store).unwrap();
        let snapshot = store.capture(&source).unwrap();
        assert!(
            !snapshot
                .entries()
                .keys()
                .any(|path| path.starts_with("ssh-same"))
        );
        assert!(
            snapshot
                .entries()
                .contains_key(std::path::Path::new("nested/ssh-same/data"))
        );
    }

    #[test]
    fn replaced_control_root_is_neither_excluded_nor_removed() {
        let parent = std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&parent).unwrap();
        let root = tempfile::tempdir_in(parent).unwrap();
        let owned = root.path().join("ssh-owned");
        fs::create_dir(&owned).unwrap();
        let context = private_context(&owned);
        fs::rename(&owned, root.path().join("retained-original")).unwrap();
        fs::create_dir(&owned).unwrap();
        fs::write(owned.join("user-data"), b"must survive").unwrap();
        assert_eq!(
            context.excludes_directory(&owned).unwrap_err().kind(),
            std::io::ErrorKind::InvalidInput
        );
        let mut store = crate::filesystem::SnapshotStore::new(&root.path().join("store")).unwrap();
        let error = context.exclude_from(&mut store).unwrap_err();
        assert_eq!(error.kind(), std::io::ErrorKind::InvalidInput);
        assert!(error.to_string().contains("identity changed"));
        drop(context);
        assert_eq!(fs::read(owned.join("user-data")).unwrap(), b"must survive");
    }

    #[tokio::test(flavor = "multi_thread", worker_threads = 2)]
    async fn owned_authentication_context_follows_tasks_and_blocking_workers() {
        let cancel = CancellationToken::new();
        let context = SshContext::default().for_submission(cancel.clone());
        let task = with_context(Some(&context), || {
            spawn(async {
                tokio::task::yield_now().await;
                let cancel = current().unwrap().cancellation().unwrap();
                spawn_blocking(move || {
                    assert!(current().is_some());
                    current().unwrap().cancellation().unwrap().cancel();
                })
                .await
                .unwrap();
                assert!(cancel.is_cancelled());
            })
        });
        assert!(current().is_none());
        task.await.unwrap();
        assert!(cancel.is_cancelled());
        assert!(spawn_blocking(|| current().is_none()).await.unwrap());
    }

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
        let mut external = Command::new("ssh");
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
