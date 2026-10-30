//! Cancellation through a real terminal drains, rather than kills, a child.

#[path = "support/pty.rs"]
#[allow(dead_code)]
mod pty;

use std::fs;
use std::io::Write;
use std::path::Path;
use std::thread;
use std::time::{Duration, Instant};

const FINAL_MARKER: &[u8] = b"CHILD_SELF_EXITED_AFTER_INTERRUPT";
const OUTPUT_BYTES: usize = 2 * 1024 * 1024;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("barrier-terminal-")
        .tempdir_in(root)
        .unwrap()
}

#[test]
fn terminal_ctrl_c_drains_a_running_child_until_it_exits_itself() {
    let root = workspace();
    let fixture = std::env::current_exe()
        .unwrap()
        .to_string_lossy()
        .replace('\\', "\\\\")
        .replace('"', "\\\"");
    fs::write(
        root.path().join("start.msync"),
        format!(
            "async child => $local::\"{fixture}\"(\"--exact\", \"terminal_drain_child_fixture\", \"--ignored\", \"--nocapture\")\n"
        ),
    )
    .unwrap();
    let executable =
        std::env::var_os("MSYNC_TEST_EXE").unwrap_or_else(|| env!("CARGO_BIN_EXE_msync").into());
    let mut terminal = pty::Terminal::spawn(Path::new(&executable), root.path(), "never");
    terminal.prompt();
    terminal.submit("/load start.msync");
    let deadline = Instant::now() + Duration::from_secs(10);
    while !root.path().join("child-ready").exists() {
        assert!(
            Instant::now() < deadline,
            "the actual external child never became ready"
        );
        thread::sleep(Duration::from_millis(5));
    }

    terminal.mark();
    terminal.send("await child\r");
    terminal.until("working:");
    terminal.mark();
    // This byte is delivered through the pseudoconsole/controlling PTY. The
    // console or terminal driver turns it into a foreground interrupt event;
    // it is not a byte sent through msync's ordinary redirected stdin.
    terminal.send("\u{3}");
    terminal.until("interrupt: draining started work; no later statement will start");
    assert!(!root.path().join("child-completed").exists());
    fs::write(root.path().join("release-child"), b"finish naturally").unwrap();
    terminal.prompt();
    assert_eq!(
        fs::read(root.path().join("child-completed")).unwrap(),
        FINAL_MARKER
    );

    let capture = terminal.submit("child::stdout -> captured");
    assert!(!pty::visible(&capture).contains("error:"), "{capture:?}");
    let settled = terminal.submit("/wait");
    assert!(!pty::visible(&settled).contains("error:"), "{settled:?}");
    let bytes = fs::read(root.path().join("captured")).unwrap();
    let begin = bytes
        .windows(b"BEGIN_DRAIN_PAYLOAD\n".len())
        .position(|part| part == b"BEGIN_DRAIN_PAYLOAD\n")
        .unwrap()
        + b"BEGIN_DRAIN_PAYLOAD\n".len();
    assert!(bytes.len() >= begin + OUTPUT_BYTES + FINAL_MARKER.len());
    assert!(
        bytes[begin..begin + OUTPUT_BYTES]
            .iter()
            .all(|byte| *byte == b'D')
    );
    assert!(
        bytes[begin + OUTPUT_BYTES..]
            .windows(FINAL_MARKER.len())
            .any(|part| part == FINAL_MARKER)
    );
    terminal.submit("child::exit_code -> exit-code");
    terminal.submit("/wait");
    assert_eq!(
        fs::read_to_string(root.path().join("exit-code"))
            .unwrap()
            .trim(),
        "0"
    );
    terminal.submit("\"repl survived\" -> after-interrupt");
    assert_eq!(
        fs::read(root.path().join("after-interrupt")).unwrap(),
        b"repl survived"
    );
    terminal.exit();
}

#[test]
#[ignore = "subprocess fixture started by the terminal cancellation journey"]
fn terminal_drain_child_fixture() {
    // Do not install an interrupt handler: native process-group isolation must
    // protect this ordinary child from the parent terminal's Ctrl-C event.
    fs::write("child-ready", b"native child is running").unwrap();
    let deadline = Instant::now() + Duration::from_secs(25);
    while !Path::new("release-child").exists() {
        assert!(
            Instant::now() < deadline,
            "terminal test never released its child"
        );
        thread::sleep(Duration::from_millis(5));
    }
    let mut stdout = std::io::stdout().lock();
    stdout.write_all(b"BEGIN_DRAIN_PAYLOAD\n").unwrap();
    stdout.write_all(&vec![b'D'; OUTPUT_BYTES]).unwrap();
    stdout.write_all(FINAL_MARKER).unwrap();
    stdout.flush().unwrap();
    fs::write("child-completed", FINAL_MARKER).unwrap();
    std::process::exit(0);
}
