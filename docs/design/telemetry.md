# Unified local telemetry

## Contract and integration

`src/telemetry.rs` provides one trace per explicit `init` session and hierarchical
spans shared by CLI, REPL submissions, parsing, compilation, formatting, execution,
and individual copies. Telemetry is disabled by default. The CLI enables it with
`--trace FILE`; `--trace -` writes JSONL to stderr. No network exporter, environment
autoconfiguration, source capture, path capture, or exception-message capture is
implemented. Normal program output remains on stdout.

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

Names are a fixed vocabulary, not dynamic user input: `run`, `repl`, `submission`,
`parse`, `compile`, `format`, `execute`, `copy`, etc. Numeric metrics can include
statement, operation, file and directory counts. `finish_result` marks errors
without recording potentially sensitive messages. Panics during unwinding are
identified separately. Successful scopes default to `ok`; therefore every
fallible integration point must mark its `Result` before returning.

## JSONL schema, version 1

Each completed span emits one JSON object followed by a newline:

| Field | Meaning |
| --- | --- |
| `schema_version` | Integer `1`; consumers can reject incompatible versions |
| `kind` | `span` |
| `trace_id` | Nonzero 32-digit hexadecimal session identifier |
| `span_id` | Session-unique 16-digit hexadecimal identifier |
| `parent_span_id` | Current thread's enclosing span ID, or `null` |
| `name` | Static phase name |
| `start_unix_ns` | Wall-clock start timestamp in nanoseconds since Unix epoch |
| `duration_ns` | Elapsed monotonic duration, independent of wall-clock changes |
| `status` | `ok`, `error`, or `panic` |
| `metrics` | Object containing explicitly supplied unsigned numeric measurements |

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
in-flight work. Cross-process/network propagation is not implemented. A detached
guard dropped through task cancellation defaults to `ok` unless the scheduler
explicitly marks the cancelled result as an error; callers must mark all terminal
failure/cancellation paths when producing task outcomes.

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
control characters, and Unicode. Numeric metrics use fixed static names and no
string-valued attributes, reducing accidental data disclosure.

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
