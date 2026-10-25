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
Already-active spans retain their original session/sink.

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

Spans complete in postorder, so a child record can precede its parent's record.
Consumers reconstruct the tree using IDs rather than record position. Concurrent
threads share one session and serialized writer; each thread has its own parent
stack. The current implementation is synchronous and does not automatically
propagate a parent across spawned threads, subprocesses, or network boundaries.
Those operations still belong to the same process session, but a worker without
an explicit enclosing scope begins at a `null` parent. `SpanGuard` is intentionally
not `Send`/`Sync`, preventing an accidental cross-thread stack pop. The API does
not promise W3C propagation or OTLP wire compatibility.

## Design rationale and external references

The [OpenTelemetry tracing API](https://opentelemetry.io/docs/specs/otel/trace/api/)
uses trace/span identifiers, parent relationships, timings, and status to represent
end-to-end activity. Its
[context propagation model](https://opentelemetry.io/docs/concepts/context-propagation/)
explains why correlation must follow execution context rather than unrelated log
lines. This implementation adopts those concepts without bringing a service SDK,
background exporter, runtime, or network behavior into a small command-line DSL.
This is structured local tracing, not a claim of full OpenTelemetry compliance.

One atomic flag makes disabled spans allocation-free and avoids the global lock
and thread-local stack. Enabled spans capture an `Arc` to their sink and write a
complete JSON line under one mutex. Buffered files avoid a syscall for every phase;
stderr remains directly observable. JSON escaping covers quotes, backslashes,
control characters, and Unicode. Numeric metrics use fixed static names and no
string-valued attributes, reducing accidental data disclosure.

Tests cover hierarchy, shared trace IDs, error/result and panic outcomes, metric
replacement, full JSON string escaping, out-of-order guard cleanup, concurrent
line integrity and unique span IDs, observable exporter failure, default disabled
behavior, file append/reconfiguration, and the public init/flush/disable journey.
