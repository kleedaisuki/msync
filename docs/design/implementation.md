# Implementation contract

This is the current architecture after the user-requested Plan → Execute redesign. The supplied `docs/dsl/spread.msync` remains the semantic seed, but the explicit directory-target and formatting changes below supersede the original implementation's behavior. Detailed proofs, transport boundaries and evidence belong in the linked focused notes rather than duplicated here.

## State ownership and pipeline

```text
Source text
   │ syntax.rs: parse + operand/operator provenance
   ▼
Script ── compiler.rs: transactional binding resolution ──► Program
                                                            │
                 scheduler::prepare: snapshot local context, inspect types
                                                            ▼
                 plan.rs: effects, fixed-point rewrites, hazard DAG
                                                            │
                 scheduler.rs: bounded ready queues and cancellation
                          ┌─────────────────────────────────┴──────────┐
                          ▼                                            ▼
             runtime.rs: finite native copies             streams.rs: async byte flows
                          └──────────────────┬─────────────────────────┘
                                             ▼
                              reports + one telemetry hierarchy
```

`Compiler` owns user endpoint/path bindings. Each submission compiles against a clone-on-write candidate; failure commits nothing, success replaces the environment. The CLI/REPL own source text, presentation and commands. `Program` contains resolved source operations, while `Plan` owns optimized operations, original indices, typed effects, dependency edges and rewrite provenance. The scheduler owns all ready/running/completed state and resource admission; transports own file handles/processes only for the task lifetime. There is no independent REPL interpreter or second grammar.

The library compiler resolves syntax-level meaning, not machine code. `check` runs the frontend only. `plan` (`compile` is its CLI alias) prepares and prints the optimized graph, allowing metadata-only remote probes but no file-data transfer. `run` prepares then executes. `--dry-run` prepares offline: local metadata is available, remote classes remain conservative, and no remote client or stream-data open occurs. Explicit telemetry output is the documented write exception.

## Syntax and diagnostics

The pure formatter and parser retain their basic APIs. `Statement.operands` optionally stores left/right `OperandSource` provenance and the operator span. A quoted operand includes decoded-byte-to-original-byte offsets; semantic checks can label the exact original qualifier, escaped regex or insertion point. Manually constructed ASTs can omit provenance and receive a statement-level fallback.

```text
Diagnostic { message, span }
Report { diagnostic, labels: Vec<Label>, help: Vec<String> }
Label { span, message }

parse(source) -> Result<Script, Vec<Diagnostic>>
parse_rich(source) -> Result<Script, Vec<Report>>
format(source) -> Result<String, Vec<Diagnostic>>
format_rich(source) -> Result<String, Vec<Report>>
render_report(source, report, color) -> String
Compiler::compile(script) -> Result<Program, Vec<Diagnostic>>
Compiler::compile_rich(script) -> Result<Program, Vec<Report>>
```

Rich reports are constructed where the frontend knows the failure, not recovered by matching English diagnostic messages. Primary spans, secondary labels and advisory help are separate data. Unknown endpoint spelling, malformed operators, missing operands, invalid regexes, read-only destinations and conflicting aliases have targeted provenance. Type-preparation diagnostics can use CLI-supplied operand spans. Later OS failures may fall back to the transfer's source/destination operands when the underlying operation provides no narrower location.

Rendering is Unicode-width-aware, clamps external byte spans, expands tabs and escapes terminal controls. The CLI applies auto/always/never policy consistently to help, REPL help, diagnostics, prompts and statuses, with Windows console adaptation. Auto respects pipes, `TERM=dumb` and `NO_COLOR`; explicit always/never takes precedence. See [syntax and rich-report details](syntax.md).

### Script formatting, not an implicit I/O transfer

`fmt FILE` and `/fmt FILE` format that script **in place**; neither executes its statements. `--check` reports differences without writes. `--stdout` is the explicit canonical-preview escape hatch; `--write` remains an alias for the default. Default `fmt -` is rejected. The pure library formatter still returns text so callers can compose it normally.

Formatting reuses parsing, preserves comments and semantic operand values, normalizes spacing/quoting, and is idempotent. It does not preserve blank-line placement or rewrite all legacy relative paths into `$pwd`: doing so could change path-alias semantics. In-place updates use an exclusively created temporary sibling, preserve permissions, reject symlink/read-only inputs, and clean only temporaries this invocation actually created.

## Paths and bindings

Built-in endpoints include `$local`, `$pwd`, `$wsl`, `$path`, `$env`; named endpoints may refer to earlier bindings or SSH/HTTP/TCP URLs. `Backend::Tcp { host, port }` requires an explicit port and does not embed protocol framing into the parser. HTTP and PATH remain read-only destinations. No DSL operand is implicitly shell code; remote arguments are quoted at the transport boundary.

`$pwd::path`, `$pwd/path` and bare `$pwd` explicitly select the local working directory. Existing bare relative paths remain compatible, while examples/help prefer explicit paths. Preparation snapshots local cwd/home/ENV/PATH resolution into native locations; execution does not silently reinterpret those operands under a later cwd. A `$pwd`-qualified alias is explicitly local and cannot be requalified remotely. A plain alias such as `.codex := ~/.codex` remains endpoint-relative, allowing `remote::.codex/x`.

Aliases substitute whole first components, not string prefixes. Declaration-time substitution resolves earlier aliases without recursive late rewriting. Concrete relative locations are bound to the current context during preparation. Compiler maps are `Arc<BTreeMap<...>>`; read-only submissions share snapshots, while mutation clones only the affected map. Successful selector validations are deduplicated per submission, not retained in an unbounded REPL cache. These frontend CPU optimizations remain distinct from semantic plan optimization; [the earlier measurements](performance.md) are not transfer throughput claims.

## File categories and destination contract

The domain has three data categories: ordinary files, directories, and byte streams (FIFO/named pipes or stream sockets). `FileClass::Missing` and `Unknown` are planning states, not additional physical file categories. Metadata classification never opens a FIFO/socket for data. Device files are not silently treated as sockets.

| Destination | Contract |
| --- | --- |
| Exact target without `/`, existing regular file | May overwrite |
| Exact target without `/`, missing | Create a file or directory tree as appropriate |
| Exact target without `/`, existing directory | Reject, even when the source is a directory |
| Target ending `/` | Append source basename; merge directories, reject all existing files in the insertion tree |
| Pipe/socket | Transfer bytes only from a non-directory source |

A selector is a final source component, matches complete direct child basenames, and requires a destination container. A matched directory is then copied recursively. A named stream source can use container insertion; an unnamed TCP stream needs an exact filename. This deliberately preserves explicit trailing-slash container semantics while eliminating ambiguity for exact existing-directory targets.

## Plan optimization and dependencies

`Plan::build(program, kinds)` consumes inspected operation classes; missing inspection rows are `Unknown`, never assumed ordinary. The public convenience `scheduler::prepare(program, live)` resolves local context, obtains offline/live classes, builds the plan, and validates known type constraints before dispatch. The CLI uses source-aware preparation to retain precise operand diagnostics.

```text
OperationKinds { source: FileClass, destination: FileClass }
PlanNode { id, original_index, operation, kinds, dependencies, effects }
Plan { nodes, edges, removed, forwarded, optimization_rounds, stream_groups, stream_connections, issues }
```

The optimizer conservatively forwards proven stable local-file sources and eliminates dead exact regular-file writes until reaching a fixed point. The motivating user example is two ordinary sources writing the same exact destination: if no intermediate read needs the first version, the first copy disappears. Intermediate reads, mutation of a forwarded origin, same-object identity, hierarchy overlap and dynamic snapshots constrain rewrites. Directories, selectors, insertion/no-clobber operations, streams and unknown effects are not scalar assignments.

After optimization, read/write/consuming effects build an acyclic graph preserving RAW, WAR and WAW hazards. Hierarchical structural dependencies prevent a file path and an ancestor directory path from being scheduled as unrelated. Physical identity can add conservative dependencies but is not proof for eliminating writes. Stream reads consume bytes and are not interchangeable repeatable reads. Independent stream resources do not become an unnecessary global execution fence. The graph retains original-index provenance, and plan display explains removals/forwarding.

Correctness is the modeled final file state plus required resource order, not the exact intermediate-write sequence or execution-time reads/errors of eliminated copies. Source metadata is still needed to establish safe rewrites. External concurrent observers, hostile mutation and filesystem transactions are outside this model. See [planning algorithms, invariants and property tests](planning.md).

## Bounded execution and stream lifetime

```text
ResourceLimits { jobs, max_streams, buffer_bytes, memory_bytes }
scheduler::execute_plan(plan, limits) -> Result<ExecutionSummary, RuntimeError>
scheduler::execute_plan_async(plan, limits, CancellationToken)
```

The scheduler launches only ready graph nodes and bounds finite copies separately from long-lived flows. An idle stream cannot consume every ordinary-copy worker. The explicit relay-buffer budget further limits admitted flows, reserving one local buffer plus one for each WSL/SSH helper; it is not a process-RSS, kernel-buffer or interpreter-memory limit. Configuration errors are rejected before effects. Finite copies use guarded native filesystem/client operations; streams use asynchronous bounded-buffer I/O and backpressure.

Stream producer/consumer connectivity is not a completion edge. Complementary channels form co-start cohorts, and the scheduler atomically admits all members after their external prerequisites and aggregate resource reservations fit. It validates the contracted graph and rejects internal completion dependencies or insufficient cohort capacity before I/O. This permits self-contained FIFO chains without blocking a producer on an unstarted consumer. Ready/running state, budgets and completion are owned by one scheduler state machine; workers do not mutate dependency counts.

Each flow terminates independently at source EOF or an OS-reported closed destination. The process/submission waits for remaining tasks; one flow's normal EOF does not cancel other flows. Cancellation closes asynchronous waits/handles and stops new task admission. Native blocking copies that already started are drained instead of pretending Tokio can abort them safely. TCP half-close and idle sink-close observability follow the one-way OS transport contract, not an invented duplex protocol. [Streams](streams.md) records actual platform support and remote-helper requirements.

`runtime::execute(program, ExecutionOptions { dry_run })` keeps its public signature and routes through preparation/scheduling. It rejects synchronous invocation inside an existing Tokio runtime with guidance to use the async entrypoint, rather than panicking in a nested runtime. Reports combine finite file/directory counts, stream bytes, operation completion and concurrency metrics. CLI/REPL candidates commit bindings after successful preparation; runtime failure does not roll back those bindings or completed writes. Filesystem writes are not a whole-script transaction; a failure can leave completed or in-flight independent writes.

The REPL owns at most one `ActiveJob`: a worker handle, cancellation token and owned diagnostic source/AST. A streaming submission returns to the command loop immediately. Read-only commands and pure declarations remain responsive, but new transfers and shared cwd/filesystem-changing commands require `/wait` or `/stop`. This deliberately avoids separate plans racing or exceeding a per-session budget. `/quit`, interactive EOF and guard destruction cancel/join; piped EOF drains. The worker carries explicit telemetry context, and completion errors render against the saved source rather than a reused input buffer.

## Safety, transports and telemetry

The regular-copy executor preflights selected trees, rejects symlinks/reparse points, unsupported device entries, same-file/hard-link copies and overlapping source/destination directories in both directions. File identity rather than lexical spelling covers Windows aliases. Exclusive create enforces no-clobber atomically. Hostile concurrent replacement between path checks and use is still outside the portable path-based guarantee.

HTTP uses native curl; ordinary SSH copies use modern SCP/SFTP plus quoted POSIX helpers. `MSYNC_SSH_CONFIG` supplies a separate `-F` file for both clients without editing existing SSH settings. Local transport scratch lives under `.temp`, remote uploads use a private `mktemp` directory, and task-owned resources are cleaned on their normal/error paths. The authenticated SSH fixture is isolated in WSL namespaces/chroot and fingerprint-checks existing SSH/account files unchanged. See [runtime safety, transport requirements and fixture evidence](runtime.md).

One opt-in local JSONL telemetry session links parsing, compilation, preparation, planning, scheduling and transport work. `TraceContext` captures a parent across tasks; `LinkedSpan` can remain alive across `.await` without entering thread-local state. `with_context` temporarily attaches a parent around synchronous blocking work. Paths, source, environment values and error bodies are excluded, exporter failure is visible, and the outer scope flushes after children finish. See [the telemetry schema and concurrency contract](telemetry.md).

## Verification and scope

Use the [validation record](validation.md) for current executed suites and platform boundaries rather than copying changing test totals here. The retained tests include source/formatter properties, random graph final-state equivalence and fixed-point idempotence, real bounded scheduling/EOF/cancellation, local/HTTP/SSH transfers, Windows pipe/TCP behavior and telemetry parent graphs. Environment-dependent WSL/SSH or Unix tests must be run in their corresponding environment; another platform's result is not substituted as evidence.

No deletion, reconciliation database, implicit shell evaluation, watch daemon, background service installation, or bidirectional protocol is introduced. The architecture retains the useful compiler separation described in the [Rust compiler overview](https://rustc-dev-guide.rust-lang.org/overview.html), while the [Unison specification](https://www.cis.upenn.edu/~bcpierce/papers/unisonspec.pdf) helps distinguish a filesystem-state contract from implementation details without claiming Unison-style reconciliation. WSL/SSH participants in a byte-stream flow require Python 3 for the fully type-annotated, nonblocking remote helper. Ordinary remote copies keep their existing POSIX/GNU-helper requirements without a Python dependency.
