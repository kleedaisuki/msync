//! User-facing v0.3.1 acceptance journeys, independent of presentation internals.

use std::fs;
use std::io::{Read, Write};
use std::path::Path;
use std::process::{Child, ChildStdin, Command, Output, Stdio};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("ux-031-")
        .tempdir_in(root)
        .unwrap()
}

struct Session {
    child: Child,
    input: Option<ChildStdin>,
    stdout: Option<JoinHandle<Vec<u8>>>,
    stderr: Option<JoinHandle<Vec<u8>>>,
}

impl Session {
    fn start(command: &mut Command) -> Self {
        let mut child = command
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        let input = child.stdin.take();
        let stdout = Some(drain(child.stdout.take().unwrap()));
        let stderr = Some(drain(child.stderr.take().unwrap()));
        Self {
            child,
            input,
            stdout,
            stderr,
        }
    }

    fn send(&mut self, input: &str) {
        let stdin = self.input.as_mut().unwrap();
        stdin.write_all(input.as_bytes()).unwrap();
        stdin.flush().unwrap();
    }

    fn finish(mut self) -> Output {
        self.input.take();
        let deadline = Instant::now() + Duration::from_secs(15);
        let status = loop {
            if let Some(status) = self.child.try_wait().unwrap() {
                break status;
            }
            assert!(
                Instant::now() < deadline,
                "CLI/REPL did not finish within 15 seconds"
            );
            thread::sleep(Duration::from_millis(10));
        };
        Output {
            status,
            stdout: self.stdout.take().unwrap().join().unwrap(),
            stderr: self.stderr.take().unwrap().join().unwrap(),
        }
    }
}

impl Drop for Session {
    fn drop(&mut self) {
        self.input.take();
        let _ = self.child.kill();
        let _ = self.child.wait();
    }
}

fn drain(mut stream: impl Read + Send + 'static) -> JoinHandle<Vec<u8>> {
    thread::spawn(move || {
        let mut bytes = Vec::new();
        stream.read_to_end(&mut bytes).unwrap();
        bytes
    })
}

fn command(directory: &Path) -> Command {
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut command = Command::new(executable);
    command.current_dir(directory).env_remove("NO_COLOR");
    command
}

fn invoke(directory: &Path, arguments: &[&str], input: &str) -> Output {
    let mut process = Session::start(command(directory).args(arguments));
    process.send(input);
    process.finish()
}

fn text(bytes: &[u8]) -> String {
    String::from_utf8(bytes.to_vec()).unwrap()
}

fn success(output: &Output) {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        text(&output.stdout),
        text(&output.stderr)
    );
}

fn readable_error(output: &Output, path: &str) {
    let error = text(&output.stderr);
    let lower = error.to_lowercase();
    assert!(!output.status.success(), "unexpected success: {error}");
    assert!(lower.contains("error"), "missing severity: {error}");
    assert!(error.contains(path), "missing affected path: {error}");
    assert!(
        field_line(&lower, "help:"),
        "missing recovery guidance: {error}"
    );
    assert!(
        field_line(&lower, "cause:"),
        "missing underlying cause: {error}"
    );
    assert!(
        !error.contains('\u{1b}'),
        "plain errors must not contain terminal escapes: {error:?}"
    );
}

fn field_line(text: &str, label: &str) -> bool {
    text.lines()
        .any(|line| line.trim_start().starts_with(label))
}

#[test]
fn missing_script_errors_name_the_action_path_cause_and_recovery_for_every_file_command() {
    let directory = workspace();
    for mode in ["check", "plan", "compile", "run", "fmt"] {
        let output = invoke(
            directory.path(),
            &[mode, "missing script.msync", "--color", "never"],
            "",
        );
        readable_error(&output, "missing script.msync");
        assert!(text(&output.stderr).to_lowercase().contains("script"));
        assert!(text(&output.stdout).is_empty());
    }
    assert_eq!(fs::read_dir(directory.path()).unwrap().count(), 0);
}

#[test]
fn missing_dsl_source_explains_the_resolved_alias_path_not_only_the_alias_spelling() {
    let directory = workspace();
    let script = format!(
        "root := $pwd::pwd/missing-parent\n{}root/input.txt -> output.txt\n",
        "# unrelated intervening context\n".repeat(8)
    );
    fs::write(directory.path().join("alias-source.msync"), script).unwrap();
    for mode in ["plan", "run"] {
        let output = invoke(
            directory.path(),
            &[mode, "alias-source.msync", "--color", "never"],
            "",
        );
        assert!(!output.status.success());
        let error = text(&output.stderr);
        assert!(
            error.contains("root/input.txt"),
            "the exact failing token must remain visible: {error}"
        );
        assert!(
            error.contains("missing-parent") && error.contains("input.txt"),
            "the user needs the resolved source as well as the alias token: {error}"
        );
        assert!(
            error.contains(
                &directory
                    .path()
                    .file_name()
                    .unwrap()
                    .to_string_lossy()
                    .to_string()
            ),
            "include the resolved endpoint path rather than making the user reconstruct $pwd: {error}"
        );
        assert!(error.lines().any(|line| line.contains("help:")), "{error}");
        assert!(!directory.path().join("output.txt").exists());
    }
}

#[test]
fn script_directory_and_invalid_utf8_errors_offer_different_remedies() {
    let directory = workspace();
    fs::create_dir(directory.path().join("scripts")).unwrap();
    fs::write(directory.path().join("binary.msync"), [0xff, 0xfe, 0x80]).unwrap();
    let is_directory = invoke(
        directory.path(),
        &["check", "scripts", "--color", "never"],
        "",
    );
    readable_error(&is_directory, "scripts");
    assert!(
        text(&is_directory.stderr)
            .to_lowercase()
            .contains("directory")
    );
    let invalid = invoke(
        directory.path(),
        &["check", "binary.msync", "--color", "never"],
        "",
    );
    readable_error(&invalid, "binary.msync");
    assert!(text(&invalid.stderr).to_lowercase().contains("utf-8"));
    assert_ne!(text(&is_directory.stderr), text(&invalid.stderr));
    assert_eq!(
        fs::read(directory.path().join("binary.msync")).unwrap(),
        [0xff, 0xfe, 0x80]
    );
}

#[test]
fn read_only_formatting_is_refused_without_changing_bytes_and_explains_preview() {
    struct RestorePermissions(std::path::PathBuf, fs::Permissions);
    impl Drop for RestorePermissions {
        fn drop(&mut self) {
            let _ = fs::set_permissions(&self.0, self.1.clone());
        }
    }
    let directory = workspace();
    let file = directory.path().join("protected.msync");
    fs::write(&file, "a->b\n").unwrap();
    let permissions = fs::metadata(&file).unwrap().permissions();
    let restore = RestorePermissions(file.clone(), permissions.clone());
    let mut readonly = permissions;
    readonly.set_readonly(true);
    fs::set_permissions(&file, readonly).unwrap();
    let output = invoke(
        directory.path(),
        &["fmt", "protected.msync", "--color", "never"],
        "",
    );
    assert!(!output.status.success());
    let error = text(&output.stderr);
    assert!(
        error.contains("protected.msync") && error.contains("read-only"),
        "{error}"
    );
    assert!(
        field_line(&error, "help:") && error.contains("--stdout"),
        "{error}"
    );
    assert_eq!(fs::read_to_string(&file).unwrap(), "a->b\n");
    drop(restore);
}

#[cfg(unix)]
#[test]
fn pwd_escapes_control_characters_in_the_actual_working_directory() {
    let directory = workspace();
    let special = directory.path().join("cwd\u{1b}]2;injected-title\u{7}");
    fs::create_dir(&special).unwrap();
    let output = invoke(&special, &["repl", "--color", "never"], "/pwd\n/quit\n");
    success(&output);
    let out = text(&output.stdout);
    assert!(out.contains("injected-title"), "{out}");
    assert!(!out.contains('\u{1b}') && !out.contains('\u{7}'), "{out:?}");
}

#[test]
fn repl_file_and_cd_failures_are_readable_and_do_not_end_the_session() {
    let directory = workspace();
    let output = invoke(
        directory.path(),
        &["repl", "--color", "never"],
        "/load \"missing script.msync\"\n/check \"missing script.msync\"\n/plan \"missing script.msync\"\n/fmt \"missing script.msync\"\n/cd missing-directory\n/help\n/quit\n",
    );
    success(&output);
    let errors = text(&output.stderr);
    assert!(
        errors.matches("missing script.msync").count() >= 4,
        "{errors}"
    );
    assert!(errors.contains("missing-directory"), "{errors}");
    assert!(errors.to_lowercase().contains("cause:"), "{errors}");
    assert!(errors.to_lowercase().contains("help:"), "{errors}");
    assert!(text(&output.stdout).contains("REPL commands"));
    assert!(!output.stdout.contains(&0x1b));
    assert!(!output.stderr.contains(&0x1b));
}

#[test]
fn unknown_command_and_invalid_arguments_use_requested_color_and_actionable_help() {
    let directory = workspace();
    let mut process = Session::start(
        command(directory.path())
            .args(["--not-an-option", "--color", "always"])
            .env("NO_COLOR", "1"),
    );
    process.send("");
    let output = process.finish();
    assert!(!output.status.success());
    let errors = text(&output.stderr);
    assert!(errors.contains("--not-an-option"), "{errors}");
    assert!(errors.contains("--help"), "{errors}");
    assert!(
        errors.contains("\u{1b}["),
        "forced color must apply even to argument errors: {errors:?}"
    );
    let unknown = invoke(
        directory.path(),
        &["repl", "--color", "never"],
        "/hlep\n/?\n/quit\n",
    );
    success(&unknown);
    let error = text(&unknown.stderr);
    assert!(
        error.contains("/hlep") && error.contains("/help"),
        "{error}"
    );
    assert!(text(&unknown.stdout).contains("REPL commands"));
}

#[test]
fn pipe_empty_states_and_clear_are_visible_without_changing_bindings_or_emitting_terminal_controls()
{
    let directory = workspace();
    let output = invoke(
        directory.path(),
        &["repl"],
        "/bindings\nroot := $pwd::pwd\n/clear\n/bindings\n/jobs\n/pwd\n/quit\n",
    );
    success(&output);
    let output_text = text(&output.stdout);
    assert!(
        output_text.to_lowercase().contains("no user bindings"),
        "{output_text}"
    );
    assert!(output_text.contains("root :="), "{output_text}");
    assert!(output_text.contains("no active jobs"), "{output_text}");
    assert!(output_text.contains("clear") || text(&output.stderr).contains("clear"));
    assert!(!output.stdout.contains(&0x1b));
    assert!(!output.stderr.contains(&0x1b));
    assert!(!output_text.contains("msync>"));
}

#[test]
fn formatting_stdout_remains_exact_machine_payload_even_when_color_is_forced() {
    let directory = workspace();
    let source = "# kept\n$pwd::pwd/a->$pwd::pwd/b\n";
    let output = invoke(
        directory.path(),
        &["fmt", "-", "--stdout", "--color", "always"],
        source,
    );
    success(&output);
    assert_eq!(text(&output.stdout), msync::syntax::format(source).unwrap());
    assert!(!output.stdout.contains(&0x1b));
    assert!(output.stderr.is_empty());
}

#[test]
fn no_color_and_dumb_term_keep_application_text_plain_but_forced_color_still_works() {
    let directory = workspace();
    for variable in ["NO_COLOR", "TERM"] {
        let value = if variable == "TERM" { "dumb" } else { "1" };
        let mut process =
            Session::start(command(directory.path()).arg("--help").env(variable, value));
        process.send("");
        let output = process.finish();
        success(&output);
        assert!(!output.stdout.contains(&0x1b));
    }
    let output = invoke(directory.path(), &["--help", "--color", "always"], "");
    success(&output);
    assert!(output.stdout.contains(&0x1b));
}

#[test]
fn app_owned_errors_escape_terminal_control_characters_in_filenames() {
    let directory = workspace();
    let filename = "missing\u{1b}]2;injected-title\u{7}.msync";
    let output = invoke(
        directory.path(),
        &["check", filename, "--color", "never"],
        "",
    );
    assert!(!output.status.success());
    let error = text(&output.stderr);
    assert!(error.contains("injected-title"), "{error}");
    assert!(
        !error.contains('\u{1b}') && !error.contains('\u{7}'),
        "{error:?}"
    );
}

#[test]
fn shell_shortcuts_without_a_terminal_do_not_steal_following_repl_input() {
    let directory = workspace();
    let output = invoke(
        directory.path(),
        &["repl", "--color", "never"],
        "/bash\n/pwsh\n/help\n/quit\n",
    );
    success(&output);
    assert!(text(&output.stdout).contains("REPL commands"));
    let error = text(&output.stderr).to_lowercase();
    assert!(
        error.contains("terminal") || error.contains("tty"),
        "{error}"
    );
    assert!(!output.stdout.contains(&0x1b));
}

#[test]
fn an_unavailable_shell_reports_its_name_cause_and_remedy_then_repl_recovers() {
    let directory = workspace();
    let mut process = Session::start(
        command(directory.path())
            .args(["repl", "--color", "never"])
            .env("PATH", directory.path()),
    );
    process.send("/pwsh Write-Output 'must not run'\n/help\n/quit\n");
    let output = process.finish();
    success(&output);
    let error = text(&output.stderr).to_lowercase();
    assert!(error.contains("pwsh"), "{error}");
    assert!(
        field_line(&error, "cause:") && field_line(&error, "help:"),
        "{error}"
    );
    assert!(text(&output.stdout).contains("REPL commands"));
    assert!(!text(&output.stdout).contains("must not run"));
}

#[test]
fn clear_keeps_an_open_job_and_shell_launches_are_blocked_until_the_job_is_stopped() {
    use std::net::TcpListener;
    use std::sync::mpsc;
    let directory = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let address = listener.local_addr().unwrap();
    let (accepted, connected) = mpsc::channel();
    let (release, released) = mpsc::channel();
    let source = thread::spawn(move || {
        let deadline = Instant::now() + Duration::from_secs(10);
        let mut socket = loop {
            match listener.accept() {
                Ok((socket, _)) => break socket,
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    assert!(Instant::now() < deadline, "background flow never connected");
                    thread::sleep(Duration::from_millis(5));
                }
                Err(error) => panic!("{error}"),
            }
        };
        socket.set_nonblocking(false).unwrap();
        socket.write_all(b"opened flow").unwrap();
        accepted.send(()).unwrap();
        let _ = released.recv_timeout(Duration::from_secs(15));
    });
    let mut process = Session::start(command(directory.path()).args(["repl", "--color", "never"]));
    process.send(&format!("wire => tcp://{address}\nwire::/ -> output.bin\n"));
    connected.recv_timeout(Duration::from_secs(5)).unwrap();
    process.send("/clear\n/jobs\n/bash printf 'UNSAFE_BASH'\n/pwsh Write-Output 'UNSAFE_PWSH'\n/stop\n/help\n/quit\n");
    let output = process.finish();
    let _ = release.send(());
    source.join().unwrap();
    success(&output);
    let out = text(&output.stdout);
    assert!(out.contains("job 1: running"), "{out}");
    assert!(out.contains("job 1: stopped"), "{out}");
    assert!(
        !out.contains("UNSAFE_BASH") && !out.contains("UNSAFE_PWSH"),
        "{out}"
    );
    let errors = text(&output.stderr);
    assert!(
        errors.matches("/wait").count() >= 2 && errors.matches("/stop").count() >= 2,
        "{errors}"
    );
    assert!(out.contains("REPL commands"));
    assert!(!output.stdout.contains(&0x1b));
}

#[test]
#[cfg_attr(windows, ignore = "requires an installed WSL distribution with Bash")]
fn bash_one_shot_preserves_raw_quotes_operators_cwd_bindings_and_following_commands() {
    let directory = workspace();
    fs::write(directory.path().join("source.txt"), "payload").unwrap();
    let output = invoke(
        directory.path(),
        &["repl", "--color", "never"],
        "root := $pwd::pwd\n/bash printf '%s\\n' 'shell -> := => text'; printf '%s' 'made by bash' > bash-marker.txt\nroot/source.txt -> copied.txt\n/bindings\n/help\n/quit\n",
    );
    success(&output);
    let output_text = text(&output.stdout);
    assert!(
        output_text.contains("shell -> := => text"),
        "stdout={output_text}\nstderr={}",
        text(&output.stderr)
    );
    assert!(output_text.contains("root :="), "{output_text}");
    assert!(output_text.contains("REPL commands"));
    assert_eq!(
        fs::read_to_string(directory.path().join("copied.txt")).unwrap(),
        "payload"
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("bash-marker.txt")).unwrap(),
        "made by bash"
    );
}

#[cfg(windows)]
#[test]
fn pwsh_one_shot_preserves_literal_syntax_and_returns_after_nonzero_exit() {
    let directory = workspace();
    let output = invoke(
        directory.path(),
        &["repl", "--color", "never"],
        "/pwsh Write-Output 'literal -> := => text'; Set-Content -LiteralPath 'pwsh-marker.txt' -Value 'from pwsh' -NoNewline; exit 7\n/help\n/quit\n",
    );
    success(&output);
    let out = text(&output.stdout);
    assert!(
        out.contains("literal -> := => text"),
        "stdout={out}\nstderr={}",
        text(&output.stderr)
    );
    assert!(out.contains("REPL commands"));
    assert!(
        text(&output.stderr).contains('7'),
        "{}",
        text(&output.stderr)
    );
    assert_eq!(
        fs::read_to_string(directory.path().join("pwsh-marker.txt")).unwrap(),
        "from pwsh"
    );
}
