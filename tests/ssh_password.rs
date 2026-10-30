#![cfg(target_os = "linux")]

#[path = "support/pty.rs"]
#[allow(dead_code)]
mod pty;

use std::fs;
use std::io::Write;
use std::net::{TcpListener, TcpStream};
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::thread;
use std::time::{Duration, Instant};

const PAYLOAD: &[u8] = b"genuine username and password SSH fixture\n";

struct Server {
    child: Child,
    root: tempfile::TempDir,
    wrapper: PathBuf,
    password: String,
    host_accounts: Vec<(&'static str, Vec<u8>)>,
}

impl Server {
    fn start() -> Self {
        Self::start_in(&Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp"))
    }

    fn start_in(scratch: &Path) -> Self {
        let host_accounts = ["/etc/passwd", "/etc/group"]
            .into_iter()
            .map(|path| (path, fs::read(path).unwrap()))
            .collect();
        fs::create_dir_all(scratch).unwrap();
        let root = tempfile::Builder::new()
            .prefix("ssh-password-")
            .tempdir_in(scratch)
            .unwrap();
        let path = root.path();
        assert!(
            Command::new("ssh-keygen")
                .args(["-q", "-t", "ed25519", "-N", "", "-f"])
                .arg(path.join("host"))
                .status()
                .unwrap()
                .success()
        );
        let random = Command::new("openssl")
            .args(["rand", "-hex", "24"])
            .output()
            .unwrap();
        assert!(random.status.success());
        let password = String::from_utf8(random.stdout).unwrap().trim().to_owned();
        let mut hash = Command::new("openssl")
            .args(["passwd", "-6", "-stdin"])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .spawn()
            .unwrap();
        writeln!(hash.stdin.take().unwrap(), "{password}").unwrap();
        let output = hash.wait_with_output().unwrap();
        assert!(output.status.success());
        let hash = String::from_utf8(output.stdout).unwrap();
        fs::write(
            path.join("passwd"),
            format!("fixture:x:1000:1000::{}:/bin/sh\n", path.display()),
        )
        .unwrap();
        fs::write(path.join("group"), "fixture:x:1000:\n").unwrap();
        fs::write(
            path.join("shadow"),
            format!("fixture:{}:20000:0:99999:7:::\n", hash.trim()),
        )
        .unwrap();
        fs::set_permissions(path.join("shadow"), fs::Permissions::from_mode(0o600)).unwrap();
        fs::write(path.join("source"), PAYLOAD).unwrap();
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let port = listener.local_addr().unwrap().port();
        drop(listener);
        fs::write(path.join("sshd_config"), format!(
            "ListenAddress 127.0.0.1\nPort {port}\nHostKey {}/host\nPidFile {}/sshd.pid\nStrictModes no\nUsePAM no\nPasswordAuthentication yes\nPubkeyAuthentication no\nKbdInteractiveAuthentication no\nSubsystem sftp internal-sftp\n",
            path.display(), path.display())).unwrap();
        fs::write(path.join("config"), format!(
            "Host Password_Fixture\n HostName 127.0.0.1\n User fixture\n Port {port}\n PreferredAuthentications password\n PubkeyAuthentication no\n KbdInteractiveAuthentication no\n StrictHostKeyChecking no\n UserKnownHostsFile {}/known_hosts\n",
            path.display())).unwrap();
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
        let child = Command::new("unshare")
            .args([
                "--user",
                "--map-root-user",
                "--mount",
                "--pid",
                "--fork",
                "--kill-child",
                "--mount-proc",
                "sh",
            ])
            .arg(
                Path::new(env!("CARGO_MANIFEST_DIR")).join("tests/support/ssh_password_fixture.sh"),
            )
            .arg(path)
            .stdout(Stdio::null())
            .stderr(fs::File::create(path.join("sshd.log")).unwrap())
            .spawn()
            .unwrap();
        let mut server = Self {
            child,
            root,
            wrapper,
            password,
            host_accounts,
        };
        let deadline = Instant::now() + Duration::from_secs(5);
        while TcpStream::connect(("127.0.0.1", port)).is_err() {
            assert!(
                server.child.try_wait().unwrap().is_none(),
                "sshd failed: {}",
                server.log()
            );
            assert!(
                Instant::now() < deadline,
                "sshd startup timed out: {}",
                server.log()
            );
            thread::sleep(Duration::from_millis(10));
        }
        server
    }

    fn terminal(&self) -> pty::Terminal {
        let mut terminal = pty::Terminal::spawn(&self.wrapper, self.root.path(), "never");
        terminal.prompt();
        terminal.submit("remote => ssh://Password_Fixture");
        terminal
    }

    fn log(&self) -> String {
        fs::read_to_string(self.root.path().join("sshd.log")).unwrap()
    }

    fn transfer(&self, target: &str) -> String {
        format!(
            "remote::{} -> {target}\r",
            self.root.path().join("source").display()
        )
    }

    fn finish_authentication(&self, repl: &mut pty::Terminal) -> String {
        let mut transcript = String::new();
        for _ in 0..16 {
            repl.mark();
            repl.send(&format!("{}\r", self.password));
            let observed = repl.until_matching(|raw| {
                let visible = pty::visible(raw);
                visible.contains("password:")
                    || visible.contains("done:")
                    || visible.contains("error:")
            });
            assert!(!observed.contains(&self.password), "password echoed");
            transcript.push_str(&observed);
            if !pty::visible(&observed).contains("password:") {
                transcript.push_str(&repl.prompt());
                return transcript;
            }
        }
        panic!(
            "native authentication did not finish within 16 prompts; terminal={:?}",
            transcript.replace(&self.password, "<redacted>")
        );
    }

    fn verify(&self, target: &str, terminal: &str) {
        let bytes = fs::read(self.root.path().join(target)).unwrap_or_else(|error| {
            panic!(
                "missing transfer target {target}: {error}; terminal={:?}; sshd={}",
                terminal.replace(&self.password, "<redacted>"),
                self.log(),
            )
        });
        assert_eq!(bytes, PAYLOAD);
        assert!(
            self.log().contains("Accepted password for fixture"),
            "{}",
            self.log()
        );
        for (path, before) in &self.host_accounts {
            assert_eq!(
                &fs::read(path).unwrap(),
                before,
                "host account file changed: {path}"
            );
        }
    }
}

impl Drop for Server {
    fn drop(&mut self) {
        // Killing the outer unshare also kills its private PID namespace init,
        // which reaps any sshd session descendants, including on test failure.
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

#[test]
#[ignore = "requires native sshd/OpenSSH, openssl and unprivileged user/mount/PID namespaces"]
fn username_password_wrong_then_correct_is_noecho_and_copies_real_data() {
    let server = Server::start();
    let mut repl = server.terminal();
    repl.mark();
    repl.send(&server.transfer("copied"));
    repl.until("password:");
    repl.mark();
    repl.send("wrong-fixture-password\r");
    let retry = repl.until("password:");
    assert!(
        !retry.contains("wrong-fixture-password"),
        "password echoed: {retry:?}"
    );
    let authenticated = server.finish_authentication(&mut repl);
    assert!(
        !authenticated.contains(&server.password),
        "password echoed: {authenticated:?}"
    );
    server.verify("copied", &authenticated);
    assert!(
        server.log().contains("Failed password for fixture"),
        "{}",
        server.log()
    );
    repl.exit();
}

#[test]
#[ignore = "requires native sshd/OpenSSH, openssl and unprivileged user/mount/PID namespaces"]
fn username_password_ctrl_c_leaves_repl_usable_and_next_attempt_succeeds() {
    let server = Server::start();
    let mut repl = server.terminal();
    // Exercise cancellation with the normal execution signal handler installed.
    repl.submit("source -> local-copy");
    repl.mark();
    repl.send(&server.transfer("cancelled"));
    repl.until("password:");
    repl.send("\u{3}");
    repl.prompt();
    assert!(!server.root.path().join("cancelled").exists());
    repl.mark();
    repl.send(&server.transfer("retried"));
    repl.until("password:");
    let authenticated = server.finish_authentication(&mut repl);
    assert!(!authenticated.contains(&server.password));
    server.verify("retried", &authenticated);
    repl.exit();
}

#[test]
#[ignore = "requires native sshd/OpenSSH, openssl and unprivileged user/mount/PID namespaces"]
fn username_password_long_cwd_handles_each_native_foreground_request() {
    let scratch = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&scratch).unwrap();
    let parent = tempfile::Builder::new()
        .prefix("ssh-password-long-")
        .tempdir_in(scratch)
        .unwrap();
    let long = parent
        .path()
        .join("long-working-directory-component-for-no-multiplexing-fallback");
    let server = Server::start_in(&long);
    assert!(server.root.path().as_os_str().len() > 95);
    let mut repl = server.terminal();
    repl.mark();
    repl.send(&server.transfer("copied"));
    repl.until("password:");
    let authenticated = server.finish_authentication(&mut repl);
    assert!(
        pty::visible(&authenticated).contains("password:"),
        "long-cwd fallback should exercise another native authentication request"
    );
    server.verify("copied", &authenticated);
    repl.exit();
}
