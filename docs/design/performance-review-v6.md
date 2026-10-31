# Independent performance and tooling integration review

## Context

This review starts from the complete 0.6 implementation, including uncommitted
language/control-flow changes. The coordinating root freezes that source and its
release executable before optimization. Earlier 0.3–0.5 performance notes are
reusable mechanism/contract evidence, not a substitute timing baseline.

Relevant retained knowledge: [optimization roadmap](optimization-roadmap.md),
[preparation indexing](performance-v6-preparation.md),
[telemetry API/schema](telemetry.md), [startup controls](performance-startup-v3.md),
and [0.6 control review](review-v6.md). Review remains independent of production
editing; executable probes are added only for observed failure paths.

## Initial inherited interface issue

`src/structured/jobs.rs` originally located work by scanning only top-level
ordinary `InstructionKind::Spawn` nodes. `programs()` also omitted typed
`Control::Spawn`. A top-level dynamic executable or argument now emits the latter,
so asynchronous job connectivity and source attribution can lose its owning
instruction. Nested runtime-freshened process IDs additionally require runtime
provenance rather than assuming that a static source process ID is sufficient.
This is routed to the REPL/jobs owner as an interface correction, not a request to
flatten or recompile typed control regions.

## Review grounding

- The [SQLite WAL contract](https://www.sqlite.org/wal.html) still permits only
  one writer and can report `SQLITE_BUSY`; WAL itself is not an application
  contention policy. Long-lived readers can inhibit checkpoint progress. The
  relevant acceptance is concurrent processes writing the same trace database
  plus an independent reader, not merely concurrent threads on one connection.
- [Tokio Notify](https://docs.rs/tokio/latest/tokio/sync/struct.Notify.html)
  stores one permit, not an event count. The documented multi-consumer example
  enables waiters before checking the predicate; the single-consumer pattern has
  different requirements. Review any polling replacement against its actual
  ownership model rather than mechanically adding a `Notify`.
- [LSP 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/)
  uses negotiated position encodings and versioned document synchronization.
  Protocol offsets and the compiler's UTF-8 byte spans are distinct contracts.

Implementation-specific findings and dispositions follow when the new shared
interfaces land. Root owns the Cargo execution window and reported benchmark
comparisons; this review does not independently claim unexecuted platform data.

## SQLite initialization and validation

The initial `configure` implementation read database ownership/schema markers
before starting its schema-creation transaction. Two processes opening one fresh
file could both observe an unclaimed empty database and subsequently race to
create the same tables. The correction is a writer-serialized initialization
transaction with ownership/schema checks repeated under its lock, not masking
arbitrary CREATE errors. `tests/performance_review_v6.rs` starts eight real CLI
writers concurrently against each of three fresh databases and requires every
session to complete with independent trace identities and a healthy database.

The initial public SQLite options and configuration accepted `busy_timeout_ms`
above `i32::MAX`. `rusqlite` 0.41.1's implementation converts to a signed SQLite
millisecond count with `expect("too big")`, causing a worker panic rather than a
source-labeled pre-effect configuration failure. The same review target checks
that 2147483648 ms is rejected before creating the database. Queue/batch policy
upper bounds should likewise have one validation contract across config and API.
Very large idle flush intervals warrant checked platform arithmetic, but a Linux
`Instant` overflow was not reproduced or established by this review.

The selected bundled `libsqlite3-sys` 0.39.1 header reports SQLite 3.54.1. This is
newer than the [WAL-reset race fix](https://www.sqlite.org/wal.html#the_wal_reset_bug)
in 3.51.3; there is no dependency update finding for that issue.

## Read-side WAL lifetime

The initial `export_jsonl` retained its outer SELECT cursor while writing the
entire export to an arbitrary `Write`. A slow consumer therefore pins the read
snapshot while concurrent writers keep extending WAL. The SQLite WAL contract
identifies this checkpoint-starvation mechanism explicitly. Bounded keyset pages
materialized before output would avoid pinning one read transaction through
potentially unbounded pipe backpressure; a deliberate snapshot alternative must
state its operational tradeoff.

## Configuration inspection round trip

The initial effective-config rendering used strings `"default"` and `"none"`
for unset telemetry/state paths, while loading interpreted both as ordinary
relative paths. Reloading inspection output therefore silently changed policy.
Unset optional keys should be omitted with provenance comments, or supported
sentinels must be part of the actual type/parse contract. This issue has been
sent to the configuration owner; no production edit is made by review.

## Language tooling

The lexical index initially confused intrinsic `$pwd::pwd` with a same-named
local binding, and classified pure symbol calls returning spaces as definite
program launches. The index owner corrected both with syntactically justified
namespace shapes and dedicated tests; its isolated actual-source target passed
eight tests according to that owner.

Initial LSP diagnostic publication flattened `LocatedReport` into `Report` and
mapped all source/label byte spans into the currently open document URI. Errors
inside included sources consequently pointed at unrelated caller text. Source
ownership must survive the LSP projection just as it survives CLI rendering.
The frontend owner has been notified. Synchronous analysis also initially ran in
the receive loop and ignored cancellation, so permitted high-work source input
could delay newer edits and shutdown; a versioned analysis worker is the material
responsiveness boundary, not a faster debounce timer.

## Integrated source recheck and focused evidence

The root's centralized storage/tooling gate passed all three
`performance_review_v6` tests (0.49 s), including 24 independently launched CLI
processes sharing freshly initialized databases. The gate also passed existing
JSONL context-retention tests (5), SQLite API tests (11), SQLite CLI tests (6),
AST navigation tests (11), and runtime activity tests (3; four ignored child
fixtures are launched by their parent tests). The disposable command output is
`.temp/performance-v6/storage-tooling.log`; these counts describe that selected
Linux gate, not the entire repository or a Windows result.

Rechecking the integrated source confirms:

- Schema ownership is checked after `BEGIN IMMEDIATE`, eliminating the observed
  empty-database creation race.
- SQLite/config policy shares keyed validation, including the signed busy-timeout
  bound; queue capacity and transaction batch size remain independent.
- Export materializes pages of at most 128 records and related rows inside a
  short transaction, then releases the transaction before output. Durable record
  IDs use `AUTOINCREMENT`, keeping the export high-watermark meaningful across
  retention deletion.
- Effective configuration omits unset path keys rather than printing unsupported
  magic strings; a configuration-owner round-trip test passed separately.
- Job ownership now uses recursive immutable instruction lookup and runtime work
  provenance, covering dynamic/control launches.
- Completion readiness uses sticky worker-owned cancellation tokens, not a lossy
  edge notification or leader-exit-only event. No-work waits remain pending and
  cancellation does not consume completion.
- LSP typed analysis runs off the protocol thread with per-document generation
  checks; included-source UTF-16 provenance has a passing actual-stdio journey.

The then-current broader LSP/latency gate found formatter/display expectation
mismatches in independent acceptance tests. Their owners are correcting those
expectations against the established canonical writer/output contract. This
review does not report that broader gate as passed before its targeted rerun.
