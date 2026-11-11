# Observability boundaries for the compiler, runtime, and editor

The developer workflow has two distinct operations: compare normal execution
with recording disabled, then record enough evidence to explain the difference.
Do not trade away diagnostic detail merely to make the second operation look
like the first. Compare durations with a matched detail level, and keep
telemetry-off timing as the primary latency control.

| Layer | Owns | Does not own |
| --- | --- | --- |
| Core diagnostics | Sink-neutral compilation observations, phase outcomes, and sizes | SQLite, CLI flags, JSON-RPC, or product policy |
| Compiler and optimization | Actual phase and pass instrumentation; IR counts and proof/rewrites at the producing phase | Runtime work, editor round trips, exporter selection |
| Runtime diagnostics | VM census and operation timeline primitives; optional observation APIs | Storage transport for telemetry, process-wide policy |
| Execution, scheduling, and transfers | Selected operations, actual resource predecessors, VM suspensions, process and transport milestones | Observer persistence or inferring that an absent operation executed |
| CLI/LSP host | Explicit opt-in, session lifetime, context propagation, schema projection, warnings | Compiler/VM analysis or the user's guest I/O |
| Exporters | JSONL and local SQLite schema, batching, retention and completion state | Guest success, trace semantics, or protocol stdout |
| VS Code client | User-owned opt-in, extension-host-local database choice, launch/round-trip measurement | SQLite writers, guest instrumentation, or network export |

The CLI and the language server use the same host session and sink. Core,
Compiler, Optimization and Runtime do not reference SQLite or the CLI. Local
execution works without a recorder. A framed LSP stream never doubles as an
exporter; only allowlisted editor observations cross that stream as normal
notifications. The VS Code client measures elapsed round trips on its own
monotonic clock; a server span measures server work. Compare those durations
as different observations of the same request, not as timestamps that can
be subtracted across clocks.

## Measurement and causal contracts

- Record basic phase and operation wall time from a monotonic clock, and a
  wall-clock timestamp for ordering and joining the exported records. Process
  CPU, managed allocation, and collection deltas describe the process during
  the measured interval, including observer work on its threads; they do not
  attribute those quantities to every nested span. Report synchronous calls
  separately from actual incomplete
  awaits. Sum neither overlapping siblings nor inclusive parent/child spans
  to infer exclusive execution time.
- The parent identifies the owner that selected work; an actual scheduling
  dependency is a link, not an invented parent. Distinct paths, aliases,
  result-bearing prerequisites and completion-only prerequisites must remain
  distinguishable. Record selected work and actual errors, not every
  syntactically possible branch. Capture useful path/resource and source
  provenance, but never add authentication material or unrelated guest
  payloads merely to make the row wider.
- Aggregate opcode, function, native, site, and taken-edge counts before
  export. In detailed developer mode also preserve the chronological runtime
  operation trace. Bounded lossless admission may wait for the local writer
  in that explicit mode; lower modes need not block normal work. Never report
  a partial session as complete when an explicit storage budget, I/O error,
  or exporter failure prevents recording.
- SQLite is the local analysis store, not a runtime prerequisite or a
  synchronization protocol. Keep existing readers and stage timing usable
  under schema v1. Exporter failure is independently observable and must not
  replace a guest error or turn a successful operation into a failed one.
- VS Code local recording is an explicit user setting, off by default.
  A workspace must not silently turn it on. The default file belongs to the
  extension host's storage (the remote host in a remote window). Do not
  confuse local development recordings with upload to VS Code product
  telemetry or an extension vendor.

The span/event distinction and explicit links follow the
[OpenTelemetry tracing model](https://opentelemetry.io/docs/specs/otel/trace/api/),
without requiring an OTLP collector. See
[SQLite WAL](https://sqlite.org/wal.html) for persistence behavior,
[VS Code extension storage](https://code.visualstudio.com/api/extension-capabilities/common-capabilities)
for local ownership, and the [LSP specification](https://microsoft.github.io/language-server-protocol/specifications/specification-current)
for the framed editor transport.

Use the [CLI guide and SQLite queries](telemetry.md) for normal operation,
and the [matched profile runner](../../tests/Performance/README.md#explain-a-beforeafter-with-product-telemetry)
for compiler and runtime comparisons.
