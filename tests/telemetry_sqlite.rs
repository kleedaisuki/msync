//! Public telemetry journeys query the normalized database, not exporter internals.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Barrier, Mutex};

use msync::telemetry::{self, SqliteOptions};
use rusqlite::{Connection, OpenFlags};

static SESSION_LOCK: Mutex<()> = Mutex::new(());

struct Fixture {
    directory: tempfile::TempDir,
}

impl Fixture {
    fn new() -> Self {
        telemetry::disable().unwrap();
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join(".temp");
        fs::create_dir_all(&root).unwrap();
        Self {
            directory: tempfile::Builder::new()
                .prefix("telemetry-sqlite-")
                .tempdir_in(root)
                .unwrap(),
        }
    }

    fn path(&self, name: &str) -> PathBuf {
        self.directory.path().join(name)
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = telemetry::disable();
    }
}

fn reader(path: &Path) -> Connection {
    Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY).unwrap()
}

fn count(connection: &Connection, sql: &str) -> i64 {
    connection.query_row(sql, [], |row| row.get(0)).unwrap()
}

#[test]
fn sqlite_flush_exposes_normalized_causality_errors_and_exact_unsigned_metrics() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let database = fixture.path("trace.db");
    let source = fixture.path("可莉 input.bin");
    telemetry::init_sqlite(&database, SqliteOptions::default()).unwrap();
    {
        let mut root = telemetry::span("session");
        root.attribute("source.path", source.to_string_lossy());
        root.metric("bytes.maximum", u64::MAX);
        root.event("checkpoint", &[("phase", "before_copy")], &[("files", 1)]);
        {
            let mut copy = telemetry::span("copy");
            let error = fs::File::open(&source).unwrap_err();
            copy.io_error("open", &error);
            copy.finish_result(&Err::<(), _>(error));
        }
        root.finish_result(&Err::<(), _>("payload is not diagnostic metadata"));
    }
    telemetry::flush().unwrap();
    let connection = reader(&database);
    assert_eq!(
        connection
            .query_row("PRAGMA journal_mode", [], |row| row.get::<_, String>(0))
            .unwrap(),
        "wal"
    );
    assert_eq!(count(&connection, "PRAGMA user_version"), 1);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM sessions"), 1);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM spans"), 2);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM events"), 2);
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans child JOIN spans parent ON child.trace_id = parent.trace_id AND child.parent_span_id = parent.span_id WHERE child.name = 'copy' AND parent.name = 'session' AND child.status = 'error'"
        ),
        1
    );
    let path: String = connection
        .query_row(
            "SELECT a.value FROM attributes a JOIN records r ON r.id = a.record_id WHERE r.name = 'session' AND a.key = 'source.path'",
            [],
            |row| row.get(0),
        )
        .unwrap();
    assert_eq!(path, source.to_string_lossy());
    let maximum: (String, Option<i64>) = connection
        .query_row(
            "SELECT value, value_int FROM metrics WHERE key = 'bytes.maximum'",
            [],
            |row| Ok((row.get(0)?, row.get(1)?)),
        )
        .unwrap();
    assert_eq!(maximum, (u64::MAX.to_string(), None));
    assert!(
        count(
            &connection,
            "SELECT COUNT(*) FROM error_causes WHERE kind = 'NotFound' AND raw_os_error IS NOT NULL"
        ) > 0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM attributes WHERE value LIKE '%payload is not diagnostic metadata%'"
        ),
        0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM error_causes WHERE message LIKE '%payload is not diagnostic metadata%'"
        ),
        0
    );
    assert!(telemetry::take_error().is_none());
}

#[test]
fn sqlite_small_bounded_queue_preserves_concurrent_tasks_and_flushes_all_records() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let database = fixture.path("concurrent.db");
    let options = SqliteOptions {
        queue_capacity: 1,
        batch_size: 3,
        ..SqliteOptions::default()
    };
    telemetry::init_sqlite(&database, options).unwrap();
    {
        let _root = telemetry::span("schedule");
        let parent = telemetry::context().unwrap();
        let ready = Arc::new(Barrier::new(8));
        std::thread::scope(|scope| {
            for worker in 0..8 {
                let parent = parent.clone();
                let ready = ready.clone();
                scope.spawn(move || {
                    let mut task = telemetry::linked_span("task", Some(&parent));
                    task.metric("worker", worker);
                    ready.wait();
                    for index in 0..10 {
                        task.event("progress", &[("phase", "transfer")], &[("index", index)]);
                    }
                    task.finish_result(&Ok::<(), ()>(()));
                });
            }
        });
    }
    telemetry::flush().unwrap();
    let connection = reader(&database);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM records"), 89);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM spans"), 9);
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
            "SELECT COUNT(*) FROM spans child WHERE child.parent_span_id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM spans parent WHERE parent.trace_id = child.trace_id AND parent.span_id = child.parent_span_id)"
        ),
        0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM events event WHERE NOT EXISTS (SELECT 1 FROM spans owner WHERE owner.trace_id = event.trace_id AND owner.span_id = event.span_id)"
        ),
        0
    );
}

#[test]
fn sqlite_reconfiguration_keeps_retained_contexts_bound_to_their_original_database() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let first = fixture.path("first.db");
    let second = fixture.path("second.db");
    telemetry::init_sqlite(&first, SqliteOptions::default()).unwrap();
    let retained = {
        let _root = telemetry::span("original");
        telemetry::context().unwrap()
    };
    telemetry::init_sqlite(&second, SqliteOptions::default()).unwrap();
    drop(telemetry::span("replacement"));
    telemetry::disable().unwrap();
    {
        let mut delayed = telemetry::linked_span("delayed", Some(&retained));
        delayed.event("late.progress", &[("phase", "transfer")], &[("bytes", 7)]);
        delayed.finish_result(&Ok::<(), ()>(()));
    }
    retained.flush().unwrap();
    let original = reader(&first);
    let replacement = reader(&second);
    assert_eq!(count(&original, "SELECT COUNT(*) FROM records"), 3);
    assert_eq!(count(&replacement, "SELECT COUNT(*) FROM records"), 1);
    assert_eq!(
        count(
            &original,
            "SELECT COUNT(*) FROM spans child JOIN spans parent ON parent.trace_id = child.trace_id AND parent.span_id = child.parent_span_id WHERE child.name = 'delayed' AND parent.name = 'original'"
        ),
        1
    );
}

#[test]
fn sqlite_rejects_newer_schema_without_replacing_a_working_jsonl_sink() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let original = fixture.path("original.jsonl");
    let database = fixture.path("future.db");
    let connection = Connection::open(&database).unwrap();
    connection
        .execute_batch("PRAGMA application_id = 0x4d53594e; PRAGMA user_version = 999;")
        .unwrap();
    drop(connection);
    telemetry::init(original.to_str().unwrap()).unwrap();
    let error = telemetry::init_sqlite(&database, SqliteOptions::default()).unwrap_err();
    assert!(
        error.contains("schema") || error.contains("version"),
        "{error}"
    );
    drop(telemetry::span("still_recording"));
    telemetry::flush().unwrap();
    let line = fs::read_to_string(original).unwrap();
    assert!(line.contains("still_recording"), "{line}");
    assert_eq!(count(&reader(&database), "PRAGMA user_version"), 999);
}

#[test]
fn sqlite_rejects_invalid_writer_limits_before_creating_a_database() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    for (name, options) in [
        (
            "no-queue.db",
            SqliteOptions {
                queue_capacity: 0,
                ..SqliteOptions::default()
            },
        ),
        (
            "no-batch.db",
            SqliteOptions {
                batch_size: 0,
                ..SqliteOptions::default()
            },
        ),
    ] {
        let database = fixture.path(name);
        assert!(telemetry::init_sqlite(&database, options).is_err());
        assert!(!database.exists(), "invalid limits created {database:?}");
    }
}

#[test]
fn sqlite_zero_sampling_never_creates_or_opens_the_destination() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let database = fixture.path("sampled-out.db");
    let options = SqliteOptions {
        sample_per_million: 0,
        ..SqliteOptions::default()
    };
    telemetry::init_sqlite(&database, options).unwrap();
    {
        let mut span = telemetry::span("not_recorded");
        span.attribute_lazy("expensive", || {
            panic!("sampled-out attributes must remain lazy")
        });
        span.metric("bytes", 1);
    }
    telemetry::flush().unwrap();
    telemetry::disable().unwrap();
    assert!(!database.exists());
}

#[test]
fn sqlite_retention_preserves_live_retained_contexts_in_the_same_database() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let database = fixture.path("retention.db");
    let options = SqliteOptions {
        max_sessions: 1,
        ..SqliteOptions::default()
    };
    telemetry::init_sqlite(&database, options.clone()).unwrap();
    let retained = {
        let _root = telemetry::span("retained_root");
        telemetry::context().unwrap()
    };
    retained.flush().unwrap();
    telemetry::init_sqlite(&database, options).unwrap();
    drop(telemetry::span("replacement_root"));
    telemetry::disable().unwrap();
    drop(telemetry::linked_span("late_child", Some(&retained)));
    retained.flush().unwrap();
    let connection = reader(&database);
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans child JOIN spans parent ON child.trace_id = parent.trace_id AND child.parent_span_id = parent.span_id WHERE child.name = 'late_child' AND parent.name = 'retained_root'"
        ),
        1
    );
    drop(connection);
    drop(retained);
}

#[test]
fn sqlite_completed_session_retention_prunes_records_and_their_details_together() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let database = fixture.path("retention-complete.db");
    let options = SqliteOptions {
        max_sessions: 1,
        ..SqliteOptions::default()
    };
    telemetry::init_sqlite(&database, options.clone()).unwrap();
    {
        let mut span = telemetry::span("old_session");
        span.attribute("old.attribute", "obsolete");
        span.metric("old.metric", 1);
    }
    telemetry::disable().unwrap();
    telemetry::init_sqlite(&database, options).unwrap();
    drop(telemetry::span("new_session"));
    telemetry::disable().unwrap();
    let connection = reader(&database);
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM sessions"), 1);
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM spans WHERE name = 'new_session'"
        ),
        1
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM attributes WHERE key = 'old.attribute'"
        ),
        0
    );
    assert_eq!(
        count(
            &connection,
            "SELECT COUNT(*) FROM metrics WHERE key = 'old.metric'"
        ),
        0
    );
}

#[test]
fn sqlite_record_limit_is_an_observable_exporter_error_not_a_silent_drop() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let database = fixture.path("record-limit.db");
    let options = SqliteOptions {
        max_records_per_session: 2,
        batch_size: 1,
        ..SqliteOptions::default()
    };
    telemetry::init_sqlite(&database, options).unwrap();
    {
        let span = telemetry::span("bounded");
        for _ in 0..4 {
            span.event("progress", &[("phase", "transfer")], &[]);
        }
    }
    let root = fixture.directory.path();
    let context = msync::compiler::CompileContext {
        pwd: root.to_owned(),
        source_file: None,
    };
    let compiled = msync::pipeline::compile(
        &mut msync::compiler::structured::Session::new(),
        "\"publication survives exporter failure\" -> output\n",
        &context,
        &[],
    )
    .unwrap();
    msync::execution::execute(
        &compiled.program,
        &msync::execution::Options::new(root.join("scratch")),
    )
    .unwrap();
    assert_eq!(
        fs::read(root.join("output")).unwrap(),
        b"publication survives exporter failure"
    );
    let error = telemetry::flush().unwrap_err();
    assert!(
        error.contains("record") && error.contains("limit"),
        "{error}"
    );
    assert!(telemetry::take_error().is_some());
    assert!(count(&reader(&database), "SELECT COUNT(*) FROM records") <= 2);
}

#[test]
fn sqlite_logical_database_budget_reports_full_without_corrupting_active_traces() {
    let _exclusive = SESSION_LOCK
        .lock()
        .unwrap_or_else(|error| error.into_inner());
    let fixture = Fixture::new();
    let database = fixture.path("bounded-pages.db");
    let options = SqliteOptions {
        max_database_bytes: 1024 * 1024,
        batch_size: 1,
        queue_capacity: 2,
        ..SqliteOptions::default()
    };
    telemetry::init_sqlite(&database, options).unwrap();
    let detail = "diagnostic context ".repeat(1024);
    {
        let span = telemetry::span("bounded_database");
        for index in 0..256 {
            span.event(
                "diagnostic.context",
                &[("detail", &detail)],
                &[("index", index)],
            );
            if telemetry::take_error().is_some() {
                break;
            }
        }
    }
    let error = telemetry::flush().unwrap_err();
    assert!(
        error.contains("Full") || error.contains("full") || error.contains("max_database_bytes"),
        "{error}"
    );
    let connection = reader(&database);
    assert_eq!(
        connection
            .query_row("PRAGMA integrity_check", [], |row| row.get::<_, String>(0))
            .unwrap(),
        "ok"
    );
    assert_eq!(count(&connection, "SELECT COUNT(*) FROM sessions"), 1);
    let pages = count(&connection, "PRAGMA page_count");
    let page_size = count(&connection, "PRAGMA page_size");
    assert!(pages * page_size <= 1024 * 1024);
}

#[test]
fn sqlite_export_releases_read_transactions_before_a_slow_output_writer() {
    let _exclusive = SESSION_LOCK.lock().unwrap();
    let fixture = Fixture::new();
    let database = fixture.path("export-reader.db");
    telemetry::init_sqlite(&database, SqliteOptions::default()).unwrap();
    {
        let root = telemetry::span("export-source");
        for index in 0..300 {
            root.event("phase", &[], &[("index", index)]);
        }
    }
    telemetry::flush().unwrap();

    struct CheckingWriter {
        database: PathBuf,
        bytes: Vec<u8>,
        checked: bool,
    }

    impl std::io::Write for CheckingWriter {
        fn write(&mut self, bytes: &[u8]) -> std::io::Result<usize> {
            if !self.checked {
                let connection = Connection::open(&self.database).unwrap();
                connection.busy_timeout(std::time::Duration::ZERO).unwrap();
                connection
                    .execute("UPDATE sessions SET exporter_error='concurrent writer'", [])
                    .unwrap();
                let busy: i64 = connection
                    .query_row("PRAGMA wal_checkpoint(TRUNCATE)", [], |row| row.get(0))
                    .unwrap();
                assert_eq!(
                    busy, 0,
                    "export must not pin a reader while output can block"
                );
                self.checked = true;
            }
            self.bytes.extend_from_slice(bytes);
            Ok(bytes.len())
        }

        fn flush(&mut self) -> std::io::Result<()> {
            Ok(())
        }
    }

    let mut output = CheckingWriter {
        database: database.clone(),
        bytes: Vec::new(),
        checked: false,
    };
    telemetry::query::export_jsonl(&database, None, &mut output).unwrap();
    let exported = std::str::from_utf8(&output.bytes).unwrap();
    assert_eq!(exported.lines().count(), 301);
    assert!(output.checked);
    assert!(
        exported
            .lines()
            .all(|line| serde_json::from_str::<serde_json::Value>(line).is_ok())
    );
}
