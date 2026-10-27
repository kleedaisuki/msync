#![cfg(windows)]

use std::collections::BTreeMap;
use std::fs;
use std::hash::{DefaultHasher, Hash, Hasher};
use std::io::Write;
use std::net::{Ipv4Addr, TcpListener};
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Output, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::thread;
use std::time::{Duration, Instant};

static COMMAND_ID: AtomicU64 = AtomicU64::new(0);

struct SshFixture {
    root: tempfile::TempDir,
    server: Option<Child>,
    port: u16,
    config: PathBuf,
    windows_config: BTreeMap<PathBuf, u64>,
    linux_config: Vec<u8>,
}

impl SshFixture {
    fn new() -> Self {
        Self::with_mode(false)
    }

    fn with_python() -> Self {
        Self::with_mode(true)
    }

    fn with_mode(python: bool) -> Self {
        let temporary = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&temporary).unwrap();
        let root = tempfile::Builder::new()
            .prefix("ssh-fixture-")
            .tempdir_in(temporary)
            .unwrap();
        let windows_config = windows_ssh_snapshot();
        let linux_config = linux_ssh_snapshot(root.path());
        let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
        let port = listener.local_addr().unwrap().port();
        let config = root.path().join("ssh_config");
        let mut fixture = Self {
            root,
            server: None,
            port,
            config,
            windows_config,
            linux_config,
        };
        fs::create_dir(fixture.root.path().join("linux")).unwrap();
        fs::create_dir(fixture.workspace()).unwrap();
        let key = fixture.root.path().join("client_key");
        let generated = fixture.command(
            Command::new("ssh-keygen")
                .args(["-q", "-t", "ed25519", "-N", "", "-f"])
                .arg(&key),
            None,
        );
        success(&generated);
        let script = fixture.root.path().join("fixture.sh");
        fs::write(
            &script,
            include_str!("support/ssh_fixture.sh").replace("\r\n", "\n"),
        )
        .unwrap();
        let mapped = fixture.command(
            Command::new("wsl")
                .args(["--exec", "wslpath", "-a", "-u"])
                .arg(fixture.root.path()),
            None,
        );
        success(&mapped);
        let linux_root = String::from_utf8(mapped.stdout).unwrap().trim().to_string();
        let log = fs::File::create(fixture.root.path().join("namespace.log")).unwrap();
        drop(listener);
        let child = Command::new("wsl")
            .args([
                "--user",
                "root",
                "--exec",
                "unshare",
                "--mount",
                "--pid",
                "--fork",
                "--kill-child=KILL",
                "--mount-proc",
                "/bin/bash",
            ])
            .arg(format!("{linux_root}/fixture.sh"))
            .arg(&linux_root)
            .arg(port.to_string())
            .arg(if python { "1" } else { "0" })
            .stdin(Stdio::piped())
            .stdout(log.try_clone().unwrap())
            .stderr(log)
            .spawn()
            .expect("WSL and root namespace support are required for this explicit test");
        fixture.server = Some(child);
        fixture.wait_for_ready();
        let host_key = fs::read_to_string(fixture.root.path().join("host_key.pub")).unwrap();
        let known_hosts = fixture.root.path().join("known_hosts");
        fs::write(&known_hosts, format!("[127.0.0.1]:{port} {host_key}")).unwrap();
        fs::write(&fixture.config, format!(
            "Host *\n HostName 127.0.0.1\n User root\n Port {port}\n IdentityFile {}\n UserKnownHostsFile {}\n GlobalKnownHostsFile none\n IdentityAgent none\n IdentitiesOnly yes\n BatchMode yes\n StrictHostKeyChecking yes\n UpdateHostKeys no\n ControlMaster no\n ControlPath none\n RequestTTY no\n ConnectTimeout 3\n ConnectionAttempts 1\n LogLevel ERROR\n",
            quoted(&key), quoted(&known_hosts),
        )).unwrap();
        let deadline = Instant::now() + Duration::from_secs(15);
        loop {
            let output = fixture.remote("printf connected");
            if output.status.success() && output.stdout == b"connected" {
                break;
            }
            assert!(
                Instant::now() < deadline,
                "SSH readiness failed: {}\n{}",
                details(&output),
                fixture.logs()
            );
            thread::sleep(Duration::from_millis(100));
        }
        fixture
    }

    fn workspace(&self) -> PathBuf {
        self.root.path().join("workspace")
    }

    fn wait_for_ready(&mut self) {
        let deadline = Instant::now() + Duration::from_secs(30);
        while !self.root.path().join("ready").exists() {
            assert!(
                self.server.as_mut().unwrap().try_wait().unwrap().is_none(),
                "isolated sshd exited: {}",
                self.logs()
            );
            assert!(
                Instant::now() < deadline,
                "isolated sshd startup timed out: {}",
                self.logs()
            );
            thread::sleep(Duration::from_millis(50));
        }
    }

    fn command(&self, command: &mut Command, input: Option<&str>) -> Output {
        bounded(command, input, self.root.path(), Duration::from_secs(35))
    }

    fn remote(&self, script: &str) -> Output {
        self.command(
            Command::new("ssh")
                .arg("-F")
                .arg(&self.config)
                .args(["--", "root@127.0.0.1", script]),
            None,
        )
    }

    fn run(&self, source: &str) -> Output {
        self.run_with_config(source, &self.config)
    }

    fn run_with_config(&self, source: &str, config: &Path) -> Output {
        let source = format!("remote => ssh://root@127.0.0.1:{}\n{source}", self.port);
        self.command(
            Command::new(env!("CARGO_BIN_EXE_msync"))
                .current_dir(self.workspace())
                .env("MSYNC_SSH_CONFIG", config)
                .args(["run", "-", "--trace"])
                .arg(self.root.path().join("trace.jsonl")),
            Some(&source),
        )
    }

    fn logs(&self) -> String {
        ["namespace.log", "sshd.log"]
            .iter()
            .map(|name| fs::read_to_string(self.root.path().join(name)).unwrap_or_default())
            .collect::<Vec<_>>()
            .join("\n")
    }

    fn assert_clean_staging(&self) {
        let remote = self.remote("find /tmp -mindepth 1 -maxdepth 1 -name 'msync.*' -print");
        success(&remote);
        assert!(
            remote.stdout.is_empty(),
            "remote staging leaked: {}",
            details(&remote)
        );
        let local = self.workspace().join(".temp");
        if local.exists() {
            assert_eq!(
                fs::read_dir(local).unwrap().count(),
                0,
                "local staging leaked"
            );
        }
    }

    fn shutdown(&mut self) {
        if let Some(mut server) = self.server.take() {
            drop(server.stdin.take());
            let deadline = Instant::now() + Duration::from_secs(8);
            while server.try_wait().unwrap().is_none() {
                if Instant::now() >= deadline {
                    let _ = server.kill();
                    let _ = server.wait();
                    panic!("WSL namespace failed to exit after EOF: {}", self.logs());
                }
                thread::sleep(Duration::from_millis(50));
            }
        }
    }

    fn finish(mut self) {
        self.assert_clean_staging();
        self.shutdown();
        let listener = self.command(
            Command::new("wsl").args([
                "--user",
                "root",
                "--exec",
                "ss",
                "-H",
                "-ltn",
                &format!("sport = :{}", self.port),
            ]),
            None,
        );
        success(&listener);
        assert!(
            listener.stdout.is_empty(),
            "WSL SSH listener survived namespace exit"
        );
        // WSL's Windows-side forwarding proxy can briefly accept TCP after its
        // Linux listener exits; the authenticated service must be gone.
        assert!(
            !self.remote("printf still-alive").status.success(),
            "SSH service survived namespace exit"
        );
        assert_eq!(
            self.windows_config,
            windows_ssh_snapshot(),
            "existing Windows SSH files changed"
        );
        assert_eq!(
            self.linux_config,
            linux_ssh_snapshot(self.root.path()),
            "existing WSL SSH files changed"
        );
    }
}

impl Drop for SshFixture {
    fn drop(&mut self) {
        if self.server.is_some() {
            // Avoid a double panic while still closing stdin and reaping the namespace.
            if std::thread::panicking() {
                if let Some(mut server) = self.server.take() {
                    drop(server.stdin.take());
                    let deadline = Instant::now() + Duration::from_secs(8);
                    while matches!(server.try_wait(), Ok(None)) && Instant::now() < deadline {
                        thread::sleep(Duration::from_millis(50));
                    }
                    let _ = server.kill();
                    let _ = server.wait();
                }
            } else {
                self.shutdown();
            }
        }
    }
}

fn bounded(command: &mut Command, input: Option<&str>, root: &Path, timeout: Duration) -> Output {
    let id = COMMAND_ID.fetch_add(1, Ordering::Relaxed);
    let out_path = root.join(format!("command-{id}.out"));
    let err_path = root.join(format!("command-{id}.err"));
    let out = fs::File::create(&out_path).unwrap();
    let err = fs::File::create(&err_path).unwrap();
    let mut child = command
        .stdin(Stdio::piped())
        .stdout(out)
        .stderr(err)
        .spawn()
        .unwrap();
    if let Some(input) = input {
        child
            .stdin
            .take()
            .unwrap()
            .write_all(input.as_bytes())
            .unwrap();
    }
    drop(child.stdin.take());
    let deadline = Instant::now() + timeout;
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if Instant::now() >= deadline {
            let _ = Command::new("taskkill")
                .args(["/F", "/T", "/PID", &child.id().to_string()])
                .stdout(Stdio::null())
                .stderr(Stdio::null())
                .status();
            let _ = child.wait();
            panic!(
                "command exceeded {timeout:?}: {command:?}\n{}",
                fs::read_to_string(err_path).unwrap_or_default()
            );
        }
        thread::sleep(Duration::from_millis(20));
    };
    Output {
        status,
        stdout: fs::read(out_path).unwrap(),
        stderr: fs::read(err_path).unwrap(),
    }
}

fn quoted(path: &Path) -> String {
    format!(
        "\"{}\"",
        path.to_string_lossy()
            .replace('\\', "/")
            .replace('"', "\\\"")
    )
}

fn details(output: &Output) -> String {
    format!(
        "{}\n{}\n{}",
        output.status,
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    )
}

fn success(output: &Output) {
    assert!(output.status.success(), "{}", details(output));
}

fn windows_ssh_snapshot() -> BTreeMap<PathBuf, u64> {
    fn visit(path: &Path, snapshot: &mut BTreeMap<PathBuf, u64>) {
        let Ok(metadata) = fs::symlink_metadata(path) else {
            return;
        };
        if metadata.file_type().is_symlink() {
            return;
        }
        if metadata.is_dir() {
            for entry in fs::read_dir(path).unwrap() {
                visit(&entry.unwrap().path(), snapshot);
            }
        } else if metadata.is_file() {
            let mut hash = DefaultHasher::new();
            fs::read(path).unwrap().hash(&mut hash);
            snapshot.insert(path.to_owned(), hash.finish());
        }
    }
    let mut snapshot = BTreeMap::new();
    if let Some(home) = std::env::var_os("USERPROFILE") {
        visit(&PathBuf::from(home).join(".ssh"), &mut snapshot);
    }
    if let Some(data) = std::env::var_os("PROGRAMDATA") {
        visit(&PathBuf::from(data).join("ssh"), &mut snapshot);
    }
    snapshot
}

fn linux_ssh_snapshot(root: &Path) -> Vec<u8> {
    let output = bounded(
        Command::new("wsl").args([
            "--user",
            "root",
            "--exec",
            "sh",
            "-c",
            "find /etc/ssh /etc/passwd /etc/shadow /etc/group /root/.ssh /home/*/.ssh -type f -exec sha256sum {} + 2>/dev/null | sort",
        ]),
        None,
        root,
        Duration::from_secs(20),
    );
    success(&output);
    output.stdout
}

#[test]
#[ignore = "requires Windows OpenSSH, WSL root, sshd and mount/PID namespaces"]
fn ssh_upload_download_and_overwrite_are_real_and_isolated() {
    let fixture = SshFixture::new();
    let input = fixture.workspace().join("payload.bin");
    let bytes: Vec<u8> = (0..65536).map(|i| (i % 251) as u8).collect();
    fs::write(&input, &bytes).unwrap();
    success(&fixture.run("payload.bin -> remote::~/uploaded.bin\n"));
    success(&fixture.run("remote::~/uploaded.bin -> downloaded.bin\n"));
    assert_eq!(
        fs::read(fixture.workspace().join("downloaded.bin")).unwrap(),
        bytes
    );
    fs::write(&input, b"replacement\0bytes").unwrap();
    success(
        &fixture.run(
            "payload.bin -> remote::~/uploaded.bin\nremote::~/uploaded.bin -> downloaded.bin\n",
        ),
    );
    assert_eq!(
        fs::read(fixture.workspace().join("downloaded.bin")).unwrap(),
        b"replacement\0bytes"
    );
    success(&fixture.run("remote::~/uploaded.bin -> remote::~/server-side.bin\nremote::~/server-side.bin -> returned.bin\n"));
    assert_eq!(
        fs::read(fixture.workspace().join("returned.bin")).unwrap(),
        b"replacement\0bytes"
    );
    success(&fixture.run(&format!(
        "other => ssh://root@localhost:{}\nremote::~/uploaded.bin -> other::~/cross.bin\nother::~/cross.bin -> crossed.bin\n",
        fixture.port,
    )));
    assert_eq!(
        fs::read(fixture.workspace().join("crossed.bin")).unwrap(),
        b"replacement\0bytes"
    );
    success(&fixture.run(". -> remote::~/working-directory\nremote::~/working-directory/payload.bin -> dot-source.bin\n"));
    assert_eq!(
        fs::read(fixture.workspace().join("dot-source.bin")).unwrap(),
        b"replacement\0bytes"
    );
    fixture.finish();
}

#[test]
#[ignore = "requires Windows OpenSSH, WSL root, sshd and mount/PID namespaces"]
fn ssh_recursive_directories_merge_and_conflicts_preflight() {
    let fixture = SshFixture::new();
    let tree = fixture.workspace().join("tree");
    fs::create_dir_all(tree.join("nested/empty")).unwrap();
    fs::write(tree.join("nested/a.bin"), b"nested\0data").unwrap();
    fs::write(tree.join(".hidden"), b"hidden").unwrap();
    success(&fixture.run("tree -> remote::~/exact\nremote::~/exact -> returned\n"));
    assert!(fixture.workspace().join("returned/nested/empty").is_dir());
    assert_eq!(
        fs::read(fixture.workspace().join("returned/nested/a.bin")).unwrap(),
        b"nested\0data"
    );
    assert_eq!(
        fs::read(fixture.workspace().join("returned/.hidden")).unwrap(),
        b"hidden"
    );
    success(&fixture.remote("printf unrelated > /root/exact/keep.txt"));
    fs::write(tree.join("nested/a.bin"), "changed nested bytes").unwrap();
    let existing_directory = fixture.run("tree -> remote::~/exact\n");
    assert!(!existing_directory.status.success());
    assert!(String::from_utf8_lossy(&existing_directory.stderr).contains("directory"));
    success(&fixture.run("tree -> remote::~/fresh\nremote::~/fresh -> remote::~/server-copy\nremote::~/server-copy -> merged\n"));
    success(&fixture.remote("test \"$(cat /root/exact/keep.txt)\" = unrelated"));
    success(&fixture.remote("mkdir -p /root/container-merge/tree; printf unrelated > /root/container-merge/tree/keep.txt"));
    success(&fixture.run(
        "tree -> remote::~/container-merge/\nremote::~/container-merge/tree -> container-merged\n",
    ));
    assert_eq!(
        fs::read_to_string(fixture.workspace().join("container-merged/keep.txt")).unwrap(),
        "unrelated"
    );
    assert_eq!(
        fs::read_to_string(fixture.workspace().join("merged/nested/a.bin")).unwrap(),
        "changed nested bytes"
    );
    success(&fixture.remote(
        "mkdir -p /root/container/tree/nested; printf keep > /root/container/tree/nested/a.bin",
    ));
    let conflict = fixture.run("tree -> remote::~/container/\n");
    assert!(!conflict.status.success(), "{}", details(&conflict));
    assert!(String::from_utf8_lossy(&conflict.stderr).contains("already exists"));
    success(&fixture.remote("test ! -e /root/container/tree/.hidden; test ! -d /root/container/tree/nested/empty; test \"$(cat /root/container/tree/nested/a.bin)\" = keep"));
    success(&fixture.remote("mkdir -p /root/server-container/exact/nested; printf keep > /root/server-container/exact/nested/a.bin"));
    assert!(
        !fixture
            .run("remote::~/exact -> remote::~/server-container/\n")
            .status
            .success()
    );
    success(&fixture.remote("test ! -e /root/server-container/exact/.hidden; test \"$(cat /root/server-container/exact/nested/a.bin)\" = keep"));
    fs::create_dir_all(fixture.workspace().join("local-container/exact/nested")).unwrap();
    fs::write(
        fixture
            .workspace()
            .join("local-container/exact/nested/a.bin"),
        "local keep",
    )
    .unwrap();
    assert!(
        !fixture
            .run("remote::~/exact -> local-container/\n")
            .status
            .success()
    );
    assert!(
        !fixture
            .workspace()
            .join("local-container/exact/.hidden")
            .exists()
    );
    assert_eq!(
        fs::read(
            fixture
                .workspace()
                .join("local-container/exact/nested/a.bin")
        )
        .unwrap(),
        b"local keep"
    );
    fixture.finish();
}

#[test]
#[ignore = "requires Windows OpenSSH, WSL root, sshd and mount/PID namespaces"]
fn ssh_literal_filenames_and_regex_selections_preserve_semantics() {
    let fixture = SshFixture::new();
    let name = "a[1] ' ;$(mkdir injected).txt";
    fs::write(
        fixture.workspace().join(name),
        "literal brackets and shell syntax",
    )
    .unwrap();
    fs::write(
        fixture.workspace().join("a1 ' ;$(mkdir injected).txt"),
        "wrong local glob match",
    )
    .unwrap();
    let operand = format!("\"{name}\"");
    success(&fixture.run(&format!("{operand} -> remote::~/\n")));
    success(&fixture.remote("printf wrong > '/root/a1 '\"'\"' ;$(mkdir injected).txt'"));
    success(&fixture.run(&format!("\"remote::~/{name}\" -> literal.txt\n")));
    assert_eq!(
        fs::read_to_string(fixture.workspace().join("literal.txt")).unwrap(),
        "literal brackets and shell syntax"
    );
    success(&fixture.remote("test ! -e /root/injected"));
    fs::create_dir_all(fixture.workspace().join("configs/nested")).unwrap();
    fs::write(fixture.workspace().join("configs/a.toml"), "alpha").unwrap();
    fs::write(fixture.workspace().join("configs/b.toml.bak"), "skip").unwrap();
    fs::write(
        fixture.workspace().join("configs/nested/deep.toml"),
        "skip deep",
    )
    .unwrap();
    success(
        &fixture.run(
            "configs/<.+\\.toml> -> remote::~/selected/\nremote::~/selected -> selected-local\n",
        ),
    );
    assert_eq!(
        fs::read_to_string(fixture.workspace().join("selected-local/a.toml")).unwrap(),
        "alpha"
    );
    assert!(
        !fixture
            .workspace()
            .join("selected-local/b.toml.bak")
            .exists()
    );
    success(&fixture.run("configs -> remote::~/all\nremote::~/all/<.+\\.toml> -> filtered/\n"));
    assert_eq!(
        fs::read_to_string(fixture.workspace().join("filtered/a.toml")).unwrap(),
        "alpha"
    );
    assert!(!fixture.workspace().join("filtered/nested").exists());
    fixture.finish();
}

#[test]
#[ignore = "requires Windows OpenSSH, WSL root, sshd and mount/PID namespaces"]
fn ssh_conflicts_failures_and_telemetry_leave_no_staging() {
    let fixture = SshFixture::new();
    fs::write(fixture.workspace().join("input.txt"), "original").unwrap();
    success(&fixture.run("input.txt -> remote::~/container/\n"));
    fs::write(fixture.workspace().join("input.txt"), "must not replace").unwrap();
    assert!(
        !fixture
            .run("input.txt -> remote::~/container/\n")
            .status
            .success()
    );
    success(&fixture.run("remote::~/container/input.txt -> returned.txt\n"));
    assert_eq!(
        fs::read_to_string(fixture.workspace().join("returned.txt")).unwrap(),
        "original"
    );
    assert!(
        !fixture
            .run("remote::~/missing -> unused.txt\n")
            .status
            .success()
    );
    assert!(!fixture.workspace().join("unused.txt").exists());
    let trace = fs::read_to_string(fixture.root.path().join("trace.jsonl")).unwrap();
    assert!(trace.contains("\"name\":\"copy\""));
    assert!(trace.contains("\"status\":\"error\""));
    assert!(trace.contains("input.txt"));
    assert!(!trace.contains("must not replace"));
    fs::write(
        fixture.workspace().join("victim.txt"),
        "unrelated local data",
    )
    .unwrap();
    success(
        &fixture.remote(
            r"mkdir -p /root/unsafe; printf malicious > '/root/unsafe/..\..\..\victim.txt'",
        ),
    );
    assert!(
        !fixture
            .run("remote::~/unsafe -> rejected-tree\n")
            .status
            .success()
    );
    assert_eq!(
        fs::read_to_string(fixture.workspace().join("victim.txt")).unwrap(),
        "unrelated local data"
    );
    assert!(!fixture.workspace().join("rejected-tree").exists());
    success(&fixture.remote("mkdir /root/case-collision; printf lower > /root/case-collision/a; printf upper > /root/case-collision/A"));
    let collision = fixture.run("remote::~/case-collision -> collision-tree\n");
    assert!(!collision.status.success());
    assert!(String::from_utf8_lossy(&collision.stderr).contains("case-colliding"));
    assert!(!fixture.workspace().join("collision-tree").exists());
    let wrong_config = fixture.root.path().join("unauthorized_config");
    let config = fs::read_to_string(&fixture.config).unwrap().replace(
        &quoted(&fixture.root.path().join("client_key")),
        &quoted(&fixture.root.path().join("missing_identity")),
    );
    fs::write(&wrong_config, config).unwrap();
    let denied = fixture.run_with_config("input.txt -> remote::~/unauthorized\n", &wrong_config);
    assert!(!denied.status.success());
    assert!(String::from_utf8_lossy(&denied.stderr).contains("Permission denied"));
    success(&fixture.remote("test ! -e /root/unauthorized"));
    fixture.finish();
}

const SOCKET_FIXTURE_PYTHON: &str = r#"
from __future__ import annotations
import os
import socket
import sys


def serve(path: str, mode: str, datafile: str) -> None:
    server: socket.socket = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        server.bind(path)
        server.listen(1)
        connection, _ = server.accept()
        with connection:
            if mode == 'send':
                with open(datafile, 'rb') as source:
                    while data := source.read(8192):
                        connection.sendall(data)
            else:
                with open(datafile, 'wb') as target:
                    while data := connection.recv(8192):
                        target.write(data)
    finally:
        server.close()
        if os.path.exists(path):
            os.unlink(path)


def count_python_helpers() -> int:
    count: int = 0
    for name in os.listdir('/proc'):
        if not name.isdigit() or int(name) == os.getpid():
            continue
        try:
            executable: str = os.readlink('/proc/' + name + '/exe')
        except OSError:
            continue
        if executable.rsplit('/', 1)[-1].startswith('python3'):
            count += 1
    return count


def count_stages(nonempty: bool) -> int:
    count: int = 0
    for parent, _, names in os.walk('/root'):
        for name in names:
            if not name.startswith('.msync-flow-'):
                continue
            try:
                if not nonempty or os.stat(os.path.join(parent, name)).st_size > 0:
                    count += 1
            except OSError:
                continue
    return count


def main() -> int:
    if sys.argv[1] == 'count':
        print(count_python_helpers())
        return 0
    if sys.argv[1] in ('pending', 'stages'):
        print(count_stages(sys.argv[1] == 'pending'))
        return 0
    serve(sys.argv[1], sys.argv[2], sys.argv[3])
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
"#;

fn wait_for_remote_socket(fixture: &SshFixture, path: &str) {
    let deadline = Instant::now() + Duration::from_secs(10);
    loop {
        let result = fixture.remote(&format!("test -S '{path}'"));
        if result.status.success() {
            return;
        }
        assert!(
            Instant::now() < deadline,
            "socket did not become ready: {}",
            details(&result)
        );
        thread::sleep(Duration::from_millis(25));
    }
}

async fn wait_for_remote_counter(config: &Path, mode: &str, positive: bool) -> bool {
    tokio::time::timeout(Duration::from_secs(8), async {
        loop {
            let output = tokio::process::Command::new("ssh")
                .arg("-F")
                .arg(config)
                .args(["--", "root@127.0.0.1"])
                .arg(format!("python3 /root/socket_fixture.py {mode}"))
                .output()
                .await
                .unwrap();
            if output.status.success()
                && String::from_utf8_lossy(&output.stdout)
                    .trim()
                    .parse::<usize>()
                    .is_ok_and(|count| (count > 0) == positive)
            {
                return true;
            }
            tokio::time::sleep(Duration::from_millis(50)).await;
        }
    })
    .await
    .unwrap_or(false)
}

async fn wait_for_live_remote_helper(config: &Path) -> bool {
    let mode = if std::env::var_os("MSYNC_TEST_REMOTE_STAGE_DATA").is_some() {
        "pending"
    } else {
        "count"
    };
    wait_for_remote_counter(config, mode, true).await
}

fn run_cancelled_remote_flow(source: &str) {
    use msync::{
        compiler::Compiler,
        scheduler::{self, ResourceLimits},
        syntax,
    };
    use tokio_util::sync::CancellationToken;
    let script = syntax::parse(source).unwrap();
    let program = Compiler::new().compile(&script).unwrap();
    let plan = scheduler::prepare(&program, true).unwrap();
    let config = PathBuf::from(std::env::var_os("MSYNC_SSH_CONFIG").unwrap());
    tokio::runtime::Builder::new_current_thread()
        .enable_all()
        .build()
        .unwrap()
        .block_on(async {
            let cancel = CancellationToken::new();
            if std::env::var_os("MSYNC_TEST_ABORT_REMOTE_FLOW").is_some() {
                let operation = plan.nodes[0].operation.clone();
                let worker = tokio::spawn({
                    let cancel = cancel.clone();
                    async move { msync::io::transfer(&operation, 64 * 1024, cancel).await }
                });
                assert!(
                    wait_for_live_remote_helper(&config).await,
                    "remote helper was never observed before future abort"
                );
                worker.abort();
                assert!(worker.await.unwrap_err().is_cancelled());
                assert!(
                    !cancel.is_cancelled(),
                    "future abort must not rely on token cancellation"
                );
                assert!(
                    wait_for_remote_counter(&config, "count", false).await,
                    "helper survived future abort while caller process stayed alive"
                );
                assert!(
                    wait_for_remote_counter(&config, "stages", false).await,
                    "file stage survived future abort"
                );
                return;
            }
            let timer = tokio::spawn({
                let cancel = cancel.clone();
                let config = config.clone();
                async move {
                    let observed = wait_for_live_remote_helper(&config).await;
                    if let Ok(delay) = std::env::var("MSYNC_TEST_DELAY_CANCEL_MS") {
                        tokio::time::sleep(Duration::from_millis(delay.parse().unwrap())).await;
                    }
                    cancel.cancel();
                    observed
                }
            });
            let result =
                scheduler::execute_plan_async(&plan, &ResourceLimits::default(), cancel).await;
            assert!(result.unwrap_err().message.contains("cancelled"));
            assert!(
                timer.await.unwrap(),
                "remote FIFO relay was never observed before cancellation"
            );
            assert!(
                wait_for_remote_counter(&config, "count", false).await,
                "helper survived cancellation while caller process stayed alive"
            );
            assert!(
                wait_for_remote_counter(&config, "stages", false).await,
                "file stage survived cancellation"
            );
        });
}
#[test]
#[ignore = "requires Windows OpenSSH, WSL root, Python 3, sshd and mount/PID namespaces"]
fn ssh_fifo_socket_and_cancellation_use_real_remote_helpers() {
    if let Ok(source) = std::env::var("MSYNC_TEST_CANCELLED_REMOTE_FLOW") {
        run_cancelled_remote_flow(&source);
        return;
    }
    let fixture = SshFixture::with_python();
    let payload: Vec<u8> = (0..256 * 1024).map(|value| (value % 251) as u8).collect();
    fs::write(fixture.workspace().join("payload.bin"), &payload).unwrap();
    fs::write(
        fixture.workspace().join("socket_fixture.py"),
        SOCKET_FIXTURE_PYTHON,
    )
    .unwrap();
    success(&fixture.run(
        "payload.bin -> remote::~/payload.bin\nsocket_fixture.py -> remote::~/socket_fixture.py\n",
    ));
    success(&fixture.run("remote::~/ -> home-snapshot/\nremote::. -> dot-snapshot/\n"));
    assert_eq!(
        fs::read(fixture.workspace().join("home-snapshot/root/payload.bin")).unwrap(),
        payload
    );
    assert_eq!(
        fs::read(fixture.workspace().join("dot-snapshot/root/payload.bin")).unwrap(),
        payload
    );
    success(&fixture.remote("mkfifo /root/to-local /root/from-local /root/idle-fifo"));

    thread::scope(|scope| {
        let producer = scope.spawn(|| fixture.remote("cat /root/payload.bin > /root/to-local"));
        success(&fixture.run("remote::~/to-local -> fifo-read.bin\n"));
        success(&producer.join().unwrap());
    });
    assert_eq!(
        fs::read(fixture.workspace().join("fifo-read.bin")).unwrap(),
        payload
    );
    thread::scope(|scope| {
        let consumer =
            scope.spawn(|| fixture.remote("cat /root/from-local > /root/fifo-output.bin"));
        success(&fixture.run("payload.bin -> remote::~/from-local\n"));
        success(&consumer.join().unwrap());
    });
    success(&fixture.run("remote::~/fifo-output.bin -> fifo-write.bin\n"));
    assert_eq!(
        fs::read(fixture.workspace().join("fifo-write.bin")).unwrap(),
        payload
    );

    thread::scope(|scope| {
        let producer = scope.spawn(|| {
            fixture.remote("python3 /root/socket_fixture.py /root/read.sock send /root/payload.bin")
        });
        wait_for_remote_socket(&fixture, "/root/read.sock");
        success(&fixture.run("remote::~/read.sock -> socket-read.bin\n"));
        success(&producer.join().unwrap());
    });
    assert_eq!(
        fs::read(fixture.workspace().join("socket-read.bin")).unwrap(),
        payload
    );
    thread::scope(|scope| {
        let consumer = scope.spawn(|| {
            fixture.remote(
                "python3 /root/socket_fixture.py /root/write.sock receive /root/socket-output.bin",
            )
        });
        wait_for_remote_socket(&fixture, "/root/write.sock");
        success(&fixture.run("payload.bin -> remote::~/write.sock\n"));
        success(&consumer.join().unwrap());
    });
    success(&fixture.run("remote::~/socket-output.bin -> socket-write.bin\n"));
    assert_eq!(
        fs::read(fixture.workspace().join("socket-write.bin")).unwrap(),
        payload
    );

    let source = format!(
        "remote => ssh://root@127.0.0.1:{}\nremote::~/idle-fifo -> {}\n",
        fixture.port,
        fixture
            .workspace()
            .join("cancelled-sink")
            .display()
            .to_string()
            .replace('\\', "/")
    );
    for abort in [false, true] {
        let mut command = Command::new(std::env::current_exe().unwrap());
        command
            .args([
                "--exact",
                "ssh_fifo_socket_and_cancellation_use_real_remote_helpers",
                "--ignored",
                "--nocapture",
            ])
            .env("MSYNC_SSH_CONFIG", &fixture.config)
            .env("MSYNC_TEST_CANCELLED_REMOTE_FLOW", &source);
        if abort {
            command.env("MSYNC_TEST_ABORT_REMOTE_FLOW", "1");
        }
        success(&fixture.command(&mut command, None));
        let count_helpers = "python3 /root/socket_fixture.py count";
        let deadline = Instant::now() + Duration::from_secs(8);
        loop {
            let result = fixture.remote(count_helpers);
            success(&result);
            if String::from_utf8_lossy(&result.stdout).trim() == "0" {
                break;
            }
            assert!(
                Instant::now() < deadline,
                "remote Python relay survived termination (abort={abort}): {}",
                details(&result)
            );
            thread::sleep(Duration::from_millis(100));
        }
    }
    fixture.finish();
}

fn wait_for_remote_stage_data(fixture: &SshFixture) {
    let deadline = Instant::now() + Duration::from_secs(8);
    loop {
        let output = fixture.remote("python3 /root/socket_fixture.py pending");
        success(&output);
        if String::from_utf8_lossy(&output.stdout)
            .trim()
            .parse::<usize>()
            .is_ok_and(|count| count > 0)
        {
            return;
        }
        assert!(
            Instant::now() < deadline,
            "remote file stage never accepted its body prefix"
        );
        thread::sleep(Duration::from_millis(25));
    }
}

fn assert_remote_file_flow_clean(fixture: &SshFixture, existing: bool) {
    let deadline = Instant::now() + Duration::from_secs(8);
    loop {
        let output = fixture.remote(
            "python3 /root/socket_fixture.py count; python3 /root/socket_fixture.py stages",
        );
        success(&output);
        if String::from_utf8_lossy(&output.stdout).trim() == "0\n0" {
            break;
        }
        assert!(
            Instant::now() < deadline,
            "remote helper or stage survived interruption: {}",
            details(&output)
        );
        thread::sleep(Duration::from_millis(50));
    }
    let command = if existing {
        "test \"$(cat /root/flow-target.bin)\" = 'previous complete payload'"
    } else {
        "test ! -e /root/flow-target.bin"
    };
    success(&fixture.remote(command));
}

#[test]
#[ignore = "requires real Windows OpenSSH/WSL Python chroot; verifies framed remote file rollback"]
fn ssh_remote_file_stages_rollback_after_prefix_reset_cancel_and_future_abort() {
    let fixture = SshFixture::with_python();
    fs::write(
        fixture.workspace().join("socket_fixture.py"),
        SOCKET_FIXTURE_PYTHON,
    )
    .unwrap();
    success(&fixture.run("socket_fixture.py -> remote::~/socket_fixture.py\n"));
    for existing in [false, true] {
        let setup = if existing {
            "printf 'previous complete payload' > /root/flow-target.bin"
        } else {
            "rm -f /root/flow-target.bin"
        };
        success(&fixture.remote(setup));
        thread::scope(|scope| {
            let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
            let port = listener.local_addr().unwrap().port();
            let fixture_ref = &fixture;
            let server = scope.spawn(move || {
                let (mut socket, _) = listener.accept().unwrap();
                socket.write_all(&vec![b'R'; 4096]).unwrap();
                wait_for_remote_stage_data(fixture_ref);
                socket2::SockRef::from(&socket)
                    .set_linger(Some(Duration::ZERO))
                    .unwrap();
            });
            let output = fixture.run(&format!(
                "wire => tcp://127.0.0.1:{port}\nwire::/ -> remote::~/flow-target.bin\n"
            ));
            assert!(
                !output.status.success(),
                "RST must not be reported as successful source EOF: {}",
                details(&output)
            );
            server.join().unwrap();
        });
        assert_remote_file_flow_clean(&fixture, existing);
        for abort in [false, true] {
            success(&fixture.remote(setup));
            thread::scope(|scope| {
                let listener = TcpListener::bind((Ipv4Addr::LOCALHOST, 0)).unwrap();
                let port = listener.local_addr().unwrap().port();
                let (release, released) = std::sync::mpsc::channel();
                let server = scope.spawn(move || {
                    let (mut socket, _) = listener.accept().unwrap();
                    socket.write_all(&vec![b'C'; 4096]).unwrap();
                    // The client's unused write half closes at open, not at cancellation.
                    released.recv_timeout(Duration::from_secs(35)).unwrap();
                });
                let source = format!(
                    "remote => ssh://root@127.0.0.1:{}\nwire => tcp://127.0.0.1:{port}\nwire::/ -> remote::~/flow-target.bin\n",
                    fixture.port
                );
                let mut child = Command::new(std::env::current_exe().unwrap());
                child
                    .args([
                        "--exact",
                        "ssh_fifo_socket_and_cancellation_use_real_remote_helpers",
                        "--ignored",
                        "--nocapture",
                    ])
                    .env("MSYNC_SSH_CONFIG", &fixture.config)
                    .env("MSYNC_TEST_CANCELLED_REMOTE_FLOW", source)
                    .env("MSYNC_TEST_REMOTE_STAGE_DATA", "1");
                if abort {
                    child.env("MSYNC_TEST_ABORT_REMOTE_FLOW", "1");
                }
                let output = fixture.command(&mut child, None);
                release.send(()).unwrap();
                server.join().unwrap();
                success(&output);
            });
            assert_remote_file_flow_clean(&fixture, existing);
        }
    }
    success(&fixture.remote("mkfifo /root/finish-no-reader"));
    fs::write(fixture.workspace().join("empty.bin"), []).unwrap();
    let source = format!(
        "remote => ssh://root@127.0.0.1:{}\n{:?} -> remote::~/finish-no-reader\n",
        fixture.port,
        fixture
            .workspace()
            .join("empty.bin")
            .to_string_lossy()
            .replace('\\', "/")
    );
    success(
        &fixture.command(
            Command::new(std::env::current_exe().unwrap())
                .args([
                    "--exact",
                    "ssh_fifo_socket_and_cancellation_use_real_remote_helpers",
                    "--ignored",
                    "--nocapture",
                ])
                .env("MSYNC_SSH_CONFIG", &fixture.config)
                .env("MSYNC_TEST_CANCELLED_REMOTE_FLOW", source)
                .env("MSYNC_TEST_DELAY_CANCEL_MS", "200"),
            None,
        ),
    );
    success(&fixture.remote("test -p /root/finish-no-reader"));
    fixture.finish();
}
