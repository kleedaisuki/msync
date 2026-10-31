//! Independent cross-component probes for configuration and structured telemetry.

use std::fs;
use std::path::Path;
use std::process::{Command, Output, Stdio};
use std::sync::{Arc, Barrier};
use std::thread;
use std::time::{Duration, Instant};

use rusqlite::{Connection, OpenFlags};

fn workspace() -> tempfile::TempDir {
    let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
    fs::create_dir_all(&root).unwrap();
    tempfile::Builder::new()
        .prefix("performance-review-v6-")
        .tempdir_in(root)
        .unwrap()
}

fn invoke(root: &Path, arguments: &[&str]) -> Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_msync"))
        .current_dir(root)
        .args(arguments)
        .env("HOME", root)
        .env("USERPROFILE", root)
        .env("NO_COLOR", "1")
        .env_remove("MSYNC_CONFIG")
        .env_remove("MSYNC_STATE_DIR")
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let deadline = Instant::now() + Duration::from_secs(15);
    while child.try_wait().unwrap().is_none() {
        if Instant::now() >= deadline {
            let _ = child.kill();
            let output = child.wait_with_output().unwrap();
            panic!(
                "CLI timed out for {arguments:?}: stdout={} stderr={}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
        }
        thread::sleep(Duration::from_millis(5));
    }
    child.wait_with_output().unwrap()
}

#[test]
fn concurrent_processes_can_initialize_and_finish_one_fresh_trace_database() {
    const WRITERS: usize = 8;
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        "\"not executed\" -> output\n",
    )
    .unwrap();
    for round in 0..3 {
        let database = format!("fresh-{round}.db");
        let start = Arc::new(Barrier::new(WRITERS));
        let writers: Vec<_> = (0..WRITERS)
            .map(|_| {
                let start = start.clone();
                let directory = root.path().to_owned();
                let database = database.clone();
                thread::spawn(move || {
                    start.wait();
                    invoke(
                        &directory,
                        &[
                            "check",
                            "entry.msync",
                            "--telemetry-db",
                            &database,
                            "--no-config",
                        ],
                    )
                })
            })
            .collect();
        let outputs: Vec<_> = writers
            .into_iter()
            .map(|writer| writer.join().unwrap())
            .collect();
        for (writer, output) in outputs.into_iter().enumerate() {
            assert!(
                output.status.success(),
                "fresh database round={round} writer={writer}: stdout={} stderr={}",
                String::from_utf8_lossy(&output.stdout),
                String::from_utf8_lossy(&output.stderr)
            );
        }
        let reader = Connection::open_with_flags(
            root.path().join(database),
            OpenFlags::SQLITE_OPEN_READ_ONLY,
        )
        .unwrap();
        let completed: i64 = reader
            .query_row(
                "SELECT COUNT(*) FROM sessions WHERE ended_unix_ns IS NOT NULL",
                [],
                |row| row.get(0),
            )
            .unwrap();
        assert_eq!(completed, WRITERS as i64);
        let traces: i64 = reader
            .query_row("SELECT COUNT(DISTINCT trace_id) FROM records", [], |row| {
                row.get(0)
            })
            .unwrap();
        assert_eq!(traces, WRITERS as i64);
        let integrity: String = reader
            .query_row("PRAGMA quick_check", [], |row| row.get(0))
            .unwrap();
        assert_eq!(integrity, "ok");
        assert!(!root.path().join("output").exists());
    }
}

#[test]
fn oversized_sqlite_busy_timeout_is_a_source_labeled_pre_effect_config_error() {
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        "\"not executed\" -> output\n",
    )
    .unwrap();
    fs::write(
        root.path().join("invalid.toml"),
        "[telemetry]\nenabled = true\npath = \"must-not-open.db\"\nbusy_timeout_ms = 2147483648\n",
    )
    .unwrap();
    let output = invoke(
        root.path(),
        &["check", "entry.msync", "--config", "invalid.toml"],
    );
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(
        error.contains("invalid.toml") && error.contains("busy_timeout_ms"),
        "{error}"
    );
    assert!(!error.contains("panicked"), "{error}");
    assert!(!root.path().join("must-not-open.db").exists());
    assert!(!root.path().join("output").exists());
}

#[test]
fn telemetry_queue_and_transaction_batch_are_independently_tunable() {
    let root = workspace();
    fs::write(
        root.path().join("entry.msync"),
        "\"not executed\" -> output\n",
    )
    .unwrap();
    fs::write(
        root.path().join("small-queue.toml"),
        "[telemetry]\nenabled = true\npath = \"small-queue.db\"\nqueue_capacity = 1\nbatch_size = 3\n",
    ).unwrap();
    let output = invoke(
        root.path(),
        &["check", "entry.msync", "--config", "small-queue.toml"],
    );
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let reader = Connection::open_with_flags(
        root.path().join("small-queue.db"),
        OpenFlags::SQLITE_OPEN_READ_ONLY,
    )
    .unwrap();
    let records: i64 = reader
        .query_row("SELECT COUNT(*) FROM records", [], |row| row.get(0))
        .unwrap();
    assert!(records > 0);
    assert!(!root.path().join("output").exists());
}
