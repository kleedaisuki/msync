#![cfg(target_os = "linux")]

#[path = "support/pty.rs"]
#[allow(dead_code)]
mod pty;

use std::fs;
use std::net::{TcpListener, TcpStream};
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

struct Server {
    child: Child,
    root: tempfile::TempDir,
    wrapper: PathBuf,
}

impl Server {
    fn start() -> Self {
        let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&scratch).unwrap();
        let root = tempfile::Builder::new()
            .prefix("ssh-auth-")
            .tempdir_in(scratch)
            .unwrap();
        let path = root.path();
        for (name, passphrase) in [("host", ""), ("client", "fixture-passphrase")] {
            assert!(
                Command::new("ssh-keygen")
                    .args(["-q", "-t", "ed25519", "-N", passphrase, "-f"])
                    .arg(path.join(name))
                    .status()
                    .unwrap()
                    .success()
            );
        }
        fs::copy(path.join("client.pub"), path.join("authorized_keys")).unwrap();
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        drop(listener);
        fs::write(path.join("sshd_config"), format!(
            "ListenAddress 127.0.0.1\nPort {port}\nHostKey {}/host\nPidFile {}/sshd.pid\nAuthorizedKeysFile {}/authorized_keys\nStrictModes no\nUsePAM no\nPasswordAuthentication no\nKbdInteractiveAuthentication no\nSubsystem sftp internal-sftp\n", path.display(), path.display(), path.display())).unwrap();
        let mut child = Command::new("/usr/sbin/sshd")
            .args(["-D", "-e", "-f"])
            .arg(path.join("sshd_config"))
            .stdout(Stdio::null())
            .stderr(fs::File::create(path.join("sshd.log")).unwrap())
            .spawn()
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(5);
        while TcpStream::connect(("127.0.0.1", port)).is_err() {
            assert!(
                child.try_wait().unwrap().is_none(),
                "sshd failed: {}",
                fs::read_to_string(path.join("sshd.log")).unwrap()
            );
            assert!(Instant::now() < deadline);
            thread::sleep(Duration::from_millis(10));
        }
        fs::write(path.join("config"), format!(
            "Host Auth_Fixture\n HostName 127.0.0.1\n Port {port}\n IdentityFile {}/client\n IdentitiesOnly yes\n IdentityAgent none\n StrictHostKeyChecking no\n UserKnownHostsFile {}/known_hosts\n ControlMaster auto\n ControlPath {}/mux\n ControlPersist 60\n", path.display(), path.display(), path.display())).unwrap();
        let wrapper = path.join("msync-wrapper");
        fs::write(
            &wrapper,
            format!(
                "#!/bin/sh\nexport MSYNC_SSH_CONFIG='{}'\nexec '{}' \"$@\"\n",
                path.join("config").display(),
                env!("CARGO_BIN_EXE_msync")
            ),
        )
        .unwrap();
        fs::set_permissions(&wrapper, fs::Permissions::from_mode(0o700)).unwrap();
        Self {
            child,
            root,
            wrapper,
        }
    }

    fn terminal(&self) -> pty::Terminal {
        let mut terminal = pty::Terminal::spawn(&self.wrapper, self.root.path(), "never");
        terminal.prompt();
        terminal.submit("remote => ssh://Auth_Fixture");
        terminal
    }
}

impl Drop for Server {
    fn drop(&mut self) {
        let _ = Command::new("ssh")
            .arg("-F")
            .arg(self.root.path().join("config"))
            .args(["-O", "exit", "Auth_Fixture"])
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status();
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[test]
#[ignore = "requires native sshd and ssh-keygen; isolated encrypted-key controlling-terminal journey"]
fn native_passphrase_prompt_cancellation_recovers_repl_then_reuses_configured_master() {
    let server = Server::start();
    fs::write(server.root.path().join("source"), "native ssh auth payload").unwrap();
    let mut repl = server.terminal();
    // Install the execution signal handler before entering a subsequent remote probe.
    repl.submit("source -> local-copy");
    repl.mark();
    repl.send("remote::/etc/hostname -> hostname\r");
    repl.until("Enter passphrase");
    repl.mark();
    repl.send("\u{3}");
    repl.prompt();
    repl.mark();
    repl.send("remote::/etc/hostname -> hostname\r");
    repl.until("Enter passphrase");
    repl.send("fixture-passphrase\r");
    let authenticated = repl.prompt();
    assert!(
        server.root.path().join("hostname").exists(),
        "{authenticated:?}"
    );
    assert!(
        !fs::read(server.root.path().join("hostname"))
            .unwrap()
            .is_empty()
    );
    let repeated = repl.submit("remote::/etc/hostname -> hostname-again");
    assert!(
        !repeated.contains("Enter passphrase"),
        "configured master not reused: {repeated:?}"
    );
    repl.exit();
}

#[test]
#[ignore = "requires native sshd and ssh-keygen; isolated encrypted-key controlling-terminal journey"]
fn native_passphrase_authentication_and_existing_configured_master_reuse() {
    let server = Server::start();
    let mut repl = server.terminal();
    repl.mark();
    repl.send("remote::/etc/hostname -> hostname\r");
    repl.until("Enter passphrase");
    repl.send("fixture-passphrase\r");
    let output = repl.prompt();
    assert!(server.root.path().join("hostname").exists(), "{output:?}");
    assert!(!output.contains("fixture-passphrase"), "passphrase echoed");
    let repeated = repl.submit("remote::/etc/hostname -> hostname-again");
    assert!(
        !repeated.contains("Enter passphrase"),
        "configured master not reused: {repeated:?}"
    );
    assert_eq!(
        fs::read(server.root.path().join("hostname")).unwrap(),
        fs::read(server.root.path().join("hostname-again")).unwrap()
    );
    repl.exit();
}

#[test]
#[ignore = "requires native sshd and ssh-keygen; verifies user-owned mux lifetime"]
fn a_preexisting_user_master_survives_repl_exit() {
    let server = Server::start();
    fs::write(&server.wrapper, format!(
        "#!/bin/sh\nexport MSYNC_SSH_CONFIG='{}'\nssh -F \"$MSYNC_SSH_CONFIG\" -fN Auth_Fixture || exit $?\nexec '{}' \"$@\"\n",
        server.root.path().join("config").display(), env!("CARGO_BIN_EXE_msync"))).unwrap();
    let mut repl = pty::Terminal::spawn(&server.wrapper, server.root.path(), "never");
    repl.until("Enter passphrase");
    repl.send("fixture-passphrase\r");
    repl.prompt();
    repl.submit("remote => ssh://Auth_Fixture");
    let output = repl.submit("remote::/etc/hostname -> hostname");
    assert!(!output.contains("Enter passphrase"));
    assert!(server.root.path().join("hostname").exists());
    repl.exit();
    assert!(
        Command::new("ssh")
            .arg("-F")
            .arg(server.root.path().join("config"))
            .args(["-O", "check", "Auth_Fixture"])
            .status()
            .unwrap()
            .success(),
        "msync stopped a user-owned master"
    );
}

#[test]
#[ignore = "requires native sshd/Python 3; foreground auth before background FIFO flow"]
fn background_fifo_authenticates_before_editor_resumes_and_owned_master_is_reaped() {
    let server = Server::start();
    let fifo = server.root.path().join("idle-fifo");
    let path = std::ffi::CString::new(fifo.as_os_str().as_encoded_bytes()).unwrap();
    // SAFETY: the CString is NUL-terminated and remains live across mkfifo.
    assert_eq!(unsafe { libc::mkfifo(path.as_ptr(), 0o600) }, 0);
    let mut repl = server.terminal();
    repl.mark();
    repl.send(&format!("remote::{} -> flow.bin\r", fifo.display()));
    repl.until("Enter passphrase");
    repl.send("fixture-passphrase\r");
    let started = repl.prompt();
    assert!(started.contains("running in background"), "{started:?}");
    let jobs = repl.submit("/jobs");
    assert!(jobs.contains("running"), "{jobs:?}");
    let stopped = repl.submit("/stop");
    assert!(!stopped.contains("Enter passphrase"));
    let reused = repl.submit("remote::/etc/hostname -> hostname");
    assert!(!reused.contains("Enter passphrase"));
    assert!(server.root.path().join("hostname").exists(), "{reused:?}");
    let directory = server.root.path().join(".temp");
    let owned: Vec<_> = fs::read_dir(&directory)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            path.file_name()
                .unwrap()
                .to_string_lossy()
                .starts_with("ssh-")
        })
        .collect();
    assert!(!owned.is_empty(), "no owned session directory observed");
    repl.exit();
    assert!(
        owned.iter().all(|path| !path.exists()),
        "owned session sockets leaked"
    );
}

#[test]
#[ignore = "requires native sshd and ssh-keygen; authenticated lease-loss and reauth journey"]
fn lost_authenticated_master_never_prompts_in_background_and_can_reauthenticate() {
    let server = Server::start();
    let path = server.root.path();
    fs::write(
        path.join("askpass"),
        format!(
            "#!/bin/sh\nprintf called >> '{}'\nprintf '%s\\n' fixture-passphrase\n",
            path.join("asked").display()
        ),
    )
    .unwrap();
    fs::set_permissions(path.join("askpass"), fs::Permissions::from_mode(0o700)).unwrap();
    let output = Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "lease_loss_child", "--ignored", "--nocapture"])
        .current_dir(path)
        .env("MSYNC_AUTH_CHILD", "1")
        .env("MSYNC_SSH_CONFIG", path.join("config"))
        .env("SSH_ASKPASS", path.join("askpass"))
        .env("SSH_ASKPASS_REQUIRE", "force")
        .env("DISPLAY", ":fixture")
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}\n{}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
}

#[test]
#[ignore = "subprocess entrypoint for isolated environment; called by lease-loss journey"]
fn lease_loss_child() {
    if std::env::var_os("MSYNC_AUTH_CHILD").is_none() {
        return;
    }
    use msync::{
        compiler::Compiler,
        runtime::{
            self,
            ssh::{SshContext, with_context},
        },
        syntax,
    };
    use tokio_util::sync::CancellationToken;
    let program = Compiler::new()
        .compile(
            &syntax::parse("remote => ssh://Auth_Fixture\nremote::/etc/hostname -> hostname\n")
                .unwrap(),
        )
        .unwrap();
    let backend = &program.operations[0].source.backend;
    let context = SshContext::default();
    let cancel = CancellationToken::new();
    context.ensure(backend, &cancel).unwrap();
    assert!(Path::new("asked").exists());
    fs::remove_file("asked").unwrap();
    let socket = fs::read_dir(".temp")
        .unwrap()
        .flat_map(|entry| fs::read_dir(entry.unwrap().path()).unwrap())
        .map(|entry| entry.unwrap().path())
        .find(|path| path.file_name().unwrap().to_string_lossy().starts_with('s'))
        .unwrap();
    assert!(
        Command::new("ssh")
            .arg("-F")
            .arg(std::env::var_os("MSYNC_SSH_CONFIG").unwrap())
            .arg("-S")
            .arg(&socket)
            .args(["-O", "exit", "Auth_Fixture"])
            .status()
            .unwrap()
            .success()
    );
    let started = Instant::now();
    let result = with_context(Some(&context), || {
        runtime::execute(&program, &runtime::ExecutionOptions::default())
    });
    assert!(result.is_err(), "lost master unexpectedly succeeded");
    assert!(started.elapsed() < Duration::from_secs(5));
    assert!(
        !Path::new("asked").exists(),
        "background client tried native authentication"
    );
    context.ensure(backend, &cancel).unwrap();
    assert!(
        Path::new("asked").exists(),
        "foreground reauthentication did not happen"
    );
    with_context(Some(&context), || {
        runtime::execute(&program, &runtime::ExecutionOptions::default())
    })
    .unwrap();
    assert!(!fs::read("hostname").unwrap().is_empty());
}

fn unencrypted_relative_identity(server: &Server, cwd: &Path) {
    assert!(
        Command::new("ssh-keygen")
            .args(["-q", "-p", "-P", "fixture-passphrase", "-N", "", "-f"])
            .arg(server.root.path().join("client"))
            .stdout(Stdio::null())
            .status()
            .unwrap()
            .success()
    );
    fs::copy(
        server.root.path().join("client"),
        cwd.join("relative-identity"),
    )
    .unwrap();
    let config = fs::read_to_string(server.root.path().join("config"))
        .unwrap()
        .replace(
            &format!("IdentityFile {}/client", server.root.path().display()),
            "IdentityFile relative-identity",
        )
        .replace("ControlMaster auto", "ControlMaster no");
    let config: String = config
        .lines()
        .map(|line| {
            if line.trim_start().starts_with("ControlPath ") {
                " ControlPath none"
            } else {
                line
            }
        })
        .collect::<Vec<_>>()
        .join("\n");
    fs::write(server.root.path().join("config"), config + "\n").unwrap();
}

fn finite_copy_from_cwd(server: &Server, cwd: &Path) {
    let mut repl = pty::Terminal::spawn(&server.wrapper, cwd, "never");
    repl.prompt();
    repl.submit("remote => ssh://Auth_Fixture");
    let output = server.root.path().join("fallback-output");
    let result = repl.submit(&format!("remote::/etc/hostname -> {}", output.display()));
    assert!(
        output.exists(),
        "finite native SSH copy regressed: {result:?}"
    );
    assert!(!fs::read(&output).unwrap().is_empty());
    let local = server.root.path().join("absolute-source");
    let uploaded = server.root.path().join("absolute-upload");
    fs::write(&local, "absolute source from a nonwritable or long cwd").unwrap();
    let result = repl.submit(&format!(
        "{} -> remote::{}",
        local.display(),
        uploaded.display()
    ));
    assert!(uploaded.exists(), "finite upload regressed: {result:?}");
    assert_eq!(fs::read(&local).unwrap(), fs::read(&uploaded).unwrap());
    repl.exit();
}

#[test]
#[ignore = "requires native sshd/keygen; finite SSH must not require short cwd or control sockets"]
fn finite_ssh_copy_from_long_cwd_preserves_relative_identity_resolution() {
    let server = Server::start();
    let cwd = server
        .root
        .path()
        .join("long-working-directory-component".repeat(3));
    fs::create_dir(&cwd).unwrap();
    assert!(cwd.as_os_str().len() > 95);
    unencrypted_relative_identity(&server, &cwd);
    finite_copy_from_cwd(&server, &cwd);
}

#[test]
#[ignore = "requires native sshd/keygen and unprivileged POSIX permissions; cwd may be read-only"]
fn finite_ssh_upload_from_readonly_cwd_uses_absolute_source_and_destination() {
    // SAFETY: geteuid has no preconditions and only reads the calling identity.
    assert_ne!(
        unsafe { libc::geteuid() },
        0,
        "run this fixture as an unprivileged user"
    );
    let server = Server::start();
    let wrapper = fs::read_to_string(&server.wrapper).unwrap();
    let wrapper = wrapper.replace(
        "exec ",
        &format!(
            "export MSYNC_STATE_DIR='{}'\nexec ",
            server.root.path().join("runtime-state").display()
        ),
    );
    fs::write(&server.wrapper, wrapper).unwrap();
    let cwd = server.root.path().join("readonly-working-directory");
    fs::create_dir(&cwd).unwrap();
    unencrypted_relative_identity(&server, &cwd);
    struct RestoreWrite(PathBuf);
    impl Drop for RestoreWrite {
        fn drop(&mut self) {
            let _ = fs::set_permissions(&self.0, fs::Permissions::from_mode(0o700));
        }
    }
    let _restore = RestoreWrite(cwd.clone());
    fs::set_permissions(&cwd, fs::Permissions::from_mode(0o500)).unwrap();
    assert!(fs::create_dir(cwd.join("write-probe")).is_err());
    // Uploads never required local staging in cwd. Downloads have a separate,
    // preexisting Scratch requirement and are not this compatibility contract.
    let mut repl = pty::Terminal::spawn(&server.wrapper, &cwd, "never");
    repl.prompt();
    repl.submit("remote => ssh://Auth_Fixture");
    let local = server.root.path().join("absolute-source");
    let uploaded = server.root.path().join("absolute-upload");
    fs::write(&local, "read-only cwd must not prohibit native uploads").unwrap();
    let result = repl.submit(&format!(
        "{} -> remote::{}",
        local.display(),
        uploaded.display()
    ));
    assert!(uploaded.exists(), "native upload regressed: {result:?}");
    assert_eq!(fs::read(&local).unwrap(), fs::read(&uploaded).unwrap());
    repl.exit();
}

#[test]
#[ignore = "requires native sshd/Python 3; unavailable mux retains foreground terminal ownership"]
fn long_cwd_fifo_fallback_runs_foreground_and_cancels_after_real_open() {
    let server = Server::start();
    let cwd = server
        .root
        .path()
        .join("long-working-directory-component".repeat(3));
    fs::create_dir(&cwd).unwrap();
    unencrypted_relative_identity(&server, &cwd);
    let fifo = server.root.path().join("fallback-fifo");
    let name = std::ffi::CString::new(fifo.as_os_str().as_encoded_bytes()).unwrap();
    // SAFETY: the CString remains NUL-terminated and live for mkfifo.
    assert_eq!(unsafe { libc::mkfifo(name.as_ptr(), 0o600) }, 0);
    let output = server.root.path().join("fallback-flow-output");
    fs::write(&output, "previous committed bytes").unwrap();
    let mut repl = pty::Terminal::spawn(&server.wrapper, &cwd, "never");
    repl.prompt();
    repl.submit("remote => ssh://Auth_Fixture");
    repl.mark();
    repl.send(&format!(
        "remote::{} -> {}\r",
        fifo.display(),
        output.display()
    ));
    repl.until("running in foreground");
    let stage = || {
        fs::read_dir(server.root.path())
            .unwrap()
            .filter_map(Result::ok)
            .any(|entry| {
                let name = entry.file_name();
                let name = name.to_string_lossy();
                name.starts_with(".msync-") && name.ends_with(".part")
            })
    };
    let deadline = Instant::now() + Duration::from_secs(10);
    while !stage() {
        assert!(
            Instant::now() < deadline,
            "real source open/target stage was never observed"
        );
        thread::sleep(Duration::from_millis(10));
    }
    repl.send("\u{3}");
    repl.prompt();
    assert_eq!(
        fs::read_to_string(&output).unwrap(),
        "previous committed bytes"
    );
    assert!(!stage(), "cancelled stage was retained");
    let copied = server.root.path().join("after-cancel");
    let retry = repl.submit(&format!("remote::/etc/hostname -> {}", copied.display()));
    assert!(copied.exists(), "foreground retry failed: {retry:?}");
    repl.exit();
}

#[test]
#[ignore = "requires native sshd and ssh-keygen; exact Unicode/percent Host name journey"]
fn native_unicode_config_alias_is_not_url_encoded_and_reuses_authentication() {
    let server = Server::start();
    let config = server.root.path().join("config");
    let text = fs::read_to_string(&config).unwrap();
    fs::write(
        &config,
        text.replace("Host Auth_Fixture\n", "Host Auth_Fixture 开发%机\n"),
    )
    .unwrap();
    let resolved = Command::new("ssh")
        .arg("-G")
        .arg("-F")
        .arg(&config)
        .arg("开发%机")
        .output()
        .unwrap();
    assert!(
        resolved.status.success(),
        "{}",
        String::from_utf8_lossy(&resolved.stderr)
    );
    assert!(
        String::from_utf8_lossy(&resolved.stdout)
            .lines()
            .any(|line| line == "hostname 127.0.0.1")
    );
    let mut repl = server.terminal();
    repl.submit("remote => ssh://开发%机");
    repl.mark();
    repl.send("remote::/etc/hostname -> unicode-hostname\r");
    repl.until("Enter passphrase");
    repl.send("fixture-passphrase\r");
    let output = repl.prompt();
    assert_eq!(
        fs::read(server.root.path().join("unicode-hostname")).unwrap(),
        fs::read("/etc/hostname").unwrap(),
        "{output:?}"
    );
    let repeated = repl.submit("remote::/etc/hostname -> unicode-again");
    assert!(!repeated.contains("Enter passphrase"), "{repeated:?}");
    assert_eq!(
        fs::read(server.root.path().join("unicode-again")).unwrap(),
        fs::read("/etc/hostname").unwrap()
    );
    repl.exit();
}
