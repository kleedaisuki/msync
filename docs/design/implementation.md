# Implementation contract

This is the current architecture after the user-requested Plan → Execute, contextual-namespace and uniform-flow redesign. The supplied `docs/dsl/spread.msync` remains the semantic seed, but the explicit directory-target and formatting changes below supersede the original implementation's behavior. Detailed proofs, transport boundaries and evidence belong in the linked focused notes rather than duplicated here.

## State ownership and pipeline

```text
Source text + explicit CompileContext
   │ syntax.rs: parse + operand/operator provenance
   ▼
Script ── compiler.rs: transactional space/value resolution ──► Program
                                                               │
                 scheduler::prepare: snapshot locations, inspect types
                                                               ▼
                 plan.rs: effects, fixed-point rewrites, hazard DAG
                                                               │
                 scheduler.rs: bounded ready cohorts + cancellation
                          ┌────────────────────────────────────┴───────┐
                          ▼                                            ▼
             runtime.rs: structural tree/finite policy     streams.rs: async adapters
                          └───────────────────┬────────────────────────┘
                                              ▼
                 io: progress + file publication + OS capability dispatch
                                              │
                              reports + one telemetry hierarchy
```

`Compiler` owns user space/value bindings. Each submission compiles against a clone-on-write candidate; failure commits nothing, success replaces the environment. The CLI/REPL own source text, logical source identity, presentation and commands. `Program` contains resolved source operations, while `Plan` owns optimized operations, original indices, typed effects, dependency edges and rewrite provenance. The scheduler owns all ready/running/completed state and resource admission; transports own file handles/processes only for the task lifetime. There is no independent REPL interpreter or second grammar.

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

## Context namespaces and binding time

The compiler separates private contextual spaces (`Pwd`, `File`) from physical `Endpoint(Backend)` transports. A namespace is not a directory. Public `Backend`, `Location` and `CopyOp` remain physical execution contracts, so the planner and I/O layer never have to interpret contextual pseudo-backends.

| Item | Result | Suffix policy |
| --- | --- | --- |
| `$pwd::pwd` | Submission's absolute logical cwd | Child paths permitted |
| `$file::this` | Absolute logical source-script path | No child suffix: known file |
| `$file::parent` | Source-script parent directory | Child paths permitted |

`$pwd` has exactly the item `pwd`. `$pwd::foo` is an unknown-item diagnostic, never an implicit child or a value-alias lookup; old `$pwd/foo` and bare `$pwd` are intentionally rejected with migration help. `$pwd::pwd/foo` and `$pwd::pwd` are the corresponding replacements. Ordinary legacy bare relative paths retain their existing local-path behavior. Formatting remains syntax-only and does not invent namespace migrations.

`name => space` preserves a lookup capability. `this => $pwd` therefore follows the cwd of every later submission, while `file => $file` follows that submission's named source script. Binding a file namespace is allowed without a current source file; accessing `file::this` or `file::parent` still requires one. `name := value` instead resolves and captures the path at declaration: `this := $pwd::pwd` and `script := $file::this` retain those absolute local values through later `/cd` or `/load`. Known-file provenance survives transitive aliases, so appending `/child` to `$file::this` or any value alias of it is a compile error.

```text
CompileContext { pwd: PathBuf, source_file: Option<PathBuf> }
CompileContext::capture(source_file: Option<&Path>) -> io::Result<CompileContext>
Compiler::compile_with_context(script, context) -> Result<Program, Vec<Diagnostic>>
Compiler::compile_rich_with_context(script, context) -> Result<Program, Vec<Report>>
```

Context paths are absolute logical identities, not canonicalized targets. Capture does not follow source symlinks, read scripts, or inspect metadata. Explicit contexts require absolute paths. The context-free compiler APIs capture cwd and deliberately supply no source file. This keeps semantic compilation independent of filesystem resolution and makes context lookup costs separable in benchmarks.

The CLI's `SourceUnit` carries diagnostic name, source text and actual `CompileContext` separately. Named `run`/`plan`/`compile`/`check` inputs and REPL file commands supply the script path independently of cwd. Stdin and direct REPL submissions never infer `$file` from a display label or substitute cwd. Two same-named scripts in different directories resolve different `this` and `parent` items. A bare `file::this` is valid only after an explicit `file => $file` declaration; the alias name is not an implicit built-in. A prior `/load` does not leave a sticky source-file context for direct REPL namespace access, while a concrete path captured by `:=` remains a valid value.

Physical built-ins remain `$local`, `$wsl`, `$path`, `$env`; SSH/HTTP/TCP URL bindings select transport spaces. TCP requires an explicit port. HTTP and PATH remain read-only destinations. Plain path-only aliases such as `.codex := ~/.codex` retain endpoint polymorphism (`remote::.codex/x`); contextual values are explicitly local and reject conflicting remote qualification. Aliases substitute whole first components, not string prefixes. No DSL operand is implicit shell code; remote arguments are quoted at the transport boundary.

Compiler maps are `Arc<BTreeMap<...>>`; read-only submissions share snapshots and mutation clones only the affected map. Successful selector validations are deduplicated per submission rather than kept in an unbounded REPL cache. These frontend CPU optimizations remain distinct from semantic plan optimization; [earlier measurements](performance.md) are not transfer throughput claims. [The syntax note](syntax.md) records migration diagnostics and contextual binding-time tests.

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

`Plan::build(program, kinds)` consumes inspected operation classes; missing inspection rows are `Unknown`, never assumed ordinary. The public convenience `scheduler::prepare(program, live)` resolves local context, obtains offline/live classes, builds the plan, and validates known type constraints before dispatch. The CLI uses source-aware preparation to retain precise operand diagnostics. Execution rejects an uninspected `Unknown` class before I/O and requests live preparation rather than dispatching a possible relay through finite-worker budgets. `Missing` remains valid for resources produced by earlier graph nodes; it is not silently equated with Unknown.

```text
OperationKinds { source: FileClass, destination: FileClass }
PlanNode { id, original_index, operation, kinds, dependencies, effects }
Plan { nodes, edges, removed, forwarded, optimization_rounds, stream_groups, stream_connections, issues }
```

The optimizer conservatively forwards proven stable local-file sources and eliminates dead exact regular-file writes until reaching a fixed point. Indexed may-overlap queries and typed live effect frontiers avoid rescanning complete unrelated histories, while sparse reachability replaces an unconditional quadratic ancestor matrix. Canonical parent prefixes are cached per plan without skipping new-child probes or ordinary file identity opens. Immutable proof-key component arrays are shared through `Arc`, avoiding duplicate path-string ownership while keeping value-based proof equality. The motivating user example is two ordinary sources writing the same exact destination: if no intermediate read needs the first version, the first copy disappears. Intermediate reads, mutation of a forwarded origin, same-object identity, hierarchy overlap and dynamic snapshots constrain rewrites. Directories, selectors, insertion/no-clobber operations, streams and unknown effects are not scalar assignments.

After optimization, read/write/consuming effects build an acyclic graph preserving RAW, WAR and WAW hazards. Hierarchical structural dependencies prevent a file path and an ancestor directory path from being scheduled as unrelated. Physical identity can add conservative dependencies but is not proof for eliminating writes. Stream reads consume bytes and are not interchangeable repeatable reads. Independent stream resources do not become an unnecessary global execution fence. The graph retains original-index provenance, and plan display explains removals/forwarding.

Correctness is the modeled final file state plus required resource order, not the exact intermediate-write sequence or execution-time reads/errors of eliminated copies. Source metadata is still needed to establish safe rewrites. External concurrent observers, hostile mutation and filesystem transactions are outside this model. See [planning algorithms, invariants and property tests](planning.md), the [indexed-planner proof obligations/counterexamples](planner-proof.md), and [actual planner scaling measurements](performance-planner-v3.md). The results retain allocation/retained-memory counters, tiny-script controls and baseline-comparability limits instead of claiming every script improves. Index/cache costs and shared-key storage are evaluated together, not confused with reduced semantic inspection.

## Bounded execution and stream lifetime

```text
ResourceLimits { jobs, max_streams, buffer_bytes, memory_bytes }
scheduler::execute_plan(plan, limits) -> Result<ExecutionSummary, RuntimeError>
scheduler::execute_plan_async(plan, limits, CancellationToken)
```

The scheduler launches only ready graph nodes and bounds finite copies separately from long-lived flows. An idle stream cannot consume every ordinary-copy worker. One payload-buffer budget covers both finite and asynchronous flows: admission reserves local payload storage, participating WSL/SSH helper buffers and any remote-writer framing buffer. Kernel-assisted finite copies reserve fallback capacity conservatively even when the successful fast path does not allocate it; a directory worker expands sequential leaves under one reservation. This cap excludes kernel buffers, framework-owned I/O staging, client/interpreter RSS, fixed diagnostic capture and unrelated process memory. Separate worker/stream slots do not bypass shared buffer admission. Configuration errors are rejected before effects. Finite copies use guarded native filesystem/client operations; streams use asynchronous bounded-buffer I/O and backpressure.

Stream producer/consumer connectivity is not a completion edge. Complementary channels form co-start cohorts, and the scheduler atomically admits all members after their external prerequisites and aggregate resource reservations fit. It validates the contracted graph and rejects internal completion dependencies or insufficient cohort capacity before I/O. This permits self-contained FIFO chains without blocking a producer on an unstarted consumer. Ready/running state, budgets and completion are owned by one scheduler state machine; workers do not mutate dependency counts.

Each flow terminates independently at source EOF or an OS-reported closed destination. The process/submission waits for remaining tasks; one flow's normal EOF does not cancel other flows. Cancellation closes asynchronous waits/handles and stops new task admission. Cancellation is cooperative: async endpoint waits close, native operations receive supported cancellation/progress signals, and completion is drained before owned handles are released. Dropping a Tokio task alone is not treated as safe kernel-I/O cancellation. For TCP/Unix destinations, peer EOF/FIN including a sending-half shutdown is the declared logical end-of-flow; reverse data is discarded by a bounded close watcher. This is an explicit one-way protocol, not generic duplex forwarding. The stream note records idle pipe/FIFO closure detection and rendezvous details. [Streams](streams.md) records actual platform support and remote-helper requirements.

`runtime::execute(program, ExecutionOptions { dry_run })` keeps its public signature and routes through preparation/scheduling. It rejects synchronous invocation inside an existing Tokio runtime with guidance to use the async entrypoint, rather than panicking in a nested runtime. Reports combine finite file/directory counts, stream bytes, operation completion and concurrency metrics. CLI/REPL candidates commit bindings after successful preparation; runtime failure does not roll back those bindings or completed writes. Filesystem writes are not a whole-script transaction; a failure can leave completed or in-flight independent writes.

The REPL owns at most one `ActiveJob`: a worker handle, cancellation token and owned diagnostic source/AST. A streaming submission returns to the command loop immediately. Read-only commands and pure declarations remain responsive, but new transfers and shared cwd/filesystem-changing commands require `/wait` or `/stop`. This deliberately avoids separate plans racing or exceeding a per-session budget. `/quit`, interactive EOF and guard destruction cancel/join; piped EOF drains. The worker carries explicit telemetry context, and completion errors render against the saved source rather than a reused input buffer.

## Uniform source/sink I/O

The semantic operation is one byte flow for all non-directory data: ordinary files, FIFO/named pipes, sockets, HTTP response bodies and remote channels. Directory handling is structural expansion into child flows. This removes the old conceptual split where only IPC was a "stream" and ordinary files lived outside the data-flow abstraction; finite/stream classes still help scheduling and legal optimization, but do not define separate byte semantics.

`src/io/mod.rs` is the common contract and OS-dispatch boundary. `FlowReport { bytes, end }` and `FlowEnd` describe accepted progress and closure. Kernel helpers return `KernelProgress { bytes, end: Option<FlowEnd> }`; `None` requests a bounded fallback using the same owned endpoints and the already advanced offset. `CopyFailure.bytes` records an accepted/uncommitted prefix rather than discarding progress when an OS operation fails. The finite runtime keeps a typed `CopyOutcome` separating committed `ExecutionReport` bytes, discarded current-file bytes and cancellation; public flow callers receive a typed `FlowEnd`, not a cancellation flag recovered from English prose. A flow progress count is not by itself a promise that every byte was published to a durable file target.

Native file-to-file paths select Linux `copy_file_range` or Windows `CopyFileExW` where applicable. File-to-plain-TCP paths select Linux `sendfile` or Windows `TransmitFile`; the Windows adapter uses the measured at-least-1-MiB-remaining cutoff and keeps smaller flows buffered to avoid setup latency. Unsupported operations resume without reopening the connection, duplicating a prefix or silently dropping an error. Other pairs use the bounded async adapter path. These are capability-selected implementations of the same flow contract, not a promise that remote encryption, arbitrary filesystems or every endpoint pair is zero-copy.

The event-driven application runtime remains Tokio, with platform epoll/IOCP integration and appropriate readiness/completion handling. The remote Python helper is an optional endpoint adapter, not a replacement for that runtime. Actual `io_uring`/Windows I/O Ring probes establish capabilities separately from measured workload suitability; no production ring adoption is inferred merely from a successful probe. The [Linux I/O investigation](io-linux-v3.md) distinguishes native ext4, tmpfs and DrvFs, verified guest-page-cache state, bounded queue depths and matched threaded controls. Deep rings improve some large single-flow DrvFs cases but do not beat the selected native ext4 copy path. The [Windows I/O investigation](io-windows-v3.md) records actual ring operations and native thresholds. Offset/completion ownership and workload-specific decisions remain explicit; there is no universal no-ring or always-ring conclusion.

### Per-file output ownership

The finite native file adapter owns `io::StagedFile`: an exclusively created sibling pathname, the destination, and no-clobber policy. Data and copied permissions are prepared on that stage; publication occurs only after transfer success and a final cancellation check. Drop removes only the uncommitted stage, never the destination. An interrupted current file therefore preserves the old target or leaves a new target absent, while reporting how many bytes were discarded before commit. Earlier committed leaves and created directories in a tree remain and can be reported separately.

Overwrite uses same-filesystem rename publication. Assignment replaces a pathname binding rather than mutating the previous inode through all its hardlink aliases; those aliases retain old bytes. This explicit v0.3 change from in-place copying is required for rollback and protects pages queued by zero-copy source flows. No-clobber uses the OS no-replace primitive where available, with a safe publication fallback rather than an exists-then-open race. The sibling placement is required for atomic same-filesystem publication and is a transport artifact, unlike repository scratch data under `.temp`. This is not a whole-tree transaction, filesystem durability barrier, or protection against hostile concurrent path replacement.

Stream-file sinks and remote file sinks must retain the same ownership/commit semantics through their adapters; their integration and interruption evidence are documented with those transports rather than assumed from the native finite path. Cancellation cleanup must not unlink a target merely because the stage shared a similar proposed name, and it must not release borrowed buffers/handles before an outstanding kernel operation is completed or cancelled.

## Safety, transports and telemetry

The regular-copy executor preflights selected trees, rejects symlinks/reparse points, unsupported device entries, same-file/hard-link copies and overlapping source/destination directories in both directions. File identity rather than lexical spelling covers Windows aliases. Exclusive create enforces no-clobber atomically. Hostile concurrent replacement between path checks and use is still outside the portable path-based guarantee.

HTTP uses native curl; ordinary SSH copies use modern SCP/SFTP plus quoted POSIX helpers. `MSYNC_SSH_CONFIG` supplies a separate `-F` file for both clients without editing existing SSH settings. Local transport scratch lives under `.temp`, remote uploads use a private `mktemp` directory, and task-owned resources are cleaned on their normal/error paths. The authenticated SSH fixture is isolated in WSL namespaces/chroot and fingerprint-checks existing SSH/account files unchanged. See [runtime safety, transport requirements and fixture evidence](runtime.md).

One opt-in local JSONL telemetry session links parsing, compilation, preparation, planning, scheduling and transport work. `TraceContext` captures a parent across tasks; `LinkedSpan` can remain alive across `.await` without entering thread-local state. `with_context` temporarily attaches a parent around synchronous blocking work. Useful endpoint/stage paths, backend choices, lifecycle phases, typed OS errors and cause chains are retained. File payloads and unrelated environment snapshots are not automatically captured. Exporter failure is visible, and the outer scope flushes after children finish. See [the telemetry schema and concurrency contract](telemetry.md).

## Verification and scope

Use the [validation record](validation.md) for current executed suites and platform boundaries rather than copying changing test totals here. The retained tests include source/formatter properties, random graph final-state equivalence and fixed-point idempotence, real bounded scheduling/EOF/cancellation, local/HTTP/SSH transfers, Windows pipe/TCP behavior and telemetry parent graphs. Environment-dependent WSL/SSH or Unix tests must be run in their corresponding environment; another platform's result is not substituted as evidence.

No deletion, reconciliation database, implicit shell evaluation, watch daemon, background service installation, or bidirectional protocol is introduced. The architecture retains the useful compiler separation described in the [Rust compiler overview](https://rustc-dev-guide.rust-lang.org/overview.html), while the [Unison specification](https://www.cis.upenn.edu/~bcpierce/papers/unisonspec.pdf) helps distinguish a filesystem-state contract from implementation details without claiming Unison-style reconciliation. WSL/SSH adapters used by asynchronous socket/IPC relays require Python 3 for the fully type-annotated, nonblocking remote helper. The specialized ordinary remote filesystem-copy path keeps its POSIX/GNU-helper requirements without a Python dependency; both implementations still satisfy the semantic byte-flow contract.
