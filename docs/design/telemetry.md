# Unified local telemetry

## Contract and integration

`src/telemetry.rs` provides one trace per explicit `init` session and hierarchical
spans shared by CLI, REPL submissions, parsing, compilation, formatting, execution,
and individual copies. Telemetry is disabled by default. The CLI enables it with
`--trace FILE`; `--trace -` writes JSONL to stderr. Trace metadata is intended to
explain real operations: source/destination paths and backends, native kernel
selection, fallback causes, opening/publication/cleanup phases, byte progress and
typed error causes. Paths and useful error messages are not stripped by default.
There is no network exporter, automatic environment-value capture, file-payload
capture, or credential capture. Callers must not deliberately put payloads,
passwords, authentication headers or secret environment values into metadata.
Normal program output remains on stdout.

```rust
telemetry::init(".temp/run.jsonl")?;
{
    let mut request = telemetry::span("run");
    let result = run_pipeline();
    request.finish_result(&result);
    request.metric("operations", operation_count);
}
telemetry::flush()?;
```

The final `flush` occurs after the outer span is dropped. Initialization and flush
errors should become visible CLI errors. A failing exporter must not panic in a
span destructor or interrupt an in-progress copy; its first failure is retained
and returned by `flush`/`take_error`. Disabling tracing also flushes. Reinitializing
first flushes the previous destination and opens the new session in append mode.
Already-active spans and explicitly captured contexts retain their original
session/sink. `TraceContext::flush` can flush an older retained sink after global
reconfiguration or disablement.

Names and keys use fixed vocabulary: `run`, `repl`, `submission`, `parse`, `compile`,
`format`, `execute`, `copy`, etc. Attribute values describe the actual operation.
Numeric metrics include counts and bytes. `finish_result` marks the outcome
without guessing a cause from an arbitrary result's display text; callers record
typed failures at the boundary before flattening them into human diagnostics.
Panics during unwinding are identified separately. Legacy scopes still default
to `ok`; cancellation-sensitive scopes explicitly opt into completion tracking.

## Operation metadata and lifecycle events

Both guard types support `attribute(key, value)` with dynamic string values,
`attribute_lazy(key, || value)` for expensive formatting, `is_recording()` to gate
larger context construction, and `metric(key, u64)` for numeric measurements.
Replacing an attribute or metric updates its value rather than producing duplicate
JSON keys. Disabled attributes do not convert their `Into<String>` inputs, and
disabled lazy closures are never called; callers should avoid evaluating their
own expensive formatting before invoking a non-lazy method.

```rust
trace.attribute_lazy("source.path", || source.display().to_string());
trace.attribute("source.backend", "local");
trace.attribute_lazy("destination.path", || target.display().to_string());
trace.event("open", &[("phase", "open"), ("endpoint", "source")], &[]);
trace.event("kernel.selected", &[("kernel", "copy_file_range")], &[]);
trace.event("transfer", &[("phase", "transfer")], &[("bytes.progress", bytes)]);
trace.event("commit", &[("phase", "commit")], &[("bytes.committed", bytes)]);
```

`event(name, attributes, metrics)` immediately emits an event attributed to the
owning span ID. Guard methods include a snapshot of the guard's current attributes
and metrics; event-specific values override those inherited values without
changing the guard. `TraceContext::event` emits on an explicitly captured span
without entering TLS. The free `telemetry::event` method uses the current
synchronous context and does nothing when no context is attached. A context event
does not automatically copy its parent's string attributes: consumers correlate
through span IDs, and callers supply any event-specific details.

Suggested vocabulary includes `source.path`, `destination.path`, `source.backend`,
`destination.backend`, `phase`, `kernel`, `fallback.reason`, `bytes.progress`,
`bytes.committed`, and `bytes.discarded`. Emit lifecycle transitions and useful
progress milestones, not an event for every byte or arbitrary payload chunks.
Files remain buffered until explicit flush or buffer exhaustion; callers needing
live visibility can flush at deliberate checkpoints rather than forcing a syscall
for every metric update.

### Typed errors before diagnostic flattening

`io_error(stage, &io::Error)` is available on guards, contexts and as a free
synchronous helper. It emits `io.error` with `phase`, `error.kind`,
`error.raw_os_error` when present, `error.message`, and a structured `error_chain`
array. Every chain entry contains its message, optional `ErrorKind`, and signed
numeric `raw_os_error`. This preserves a `CopyFailure` wrapper and its original OS
cause without parsing English prose or losing the wrapped error's native code.
The generic `record_error(stage, &dyn Error)` emits `error` using the same causal
representation. Error chains are bounded to 32 entries and explicitly marked if
truncated, so a cyclic custom `Error::source` cannot hang telemetry.

An error event is not automatically a failed span: an unsupported native kernel
can be a recoverable fallback. The owner separately calls `finish_result`, `error`,
or `cancelled` according to the actual operation outcome. Byte progress belongs
in numeric metrics at the layer owning the typed `CopyFailure`; do not recover it
by parsing the wrapper's displayed message. Runtime diagnostic rendering should
keep phase/source/destination labels and actionable remedies rather than just
displaying the OS error string.

## JSONL schema, version 1

Each completed span emits one JSON object followed by a newline:

| Field | Meaning |
| --- | --- |
| `schema_version` | Integer `1`; consumers can reject incompatible versions |
| `kind` | `span` for completed scopes; `event` for lifecycle observations |
| `trace_id` | Nonzero 32-digit hexadecimal session identifier |
| `span_id` | Session-unique 16-digit hexadecimal identifier |
| `parent_span_id` | Span's explicit/thread-local parent ID, or `null`; absent on events |
| `name` | Static phase name |
| `start_unix_ns` | Wall-clock start timestamp in nanoseconds since Unix epoch |
| `duration_ns` | Elapsed monotonic duration, independent of wall-clock changes |
| `status` | Span outcome: `ok`, `error`, `cancelled`, or `panic` |
| `metrics` | Object containing explicitly supplied unsigned numeric measurements |
| `attributes` | Object containing dynamic string metadata; paths are permitted |
| `completion` | Span lifecycle: `complete`, `incomplete`, or explicit `cancelled` |
| `timestamp_unix_ns` | Event observation timestamp; events do not have duration |
| `error_chain` | Error events only: typed cause entries with message/kind/raw OS code |
| `error_chain_truncated` | Error events only: causal chain exceeded the recording bound |

These fields extend schema version 1 additively. Existing span fields retain their
meaning; readers must branch on `kind` and tolerate added attributes and outcomes.
An event's `span_id` names its owning span, not a new unique span. Event records
therefore must not be counted as additional graph vertices or roots.

Span records are emitted on completion, so a child can precede its parent.
Concurrent tasks may complete in any order, and a captured parent can finish before
its delayed child. Consumers reconstruct the tree using IDs rather than record
position. Concurrent threads share a serialized writer. The API does not promise
W3C propagation or OTLP wire compatibility.

## Explicit context across concurrent tasks

`SpanGuard` uses a synchronous thread-local parent stack and is intentionally
not `Send`/`Sync`. Do not hold it across `.await` in a spawned task: another task
could run on that thread while the original is suspended. Instead, capture the
parent once before dispatch, create detached spans for task lifetimes, and attach
their contexts only while executing synchronous blocking work:

```rust
let parent = telemetry::context();
jobs.spawn(async move {
    let mut task = telemetry::linked_span("task", parent.as_ref());
    task.expect_completion();
    let worker_parent = task.context();
    let result = tokio::task::spawn_blocking(move || {
        telemetry::with_context(worker_parent.as_ref(), || {
            // Existing runtime code can continue using telemetry::span("copy").
            execute_copy()
        })
    }).await;
    task.finish_result(&result);
});
```

`TraceContext` is an opaque, cloneable `Send + Sync` snapshot owning the original
session and parent ID. `LinkedSpan` is `Send + Sync`, never enters a thread-local
stack, and can safely remain alive across task suspension or migrate between
threads. Its `error`, `finish_result`, and `metric` methods have the same semantics
as `SpanGuard`; `context` makes it the explicit parent of further child work.
`linked_span(..., None)` is disabled, even if global tracing is enabled. This
prevents missing context from silently producing unrelated roots.

`with_context` is synchronous and panic-safe: it temporarily attaches exactly the
supplied context, runs a closure, and removes that attachment without creating a
dummy span. An attachment has a separate identity from its parent span, so nested
attachments of the same parent remain correct even if guards finish out of order.
`with_context(None, f)` simply runs `f` under the existing context. Returning a future
from this closure does not keep the attachment alive while the future is polled.
For purely asynchronous child phases, use another `linked_span` with the explicit
parent rather than a synchronous `span`.

Captured contexts take precedence over the mutable global session, preserving one
trace graph across worker dispatch even if telemetry is reconfigured. Disabling
the global session stops new unparented spans but does not invalidate contexts of
in-flight work. Cross-process/network propagation is not implemented.

### Correct completion under asynchronous cancellation

Use `expect_completion()` at the beginning of cancellation-sensitive scopes. It
sets a pending state without changing the default behavior of older callers.
`finish_result` and `error` provide a terminal result. Dropping a still-pending
guard, including when an async future is aborted after opening an endpoint, records
`status: cancelled` and `completion: incomplete` rather than false success.
`cancelled()` records an explicit cancellation (`completion: cancelled`), and a
subsequent successful `finish_result` does not erase it. Panic unwinding takes
precedence in status reporting. The same APIs exist on `SpanGuard`, but existing
callers which never request completion tracking keep their previous semantics.

## Design rationale and external references

The [OpenTelemetry tracing API](https://opentelemetry.io/docs/specs/otel/trace/api/)
uses trace/span identifiers, parent relationships, timings, and status to represent
end-to-end activity. Its
[context propagation model](https://opentelemetry.io/docs/concepts/context-propagation/)
explains why correlation must follow execution context rather than unrelated log
lines. This implementation adopts those concepts without bringing a service SDK,
background exporter, runtime, or network behavior into a small command-line DSL.
This is structured local tracing, not a claim of full OpenTelemetry compliance.

The upstream [tracing span API](https://docs.rs/tracing/latest/tracing/span/struct.Span.html)
specifically warns against keeping thread-entered guards across `.await`; the
detached API resolves that hazard by making propagation explicit instead of
teaching a thread-local stack about asynchronous scheduling.

Atomic fast-path checks keep disabled spans allocation-free and avoid the global
lock and thread-local stack when no contexts are active. Enabled spans capture an
`Arc` to their sink and write a
complete JSON line under one mutex. Buffered files avoid a syscall for every phase;
stderr remains directly observable. JSON escaping covers quotes, backslashes,
control characters, and Unicode. Fixed names keep queries stable, while dynamic
attributes retain the context needed to understand an operation and its failure.

Tests cover hierarchy, shared trace IDs, error/result and panic outcomes, metric
replacement, full JSON string escaping, out-of-order guard cleanup, concurrent
line integrity and unique span IDs, observable exporter failure, default disabled
behavior, file append/reconfiguration, and the public init/flush/disable journey.
Concurrent integration tests overlap eight async tasks on a multithreaded Tokio
runtime, pass their contexts through blocking workers, and assert exactly one root,
one trace ID, unique span IDs, and correct `schedule -> task -> copy/stream` parent
relationships. Further tests cover session retention after reconfiguration and
disablement, attachment recovery after panics, repeated parent attachments, and
the `Send + Sync` contract without adding dependencies to the telemetry module.
Additional tests verify dynamic native/Unicode path metadata, event snapshot
overrides without duplicate keys, preservation of typed wrapper/OS error chains,
bounded cyclic causes, lazy disabled attributes, explicit/pending completion
semantics, and aborting an async task after a real file open without reporting
success or automatically recording file contents.
A real scheduler-to-runtime publication test replaces an existing file, verifies
its contents and byte report, and checks `file.open`/`file.transfer`/`file.commit`
events, concrete endpoint names, exact committed bytes, one trace root, and valid
span ownership for every event.
