//! Cross-domain acceptance: public configuration/telemetry/runtime journeys.

#[path = "support/journey_process.rs"]
mod journey_process;

use std::fs;
use std::path::Path;
use std::process::Output;

use journey_process::Process;

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("performance-journeys-")
        .tempdir_in(root)
        .unwrap()
}

fn invoke(root: &Path, args: &[&str]) -> Output {
    Process::start(Process::command(root, args)).finish()
}

fn success(output: Output) -> Output {
    assert!(
        output.status.success(),
        "stdout={}\nstderr={}",
        String::from_utf8_lossy(&output.stdout),
        String::from_utf8_lossy(&output.stderr)
    );
    output
}

fn assert_outputs(root: &Path) {
    assert_eq!(
        fs::read(root.join("exact")).unwrap(),
        fs::read(root.join("inputs/00.txt")).unwrap()
    );
    let mut selected = fs::read_dir(root.join("selected"))
        .unwrap()
        .map(|entry| entry.unwrap().file_name().into_string().unwrap())
        .collect::<Vec<_>>();
    selected.sort();
    assert_eq!(
        selected,
        (0..8)
            .map(|index| format!("{index:02}.txt"))
            .collect::<Vec<_>>()
    );
    for name in selected {
        assert_eq!(
            fs::read(root.join("selected").join(&name)).unwrap(),
            fs::read(root.join("inputs").join(name)).unwrap()
        );
    }
    assert_eq!(fs::read(root.join("state")).unwrap(), b"after");
    assert!(!root.join("transient").exists());
    assert!(!root.join("wrong").exists());
}

#[test]
fn optimized_copy_selector_and_control_preserve_real_outputs_and_source_free_objects() {
    let root = workspace();
    fs::create_dir(root.path().join("inputs")).unwrap();
    for index in 0..32 {
        fs::write(
            root.path().join("inputs").join(format!("{index:02}.txt")),
            format!("payload-{index:03}-可莉\n"),
        )
        .unwrap();
    }
    fs::write(root.path().join("inputs/ignored.bin"), b"not selected").unwrap();
    fs::write(
        root.path().join("entry.msync"),
        "boolean present(path):\n    return exists path\nready = exists inputs/00.txt\ndone = false\nlabel = \"before\"\nloop:\n    if done:\n        break\n    label = \"after\"\n    done = true\ninputs/00.txt -> exact\nfor file in inputs/<.+\\.txt> | views::filter(present) | views::take(8):\n    file -> selected/\n\"obsolete\" -> transient\ndelete transient\nif ready and done and label == \"after\" and not exists transient:\n    label -> state\nelse:\n    \"wrong\" -> wrong\n",
    )
    .unwrap();
    for args in [
        ["check", "entry.msync"].as_slice(),
        ["plan", "entry.msync"].as_slice(),
        ["run", "entry.msync", "--dry-run"].as_slice(),
    ] {
        success(invoke(root.path(), args));
    }
    for name in ["selected", "exact", "state", "transient", "wrong"] {
        assert!(!root.path().join(name).exists(), "preview created {name}");
    }
    success(invoke(
        root.path(),
        &["compile", "entry.msync", "-o", "program.mobj"],
    ));
    success(invoke(root.path(), &["run", "entry.msync"]));
    assert_outputs(root.path());
    fs::remove_file(root.path().join("entry.msync")).unwrap();
    fs::remove_dir_all(root.path().join("selected")).unwrap();
    fs::remove_file(root.path().join("exact")).unwrap();
    fs::remove_file(root.path().join("state")).unwrap();
    success(invoke(root.path(), &["run", "program.mobj"]));
    assert_outputs(root.path());
}

#[test]
fn released_program_completion_is_visible_while_repl_input_remains_idle() {
    use std::io::Write;
    use std::net::{Shutdown, TcpListener};
    use std::thread;
    use std::time::{Duration, Instant};

    let root = workspace();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    fs::write(
        root.path().join("child.msync"),
        format!(
            "wire => tcp://{}\nwire::/ -> child-output\n",
            listener.local_addr().unwrap()
        ),
    )
    .unwrap();
    let executable = serde_json::to_string(env!("CARGO_BIN_EXE_msync")).unwrap();
    let mut repl = Process::start(Process::command(root.path(), &["repl", "--color", "never"]));
    repl.send(
        format!(
            "release child => $local::{executable}(\"run\", \"child.msync\", \"--color\", \"never\")\n"
        )
        .as_bytes(),
    );
    let deadline = Instant::now() + Duration::from_secs(10);
    let mut peer = loop {
        match listener.accept() {
            Ok((peer, _)) => break peer,
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                assert!(Instant::now() < deadline, "released child never connected");
                thread::sleep(Duration::from_millis(5));
            }
            Err(error) => panic!("accepting the real child source failed: {error}"),
        }
    };
    repl.until(|stdout, _| stdout.contains("running in background"));
    assert!(!root.path().join("child-output").exists());
    repl.send(b"/help\n");
    repl.until(|stdout, _| stdout.contains("REPL commands"));
    peer.write_all(b"committed while parent input is idle")
        .unwrap();
    peer.shutdown(Shutdown::Write).unwrap();
    drop(peer);

    // No input is sent between releasing the child and observing its completion.
    repl.until(|stdout, _| stdout.contains("job 1: completed"));
    assert_eq!(
        fs::read(root.path().join("child-output")).unwrap(),
        b"committed while parent input is idle"
    );
    repl.send(b"/jobs\n/quit\n");
    let output = success(repl.finish());
    assert!(String::from_utf8_lossy(&output.stdout).contains("no active jobs"));
    assert!(
        output.stderr.is_empty(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
}

fn configured_database(root: &Path) -> std::path::PathBuf {
    let policy = root.join("policy");
    fs::create_dir_all(&policy).unwrap();
    fs::write(
        policy.join("config.toml"),
        "[execution]\njobs = 1\nmax_streams = 2\nbuffer_size = \"8KiB\"\nmemory_limit = \"128KiB\"\nstate_dir = \"state\"\n[telemetry]\nenabled = true\nbackend = \"sqlite\"\npath = \"records.db\"\nqueue_capacity = 16\nbatch_size = 4\nflush_interval_ms = 10\n[ui]\ncolor = \"never\"\n",
    )
    .unwrap();
    policy.join("records.db")
}

fn read_database(path: &Path) -> rusqlite::Connection {
    rusqlite::Connection::open_with_flags(path, rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY).unwrap()
}

fn count(connection: &rusqlite::Connection, sql: &str) -> i64 {
    connection.query_row(sql, [], |row| row.get(0)).unwrap()
}

#[test]
fn configuration_selected_sqlite_preserves_real_execution_and_read_only_operator_queries() {
    let root = workspace();
    let database = configured_database(root.path());
    let configuration = success(invoke(
        root.path(),
        &["config", "--config", "policy/config.toml", "--jobs", "2"],
    ));
    let effective: toml::Value =
        toml::from_str(&String::from_utf8_lossy(&configuration.stdout)).unwrap();
    assert_eq!(effective["execution"]["jobs"].as_integer(), Some(2));
    assert_eq!(effective["execution"]["max_streams"].as_integer(), Some(2));
    assert_eq!(
        Path::new(effective["telemetry"]["path"].as_str().unwrap()),
        database
    );
    assert!(
        !database.exists(),
        "configuration inspection initialized SQLite"
    );
    assert!(!root.path().join("policy/state").exists());

    let payload = (0_u8..=255).cycle().take(131_072).collect::<Vec<_>>();
    fs::write(root.path().join("input"), &payload).unwrap();
    fs::write(
        root.path().join("entry.msync"),
        "ready = exists input\nif ready:\n    input -> output\nelse:\n    \"wrong\" -> output\n",
    )
    .unwrap();
    success(invoke(
        root.path(),
        &[
            "run",
            "entry.msync",
            "--config",
            "policy/config.toml",
            "--jobs",
            "2",
        ],
    ));
    assert_eq!(fs::read(root.path().join("output")).unwrap(), payload);
    assert!(
        !root.path().join("records.db").exists(),
        "config path incorrectly used invocation cwd"
    );
    let connection = read_database(&database);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM sessions"), 1);
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM sessions WHERE ended_unix_ns IS NULL"
        ),
        0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans child WHERE child.parent_span_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM spans parent WHERE child.trace_id=parent.trace_id AND child.parent_span_id=parent.span_id)"
        ),
        0,
        "completed database contains orphaned causal spans"
    );
    for name in [
        "session",
        "submission",
        "parse",
        "compile",
        "contextualize",
        "execute",
        "copy",
        "schedule",
        "ordinary.publish",
        "interpreter.block",
        "interpreter.prepare",
        "interpreter.schedule",
        "interpreter.instruction",
    ] {
        let matches: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM spans WHERE name=?1 AND status='ok'",
                [name],
                |row| row.get(0),
            )
            .unwrap();
        assert!(matches > 0, "configured trace is missing successful {name}");
    }
    for (phase, metric) in [
        ("interpreter.block", "block.id"),
        ("interpreter.block", "instructions"),
        ("interpreter.prepare", "block.id"),
        ("interpreter.schedule", "dependencies"),
        ("interpreter.instruction", "instruction.id"),
    ] {
        let missing: i64 = connection.query_row(
            "SELECT COUNT(*) FROM spans s WHERE s.name=?1 AND NOT EXISTS (SELECT 1 FROM metrics m WHERE m.record_id=s.id AND m.key=?2)",
            [phase, metric],
            |row| row.get(0),
        ).unwrap();
        assert_eq!(missing, 0, "{phase} lost causal metric {metric}");
    }
    for phase in [
        "interpreter.prepare",
        "interpreter.schedule",
        "interpreter.instruction",
    ] {
        let rooted: i64 = connection.query_row(
            "SELECT COUNT(*) FROM spans child JOIN spans parent ON child.trace_id=parent.trace_id AND child.parent_span_id=parent.span_id WHERE child.name=?1 AND parent.name='interpreter.block'",
            [phase],
            |row| row.get(0),
        ).unwrap();
        assert!(rooted > 0, "{phase} is detached from its actual block");
    }
    assert!(
        count(
            &connection,
            "SELECT COUNT(*) FROM events e JOIN metrics m ON m.record_id=e.id WHERE e.name='scheduler.ready' AND m.key='ready'"
        ) > 0
    );
    let records = count(&connection, "SELECT COUNT(*) FROM records");
    let database_arg = database.to_str().unwrap();
    let summary = success(invoke(root.path(), &["telemetry", "summary", database_arg]));
    let summary: serde_json::Value = serde_json::from_slice(&summary.stdout).unwrap();
    assert_eq!(summary["sessions"], 1);
    let exported = success(invoke(root.path(), &["telemetry", "export", database_arg]));
    let exported = String::from_utf8(exported.stdout).unwrap();
    let exported = exported
        .lines()
        .map(|line| serde_json::from_str::<serde_json::Value>(line).unwrap())
        .collect::<Vec<_>>();
    assert_eq!(exported.len() as i64, records);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM records"), records);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM sessions"), 1);
}

#[test]
fn configuration_selected_sqlite_retains_cancellation_without_partial_publication() {
    use std::net::TcpListener;
    use std::thread;
    use std::time::{Duration, Instant};

    let root = workspace();
    let database = configured_database(root.path());
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let mut repl = Process::start(Process::command(
        root.path(),
        &["repl", "--config", "policy/config.toml"],
    ));
    repl.send(
        format!(
            "wire => tcp://{}\nwire::/ -> output\n",
            listener.local_addr().unwrap()
        )
        .as_bytes(),
    );
    let deadline = Instant::now() + Duration::from_secs(10);
    let _held_source = loop {
        match listener.accept() {
            Ok((peer, _)) => break peer,
            Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                assert!(
                    Instant::now() < deadline,
                    "configured transfer never connected"
                );
                thread::sleep(Duration::from_millis(5));
            }
            Err(error) => panic!("accept failed: {error}"),
        }
    };
    repl.until(|stdout, _| stdout.contains("running in background"));
    repl.send(b"/help\n");
    repl.until(|stdout, _| stdout.contains("REPL commands"));
    repl.send(b"/stop\n/jobs\n/quit\n");
    let output = success(repl.finish());
    assert!(String::from_utf8_lossy(&output.stdout).contains("no active jobs"));
    assert!(!root.path().join("output").exists());
    let connection = read_database(&database);
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM sessions WHERE ended_unix_ns IS NULL"
        ),
        0
    );
    assert!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans WHERE status='cancelled'"
        ) > 0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM records WHERE name='ordinary.publish' AND status='ok'"
        ),
        0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans child WHERE child.parent_span_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM spans parent WHERE child.trace_id=parent.trace_id AND child.parent_span_id=parent.span_id)"
        ),
        0
    );
    let sessions = success(invoke(
        root.path(),
        &["telemetry", "sessions", database.to_str().unwrap()],
    ));
    let sessions: serde_json::Value = serde_json::from_slice(&sessions.stdout).unwrap();
    assert_eq!(sessions.as_array().unwrap().len(), 1);
    assert!(!sessions[0]["ended_unix_ns"].is_null());
    assert_eq!(sessions[0]["status"], "complete");
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans WHERE name='session' AND status='ok'"
        ),
        1
    );
}

#[test]
fn invalid_configuration_does_not_initialize_its_database_or_runtime_state() {
    let root = workspace();
    let database = configured_database(root.path());
    let config = root.path().join("policy/config.toml");
    let valid = fs::read_to_string(&config).unwrap();
    fs::write(
        root.path().join("entry.msync"),
        "\"must not execute\" -> output\n",
    )
    .unwrap();
    for (needle, replacement, key) in [
        ("jobs = 1", "jobs = 0", "jobs"),
        ("jobs = 1", "jobz = 1", "jobz"),
    ] {
        fs::write(&config, valid.replacen(needle, replacement, 1)).unwrap();
        let output = invoke(
            root.path(),
            &["run", "entry.msync", "--config", "policy/config.toml"],
        );
        assert!(!output.status.success());
        let error = String::from_utf8_lossy(&output.stderr);
        assert!(
            error.contains("config.toml") && error.contains(key),
            "{error}"
        );
        assert!(
            error.contains(":2:"),
            "missing actionable source location: {error}"
        );
        assert!(
            !database.exists(),
            "invalid config initialized its telemetry sink"
        );
        assert!(!root.path().join("policy/state").exists());
        assert!(!root.path().join("output").exists());
    }
}
