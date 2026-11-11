# SQLite telemetry storage

The current C# recorder and self-profiling levels are described in
[Unified local telemetry](telemetry.md#end-to-end-self-profiling).
It preserves application ID `0x4d53594e`, schema version 1 and the relational
storage contract below. The older implementation notes refer to the retired
Rust runtime, not to the current provider or command set.

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

Recording uses nonblocking queue admission. A full queue increments an explicit
dropped-record count; it does not stall the interpreter behind SQLite. Flush and
shutdown report incomplete recording, and the session's exporter_error includes
the gap count. A flush remains a queue barrier for accepted records. The normal shutdown path drains queued
records, marks the session ended, and joins the worker. Destructor failures do not
panic or mask an ongoing file operation; explicit API flush exposes them.

Default session retention, record count and database size use the database's
representable maximum rather than imposing small policy quotas. Explicitly
configured budgets remain available and report incomplete recording rather than
changing the file-operation result. Configured retention removes completed
sessions through cascading foreign keys. Rows belonging to active sessions are never evicted merely
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

An explicitly configured `max_database_bytes` configures a SQLite
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

## Delivery-first observer outcome (2026-11-01)

Optional observer initialization or closure errors produce structured warnings
with the selected destination and underlying cause. They never replace the
result of a successful file operation. A failed operation remains failed even if
its observer also fails. Telemetry query/export commands still report failure
when their own requested output is unavailable; recording health and application
outcome are distinct states. Producers never wait for queue space. Completed
sessions with dropped records are explicitly marked incomplete/error, not
presented as complete traces.

## Current typed VM profile counters

The .NET runtime produces bounded-by-site summaries for the existing SQLite
record schema; it does not know about SQLite, emit a row per executed opcode,
or time each instruction. The CLI chooses aggregate or instruction-detail
collection for an execution. An embedded host may do the same through
`ExecutionOptions.Profile`, then call `RuntimeProfile.Drain()` after guest
execution and any host-driven Range display have joined. Escaped REPL child
processes can still complete in the background and emit I/O observations, but
their output collection does not dispatch guest code. A deferred Range is
counted in the submission that demands it, with the actual module projection
of its executing frame.

| Record | Scope | Meaning |
| --- | --- | --- |
| `vm.function` | Image and function ID | Entered frames, attempted instructions and operand-stack Number materializations |
| `vm.opcode` | Image, function ID and opcode | Dispatch counts |
| `vm.native` | Image and native name | Native invocation entries; `fast_calls` is the subset executed inline by the VM |
| `vm.instruction` | Image, function ID and PC | Site visits, source span when present, and name of a direct native instruction |
| `vm.edge` | Image, function ID and source/target PC | Actual next instruction reached after a jump, branch, or iterator test |
| `vm.call` | Caller image/function/PC and resolved target image/function | Source calls dispatched from bytecode call sites |

The last three records require instruction-detail collection. Function names
alone are not unique; join on trace ID, image ID and function ID, and add PC
for site queries. Image IDs are trace-local and weakly associated with live
module projections, so clearing completed counters does not retain old REPL
images. Counts include attempted dispatches even when an instruction fails;
an edge is recorded only when its next instruction is fetched. Callbacks
driven by native Range code count as function entries, not as fictitious
guest call sites. The boxing metric counts only NumberValue materialization
in the operand stack; native constructors and overall process allocation are
different measurements.

The interpreter chooses a statically specialized frame policy at function
entry. The same switch and operand-stack implementation serve both modes.
The disabled value-type specialization erases opcode, edge, call-target and
boxing hooks; it does not evaluate a profiler branch at every instruction
or box. The generic native invocation boundary still checks for the optional
profile. See the [.NET Native AOT value-type specialization contract](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/).
The counter design is analogous to [LLVM's instrumentation profiles](https://www.llvm.org/docs/InstrProfileFormat.html)
in keeping code identity and dynamic counters separate. Here the profiles
inform engineering decisions; they are neither artifact validity proofs nor
inputs that change execution semantics.

### Diagnostic frame and wait intervals

At the diagnostic level, `vm.frame` records each actual guest invocation
from frame entry through its ownership cleanup. It identifies the executing
function and, when a VM profile is also present, shares its image ID. The
frame records return, failure or cancellation and counts its observed
incomplete awaits. Its wall duration is inclusive of child frames, native
calls and cleanup.

`vm.await` is emitted only when the original Task or ValueTask is
incomplete at the VM await boundary. It carries the waiting frame, reason,
PC, opcode and source site where applicable; a known completion Task also
has a dependency link. Root result/drain waits and frame ownership waits have
a reason rather than an opcode. Already-ready results, faults and
cancellations do not produce wait spans. An incomplete check can race with
task completion; the interval does not establish a scheduler thread switch
or measure CPU consumption. A caller's wait can overlap a child's wait and
independently tracked work, so these intervals must not be summed as
exclusive time.

`vm.native` span rows time actual selected native invocations, including
immediate operations, separately from aggregate `vm.native` event rows.
The route distinguishes direct bytecode, dynamic callable values, inline
arithmetic, and deferred completion. A native admission which returns a
deferred value marks that result; a deferred native operation has a child
completion span spanning its operand wait and execution. Those intervals
can overlap other VM waits and must not be summed as exclusive native CPU.

A gated real standard-input reader checks both nested source and native
awaits, parent IDs, offsets, return, cancellation and error propagation.
A ready standard-input control checks that asynchronous methods which
complete synchronously do not produce wait records, while its actual native
call still has an operation span. Inline power, a native callable value, and
a gated deferred native operand independently check the other native routes.
The disabled dispatch
specialization returns original awaitables without checking completion or
creating wait scopes.

A second isolated Native AOT check used `1d216ae` as the base and added only
the runtime timeline API and VM frame, native, and wait observations. With
collection disabled, six alternating fresh-process pairs on logical CPU 12
checked the same frozen Set, Hash and SHA-256 bytecode and results. Paired
median process CPU deltas were −2.1%, +2.2% and −0.5%, with mixed signs within
all three workloads; wall deltas were −0.9%, −1.3% and +0.5%. This comparison
does not establish a disabled-path regression or a performance gain. In
the disabled dispatch body, the only new await helpers reached were
pass-throughs for iterator next/disposal; no wait-scope or completion-test
helper was called. The stripped harness grew by 29,344 bytes (+0.47%).
Receipts: `artifacts/benchmarks/vm-execution-head-v086/diagnostics-off/`.

### Disabled-mode check

An isolated Linux x64 SDK 10.0.403 Native AOT candidate applied only runtime
profiling to `887eced`, excluding the concurrent array-fetch, scheduler, and
native-helper changes. Disassembly shows separate enabled and disabled VM
state machines and no atomic counter instruction or profile call in the
disabled `MoveNext` body. The stripped runtime harness grew by 37,104 bytes
(0.64%); the duplicated state machine is native code, not a second source
evaluator. The semantic probe verifies counts, source spans,
resolved targets, edges and Number materializations through serialized
relocatable and executable input, and verifies concurrent accumulation
with repeated image reuse. Run it with
`dotnet run -c Release --project tests/Msync.Runtime.Tests -- --profile`.

The frozen scalar, unrolled, and stacked artifacts from the
[execution audit](performance-assumption-audit-v086.md#scalar-causal-control)
ran with the same result bits and only 16 fixed extra managed bytes per
execution with collection disabled. Eight alternating fresh-process pairs
on logical CPU 12 (262,144 iterations per case, warmup and three measured
samples) had a −5.2% median paired process CPU delta for the candidate,
with six faster pairs and a −19.9% to +5.8% range. The wall-time native
control had scheduling outliers, so this is a check against an evident
disabled-path regression, not a throughput improvement attributed to
telemetry. Receipts: `artifacts/benchmarks/vm-execution-head-v086/profile-off/`.
