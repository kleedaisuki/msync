//! Interactive acceptance: real Windows ConPTY / Unix controlling terminals.

#[path = "support/pty.rs"]
mod pty;

use std::fs;
use std::io::{Read, Write};
use std::path::Path;
use std::sync::mpsc;
use std::thread;
use std::time::{Duration, Instant};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("repl-terminal-")
        .tempdir_in(root)
        .unwrap()
}

#[test]
fn native_terminal_plan_and_limits_separate_paths_dependencies_and_resource_units() {
    let directory = workspace();
    fs::write(
        directory.path().join("source.txt"),
        "not transferred by plan",
    )
    .unwrap();
    fs::write(
        directory.path().join("flow.msync"),
        "$pwd::pwd/source.txt -> $pwd::pwd/result.txt\n",
    )
    .unwrap();
    let mut repl = terminal(directory.path(), "never");
    let plan = repl.submit("/plan flow.msync");
    for expected in [
        "Metadata preview only",
        "from:",
        "to:",
        "source.txt",
        "result.txt",
    ] {
        assert!(pty::visible(&plan).contains(expected), "{plan:?}");
    }
    assert!(!directory.path().join("result.txt").exists());
    let limits = repl.submit("/limits");
    assert!(pty::visible(&limits).contains("64 KiB"));
    assert!(pty::visible(&limits).contains("64 MiB"));
    repl.exit();
}

fn terminal(directory: &Path, color: &str) -> pty::Terminal {
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut terminal = pty::Terminal::spawn(Path::new(&executable), directory, color);
    terminal.prompt();
    terminal.mark();
    terminal
}

#[test]
fn native_terminal_highlights_before_enter_and_keeps_unicode_editing_history_and_clear_state() {
    let directory = workspace();
    fs::create_dir(directory.path().join("x")).unwrap();
    fs::write(directory.path().join("x/source.txt"), "actual edited path").unwrap();
    let mut repl = terminal(directory.path(), "always");

    repl.send("probe := $pwd::pwd");
    let edit = repl.until_matching(|raw| {
        pty::color_of(raw, "$pwd").is_some() && pty::color_of(raw, ":=").is_some()
    });
    assert_ne!(
        pty::color_of(&edit, "$pwd"),
        pty::color_of(&edit, ":="),
        "namespace and operator need distinct lexical styles before Enter: {edit:?}"
    );
    assert!(!directory.path().join("probe").exists());
    repl.mark();
    repl.send("\u{3}");
    repl.prompt();
    repl.mark();
    assert!(pty::visible(&repl.submit("/bindings")).contains("no user bindings"));

    repl.mark();
    repl.send("root := $pwd::pwd/界x");
    repl.until("界x");
    // Move left over x, then erase the entire preceding wide Unicode character.
    repl.send("\u{1b}[D\u{7f}");
    repl.until("pwd/x");
    repl.accept();
    assert!(pty::visible(&repl.submit("root/source.txt -> copied.txt")).contains("done:"));
    assert_eq!(
        fs::read_to_string(directory.path().join("copied.txt")).unwrap(),
        "actual edited path"
    );

    repl.mark();
    repl.send("/clear");
    repl.until("/clear");
    repl.accept();
    let cleared = repl.until_matching(|raw| {
        raw.contains("\u{1b}[2J")
            || raw.contains("\u{1b}[J")
            || raw.contains("\u{1b}[0J")
            || (raw.contains("\u{1b}[H") && raw.matches("\u{1b}[K").count() >= 29)
    });
    assert!(
        !cleared.contains("\u{1b}[3J"),
        "clear must not erase terminal scrollback"
    );
    repl.mark();
    repl.send("\u{1b}[A");
    repl.until("/clear");
    repl.mark();
    repl.send("\u{1b}[A");
    repl.until("root/source.txt -> copied.txt");
    repl.send("\u{3}");
    repl.prompt();
    repl.mark();
    assert!(pty::visible(&repl.submit("root/source.txt -> after-clear.txt")).contains("done:"));
    assert_eq!(
        fs::read_to_string(directory.path().join("after-clear.txt")).unwrap(),
        "actual edited path"
    );
    repl.exit();
    assert_eq!(
        fs::read_dir(directory.path()).unwrap().count(),
        3,
        "the editor must not persist a history/cache file into its isolated HOME"
    );
}

#[test]
fn monochrome_terminal_still_edits_and_ctrl_c_discards_input_without_exiting() {
    let directory = workspace();
    let mut repl = terminal(directory.path(), "never");
    repl.send("discard := $pwd::pwd");
    let edit = repl.until("$pwd::pwd");
    assert_eq!(
        pty::color_of(&edit, "$pwd"),
        None,
        "--color never must leave typed tokens monochrome: {edit:?}"
    );
    repl.mark();
    repl.send("\u{3}");
    repl.prompt();
    repl.mark();
    repl.send("/?\r");
    repl.until("REPL commands");
    repl.mark();
    repl.send("/bindings\r");
    repl.until("no user bindings");
    repl.exit();
}

#[test]
fn shell_command_ctrl_c_returns_to_a_live_repl_instead_of_killing_its_parent() {
    let directory = workspace();
    let mut repl = terminal(directory.path(), "never");
    #[cfg(windows)]
    repl.send("/pwsh Write-Output ('CHILD_' + 'READY'); Start-Sleep -Seconds 30\r");
    #[cfg(unix)]
    repl.send("/bash printf 'CHILD_%s\\n' READY; sleep 30\r");
    repl.until("CHILD_READY");
    repl.mark();
    repl.send("\u{3}");
    repl.prompt();
    repl.mark();
    repl.send("/?\r");
    repl.until("REPL commands");
    repl.exit();
}

#[test]
#[cfg_attr(windows, ignore = "requires an installed WSL distribution with Bash")]
fn interactive_bash_entry_returns_without_changing_parent_cwd_or_bindings() {
    let directory = workspace();
    fs::write(
        directory.path().join("source.txt"),
        "parent state survives shell",
    )
    .unwrap();
    let mut repl = terminal(directory.path(), "never");
    repl.submit("keep := $pwd::pwd");
    repl.mark();
    repl.send("/bash");
    repl.until("/bash");
    repl.mark();
    repl.send("\r");
    repl.until("type exit to return to msync");
    repl.mark();
    // Avoid writing the user's bash history. The output marker is not present
    // literally in the input, so terminal echo cannot satisfy this assertion.
    repl.send("HISTFILE=/dev/null; printf 'BASH_%s_READY\\n' INTERACTIVE; cd /; exit\r");
    repl.until_followed_by("BASH_INTERACTIVE_READY", "msync>");
    repl.mark();
    assert!(pty::visible(&repl.submit("keep/source.txt -> after-shell.txt")).contains("done:"));
    assert_eq!(
        fs::read_to_string(directory.path().join("after-shell.txt")).unwrap(),
        "parent state survives shell"
    );
    repl.exit();
}

#[cfg(windows)]
#[test]
fn interactive_pwsh_enters_and_exits_with_isolated_profile_history_and_parent_bindings() {
    let directory = workspace();
    fs::write(
        directory.path().join("source.txt"),
        "parent state survives pwsh",
    )
    .unwrap();
    let mut repl = terminal(directory.path(), "never");
    repl.submit("keep := $pwd::pwd");
    repl.mark();
    repl.send("/pwsh");
    repl.until("/pwsh");
    repl.mark();
    repl.send("\r");
    repl.until("type exit to return to msync");
    let name = directory
        .path()
        .file_name()
        .unwrap()
        .to_string_lossy()
        .into_owned();
    repl.until_matching(|raw| {
        let output = pty::visible(raw);
        output.contains("PS ") && output.contains(&name) && output.contains('>')
    });
    // Windows PSReadLine's default Windows keymap does not bind Ctrl-D to EOF.
    // APPDATA and USERPROFILE are isolated by the PTY host, including any history
    // for this normal exit command; no existing user profile/history is modified.
    repl.mark();
    repl.send("exit\r");
    repl.until("returned from /pwsh");
    repl.prompt();
    assert!(pty::visible(&repl.submit("keep/source.txt -> after-pwsh.txt")).contains("done:"));
    assert_eq!(
        fs::read_to_string(directory.path().join("after-pwsh.txt")).unwrap(),
        "parent state survives pwsh"
    );
    repl.exit();
}

#[test]
fn command_completion_changes_the_submitted_buffer_not_just_a_visible_hint() {
    let directory = workspace();
    let mut repl = terminal(directory.path(), "never");
    repl.send("/bi\t\r");
    repl.until_followed_by("no user bindings", "msync>");
    repl.exit();
}

#[test]
fn terminal_ctrl_c_joins_an_open_flow_preserves_target_and_physically_closes_the_peer() {
    let directory = workspace();
    let target = directory.path().join("target.txt");
    fs::write(&target, "keep published bytes").unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let address = listener.local_addr().unwrap();
    let prefix = b"accepted before interactive cancellation";
    let (opened, connected) = mpsc::channel();
    let (closed, eof) = mpsc::channel();
    let source = thread::spawn(move || {
        let deadline = Instant::now() + Duration::from_secs(10);
        let mut socket = loop {
            match listener.accept() {
                Ok((socket, _)) => break socket,
                Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                    assert!(Instant::now() < deadline, "terminal flow never connected");
                    thread::sleep(Duration::from_millis(2));
                }
                Err(error) => panic!("{error}"),
            }
        };
        socket.set_nonblocking(false).unwrap();
        socket
            .set_read_timeout(Some(Duration::from_secs(10)))
            .unwrap();
        socket.write_all(prefix).unwrap();
        opened.send(()).unwrap();
        let mut reverse = [0_u8; 1];
        closed.send(socket.read(&mut reverse).unwrap()).unwrap();
    });
    let mut repl = terminal(directory.path(), "never");
    repl.submit(&format!("wire => tcp://{address}"));
    assert!(
        pty::visible(&repl.submit("wire::/ -> $pwd::pwd/target.txt"))
            .contains("running in background")
    );
    connected.recv_timeout(Duration::from_secs(5)).unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        let staged = fs::read_dir(directory.path())
            .unwrap()
            .filter_map(Result::ok)
            .any(|entry| {
                entry.path() != target
                    && fs::File::open(entry.path())
                        .and_then(|file| file.metadata())
                        .is_ok_and(|metadata| {
                            metadata.is_file() && metadata.len() >= prefix.len() as u64
                        })
            });
        if staged {
            break;
        }
        assert!(
            Instant::now() < deadline,
            "wait for the physically written unpublished prefix"
        );
        thread::sleep(Duration::from_millis(2));
    }
    assert!(
        matches!(eof.try_recv(), Err(mpsc::TryRecvError::Empty)),
        "the opened source must remain connected before Ctrl-C"
    );
    assert_eq!(fs::read_to_string(&target).unwrap(), "keep published bytes");
    repl.mark();
    repl.send("\u{3}");
    repl.until("job 1: stopped");
    assert_eq!(
        eof.recv_timeout(Duration::from_secs(5)).unwrap(),
        0,
        "Ctrl-C must release the physical connection"
    );
    source.join().unwrap();
    assert_eq!(fs::read_to_string(&target).unwrap(), "keep published bytes");
    assert_eq!(
        fs::read_dir(directory.path()).unwrap().count(),
        1,
        "no uncommitted stage may survive the joined cancellation"
    );
    repl.mark();
    repl.send("/?\r");
    repl.until("REPL commands");
    repl.exit();
}

#[cfg(unix)]
#[test]
fn queued_physical_statements_are_not_discarded_between_readline_calls() {
    let directory = workspace();
    fs::write(
        directory.path().join("source.txt"),
        "preserve both queued statements",
    )
    .unwrap();
    let executable = std::env::var_os("MSYNC_PTY_PROBE_BINARY")
        .map(std::path::PathBuf::from)
        .unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut repl = pty::Terminal::spawn(&executable, directory.path(), "never");
    repl.prompt();
    repl.mark();
    repl.send("source.txt -> first.txt\rsource.txt -> second.txt\r");
    repl.until_matching_within(Duration::from_secs(5), |_| {
        directory.path().join("second.txt").exists()
    });
    for file in ["first.txt", "second.txt"] {
        assert_eq!(
            fs::read_to_string(directory.path().join(file)).unwrap(),
            "preserve both queued statements"
        );
    }
    repl.exit();
}

#[cfg(unix)]
#[test]
fn bracketed_multiline_paste_waits_for_enter_then_preserves_each_submission() {
    let directory = workspace();
    fs::write(
        directory.path().join("source.txt"),
        "bracketed paste payload",
    )
    .unwrap();
    let mut repl = terminal(directory.path(), "never");
    repl.send("\u{1b}[200~base := $pwd::pwd\nbase/source.txt -> pasted.txt\n\u{1b}[201~");
    repl.until("base/source.txt -> pasted.txt");
    assert!(
        !directory.path().join("pasted.txt").exists(),
        "pasted statements must not execute before explicit Enter"
    );
    repl.mark();
    repl.send("\r");
    repl.until("done:");
    assert_eq!(
        fs::read_to_string(directory.path().join("pasted.txt")).unwrap(),
        "bracketed paste payload"
    );
    repl.exit();
}
