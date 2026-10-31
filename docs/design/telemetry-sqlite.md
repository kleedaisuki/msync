# SQLite telemetry storage

## Ownership and durable contract

The telemetry API retains its established span/context lifecycle and JSONL wire
format. SQLite is an additional structured sink, not a blob archive. One bounded
writer queue and one worker-owned `rusqlite::Connection` belong to each trace
session. Retained contexts retain that sink after global reconfiguration; only
its last owner closes the session. No tracing-disabled path opens a database.

The producer forwards already-rendered JSON records to preserve the existing API
and sinks. The worker decodes records and normalizes them into relational tables:

- `sessions(trace_id, process_id, started_unix_ns, ended_unix_ns)`.
- `records(id, trace_id, kind, span_id, parent_span_id, name, start_unix_ns,
  duration_ns, timestamp_unix_ns, status, completion, error_chain_truncated)`.
- `attributes(record_id, key, value)`.
- `metrics(record_id, key, value, value_int)`: `value` is the exact decimal unsigned
  integer; `value_int` is populated only when the number fits SQLite's signed
  integer domain. Large `u64` values must not silently wrap or become floating-point.
- `error_causes(record_id, depth, message, kind, raw_os_error)`.

`spans` and `events` are views over `records`. Event ownership and span ancestry
use `(trace_id, span_id)`, not record order; children and events can precede their
completed parent span. There is no foreign key requiring the parent span to have
completed already. Record-owned rows use cascading foreign keys. Indexes cover
trace/span ownership, parent linkage, phase duration, timestamp ordering and
attribute/metric lookup.

The database has application ID `0x4d53594e` and schema `user_version = 1`.
Initialization rejects another application's database, a nonempty unclaimed
database, and future schema versions. It never silently adopts arbitrary tables.
The schema marker is not a claim of future migration support: future releases
must explicitly implement migrations before accepting changed schemas.

## Batching, failure and bounds

SQLite WAL permits concurrent readers while the sole writer commits bounded
batches. Defaults are 128 records per transaction, a 100 ms idle flush interval,
a 4096-record producer queue, and a 1000 ms busy timeout. NORMAL synchronization
is the default; FULL is configurable for callers requiring a WAL sync on each
transaction. OFF is not offered as an optimization.

A full queue applies backpressure rather than dropping arbitrary causal records.
A flush is a queue barrier: it waits for all preceding records to commit or
returns the first retained exporter error. The normal shutdown path drains queued
records, marks the session ended, and joins the worker. Destructor failures do not
panic or mask an ongoing file operation; explicit API flush exposes them.

A per-session record cap defaults to one million and raises a visible exporter
error instead of silently producing an incomplete trace. Retention keeps the
newest 100 completed sessions by default, removing their normalized rows through
cascading foreign keys. Rows belonging to active sessions are never evicted merely
to satisfy that target. A crashed process can consequently leave an unfinished
session for diagnosis; this is not a hard global database-size guarantee.
Trace-level sampling is decided before enabling a session by the public API and
keeps or excludes its complete causal graph rather than randomly dropping spans.

## Operational limitations and references

WAL is local-host storage, not a multi-host/network-filesystem protocol. NORMAL
commits are visible to readers after a successful flush and preserve database
consistency; a sudden power failure can lose recent transactions. FULL changes
that synchronization contract, not API visibility semantics. Long-lived read
transactions can delay checkpoint progress; query helpers should use short,
bounded reads and release statements promptly.

- [SQLite WAL](https://www.sqlite.org/wal.html): writer/read concurrency, local-host
  restriction, and checkpoint behavior.
- [SQLite synchronous](https://www.sqlite.org/pragma.html#pragma_synchronous):
  NORMAL versus FULL WAL guarantees.
- [rusqlite Connection](https://docs.rs/rusqlite/latest/rusqlite/struct.Connection.html):
  connection thread ownership, transactions and bounded busy handling.

### Initialization and storage bounds

Schema identity is read while holding `BEGIN IMMEDIATE`, not before taking the
writer lock. Two first-open processes therefore see either an unclaimed empty
database or the fully committed msync schema, never a stale empty-state decision.
Foreign schemas are rejected without changing their journaling mode.

`max_database_bytes` defaults to 256 MiB (minimum 1 MiB) and configures a SQLite
page-count cap. A database already exceeding the chosen page budget is rejected;
future page allocation fails visibly with `SQLITE_FULL`. This also limits growth
caused by abandoned sessions, which are intentionally not pruned as if complete.
The WAL recycling target uses the same byte budget. A long-lived reader can delay
WAL reclamation, so the page cap is not an exact total-file-size promise including
WAL and shared-memory files. Readers must use bounded transactions; operators can
release those readers and checkpoint before expecting space reclamation.

An explicit terminal shutdown drains and joins the writer after the outer CLI
span has ended. It marks `sessions.status` complete or error and records the first
exporter failure in `sessions.exporter_error` if the database remains writable.
Ordinary flush and disable preserve active retained-context sessions; they do not
pretend that an execution which still owns the sink has ended.

Record IDs use SQLite `AUTOINCREMENT`, so retention cannot recycle a deleted
highest ID into a later record. Paged readers can capture a maximum ID and safely
exclude later inserts without holding a long-lived WAL read transaction. This
uses SQLite's sequence ownership instead of a second application-side allocator.

The worker blocks on its queue when no batch is pending. The flush deadline starts
with the first record of a batch, and timed waiting is used only while that batch
needs admission. Disabled, empty and failed sinks therefore do not add a periodic
100 ms background poller; explicit flush and close still wake the worker directly.

Operator query indexes match their actual ordering: `(kind, duration_ns DESC, id)`
serves global slow-span inspection, and `(kind, timestamp_unix_ns, id)` serves
chronological event reads. Trace/owner indexes serve filtered causality joins;
phase-name-first indexes alone cannot avoid sorting a global slow-span query.

## Focused validation

The root-owned native Linux storage/tooling gate recorded in
`.temp/performance-v6/storage-tooling.log` passed 11 SQLite API tests and six real
SQLite CLI journeys. These cover normalized causality/error chains and exact
unsigned metrics, bounded concurrent producers, explicit record/page cap errors,
retained-context reconfiguration, completed-session retention and live-session
preservation, future-schema rejection, zero-sampling non-creation, slow-output
export without a retained read transaction, real macro publication, runtime
failure diagnostics, and read-only query/export commands.

The same gate passed the existing five JSONL integration tests and the independent
fresh-database concurrent-process initialization regression. These results are
local incremental acceptance, not a power-failure, remote-filesystem or Windows
certification. No Cargo command was launched independently by the storage owner.
