//! SQLite tracing is exercised through the actual CLI and read-only operator tools.

use std::fs;
use std::io::Read;
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::thread;
use std::time::{Duration, Instant};

use rusqlite::{Connection, OpenFlags};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("telemetry-sqlite-cli-")
        .tempdir_in(root)
        .unwrap()
}

fn run(root: &Path, args: &[&str]) -> Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args(args)
        .env("NO_COLOR", "1")
        .env("HOME", root)
        .env("USERPROFILE", root)
        .env_remove("MSYNC_CONFIG")
        .env_remove("MSYNC_STATE_DIR")
        .env(
            "MSYNC_TEST_UNRELATED_SECRET",
            "this unrelated value must not be captured",
        )
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut stdout = child.stdout.take().unwrap();
    let mut stderr = child.stderr.take().unwrap();
    let out = thread::spawn(move || {
        let mut bytes = Vec::new();
        stdout.read_to_end(&mut bytes).unwrap();
        bytes
    });
    let err = thread::spawn(move || {
        let mut bytes = Vec::new();
        stderr.read_to_end(&mut bytes).unwrap();
        bytes
    });
    let deadline = Instant::now() + Duration::from_secs(20);
    let status = loop {
        if let Some(status) = child.try_wait().unwrap() {
            break status;
        }
        if Instant::now() >= deadline {
            let _ = child.kill();
            let _ = child.wait();
            panic!("telemetry CLI timed out: {args:?}");
        }
        thread::sleep(Duration::from_millis(5));
    };
    Output {
        status,
        stdout: out.join().unwrap(),
        stderr: err.join().unwrap(),
    }
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

fn reader(path: &Path) -> Connection {
    Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY).unwrap()
}

fn count(connection: &Connection, sql: &str) -> i64 {
    connection.query_row(sql, [], |row| row.get(0)).unwrap()
}

#[test]
fn real_macro_publication_has_queryable_compile_execution_and_io_ownership() {
    let root = workspace();
    let payload = "payload-must-not-enter-telemetry-可莉";
    fs::write(
        root.path().join("entry.msync"),
        format!("macro publish(value):\n    value -> target.txt\npublish(\"{payload}\")\n"),
    )
    .unwrap();
    success(run(
        root.path(),
        &[
            "run",
            "entry.msync",
            "--telemetry-db",
            "trace.db",
            "--no-config",
        ],
    ));
    assert_eq!(
        fs::read_to_string(root.path().join("target.txt")).unwrap(),
        payload
    );
    let connection = reader(&root.path().join("trace.db"));
    for name in [
        "session",
        "submission",
        "parse",
        "compile",
        "execute",
        "copy",
        "ordinary.acquire",
        "ordinary.stage",
        "ordinary.publish",
    ] {
        let matches: i64 = connection
            .query_row(
                "SELECT COUNT(*) FROM spans WHERE name = ?1 AND status = 'ok'",
                [name],
                |row| row.get(0),
            )
            .unwrap();
        assert!(matches > 0, "missing successful {name}");
    }
    assert_eq!(
        count(&connection, "SELECT COUNT(DISTINCT trace_id) FROM records"),
        1
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans WHERE parent_span_id IS NULL"
        ),
        1
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans child WHERE child.parent_span_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM spans parent WHERE child.trace_id = parent.trace_id AND child.parent_span_id = parent.span_id)"
        ),
        0
    );
    let assigned: String = connection
        .query_row(
            "SELECT m.value FROM metrics m JOIN records r ON r.id = m.record_id WHERE r.name = 'copy' AND m.key = 'bytes.assigned'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(assigned, payload.len().to_string());
    let values: Vec<String> = connection
        .prepare("SELECT value FROM attributes UNION ALL SELECT message FROM error_causes")
        .unwrap()
        .query_map([], |row| row.get(0))
        .unwrap()
        .collect::<rusqlite::Result<_>>()
        .unwrap();
    assert!(values.iter().all(|value| !value.contains(payload)));
    assert!(
        values
            .iter()
            .all(|value| !value.contains("this unrelated value must not be captured"))
    );
    assert!(values.iter().any(|value| value.contains("target.txt")));
}

#[test]
fn compile_and_dry_run_record_static_work_without_touching_runtime_outputs() {
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        "\"not executed\" -> target.txt\n",
    )
    .unwrap();
    for mode in ["check", "run"] {
        let arguments = if mode == "run" {
            vec![
                "run",
                "entry.msync",
                "--dry-run",
                "--telemetry-db",
                "static.db",
                "--no-config",
            ]
        } else {
            vec![
                "check",
                "entry.msync",
                "--telemetry-db",
                "static.db",
                "--no-config",
            ]
        };
        success(run(root.path(), &arguments));
    }
    assert!(!root.path().join("target.txt").exists());
    let connection = reader(&root.path().join("static.db"));
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM sessions"), 2);
    assert!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans WHERE name = 'compile'"
        ) >= 2
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans WHERE name = 'execute'"
        ),
        0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM records WHERE name = 'ordinary.publish'"
        ),
        0
    );
}

#[test]
fn runtime_failure_keeps_path_and_diagnostic_context_in_the_database() {
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        "missing-source -> target.txt\n",
    )
    .unwrap();
    let output = run(
        root.path(),
        &[
            "run",
            "entry.msync",
            "--telemetry-db",
            "failure.db",
            "--no-config",
        ],
    );
    assert!(!output.status.success());
    assert!(!root.path().join("target.txt").exists());
    let connection = reader(&root.path().join("failure.db"));
    assert!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans WHERE status = 'error'"
        ) > 0
    );
    assert!(
        count(
            &connection,
            "SELECT COUNT(*) FROM attributes WHERE value LIKE '%missing-source%'"
        ) > 0
    );
    assert!(
        count(
            &connection,
            "SELECT COUNT(*) FROM records WHERE name = 'diagnostic'"
        ) > 0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans WHERE name = 'ordinary.publish' AND status = 'ok'"
        ),
        0
    );
}

#[test]
fn help_version_and_explicit_disable_do_not_create_a_database() {
    let root = workspace();
    for flag in ["--help", "--version"] {
        success(run(
            root.path(),
            &[flag, "--telemetry-db", "absent.db", "--no-config"],
        ));
        assert!(!root.path().join("absent.db").exists());
    }
    fs::write(root.path().join("entry.msync"), "\"done\" -> output\n").unwrap();
    fs::write(
        root.path().join("settings.toml"),
        "[telemetry]\nenabled = true\nbackend = \"sqlite\"\npath = \"absent.db\"\n",
    )
    .unwrap();
    success(run(
        root.path(),
        &[
            "run",
            "entry.msync",
            "--config",
            "settings.toml",
            "--no-telemetry",
        ],
    ));
    assert!(!root.path().join("absent.db").exists());
    assert_eq!(fs::read(root.path().join("output")).unwrap(), b"done");
}

#[test]
fn sqlite_initialization_failure_is_reported_before_transfer_effects() {
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        "\"must not publish\" -> output\n",
    )
    .unwrap();
    fs::create_dir(root.path().join("not-a-database")).unwrap();
    let output = run(
        root.path(),
        &[
            "run",
            "entry.msync",
            "--telemetry-db",
            "not-a-database",
            "--no-config",
        ],
    );
    assert!(!output.status.success());
    assert!(!root.path().join("output").exists());
    let diagnostic = String::from_utf8_lossy(&output.stderr);
    assert!(diagnostic.contains("telemetry"), "{diagnostic}");
    assert!(diagnostic.contains("not-a-database"), "{diagnostic}");
}

#[test]
fn read_only_operator_commands_query_and_export_the_same_records() {
    let root = workspace();
    fs::write(root.path().join("entry.msync"), "\"payload\" -> output\n").unwrap();
    success(run(
        root.path(),
        &[
            "run",
            "entry.msync",
            "--telemetry-db",
            "trace.db",
            "--no-config",
        ],
    ));
    let database = root.path().join("trace.db");
    let connection = reader(&database);
    let records = count(&connection, "SELECT COUNT(*) FROM records");
    let sessions = count(&connection, "SELECT COUNT(*) FROM sessions");
    let listed = success(run(root.path(), &["telemetry", "sessions", "trace.db"]));
    let listed: serde_json::Value = serde_json::from_slice(&listed.stdout).unwrap();
    assert_eq!(listed.as_array().unwrap().len(), 1);
    assert_eq!(listed[0]["status"], "complete");
    assert!(listed[0]["ended_unix_ns"].is_number());
    let summary = success(run(root.path(), &["telemetry", "summary", "trace.db"]));
    let summary: serde_json::Value = serde_json::from_slice(&summary.stdout).unwrap();
    assert_eq!(summary["sessions"], 1);
    assert!(summary["spans"].as_u64().unwrap() > 0);
    assert_eq!(summary["error_spans"], 0);
    assert_eq!(summary["cancelled_spans"], 0);
    let spans = success(run(
        root.path(),
        &["telemetry", "spans", "trace.db", "--limit", "2"],
    ));
    let spans: serde_json::Value = serde_json::from_slice(&spans.stdout).unwrap();
    assert_eq!(spans.as_array().unwrap().len(), 2);
    let exported = success(run(root.path(), &["telemetry", "export", "trace.db"]));
    let exported = String::from_utf8(exported.stdout).unwrap();
    let decoded: Vec<serde_json::Value> = exported
        .lines()
        .map(|line| serde_json::from_str(line).unwrap())
        .collect();
    assert_eq!(decoded.len() as i64, records);
    assert!(decoded.iter().all(|record| record["schema_version"] == 1));
    assert!(
        decoded
            .iter()
            .any(|record| record["kind"] == "span" && record["name"] == "session")
    );
    let owner = decoded
        .iter()
        .find(|record| record["kind"] == "event")
        .expect("publication has lifecycle events")["span_id"]
        .as_str()
        .unwrap();
    let events = success(run(
        root.path(),
        &["telemetry", "events", "trace.db", "--span-id", owner],
    ));
    let events: serde_json::Value = serde_json::from_slice(&events.stdout).unwrap();
    let events = events.as_array().unwrap();
    assert!(!events.is_empty());
    assert!(events.iter().all(|event| event["span_id"] == owner));
    let empty = success(run(
        root.path(),
        &[
            "telemetry",
            "export",
            "trace.db",
            "--trace-id",
            "00000000000000000000000000000000",
        ],
    ));
    assert!(empty.stdout.is_empty());
    assert_eq!(
        count(&connection, "SELECT COUNT(*) FROM sessions"),
        sessions
    );
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM records"), records);
    let missing = run(root.path(), &["telemetry", "summary", "missing.db"]);
    assert!(!missing.status.success());
    assert!(!root.path().join("missing.db").exists());
}
