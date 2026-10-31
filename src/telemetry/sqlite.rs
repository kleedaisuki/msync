//! Bounded, transactional storage for the existing telemetry record vocabulary.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, mpsc};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

use rusqlite::{Connection, OpenFlags, Transaction, TransactionBehavior, params};
use serde::Deserialize;

pub(super) const APPLICATION_ID: i64 = 0x4d53_594e;
pub(super) const SCHEMA_VERSION: i64 = 1;

#[derive(Clone, Debug, serde::Deserialize, serde::Serialize)]
#[serde(default, deny_unknown_fields)]
pub struct Options {
    pub queue_capacity: usize,
    pub batch_size: usize,
    pub flush_interval_ms: u64,
    pub busy_timeout_ms: u64,
    pub max_sessions: usize,
    pub max_records_per_session: usize,
    pub max_database_bytes: u64,
    pub synchronous_full: bool,
    pub sample_per_million: u32,
}

impl Default for Options {
    fn default() -> Self {
        Self {
            queue_capacity: 4096,
            batch_size: 128,
            flush_interval_ms: 100,
            busy_timeout_ms: 1000,
            max_sessions: 100,
            max_records_per_session: 1_000_000,
            max_database_bytes: 256 * 1024 * 1024,
            synchronous_full: false,
            sample_per_million: 1_000_000,
        }
    }
}

impl Options {
    pub fn validate(&self) -> Result<(), String> {
        self.validate_keyed().map_err(|(_, message)| message)
    }

    pub fn validate_keyed(&self) -> Result<(), (&'static str, String)> {
        if self.queue_capacity == 0 || self.queue_capacity > 1_000_000 {
            return Err((
                "queue_capacity",
                "SQLite telemetry queue_capacity must be between 1 and 1000000".into(),
            ));
        }
        if self.batch_size == 0 || self.batch_size > 65_536 {
            return Err((
                "batch_size",
                "SQLite telemetry batch_size must be between 1 and 65536".into(),
            ));
        }
        if self.flush_interval_ms == 0
            || self.flush_interval_ms > 86_400_000
            || Instant::now()
                .checked_add(Duration::from_millis(self.flush_interval_ms))
                .is_none()
        {
            return Err((
                "flush_interval_ms",
                "SQLite telemetry flush_interval_ms must be between 1 and 86400000".into(),
            ));
        }
        if self.busy_timeout_ms > i32::MAX as u64 {
            return Err((
                "busy_timeout_ms",
                "SQLite telemetry busy_timeout_ms must fit a signed 32-bit integer".into(),
            ));
        }
        if self.max_sessions == 0 || self.max_sessions > i64::MAX as usize {
            return Err((
                "max_sessions",
                "SQLite telemetry max_sessions must be a positive signed integer".into(),
            ));
        }
        if self.max_records_per_session == 0 || self.max_records_per_session > i64::MAX as usize {
            return Err((
                "max_records_per_session",
                "SQLite telemetry max_records_per_session must be a positive signed integer".into(),
            ));
        }
        if self.max_database_bytes < 1024 * 1024 || self.max_database_bytes > i64::MAX as u64 {
            return Err(("max_database_bytes", "SQLite telemetry max_database_bytes must be between 1048576 and the signed 64-bit maximum".into()));
        }
        if self.sample_per_million > 1_000_000 {
            return Err((
                "sample_per_million",
                "SQLite telemetry sample_per_million must not exceed 1000000".into(),
            ));
        }
        Ok(())
    }
}

type Failure = Arc<Mutex<Option<String>>>;
type Reply = mpsc::SyncSender<Result<(), String>>;

enum Message {
    Record(String),
    Flush(Reply),
    Close(Reply),
}

pub(super) struct Writer {
    sender: mpsc::SyncSender<Message>,
    worker: Option<JoinHandle<()>>,
    failure: Failure,
}

impl Writer {
    pub(super) fn open(path: &Path, trace_id: u128, options: &Options) -> Result<Self, String> {
        options.validate()?;
        if path.as_os_str().is_empty() || path == Path::new(":memory:") {
            return Err("SQLite telemetry requires a persistent database filename".into());
        }
        let (sender, receiver) = mpsc::sync_channel(options.queue_capacity);
        let (ready, startup) = mpsc::sync_channel(1);
        let failure = Arc::new(Mutex::new(None));
        let shared = failure.clone();
        let path = path.to_owned();
        let options = options.clone();
        let worker = thread::Builder::new()
            .name("msync-telemetry-sqlite".into())
            .spawn(move || run(path, trace_id, options, receiver, ready, shared))
            .map_err(|error| format!("cannot start SQLite telemetry worker: {error}"))?;
        match startup.recv() {
            Ok(Ok(())) => Ok(Self {
                sender,
                worker: Some(worker),
                failure,
            }),
            result => {
                let _ = worker.join();
                match result {
                    Ok(Err(error)) => Err(error),
                    _ => Err("SQLite telemetry worker exited during initialization".into()),
                }
            }
        }
    }

    pub(super) fn write(&self, line: &str) -> Result<(), String> {
        if self.worker.is_none() {
            return Err("SQLite telemetry writer is closed".into());
        }
        if let Some(error) = self.failure() {
            return Err(error);
        }
        self.sender
            .send(Message::Record(line.to_owned()))
            .map_err(|_| self.worker_error())
    }

    pub(super) fn flush(&self) -> Result<(), String> {
        if self.worker.is_none() {
            return self.failure().map_or(Ok(()), Err);
        }
        let (sender, receiver) = mpsc::sync_channel(1);
        self.sender
            .send(Message::Flush(sender))
            .map_err(|_| self.worker_error())?;
        receiver.recv().map_err(|_| self.worker_error())?
    }

    pub(super) fn close(&mut self) -> Result<(), String> {
        let Some(worker) = self.worker.take() else {
            return self.failure().map_or(Ok(()), Err);
        };
        let (sender, receiver) = mpsc::sync_channel(1);
        let result = self
            .sender
            .send(Message::Close(sender))
            .map_err(|_| self.worker_error())
            .and_then(|()| receiver.recv().map_err(|_| self.worker_error()))
            .and_then(|result| result);
        if let Err(error) = result {
            remember(&self.failure, error);
        }
        if worker.join().is_err() {
            remember(&self.failure, "SQLite telemetry worker panicked".into());
        }
        self.failure().map_or(Ok(()), Err)
    }

    pub(super) fn failure(&self) -> Option<String> {
        self.failure
            .lock()
            .unwrap_or_else(|error| error.into_inner())
            .clone()
    }

    fn worker_error(&self) -> String {
        self.failure()
            .unwrap_or_else(|| "SQLite telemetry worker stopped unexpectedly".into())
    }
}

impl Drop for Writer {
    fn drop(&mut self) {
        let _ = self.close();
    }
}

fn remember(failure: &Failure, error: String) {
    failure
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .get_or_insert(error);
}

fn run(
    path: PathBuf,
    trace_id: u128,
    options: Options,
    receiver: mpsc::Receiver<Message>,
    ready: Reply,
    failure: Failure,
) {
    let mut store = match Store::open(path, trace_id, options) {
        Ok(store) => {
            let _ = ready.send(Ok(()));
            store
        }
        Err(error) => {
            remember(&failure, error.clone());
            let _ = ready.send(Err(error));
            return;
        }
    };
    let interval = Duration::from_millis(store.options.flush_interval_ms);
    let mut deadline = Instant::now() + interval;
    loop {
        let message = if store.pending.is_empty() || failed(&failure) {
            receiver
                .recv()
                .map_err(|_| mpsc::RecvTimeoutError::Disconnected)
        } else {
            receiver.recv_timeout(deadline.saturating_duration_since(Instant::now()))
        };
        match message {
            Ok(Message::Record(line)) => {
                let first = store.pending.is_empty();
                let result = if failed(&failure) {
                    Ok(())
                } else {
                    store.push(&line)
                };
                if let Err(error) = result {
                    remember(&failure, error);
                }
                if first {
                    deadline = Instant::now() + interval;
                }
            }
            Ok(Message::Flush(reply)) => {
                flush(&mut store, &failure);
                let result = failure
                    .lock()
                    .unwrap_or_else(|error| error.into_inner())
                    .clone()
                    .map_or(Ok(()), Err);
                let _ = reply.send(result);
                deadline = Instant::now() + interval;
            }
            Ok(Message::Close(reply)) => {
                close(&mut store, &failure);
                let result = failure
                    .lock()
                    .unwrap_or_else(|error| error.into_inner())
                    .clone()
                    .map_or(Ok(()), Err);
                let _ = reply.send(result);
                return;
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                close(&mut store, &failure);
                return;
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                flush(&mut store, &failure);
                deadline = Instant::now() + interval;
            }
        }
        if !store.pending.is_empty() && !failed(&failure) && Instant::now() >= deadline {
            flush(&mut store, &failure);
            deadline = Instant::now() + interval;
        }
    }
}

fn failed(failure: &Failure) -> bool {
    failure
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .is_some()
}

fn flush(store: &mut Store, failure: &Failure) {
    if let Err(error) = store.flush() {
        remember(failure, error);
    }
}

fn close(store: &mut Store, failure: &Failure) {
    flush(store, failure);
    let error = failure
        .lock()
        .unwrap_or_else(|error| error.into_inner())
        .clone();
    if let Err(error) = store.close(error.as_deref()) {
        remember(failure, error);
    }
}

struct Store {
    connection: Connection,
    path: PathBuf,
    trace_id: String,
    options: Options,
    pending: Vec<Record>,
    records: usize,
}

impl Store {
    fn open(path: PathBuf, trace_id: u128, options: Options) -> Result<Self, String> {
        let mut connection = Connection::open_with_flags(
            &path,
            OpenFlags::SQLITE_OPEN_READ_WRITE
                | OpenFlags::SQLITE_OPEN_CREATE
                | OpenFlags::SQLITE_OPEN_NO_MUTEX,
        )
        .map_err(|error| sql_error("open", &path, error))?;
        connection
            .busy_timeout(Duration::from_millis(options.busy_timeout_ms))
            .map_err(|error| sql_error("configure busy timeout for", &path, error))?;
        configure(&mut connection, &path, &options)?;
        let trace_id = format!("{trace_id:032x}");
        connection
            .execute(
                "INSERT INTO sessions(trace_id, process_id, started_unix_ns) VALUES (?1, ?2, ?3)",
                params![trace_id, i64::from(std::process::id()), timestamp()],
            )
            .map_err(|error| sql_error("start session in", &path, error))?;
        let store = Self {
            connection,
            path,
            trace_id,
            options,
            pending: Vec::new(),
            records: 0,
        };
        store.retain()?;
        Ok(store)
    }

    fn push(&mut self, line: &str) -> Result<(), String> {
        if self.records + self.pending.len() >= self.options.max_records_per_session {
            self.flush()?;
            return Err(format!(
                "SQLite telemetry record limit ({}) reached for session {} in {}",
                self.options.max_records_per_session,
                self.trace_id,
                self.path.display()
            ));
        }
        let record: Record = serde_json::from_str(line)
            .map_err(|error| format!("cannot decode internal telemetry record: {error}"))?;
        record.validate(&self.trace_id)?;
        self.pending.push(record);
        if self.pending.len() >= self.options.batch_size {
            self.flush()?;
        }
        Ok(())
    }

    fn flush(&mut self) -> Result<(), String> {
        if self.pending.is_empty() {
            return Ok(());
        }
        let transaction = self
            .connection
            .transaction()
            .map_err(|error| sql_error("begin telemetry batch in", &self.path, error))?;
        for record in &self.pending {
            insert(&transaction, record)
                .map_err(|error| sql_error("insert telemetry record into", &self.path, error))?;
        }
        transaction
            .commit()
            .map_err(|error| sql_error("commit telemetry batch in", &self.path, error))?;
        self.records += self.pending.len();
        self.pending.clear();
        Ok(())
    }

    fn close(&mut self, failure: Option<&str>) -> Result<(), String> {
        let transaction = self
            .connection
            .transaction()
            .map_err(|error| sql_error("begin session closure in", &self.path, error))?;
        transaction
            .execute(
                "UPDATE sessions SET ended_unix_ns=?1,status=?2,exporter_error=?3 WHERE trace_id=?4",
                params![timestamp(), if failure.is_some() { "error" } else { "complete" }, failure, self.trace_id],
            )
            .map_err(|error| sql_error("finish session in", &self.path, error))?;
        retain(&transaction, self.options.max_sessions)
            .map_err(|error| sql_error("apply session retention to", &self.path, error))?;
        transaction
            .commit()
            .map_err(|error| sql_error("commit session closure in", &self.path, error))?;
        self.connection
            .execute_batch("PRAGMA wal_checkpoint(PASSIVE)")
            .map_err(|error| sql_error("checkpoint", &self.path, error))
    }

    fn retain(&self) -> Result<(), String> {
        retain(&self.connection, self.options.max_sessions)
            .map_err(|error| sql_error("apply session retention to", &self.path, error))
    }
}

fn retain(connection: &Connection, max_sessions: usize) -> rusqlite::Result<()> {
    connection
        .execute(
            "DELETE FROM sessions WHERE trace_id IN (
                SELECT trace_id FROM sessions WHERE ended_unix_ns IS NOT NULL
                ORDER BY ended_unix_ns DESC, trace_id DESC LIMIT -1 OFFSET ?1
            )",
            [max_sessions as i64],
        )
        .map(|_| ())
}

fn configure(connection: &mut Connection, path: &Path, options: &Options) -> Result<(), String> {
    // Claim a new database only after acquiring the writer lock: another process
    // may have created the complete schema after this connection opened it.
    let transaction = connection
        .transaction_with_behavior(TransactionBehavior::Immediate)
        .map_err(|error| sql_error("lock schema initialization in", path, error))?;
    let application: i64 = transaction
        .pragma_query_value(None, "application_id", |row| row.get(0))
        .map_err(|error| sql_error("read application identity from", path, error))?;
    let version: i64 = transaction
        .pragma_query_value(None, "user_version", |row| row.get(0))
        .map_err(|error| sql_error("read schema version from", path, error))?;
    let empty: bool = transaction
        .query_row(
            "SELECT NOT EXISTS(SELECT 1 FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%')",
            [],
            |row| row.get(0),
        )
        .map_err(|error| sql_error("inspect schema in", path, error))?;
    if application != APPLICATION_ID && !(application == 0 && version == 0 && empty) {
        return Err(format!(
            "not an msync telemetry database: {}",
            path.display()
        ));
    }
    if application == APPLICATION_ID && version != SCHEMA_VERSION {
        return Err(format!(
            "unsupported SQLite telemetry schema version {version} in {}; expected {SCHEMA_VERSION}",
            path.display()
        ));
    }
    page_limit(&transaction, path, options.max_database_bytes)?;
    if application == 0 {
        transaction
            .execute_batch(SCHEMA)
            .map_err(|error| sql_error("create schema in", path, error))?;
        transaction
            .pragma_update(None, "application_id", APPLICATION_ID)
            .and_then(|()| transaction.pragma_update(None, "user_version", SCHEMA_VERSION))
            .map_err(|error| sql_error("mark schema identity in", path, error))?;
    }
    transaction
        .commit()
        .map_err(|error| sql_error("commit schema initialization in", path, error))?;
    connection
        .execute_batch(if options.synchronous_full {
            "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA wal_autocheckpoint=1000;"
        } else {
            "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA wal_autocheckpoint=1000;"
        })
        .map_err(|error| sql_error("configure", path, error))?;
    connection
        .pragma_update(
            None,
            "journal_size_limit",
            options.max_database_bytes as i64,
        )
        .map_err(|error| sql_error("configure WAL recycling for", path, error))?;
    Ok(())
}

fn page_limit(connection: &Connection, path: &Path, bytes: u64) -> Result<(), String> {
    let page_size: i64 = connection
        .pragma_query_value(None, "page_size", |row| row.get(0))
        .map_err(|error| sql_error("read page size from", path, error))?;
    let page_count: i64 = connection
        .pragma_query_value(None, "page_count", |row| row.get(0))
        .map_err(|error| sql_error("read page count from", path, error))?;
    if page_size <= 0 || page_count < 0 {
        return Err(format!(
            "invalid SQLite telemetry page metadata in {}",
            path.display()
        ));
    }
    let max_pages = (bytes / page_size as u64) as i64;
    if page_count > max_pages {
        return Err(format!(
            "SQLite telemetry database {} exceeds max_database_bytes {bytes} ({} pages of {page_size} bytes)",
            path.display(),
            page_count,
        ));
    }
    connection
        .pragma_update(None, "max_page_count", max_pages)
        .map_err(|error| sql_error("set page limit for", path, error))
}

fn sql_error(action: &str, path: &Path, error: rusqlite::Error) -> String {
    match error {
        rusqlite::Error::SqliteFailure(code, detail) => format!(
            "cannot {action} SQLite telemetry database {}: {} (SQLite {:?}, extended code {})",
            path.display(),
            detail.unwrap_or_else(|| code.to_string()),
            code.code,
            code.extended_code
        ),
        error => format!(
            "cannot {action} SQLite telemetry database {}: {error}",
            path.display()
        ),
    }
}

fn timestamp() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos()
        .min(i64::MAX as u128) as i64
}

#[derive(Deserialize)]
struct Record {
    schema_version: u32,
    kind: String,
    trace_id: String,
    span_id: String,
    parent_span_id: Option<String>,
    name: String,
    start_unix_ns: Option<i64>,
    duration_ns: Option<i64>,
    timestamp_unix_ns: Option<i64>,
    status: Option<String>,
    completion: Option<String>,
    #[serde(default)]
    attributes: BTreeMap<String, String>,
    #[serde(default)]
    metrics: BTreeMap<String, u64>,
    #[serde(default)]
    error_chain: Vec<Cause>,
    #[serde(default)]
    error_chain_truncated: bool,
}

#[derive(Deserialize)]
struct Cause {
    message: String,
    kind: Option<String>,
    raw_os_error: Option<i32>,
}

impl Record {
    fn validate(&self, trace_id: &str) -> Result<(), String> {
        if self.schema_version != 1 || self.trace_id != trace_id {
            return Err("SQLite telemetry record has incompatible schema or trace identity".into());
        }
        let valid = match self.kind.as_str() {
            "span" => {
                self.start_unix_ns.is_some() && self.duration_ns.is_some() && self.status.is_some()
            }
            "event" => self.timestamp_unix_ns.is_some(),
            _ => false,
        };
        if !valid {
            return Err("SQLite telemetry record has an invalid kind or incomplete timing".into());
        }
        Ok(())
    }
}

fn insert(transaction: &Transaction<'_>, record: &Record) -> rusqlite::Result<()> {
    transaction.prepare_cached(
        "INSERT INTO records(trace_id,kind,span_id,parent_span_id,name,start_unix_ns,duration_ns,timestamp_unix_ns,status,completion,error_chain_truncated)
         VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11)",
    )?.execute(params![
        record.trace_id, record.kind, record.span_id, record.parent_span_id, record.name,
        record.start_unix_ns, record.duration_ns, record.timestamp_unix_ns, record.status,
        record.completion, record.error_chain_truncated,
    ])?;
    let id = transaction.last_insert_rowid();
    for (key, value) in &record.attributes {
        transaction
            .prepare_cached("INSERT INTO attributes(record_id,key,value) VALUES (?1,?2,?3)")?
            .execute(params![id, key, value])?;
    }
    for (key, value) in &record.metrics {
        transaction
            .prepare_cached(
                "INSERT INTO metrics(record_id,key,value,value_int) VALUES (?1,?2,?3,?4)",
            )?
            .execute(params![
                id,
                key,
                value.to_string(),
                i64::try_from(*value).ok()
            ])?;
    }
    for (depth, cause) in record.error_chain.iter().enumerate() {
        transaction.prepare_cached("INSERT INTO error_causes(record_id,depth,message,kind,raw_os_error) VALUES (?1,?2,?3,?4,?5)")?
            .execute(params![id, depth as i64, cause.message, cause.kind, cause.raw_os_error])?;
    }
    Ok(())
}

const SCHEMA: &str = r#"
CREATE TABLE sessions (
    trace_id TEXT PRIMARY KEY,
    process_id INTEGER NOT NULL,
    started_unix_ns INTEGER NOT NULL,
    ended_unix_ns INTEGER,
    status TEXT NOT NULL DEFAULT 'active',
    exporter_error TEXT
) STRICT;
CREATE TABLE records (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    trace_id TEXT NOT NULL REFERENCES sessions(trace_id) ON DELETE CASCADE,
    kind TEXT NOT NULL CHECK(kind IN ('span','event')),
    span_id TEXT NOT NULL,
    parent_span_id TEXT,
    name TEXT NOT NULL,
    start_unix_ns INTEGER,
    duration_ns INTEGER,
    timestamp_unix_ns INTEGER,
    status TEXT,
    completion TEXT,
    error_chain_truncated INTEGER NOT NULL DEFAULT 0
) STRICT;
CREATE UNIQUE INDEX span_identity ON records(trace_id,span_id) WHERE kind='span';
CREATE INDEX record_owner ON records(trace_id,span_id,id);
CREATE INDEX record_parent ON records(trace_id,parent_span_id);
CREATE INDEX record_phase_duration ON records(name,duration_ns DESC);
CREATE INDEX record_time ON records(trace_id,COALESCE(start_unix_ns,timestamp_unix_ns),id);
CREATE INDEX record_kind_duration ON records(kind,duration_ns DESC,id);
CREATE INDEX record_kind_time ON records(kind,timestamp_unix_ns,id);
CREATE TABLE attributes (
    record_id INTEGER NOT NULL REFERENCES records(id) ON DELETE CASCADE,
    key TEXT NOT NULL,
    value TEXT NOT NULL,
    PRIMARY KEY(record_id,key)
) STRICT;
CREATE INDEX attribute_lookup ON attributes(key,value,record_id);
CREATE TABLE metrics (
    record_id INTEGER NOT NULL REFERENCES records(id) ON DELETE CASCADE,
    key TEXT NOT NULL,
    value TEXT NOT NULL,
    value_int INTEGER,
    PRIMARY KEY(record_id,key)
) STRICT;
CREATE INDEX metric_lookup ON metrics(key,value_int,record_id);
CREATE TABLE error_causes (
    record_id INTEGER NOT NULL REFERENCES records(id) ON DELETE CASCADE,
    depth INTEGER NOT NULL,
    message TEXT NOT NULL,
    kind TEXT,
    raw_os_error INTEGER,
    PRIMARY KEY(record_id,depth)
) STRICT;
CREATE VIEW spans AS SELECT * FROM records WHERE kind='span';
CREATE VIEW events AS SELECT * FROM records WHERE kind='event';
"#;
