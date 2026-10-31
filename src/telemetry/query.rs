//! Read-only, indexed inspection and lossless JSONL export of local telemetry.

use std::collections::BTreeMap;
use std::io::Write;
use std::path::Path;

use rusqlite::{Connection, OpenFlags};
use serde::Serialize;
use serde_json::{Map, Value};

use super::sqlite::{APPLICATION_ID, SCHEMA_VERSION};

const DEFAULT_LIMIT: usize = 100;
const MAX_LIMIT: usize = 10_000;

#[derive(Debug, Serialize)]
pub struct Summary {
    pub sessions: u64,
    pub spans: u64,
    pub events: u64,
    pub error_spans: u64,
    pub cancelled_spans: u64,
    /// Durations overlap; these totals are work measurements, not wall time.
    pub phases: Vec<Phase>,
}

#[derive(Debug, Serialize)]
pub struct Phase {
    pub name: String,
    pub count: u64,
    pub total_duration_ns: u64,
    pub max_duration_ns: u64,
}

#[derive(Debug, Serialize)]
pub struct Session {
    pub trace_id: String,
    pub process_id: u32,
    pub started_unix_ns: i64,
    pub ended_unix_ns: Option<i64>,
    pub status: String,
    pub exporter_error: Option<String>,
    pub outcome: Option<String>,
}

#[derive(Debug)]
struct Record {
    id: i64,
    trace_id: String,
    kind: String,
    span_id: String,
    parent_span_id: Option<String>,
    name: String,
    start_unix_ns: Option<i64>,
    duration_ns: Option<i64>,
    timestamp_unix_ns: Option<i64>,
    status: Option<String>,
    completion: Option<String>,
    error_chain_truncated: bool,
}

fn open(path: &Path) -> Result<Connection, String> {
    let connection = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY)
        .map_err(|error| format!("cannot read telemetry database {}: {error}", path.display()))?;
    connection
        .busy_timeout(std::time::Duration::from_millis(1000))
        .map_err(sql_error)?;
    let application: i64 = connection
        .query_row("PRAGMA application_id", [], |row| row.get(0))
        .map_err(sql_error)?;
    let version: i64 = connection
        .query_row("PRAGMA user_version", [], |row| row.get(0))
        .map_err(sql_error)?;
    if application != APPLICATION_ID || version != SCHEMA_VERSION {
        return Err(format!(
            "unsupported telemetry database identity/schema: application_id={application}, version={version}; expected application_id={APPLICATION_ID}, version={SCHEMA_VERSION}"
        ));
    }
    Ok(connection)
}

fn sql_error(error: rusqlite::Error) -> String {
    format!("cannot query telemetry database: {error}")
}

/// Lists recent recording sessions, including incomplete and exporter-failed runs.
pub fn sessions(path: &Path, limit: usize) -> Result<Vec<Session>, String> {
    check_limit(limit)?;
    let limit = i64::try_from(limit).map_err(|error| format!("invalid query limit: {error}"))?;
    let connection = open(path)?;
    let mut statement = connection.prepare(
        "SELECT s.trace_id,s.process_id,s.started_unix_ns,s.ended_unix_ns,s.status,s.exporter_error,
            (SELECT CASE
                WHEN MAX(r.status='panic') THEN 'panic'
                WHEN MAX(r.status='error') THEN 'error'
                WHEN MAX(r.status='cancelled') THEN 'cancelled'
                WHEN COUNT(*)>0 THEN 'ok' ELSE NULL END
             FROM spans r WHERE r.trace_id=s.trace_id AND r.parent_span_id IS NULL)
         FROM sessions s ORDER BY s.started_unix_ns DESC,s.trace_id DESC LIMIT ?1"
    ).map_err(sql_error)?;
    statement
        .query_map([limit], |row| {
            Ok(Session {
                trace_id: row.get(0)?,
                process_id: row.get(1)?,
                started_unix_ns: row.get(2)?,
                ended_unix_ns: row.get(3)?,
                status: row.get(4)?,
                exporter_error: row.get(5)?,
                outcome: row.get(6)?,
            })
        })
        .map_err(sql_error)?
        .collect::<Result<Vec<_>, _>>()
        .map_err(sql_error)
}

fn check_limit(limit: usize) -> Result<(), String> {
    if limit == 0 || limit > MAX_LIMIT {
        return Err(format!(
            "telemetry query limit must be between 1 and {MAX_LIMIT}"
        ));
    }
    Ok(())
}

/// Summarizes completed spans and lifecycle events without modifying the database.
pub fn summary(path: &Path, trace_id: Option<&str>) -> Result<Summary, String> {
    let connection = open(path)?;
    let filter = trace_id.map_or("", |_| " WHERE trace_id=?1");
    let count = |table: &str| -> Result<u64, String> {
        let query = format!("SELECT COUNT(*) FROM {table}{filter}");
        let mut statement = connection.prepare(&query).map_err(sql_error)?;
        statement
            .query_row(rusqlite::params_from_iter(trace_id), |row| unsigned(row, 0))
            .map_err(sql_error)
    };
    let condition = trace_id.map_or("", |_| " AND trace_id=?1");
    let mut statement = connection
        .prepare(&format!(
            "SELECT COUNT(CASE WHEN status IN ('error','panic') THEN 1 END), COUNT(CASE WHEN status='cancelled' THEN 1 END) FROM spans WHERE 1=1{condition}"
        ))
        .map_err(sql_error)?;
    let (error_spans, cancelled_spans) = statement
        .query_row(rusqlite::params_from_iter(trace_id), |row| {
            Ok((unsigned(row, 0)?, unsigned(row, 1)?))
        })
        .map_err(sql_error)?;
    let mut statement = connection
        .prepare(&format!(
            "SELECT name,COUNT(*),TOTAL(duration_ns),MAX(duration_ns) FROM spans{filter} GROUP BY name ORDER BY TOTAL(duration_ns) DESC,name LIMIT 25"
        ))
        .map_err(sql_error)?;
    let rows = statement
        .query_map(rusqlite::params_from_iter(trace_id), |row| {
            Ok(Phase {
                name: row.get(0)?,
                count: unsigned(row, 1)?,
                total_duration_ns: row.get::<_, f64>(2)?.min(u64::MAX as f64) as u64,
                max_duration_ns: unsigned(row, 3)?,
            })
        })
        .map_err(sql_error)?;
    let phases = rows.collect::<Result<Vec<_>, _>>().map_err(sql_error)?;
    Ok(Summary {
        sessions: count("sessions")?,
        spans: count("spans")?,
        events: count("events")?,
        error_spans,
        cancelled_spans,
        phases,
    })
}

fn unsigned(row: &rusqlite::Row<'_>, index: usize) -> rusqlite::Result<u64> {
    let value: i64 = row.get(index)?;
    u64::try_from(value).map_err(|_| rusqlite::Error::IntegralValueOutOfRange(index, value))
}

fn record(row: &rusqlite::Row<'_>) -> rusqlite::Result<Record> {
    Ok(Record {
        id: row.get(0)?,
        trace_id: row.get(1)?,
        kind: row.get(2)?,
        span_id: row.get(3)?,
        parent_span_id: row.get(4)?,
        name: row.get(5)?,
        start_unix_ns: row.get(6)?,
        duration_ns: row.get(7)?,
        timestamp_unix_ns: row.get(8)?,
        status: row.get(9)?,
        completion: row.get(10)?,
        error_chain_truncated: row.get(11)?,
    })
}

const RECORD_COLUMNS: &str = "id,trace_id,kind,span_id,parent_span_id,name,start_unix_ns,duration_ns,timestamp_unix_ns,status,completion,error_chain_truncated";

fn attributes(connection: &Connection, id: i64) -> Result<BTreeMap<String, String>, String> {
    let mut statement = connection
        .prepare_cached("SELECT key,value FROM attributes WHERE record_id=?1 ORDER BY key")
        .map_err(sql_error)?;
    statement
        .query_map([id], |row| Ok((row.get(0)?, row.get(1)?)))
        .map_err(sql_error)?
        .collect::<Result<BTreeMap<_, _>, _>>()
        .map_err(sql_error)
}

fn metrics(connection: &Connection, id: i64) -> Result<BTreeMap<String, u64>, String> {
    let mut statement = connection
        .prepare_cached("SELECT key,value FROM metrics WHERE record_id=?1 ORDER BY key")
        .map_err(sql_error)?;
    let rows = statement
        .query_map([id], |row| {
            Ok((row.get::<_, String>(0)?, row.get::<_, String>(1)?))
        })
        .map_err(sql_error)?;
    rows.map(|row| {
        let (key, value) = row.map_err(sql_error)?;
        let value = value.parse::<u64>().map_err(|error| {
            format!("invalid unsigned telemetry metric '{key}' in record {id}: {error}")
        })?;
        Ok((key, value))
    })
    .collect()
}

fn errors(connection: &Connection, id: i64) -> Result<Vec<Value>, String> {
    let mut statement = connection
        .prepare_cached(
            "SELECT message,kind,raw_os_error FROM error_causes WHERE record_id=?1 ORDER BY depth",
        )
        .map_err(sql_error)?;
    statement
        .query_map([id], |row| {
            Ok(serde_json::json!({
                "message": row.get::<_, String>(0)?,
                "kind": row.get::<_, Option<String>>(1)?,
                "raw_os_error": row.get::<_, Option<i64>>(2)?,
            }))
        })
        .map_err(sql_error)?
        .collect::<Result<Vec<_>, _>>()
        .map_err(sql_error)
}

fn value(connection: &Connection, record: Record) -> Result<Value, String> {
    let mut output = Map::new();
    output.insert("schema_version".into(), Value::from(1));
    output.insert("kind".into(), Value::from(record.kind.clone()));
    output.insert("trace_id".into(), Value::from(record.trace_id));
    output.insert("span_id".into(), Value::from(record.span_id));
    output.insert("name".into(), Value::from(record.name));
    output.insert(
        "attributes".into(),
        serde_json::to_value(attributes(connection, record.id)?).map_err(json_error)?,
    );
    output.insert(
        "metrics".into(),
        serde_json::to_value(metrics(connection, record.id)?).map_err(json_error)?,
    );
    if record.kind == "span" {
        output.insert(
            "parent_span_id".into(),
            record.parent_span_id.map_or(Value::Null, Value::from),
        );
        output.insert("start_unix_ns".into(), optional(record.start_unix_ns));
        output.insert("duration_ns".into(), optional(record.duration_ns));
        output.insert(
            "status".into(),
            record.status.map_or(Value::Null, Value::from),
        );
        output.insert(
            "completion".into(),
            record.completion.map_or(Value::Null, Value::from),
        );
    } else {
        output.insert(
            "timestamp_unix_ns".into(),
            optional(record.timestamp_unix_ns),
        );
        let errors = errors(connection, record.id)?;
        if !errors.is_empty() || record.error_chain_truncated {
            output.insert("error_chain".into(), Value::Array(errors));
            output.insert(
                "error_chain_truncated".into(),
                Value::from(record.error_chain_truncated),
            );
        }
    }
    Ok(Value::Object(output))
}

fn optional(value: Option<i64>) -> Value {
    value.map_or(Value::Null, Value::from)
}

fn json_error(error: serde_json::Error) -> String {
    format!("cannot serialize telemetry query: {error}")
}

/// Returns the longest completed spans. Attribute and metric values stay typed.
pub fn spans(path: &Path, trace_id: Option<&str>, limit: usize) -> Result<Vec<Value>, String> {
    records(path, trace_id, None, "span", limit)
}

/// Returns lifecycle/error events, optionally scoped to a particular span owner.
pub fn events(
    path: &Path,
    trace_id: Option<&str>,
    span_id: Option<&str>,
    limit: usize,
) -> Result<Vec<Value>, String> {
    records(path, trace_id, span_id, "event", limit)
}

fn records(
    path: &Path,
    trace_id: Option<&str>,
    span_id: Option<&str>,
    kind: &str,
    limit: usize,
) -> Result<Vec<Value>, String> {
    check_limit(limit)?;
    let connection = open(path)?;
    let mut sql = format!("SELECT {RECORD_COLUMNS} FROM records WHERE kind=?1");
    let mut values = vec![rusqlite::types::Value::Text(kind.to_owned())];
    if let Some(trace_id) = trace_id {
        values.push(trace_id.to_owned().into());
        sql.push_str(&format!(" AND trace_id=?{}", values.len()));
    }
    if let Some(span_id) = span_id {
        values.push(span_id.to_owned().into());
        sql.push_str(&format!(" AND span_id=?{}", values.len()));
    }
    sql.push_str(if kind == "span" {
        " ORDER BY duration_ns DESC,id"
    } else {
        " ORDER BY timestamp_unix_ns,id"
    });
    sql.push_str(&format!(" LIMIT {limit}"));
    let mut statement = connection.prepare(&sql).map_err(sql_error)?;
    statement
        .query_map(rusqlite::params_from_iter(values), record)
        .map_err(sql_error)?
        .map(|row| value(&connection, row.map_err(sql_error)?))
        .collect()
}

/// Exports schema-1 JSONL through bounded read pages, never holding a database
/// read transaction across a potentially blocking output write. New records are
/// excluded by an initial ID cutoff; concurrent retention can remove old pages.
pub fn export_jsonl(
    path: &Path,
    trace_id: Option<&str>,
    output: &mut dyn Write,
) -> Result<(), String> {
    let mut connection = open(path)?;
    let filter = trace_id.map_or("", |_| " WHERE trace_id=?1");
    let cutoff: i64 = connection
        .query_row(
            &format!("SELECT COALESCE(MAX(id),0) FROM records{filter}"),
            rusqlite::params_from_iter(trace_id),
            |row| row.get(0),
        )
        .map_err(sql_error)?;
    let mut after = 0;
    loop {
        let page = export_page(&mut connection, trace_id, after, cutoff)?;
        if page.is_empty() {
            return Ok(());
        }
        for (id, item) in page {
            serde_json::to_writer(&mut *output, &item).map_err(json_error)?;
            output
                .write_all(b"\n")
                .map_err(|error| format!("cannot write telemetry export: {error}"))?;
            after = id;
        }
    }
}

fn export_page(
    connection: &mut Connection,
    trace_id: Option<&str>,
    after: i64,
    cutoff: i64,
) -> Result<Vec<(i64, Value)>, String> {
    let transaction = connection.transaction().map_err(sql_error)?;
    let filter = trace_id.map_or("", |_| " AND trace_id=?3");
    let sql = format!(
        "SELECT {RECORD_COLUMNS} FROM records WHERE id>?1 AND id<=?2{filter} ORDER BY id LIMIT 128"
    );
    let mut parameters = vec![
        rusqlite::types::Value::Integer(after),
        rusqlite::types::Value::Integer(cutoff),
    ];
    parameters.extend(trace_id.map(|trace_id| rusqlite::types::Value::Text(trace_id.to_owned())));
    let page = {
        let mut statement = transaction.prepare(&sql).map_err(sql_error)?;
        statement
            .query_map(rusqlite::params_from_iter(parameters), record)
            .map_err(sql_error)?
            .map(|row| {
                let row = row.map_err(sql_error)?;
                Ok((row.id, value(&transaction, row)?))
            })
            .collect::<Result<Vec<_>, String>>()?
    };
    transaction.commit().map_err(sql_error)?;
    Ok(page)
}

/// CLI integration kept independent of argument parsing and application startup.
pub fn run(arguments: &[String], output: &mut dyn Write) -> Result<(), String> {
    let [command, database, rest @ ..] = arguments else {
        return Err("usage: msync telemetry sessions|summary|spans|events|export DATABASE [--trace-id ID] [--span-id ID] [--limit N]".into());
    };
    let mut trace_id = None;
    let mut span_id = None;
    let mut limit = DEFAULT_LIMIT;
    let mut index = 0;
    while index < rest.len() {
        let argument = &rest[index];
        let value = rest
            .get(index + 1)
            .ok_or_else(|| format!("{argument} requires a value"))?;
        match argument.as_str() {
            "--trace-id" if trace_id.is_none() && command != "sessions" => {
                trace_id = Some(value.as_str())
            }
            "--span-id" if span_id.is_none() && command == "events" => {
                span_id = Some(value.as_str())
            }
            "--limit" if matches!(command.as_str(), "events" | "spans" | "sessions") => {
                limit = value
                    .parse::<usize>()
                    .map_err(|error| format!("invalid telemetry query limit: {error}"))?;
            }
            _ => {
                return Err(format!(
                    "unknown or unsupported telemetry query option '{argument}'"
                ));
            }
        }
        index += 2;
    }
    let database = Path::new(database);
    match command.as_str() {
        "sessions" => {
            serde_json::to_writer(&mut *output, &sessions(database, limit)?).map_err(json_error)?
        }
        "summary" => serde_json::to_writer(&mut *output, &summary(database, trace_id)?)
            .map_err(json_error)?,
        "spans" => serde_json::to_writer(&mut *output, &spans(database, trace_id, limit)?)
            .map_err(json_error)?,
        "events" => {
            serde_json::to_writer(&mut *output, &events(database, trace_id, span_id, limit)?)
                .map_err(json_error)?
        }
        "export" => return export_jsonl(database, trace_id, output),
        _ => {
            return Err(format!(
                "unknown telemetry query '{command}'; choose sessions, summary, spans, events or export"
            ));
        }
    }
    output
        .write_all(b"\n")
        .map_err(|error| format!("cannot write telemetry query: {error}"))
}
