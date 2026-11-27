# Unified local telemetry

See [observability ownership](observability-architecture-v086.md) for the
compiler, runtime, CLI/LSP, exporter and extension boundaries.

## Native AOT implementation (0.8)

The C# CLI retains opt-in `--trace FILE`, `--trace -`, `--telemetry-db FILE`,
configured destinations, and `--no-telemetry`. `TelemetrySession` owns one
bounded BCL channel and one writer task. Ordinary levels use nonblocking
`TryWrite`; overflow is counted and reported as incomplete recording. The
explicit diagnostics level instead waits for the writer. Disabled
execution supplies no runtime observer, compiler trace or profile. The
uninstrumented interpreter does not test for profile counters per opcode.

Compatibility runtime observations describe actual transfer completion/failure,
native failures, and process lifecycle. The CLI records which stages really
ran, with an explicit parent span ID. Ordinary runtime effects attach to the
runtime owner in a standalone command or the session in REPL, because a
Process can outlive its submission. Diagnostics additionally propagates its
selected operation ancestry through asynchronous work. The root record is
held outside the bounded queue and
written after admitted records drain, so a full queue cannot silently lose the
command outcome. Root errors retain the exception chain; runtime failure records
retain their operation-specific causes and useful source/destination metadata.
Runtime `success`/`failure` outcomes map to the established JSONL `ok`/`error`
vocabulary. The serializer uses generated `System.Text.Json` metadata, not
reflection-based serialization.

SQLite recording uses the mature
[Microsoft.Data.Sqlite provider](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)
at version 10.0.14. Its
[default bundled SQLitePCLRaw provider](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions)
uses a native library adjacent to the Native AOT executable: `libe_sqlite3.so`
on Linux, `e_sqlite3.dll` on Windows. Distribution remains independent of an
installed .NET runtime, but the native sidecar must be packaged and tested.
No ORM or runtime code generation is involved. This deliberately avoids a new
custom SQLite binding or a less established static-link package.

The database keeps application ID `0x4d53594e`, schema version 1, and the existing
normalized tables and views. The original Rust telemetry query/export commands
can read recordings from the C# implementation; schema compatibility does not
require reparsing opaque JSON blobs. Schema claiming uses an immediate SQLite
transaction; foreign databases are not adopted or changed to WAL. A single
worker owns reusable SQL commands, commits bounded batches, and flushes an
available partial batch immediately. This stays within the configured maximum
flush interval without an idle timer. NORMAL WAL synchronization remains the
default; FULL is explicit. Record/page/session budgets affect observation only.

Observer initialization, writing and closing failures warn with destination and
cause; they do not replace the actual command result. Explicit custom parents
are not created. A conventional state parent is created only when an actual
observer opens. Disabled telemetry, zero whole-session sampling, configuration
inspection, help, version and LSP admission create no observer output.

The executable harness is `tests/Telemetry/journeys.py`, with fixtures below
`.temp/telemetry-v080`. Its six actual-process journeys cover JSONL/runtime
bytes, SQLite native loading and repeated-session append, failed-copy outcome
and causes, observer-open failure preserving file work, disabled/zero-sampling
non-admission, and preservation of an unrelated SQLite database. Managed CLI
execution and the installed-layout Linux Native AOT package both passed all six
journeys. The native image SHA-256 was
`c8333cd814b31a6d402525abe483b60bfbe07a6e9c35008c05f62e5d95daa7f9`;
the exact invocation was:

```sh
python3 tests/Telemetry/journeys.py artifacts/package/msync-0.8.0-linux-x86_64/bin/msync
```

The JSONL success/failure recordings each contained three records; two SQLite
invocations appended six records and two completed sessions. The original Rust
0.7.3 `telemetry summary` command independently read the resulting schema-1
database and reported both sessions and the command/compile/transfer phases.
Native results live below `.temp/telemetry-v080/c8333cd814b3/`. These are Linux
native package results, not a claim of Windows execution.

## End-to-end self-profiling

```sh
msync run workload.msync --telemetry-db .temp/workload.sqlite --telemetry-level profile
msync run-ir workload.mexe --telemetry-db .temp/workload.sqlite --telemetry-level instructions
msync run-ir workload.mexe --telemetry-db .temp/diagnostic.sqlite --telemetry-level diagnostics
```

Recording remains disabled unless a destination is supplied on the command
line or configured under `[telemetry]` with `enabled = true`. The CLI
`--telemetry-level` overrides the TOML `level` and does not itself enable
recording. `--trace` writes the same records as JSONL, with stdout reserved
for program or artifact output. Use separate trace IDs for repeated runs and
compare matching workloads/optimization levels. Root attributes include
`input.kind`, `optimization.level`, `telemetry.level` and
`implementation.build` (assembly informational version).

| Level | Additional recording |
| --- | --- |
| `summary` (default when enabled) | Command outcome, actual artifact read/write and compile containers, binding/preparation commands, runtime initialization and execution, existing transfer/process observations |
| `phases` | Source reads, parse/bind/analyze/HIR lowering/optimization spans, preparation within the standalone execution host, scheduler counts on the execute span |
| `profile` | Detailed compiler phase/function/pass spans and work/decision metrics, aggregate optimizer-pass events and VM function/opcode/native counts, process CPU and managed allocation/collection counts |
| `instructions` | Aggregate dynamic bytecode positions, branch/iterator edges and actual source call-site targets |
| `diagnostics` | Exact operation timeline: VM frames and awaits that see incomplete work, scheduler admission and dependency links, transfer/transport and process lifecycle; lossless queue backpressure |

### Native guest-tier counters (0.9)

At phases/profile/instructions levels, the actual `execute` span includes
`tier.compilations`, `tier.compilation_ns`, `tier.code_bytes`, `tier.entries`,
`tier.operations`, `tier.direct_operations`, `tier.raw_operations`, `tier.guard_misses`,
`tier.deoptimizations`, `tier.suspensions`, `tier.recompilations`,
`tier.cancellation_polls` and `tier.unavailable_compilations`. These describe
the guest execution backend, not the host CLR JIT or retired numeric workers.
JIT-off emits real zeros. An entry count does not prove a useful optimization;
compare direct/bridge work and compilation costs with uninstrumented timing.
`tier.linked_entries` and `tier.linked_operations` separately count prelinked
managed Tier-1 regions; they are not reported as generated-native entries.

CLI snapshots are recorded even when execution fails. A persistent REPL records
per-submission differences rather than repeatedly publishing lifetime totals.
`tests/Telemetry/tiering.py PATH_TO_MSYNC` verifies actual native entry and the
JIT-off control through SQLite, then checks two real REPL submissions each
report only their own compilation/entry. Diagnostic executions are separate
from benchmark samples, and counters must not be inferred from disassembly.

### Native scheduler counters (0.9)

The native interpreter exposes the neutral `SchedulingStatistics` snapshot and
the CLI records it on the actual `execute` span, including failed execution.
These are native admission events, not historical interpreter-tier estimates:

| Metric | Native meaning |
| --- | --- |
| `scheduler.admitted_operations` | Actually admitted transfer dispatches, including directory/metadata actions; one Range admission is one operation, not a guessed file count. |
| `scheduler.admitted_transfers` | Payload transfer dispatches, excluding EnsureDirectory/ApplyDirectoryMetadata plan actions. |
| `scheduler.submitted_plans` | Executed plan submissions, not plans merely present in an image. |
| `scheduler.submitted_batches` | Actual prepared-plan dispatch units; the current native executor submits individual nodes rather than executing recorded grouping hints as batches. |
| `scheduler.eliminated_transfers` | Recorded eliminated nodes associated with plans actually submitted. |
| `scheduler.resource_nodes_visited` | Real `ResourceIndex` prefix-tree lookup/registration visits. |
| `scheduler.dependency_edges` | Unique resource, ordering and explicit plan predecessors matched by transfer admission or native resource queries. |
| `scheduler.native_hazard_waits` | Native resource-query awaits that actually encounter incomplete predecessors. |

Counters remain available without recording. Admission and wait clocks run only
when an observer/timeline is enabled; otherwise timing fields remain zero.
Admission time includes synchronous dispatch work; accumulated wait time may
overlap across operations and is not command wall time. Detailed mode records
`scheduler.admission`, `scheduler.native_dependencies` and `scheduler.wait`, with
actual task links, pending counts and suspension flags. Transfer observations
cover real planned or residual I/O intervals, retain concrete errors, and use a
kind label rather than copying String payloads into source metadata.

An unresolved source preserves its statically proven kind: a deferred String
payload does not read the filesystem, whereas Path/Range/unknown sources retain
conservative read footprints. This classification does not demand the future;
its payload dependency and Process lifetime remain intact. The scheduler journey
in `tests/Telemetry/journeys.py --case scheduler` checks a prepared literal copy
and a process-gated source: independent publication succeeds while a relevant
query waits, the wait links to the held admission, and a deferred Path control
copies checked file bytes rather than the path spelling.
`tests/NativeExecution/SchedulingChecks.cs` additionally checks actual residual
file work and public counters with recording disabled (zero timing fields),
then the same source with an observer and one real transfer interval. Failure
journeys retain scheduler counts on the failed execute span and concrete I/O
causes on the transfer record.

Object and executable loads are artifact reads, not compiler stages.
An executable submitted for replay does not report binding or planning;
relocatable source, HIR and objects report the stages their route actually
enters. Standalone preparation uses the host's real binder and contextual
planner and retains their resource ownership. The `execute` span begins
after host initialization. A REPL submission has its own compile and execute
children, including the result display that can demand a deferred Range;
shell-prelude submissions are labeled separately. Runtime profile rows belong
to the execute span via their event owner, and aggregate optimizer pass rows
belong to the enclosing compile span. Pass event `duration_ns` is a metric
summing disjoint invocations, not a wall-time interval.

For the current native-v1 runtime, `vm.function` contains frame entries
(`calls`), dynamic opcode attempts (`instructions`), logical allocation counts,
elements, storage bytes, frame-placed allocation counts, and tiering counters.
`logical_storage_bytes` counts the raw region plus reference slots for executed
IR allocations; it excludes managed object/array headers, initialization flags,
and allocations made separately for frames or closure environments. It is not
a live-heap or total managed-allocation measurement. The historical boxed VM's
`number_boxes` metric does not describe this representation.

Field addressing and memory operations are visible as opcode/site counts,
including `AddressOffset`, `ProjectAddress`, `Load` and `Store` when present in
the executed IR. There are no dedicated counters for fixed-layout versus checked
projections, ready versus pending memory reads, or per-field access latency.
Instruction hits describe IR execution, not physical memory-access counts.
`vm.opcode` and
`vm.instruction` sum the same attempted instructions in different dimensions.
At instruction detail, `bridge.entries` identifies the actual transition
into the tier bridge; `bridge.executions` counts instructions dispatched
inside a batch, including its first executed operation. A guard or
readiness path entering the bridge from otherwise native code also records
`bridge.guard_entries`. The source opcode and linked backend opcode can
differ (for example, an address-plus-load pair becomes one offset load);
bridge records carry both, plus the linked handler and the source span.
Dedicated frame CALL continuations are not recorded as generic bridge
entries.
`vm.edge` measures actual next-instruction transitions after jumps or
iterator-next; it does not count a successor never entered after an abort.
`vm.call` captures the actual selected source target; callbacks admitted
by a native range adaptor contribute function entries without a fictitious
source call-site. `vm.native.calls` includes `fast_calls` as a subset.
`image.id` is a session-local identity, not a content hash. Group by trace
and image before joining function IDs or instruction positions. Use exact
artifact bytes or source revisions as separate benchmark inputs.

The HPIR candidate uses the same VM census with `ir.kind=hpir`.
`stack_frames` and `stack_frame_bytes` count native frame entries and their
payloads; `call_physical_argument_bytes` counts the actual parameter bytes
copied across those frames. `stack_segment_native_bytes` counts real native
segment reservations, including their headers and unused capacity, not
live frame payload. `hpir.stack.segment` records allocation, replacement,
or reentry at the transition, with the attributed caller and target.
`hpir.execute` records a failed or cancelled execution with its instruction
context and the number of native frames discarded. Detailed census adds
source-attributed executed instruction and taken edge counts; ordinary
copies and calls do not allocate observer objects in the recording-off path.
`hpir.heap.collect` describes the collection triggered by an allocation,
including the initiating function, instruction and layout. Root counts
separate exact base locations from borrowed stack/interior pointers; borrow
matches and indexed objects describe the actual collector work performed
for those pointers. Live, reclaimed, reusable and released bytes count
native extents, including their object headers. `vm.native` identifies the
allocation layout and issuing site; its allocation counts are independent
of the number of new native slabs reserved by the allocator.
Typed scalar services are counted at their physical call sites by binding
ID and service name. Float and Bool math calls use one fixed-width result
with no scheduler events. `hpir.host.write` records source attribution,
output character count, and whether the actual writer was incomplete on
admission. Failure keeps the writer's concrete error at the source call.
Ready String operations use \`vm.native\` with the selected String member
or ordinal comparison. \`text_ascii_case\` and \`text_unicode_case\` count
entered casing implementations, while \`text_inputs_reused\` counts
immutable input results returned without a guest allocation. Only
String-producing operations are potential guest collection points.
`hpir.regex` reports the actual typed operator or standard call, literal
versus dynamic pattern, input/output UTF-16 sizes and match or reuse.
Malformed patterns retain their engine exception and issuing source.
`hpir.regex.find` adds first-match presence and actual capture-group count;
`regex.group_layout_checks_elided` counts exact snapshot element layouts
reused instead of re-reading the runtime object header.
Selection separately counts `paths.live_selection_plans` and
`paths.live_selection_transfers`; `file.selection.transfer` describes the
deferred host cohort call, and `hpir.effect.admit` records the actual
directory read/write predecessors rather than enumerating at capture.
`range.path_selection_opens` counts selected regex Path cursor sites.
The VM records `fs.path_selection.open` independently of plain
directory Open. Cursor spans retain `selection.pattern` and the same
source, first-pull, yielded and close reasons as typed directories. An
overlapping writer remains a real dependency of the first pull.
One `hpir.link.text_literals` interval accounts for linking the image's
UTF-16 literal table into native memory, including its exact word count
and reserved bytes. A later static-address operation does not allocate.

`connections.literal_relocations` counts physical endpoint-key
relocations; `paths.typed_qualifications` counts selected Path construction
sites. `hpir.path.qualify` records the actual endpoint kind/address at
the source site, without acquiring a transport-registration span.
The disabled path needs neither source lookup nor address Text decoding
for ordinary SSH/WSL qualification.
Typed input uses the same reader-admission spans and a distinct
`hpir.work.submit` and future demand. A Text projection (`is_null` or
numeric parsing) can exist while the reader remains pending; only an
observed demand publishes UTF-16 in the guest heap. A direct materializing
text demand has its own `hpir.future.text_demand` span. When a scalar
projection reaches it, that span is nested under the scalar demand.
`compiler.llvm.lower.readiness` reports `flow.observed_text_conversions`
when a dominating control demand already published the same input
completion; the emitted parse is then a ready `ParseF64` or `ParseBool`
service, not a new scalar projection or host demand.
`flow.observed_text_nonentry` counts such conversions after an observation
outside the function entry, for example immediately following a loop.
Subsequent call and capture metrics reflect the ready producer result.
`flow.snapshot_item_edges` counts materialization and pattern-prefix
edges carrying item readiness after traversal has already completed.
Typed Process stdout/stderr materialize under the same text demand, with
`text.completion`, `text.characters` and the causal link to the one
Process job. Frame exit distinguishes promoted from joined work.
`gc.process_results_released` counts completed host results whose exact
guest Process graph is no longer reachable.
`process.argv.sites` and `process.argv.operands` count source Process
launches and their producer-selected argument carriers. The `ProcessStart`
import records the Text/Path/Number/Bool ABI independently of physical
word width. `hpir.process.argv` attributes host argument materialization to
the original Process launch and its executable; it never records argument
contents. The bound Path supplies its own anchors and only host strings
cross into process work. There is no guest String formatting round trip for
Number or Bool arguments.
`process.spawn.ready_vector` counts typed snapshot admissions;
`process.control.sites` identifies fixed Process controls at selection.
`hpir.process.start_wait` records waiting for startup only. The separate
`hpir.process.control` spans link the selected startup or completion task,
with the operation and `process.wait_for` recorded on the original call.
`process.stdout.ready` and `process.stderr.ready` in `vm.native` record
materialization after a known completion edge. `future.ready_demands_elided`
counts selected Bool producer results copied or aliased without a wait.
Direct Bool-to-file formatting is selected as `paths.transfer_bool_text`.
The corresponding execution site is `transfer.live_bool_text`; the file
admission span links the original typed result task as
`transfer.text_input` and records whether it was pending.
A projected ready `Path | String` transfer counts once in
`paths.transfer_union_sites`. Its selected Path or String arm reaches
the existing `hpir.effect.admit` span with `transfer.kind = path` or
`text`; the other arm submits no job. The metric counts source sites,
not the two physical import branches.
`future.folded_field_lanes` counts loads and stores that use a preserved
layout on the memory operand after LLVM folded its zero-offset GEP.
`future.union_cells` counts surviving SSA union values with an actual
scalar completion lane; fully ready unions are not counted. The producer's
`flow.future_parameters`, `flow.future_results` and `flow.future_fields`
include those selected boundaries. A pending null predicate uses
`hpir.future.lift` with `operation = ScalarIsNull`; existing root maps and
demand telemetry report its selected dependency. O0's
`compiler.hpir.canonicalize.values` records promoted-local and pruned
function counts and includes the existing local-promotion and DCE events.
`flow.snapshot_completion_sites` counts literal producers whose actual
Number or Bool elements can retain completions. `vectors.completion_elements`
counts the selected physical item lanes; `future.snapshot_items` counts
iterator outputs with an extra exact-root lane in their generated frame.
`future.shared_completions_reused` counts nodes already completed by
another consumer during a real host wait; `roots.active_completion_paths`
counts exact roots kept alive while that wait pumps cooperative work.
`paths.transfer_number_text` counts a Number cast observed directly by
FileOut. `hpir.effect.number_arguments` distinguishes inline, published,
and genuinely pending Number. Only the latter publishes a precise
guest root and a typed host handoff. Admission and execution link the
`transfer.number_input` dependency; the guest advance span distinguishes
parking and publication, and `roots.effect_number_marked` counts roots
kept alive by the handoff rather than the active guest frames.
`strings.chunk_appends` counts selected typed chunk append sites.
`hpir.text.append_chunk` records the actual storage decision
(`initial`, `extended`, `grown`, or `forked`) and
`chunks.length`, `chunks.capacity` and `chunks.copied` in String
references. The existing `range.fields.snapshot_consumers` metric
reports the selected policy operations and Process argument snapshot
alongside other producer-owned snapshot consumers.
Explicit file writes report `paths.file_text_writes`,
`paths.file_text_appends` and `paths.file_text_deferred_inputs` at
selection. `hpir.file_write.arguments` links a pending typed operand
before reservation. `hpir.file_write.admit` links the live and prepared
predecessors of the destination lease, and `hpir.file_write.write`
records the concrete operation, input character count, dry-run action
and failure. The guest observes the physical completion at that site.
`paths.rename_sites` counts selected `fs.rename` calls. At execution,
`hpir.file_rename.admit` attributes both write locations and their effective
endpoint, counts atomic source/destination registrations including their
physical aliases, and links overlapping residual or prepared predecessors.
`hpir.file_rename.rename` reports the synchronous host operation, dry-run
simulation or failure at the original guest source.
The selector reports `vectors.at`, `vectors.front`, `vectors.first`, `vectors.size` and
`vectors.bounds_known` for materialized vector consumers. `vectors.type_quantifiers`
and `vectors.scalar_quantifiers` count typed predicate loops, while
`vectors.empty_quantifiers` counts shapes that need only their Boolean identity.
Actual calls at the sole
predicate site remain visible in `vm.call` and the function census.
`range.transform.scope_text_vector_plans` counts mapped member-pointer
plans, not element copies. `range.transform.completion_calls` counts call
sites that forward a Number completion lane rather than forcing its
payload. `range.numeric_snapshot_sites` counts Number plan materializations
into the usual snapshot ABI; `vectors.reverse_snapshots` counts ready
word-buffer reversal sites. `meta.static_alternative_sets` and
`meta.static_alternatives` count Type alternative sets emitted directly by
the HIR producer and the actual number of witnesses, respectively.
`meta.same_type_witnesses` and
`meta.type_name_lookups` count selected sites; the image phase records
`meta.type_display_literals` for the producer-built Text suffix, and
`vm.native` attributes actual `meta.type_name` lookups to the guest call site. The execution
census records reachable ordinal conversion and pointer operations; only
the cold failure path invokes a host service at the original source.
LLVM production records `builtin.range.nullable_number_items_packed` per
function for literal operands selected into a shared finite carrier.
For direct snapshot field consumers, `range.fields.snapshot_consumers`
counts selected source operations and `range.fields.snapshot_operands`
counts their individually selected operands. The producer records actual worklist
nodes and edges, unseeded components' affected nodes, propagated conflicts
and joins of incompatible payload carriers. `range.fields.snapshot_transform_sources`
counts exact Number transform sources whose return ABI flows through
materialization. `arguments.scope_layouts_retained` counts the physical
domains forwarded at by-value function entry. HPIR accesses the selected
carrier without a second field analysis. `range.slide.borrowed_plans`,
`range.slide.frame_cursors`, `range.slide.pulls` and `range.slide.local_reads`
count selected two-item window sites; the VM census sees the actual
comparison hits and zero window allocations in the standard sorted check.
Ready scalar casts report `casts.primitive`, `casts.bool_text_literal`,
`casts.string_parse` and `casts.path_parse` at compiler selection.
`hpir.scalar.parse` attributes Bool/Path parsing to its physical carrier,
result kind, UTF-16 input length and original cast site; a failure retains
the concrete diagnostic. A Path-to-Number span records its parser path;
the function census counts actual String-to-Number short-integer, general
and invalid results, without a span for every line. Primitive Bool/Number
casts appear as ordinary
executed guest instructions rather than managed work.
`compiler.hpir.image.decode` records whether input is extent-bounded
and how many file-length queries the decoder made. The separate
`input.read_line` metrics expose whether the reader turn or the actual
buffer refill suspended.
Live Text Range records typed `hpir.text_range.open`, `.next`, and
`.close` intervals with the cursor and source site. Open registers a host
cursor without reading; Next records the actual suspended or completed
read, UTF-16 units and EOF. Close distinguishes explicit control flow,
exhaustion and unwind. A file cursor only checks the live resource
frontier on its first pull, and `hpir.effect.read_dependencies` links its
actual overlapping transfer predecessors. The execution census counts
yielded lines, EOF pulls and UTF-16 units without storing line contents.

Typed directory Path cursors report `range.directory_sources` and the
selected open/pull/close sites. `hpir.directory.open`, `.next` and `.close`
record actual cursor identity, resource, recursive mode, first pull,
yield and cleanup reason. `hpir.effect.read_dependencies` links actual
overlapping file operations at first pull; creating the directory plan
does not enumerate or consult the namespace.
An iterator with a guaranteed static prefix reports
`range.iterator_selection.recipes`. Binding attributes
`backend.hpir.bind.iterator_selection` to the lexical directory, regex,
and source, with the observed member count or reason the plan remains
residual. The cursor reports `selection.membership=frozen|live`; frozen
pulls count actual yielded paths without reporting a host first pull.
The Path-snapshot filter reports `range.filter.path_plans`,
`range.filter.path_opens`, `range.filter.path_pull_sites`, and the number
of selected `range.filter.predicate_demands` sites. The VM census counts
actual selected predicate calls and actual Future-demand calls separately;
the ready arm of a mixed Bool predicate executes no demand import.
`strings.join.live_cursors` reports selection of the live terminal;
`hpir.text_range.join` reports actual consumed lines and final UTF-16 size.
It retains the underlying open/pull/close intervals instead of attributing
input waits or close failures to a generic String operation.

`hpir.file_read.admit` reports the exact metadata operation, resource,
endpoint, ready completion and number of relevant live predecessors.
`hpir.file_read.read` records the concrete host query and any underlying
filesystem error. `vm.native` uses the original qualified operation name;
a ready metadata read does not allocate a guest completion or managed job.
`io.read_text` adds the physical resource kind, stream co-start decision,
and resulting UTF-16 count. A transfer consuming the same pending result
records a `transfer.text_input` causal link, readiness at admission and
at actual payload demand, and the transferred Text size. Scope exit
records which jobs were promoted as effect dependencies; collection
counts old completed read results released after their guest cells die.
`effects.transfer.shared_frontier` counts selected void `SharedTransfer`
sites. `hpir.effect.admit` and related read/transfer intervals record
`dependencies.cross` and causal links for actual overlapping work from the
other admission partition; `dependencies.prepared` continues to count
precomputed plan edges. Pure prepared calls do no live resource lookup.

Selection counts `effects.work.lexical_frontiers`,
`effects.work.lexical_exits`, `effects.work.owner_joins`, and
`effects.work.process_joins`. The lexical Mark and Publish imports
record `hpir.work.lexical_mark` and `hpir.work.lexical_publish` at the
original control edge. Publication reports the saved job ordinal, work
examined, pending finite transfers and selected process-dependent flows;
`work.predecessor` links actual completion jobs. A subsequent host effect
links its published predecessor as `work.lexical_predecessor`, separate
from resource hazard links. Bare and selected Process joins report their
actual joined and still-waiting job counts and retain attributed failures.

The x64 HPIR executor retains the same IR instruction and edge
census. `dispatch.engine` distinguishes the execution mechanism.
`machine_entries` counts native block invocations; `machine_instructions`
counts actual HPIR operations performed by native code. `machine_calls`
and `machine_returns` count completed in-segment guest transitions, not
host or segment transitions. Their instruction, call-site and frame totals
are included in the ordinary HPIR counters. `hpir.machine.emit.function`
attributes emitted instructions, direct call sites and code bytes to the
function and source. `guest.direct_cached_sites` distinguishes baked-in
addresses from `guest.direct_late_sites` that read the mutable entry table;
remaining direct sites are local relocations. `scalar.forwarded_operands`
and `scalar.elided_stores` record the actual scalar-run choices.
`hpir.machine.publish` records executable-page publication separately.
`guest.inline_service_sites` counts typed physical
layout and CPU-supported ready math sites emitted as instructions; `vm.native.machine_inline_calls`
counts their dynamic executions without losing the original operation/site
census or adding a guest frame. Unobserved machine code has no counters.
For adaptive runs, `hpir.execute` records the promotion outcome, count of
cached bodies and published code bytes; a promoted run reports both
engines. A `hpir.machine.promote` span attributes a function cache miss to
the actual backedge or direct call and records the source, resume PC,
function-local interpreted work and budget, and native calls waiting for
the target. Each promotion owns the emission and page-publication spans.
Unsupported hosts stay physical. If publication fails, ready entries
remain usable and the rest stay physical without retrying.
The native RET boundary selects the next stack-segment or work-owned exit
at managed reentry, using the same return branch for both.

`hpir.link.globals` records linked singleton roots, payload and reserve
bytes. Collection reports `roots.globals` separately from frame root
locations. Compiler HPIR selection reports actual global relocations per
function, plus finite-union packs, projections, repacks and observed tags;
producer lowering separately counts removed representation round trips.

`tail.self.loop_sites` counts selected closed self continuations;
`tail.self.argument_moves` and `tail.self.move_cycles` expose the
parallel physical rebinding. Rejected candidates distinguish ABI,
local storage, an observing continuation, and calls that may own work.
The executed same-frame loop is recorded by ordinary `vm.edge` rows
at the original call source; it does not increment physical CALL or
frame counts.

`hpir.guest.launch`, `hpir.guest.slice`, `hpir.guest.resume`,
`hpir.guest.demand` and `hpir.guest.settle` attribute actual cooperative
Number delivery. A parked slice links the host completion that prevented
progress and records its resume PC; an empty range has no callback slice.
Collection's `roots.cooperative_frames` counts parked stacks and the
paused caller scanned alongside the active stack. Selection reports the
actual typed continuation sites, not syntactically declared callbacks.
A work-owned return reports the number of actual guest completion roots
that can survive the frame. Record lists belong to their exact frame, so
settling one frame does not enumerate unrelated jobs. A frame that owns
only non-returnable effects settles them without scanning the guest heap.
Prepared effect selection reports the sliced operands and the number
retained for unrelated users; printing a shared literal does not disqualify
its independent prepared transfer.
An admitted metadata predicate reports `effects.context_checks` at
selection and a source-attributed `backend.hpir.bind.context_check` with
its frozen/residual decision. Only a residual predicate opens
`hpir.effect.context_check` during execution.

The profile records aggregated counters, not one SQL write, timestamp or
object per opcode. It drains at execution/submission boundaries after guest
work settles, and queues the bounded-workload counter table as one envelope,
then persists rows in bounded SQL transactions. Queue overload or sink
failure leaves the recording explicitly incomplete. Profiling increases
execution work: compare timing with telemetry off; use profile runs for
work counts, and interpret `execute` allocation and CPU as process-wide
deltas, which can include exporter and other work. Compiler allocation
counts are synchronous thread-local deltas. Trace wall time starts with
observer admission. Root metrics `startup.arguments_ns`,
`startup.configuration_ns`, and `startup.exporter_ns` measure the completed
early intervals, without inventing before-admission spans. Exporter startup
overlaps the root span; process launch before managed entry is outside these
metrics.

### Causal runtime timeline

`diagnostics` records one interval for each instrumented operation actually
selected, not for candidate paths mentioned in the source or an unselected
branch. It retains path/endpoint provenance, source positions, typed outcomes,
and operational causes. It does not collect process output payloads, arbitrary
argv or credentials as trace attributes. A frame interval includes nested
execution and time spent awaiting owned work. `vm.await` records a task that
was incomplete when inspected immediately before an await; it does not equate
syntactically async code with suspension or estimate on-CPU time.

Selected native operations have inclusive `vm.native` spans even when they
finish synchronously; the same name in `events` denotes aggregate call counts.
A deferred native admission and its eventual completion are separately
labeled. The scheduler distinguishes relevant resource dependencies, ordering fences,
capacity waits, owner/global joins and unresolved-member fallbacks.
`scheduler.wait` reports matched and pending task counts and the observed
ready/incomplete state. Each `runtime.dependency` event is owned by the wait
span; `dependency.span_id` points to the corresponding admission when this
trace owns it, while `dependency.task_id` identifies the actual awaited task
even when it has no instrumented producer. The link is separate from parent
ancestry, so one wait can depend on multiple producers without inventing a
tree. Transfer, transport and process intervals remain children of their
causal operation across actual worker boundaries. Scheduler counters on
`execute` include cumulative admission and wait durations; those can overlap
each other, operation spans, or VM awaits. Do not add them to obtain an
exclusive time breakdown.

This highest level uses the existing bounded
[.NET Channel](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels#full-mode-behavior)
in lossless wait mode. Producers may wait for the local writer, including
when a lot of selected work occurs; other levels still use nonblocking
admission and report overflow. Explicit whole-trace sampling and recording
budgets still apply. A failing exporter closes admission to release blocked
producers and records incomplete session health rather than changing the
program's result. Compare latency with telemetry off, not by subtracting
nested diagnostic intervals.

The schema remains v1. For example, identify hot functions in the latest
complete recording with the stock SQLite CLI:

```sh
sqlite3 -readonly .temp/workload.sqlite <<'SQL'
WITH trace AS (
  SELECT trace_id FROM sessions WHERE status = 'complete'
  ORDER BY started_unix_ns DESC LIMIT 1
)
SELECT image.value AS image, fnid.value AS function_id, fn.value AS function,
       sum(m.value_int) AS instructions
FROM events AS e JOIN trace USING (trace_id)
JOIN attributes AS image ON image.record_id = e.id AND image.key = 'image.id'
JOIN attributes AS fnid ON fnid.record_id = e.id AND fnid.key = 'function.id'
JOIN attributes AS fn ON fn.record_id = e.id AND fn.key = 'function.name'
JOIN metrics AS m ON m.record_id = e.id AND m.key = 'instructions'
WHERE e.name = 'vm.function'
GROUP BY image.value, fnid.value, fn.value ORDER BY instructions DESC LIMIT 20;
SQL
```

On a diagnostic database, identify what a real scheduler wait depended on:

```sql
SELECT w.trace_id, w.span_id AS wait_id, w.duration_ns,
       policy.value AS policy, reason.value AS reason,
       link.value AS producer_id, producer.name AS producer
FROM spans AS w
JOIN attributes AS policy ON policy.record_id=w.id AND policy.key='dependency.scope'
JOIN attributes AS reason ON reason.record_id=w.id AND reason.key='wait.reason'
LEFT JOIN events AS e ON e.trace_id=w.trace_id AND e.span_id=w.span_id
    AND e.name='runtime.dependency'
LEFT JOIN attributes AS link ON link.record_id=e.id AND link.key='dependency.span_id'
LEFT JOIN spans AS producer ON producer.trace_id=w.trace_id AND producer.span_id=link.value
WHERE w.name='scheduler.wait'
ORDER BY w.duration_ns DESC LIMIT 20;
```

Compare the same checked workload with [the paired CLI measurement driver](../../tests/Performance/profile_compare.py).
The collector follows the explicit parent/owner model of the
[OpenTelemetry tracing API](https://opentelemetry.io/docs/specs/otel/trace/api/)
without requiring a global tracing provider or a network exporter.

The focused [CLI journey](../../tests/Telemetry/journeys.py) checks the
aggregate levels, source/HIR/object/executable stage identities, optimizer-pass
events, profile count reconciliation, REPL submission ownership, and scheduler
counts plus a real destination payload. The nine journeys also exercise
existing JSONL/SQLite behavior, sink failure, failed transfers, and disabled
or sampled-out admission. They passed on the isolated Linux Native AOT
candidate `3a605f6253525ef5959caad242073a53edc655737a0f2dcd16a14503a8139ea8`
and the split CLI's isolated managed Release build. The Native receipt is
`.temp/telemetry-v080/3a605f625352/results.json`.

The extended Linux Native AOT candidate
`316c3310173b2ba07155a9a4bcd40a4c593771a0882aa13522d5cfd52f105214`
passed eleven CLI journeys. The two-route diagnostics control uses a real
gated child and a queue capacity of one; both runs have complete ownership,
an incomplete relevant wait linked to the held publication, a synchronously
completed `ranges.front` timing, exact copied/collected bytes, and no observed
unselected path or child payload. A deliberate SQLite record budget failure
releases producers without failing a real copy. The same binary passed four
loopback OpenSSH journeys, including runtime-selected and prepared work with
one client per transfer; see [I/O verification](io-process-v080.md#diagnostic-timelines-at-selected-physical-boundaries).
Receipts: `.temp/timeline-integrated/journeys-native.log` and
`.temp/telemetry-v080/316c3310173b/results.json`.

The paired driver also exercised the Native O0/O2 bound
[counter recurrence](../../tests/LoopOptimization/counter-recurrence.msync):
32,771 updates with a checked result of `163846.26844396352`, five alternating
telemetry-off pairs after warmup on logical CPU 11, then separate diagnostics
recordings. O0/O2 medians were 56.93/41.27 ms (paired median difference
−18.74 ms). The profiles explain the workload change: **950,440 → 557,136
bytecodes**, **2 → 1 source frames**, and **65,542 → 65,542 operand Number
boxes**. That implicates instruction/control-flow savings without suggesting
that private numeric storage was improved. This comparison changes the
whole optimization level, not one isolated pass. The O0 frame spans sum to
79.95 ms within a 40.79 ms execute span; their overlap is precisely why
inclusive intervals are not an exclusive CPU accounting. Raw matched
profiles and SQLite files are in `.temp/timeline-study/compare/`.

## Compiler telemetry

The v0.9 compiled-storage contract adds `compiler.storage.analysis` for producers
without an existing converged optimizer analysis, `compiler.storage.function` (function
identity, slots/escape/parameter/address/layout decisions) and
`compiler.storage.calls` (call sites, snapshot decisions and borrowed arguments).
Plans are relocated through compaction/coloring/folding and encoded in artifacts.
Artifact decoding no longer emits `compiler.verify.*`, `compiler.hir.verify` or
`compiler.artifact.allocation_hints`; explicit `NativeArtifact.Validate` remains
available to compiler/test diagnostics. This supersedes the historical admission
behavior in the table below. Runtime `vm.function` reports snapshot allocations
and tail argument buffer reuse separately from semantic snapshot/copy counts.

Compiler recording uses the same local JSONL/SQLite session as execution. `phases`
retains the existing coarse boundaries. `profile`, `instructions` and `diagnostics`
also record the following actual work; selecting a level alone never enables a
recorder. Diagnostics uses the existing lossless writer admission policy.

| Work | Observations and evidence |
| --- | --- |
| Source parsing | `compiler.parse` owns `compiler.lex` and `compiler.syntax`; source identity/UTF-16 length, token and top-level statement counts. Detail mode counts explicit `object` declarations, anonymous expressions and named expressions when their syntax branch is entered, including attempts before a syntax error. Supplied parsed documents emit `compiler.parse.reused`, not a fabricated parse interval. |
| Binding | `compiler.bind`, `compiler.bind.standard_demand`, `compiler.bind.standard_module`; declarations, locations, types, templates and modules, available/selected standard declarations, module exports, declaration-ordered layout field count and projection decisions. |
| Named Scope tail result | `compiler.bind.named_scope_result` identifies the source, enclosing function and Scope declaration; `scope.tail_result` counts a declaration lowered with a read of the same singleton. Statement-position declarations use the ordinary binding path. |
| Standard-source cache | `compiler.standard.document` identifies the module and whether its lazy document was cold or warm. A warm access is not another parse. |
| Semantics | `compiler.semantic.body` counts analyzed HIR node kinds, including repeated analysis; `compiler.semantic.standard_module` identifies module work. |
| Specialization | `compiler.semantic.instantiate` records template identity/name, call-site source range, publish/query mode, argument/capture counts and completed/incomplete cache hits or misses. `compiler.semantic.specialization` identifies a newly reserved function and its canonical schema key before body construction; `plan.exits` counts completion/return paths. `compiler.semantic.bind_plan` attributes call-site binding and effect projection to source/function identity, reports formal objects/cells/events and projected writes, and distinguishes applying effects from a query. No late function-folding phase remains. |
| Range construction and materialization | `compiler.semantic.range_literal` counts initialized elements; `compiler.semantic.call` records ordinary callee-then-arguments evaluation. `compiler.bind.source_sugar` records source spans and the source operator, and `compiler.bind.range_comprehension` covers binding the deferred adapters. Runtime `range.initialize` reports source, retained datum count and ready datum count. At diagnostics level, `range.materialize.datum` reports the source, elements collected, and any traversal or snapshot construction failure. `range.closed.construct` reports the endpoint count, direction, and endpoint failures; primitive cursor opens and steps use the existing execution counters. Traversal callbacks remain explicit adapter work, not literal-item thunks. |
| Source patterns | `compiler.bind.match_decision` records arm count and equal-demand prefix sharing. Semantic metrics count coverage arms and field-layout candidates. Runtime `range.pattern_prefix` records the source, requested and observed elements, and whether it reused a stored snapshot or read a cursor. Generic substitutions and unevaluated type queries record counts in the enclosing compiler phases. |
| Flow analysis | `compiler.flow.prepare` owns `compiler.flow.graph` and `compiler.flow.solve`; block/edge/operation/input-point counts and actual solver block transfers. Transfers include revisits, not just distinct reachable blocks. |
| HIR sealing | `compiler.hir.freeze` and `compiler.hir.seal` report nodes, edges, types, locations, functions, templates, layouts and sources. Compare the two censuses to explain discarded construction. |
| Native lowering | `compiler.lower.reserve`, `.layouts`, `.field_bindings`, `.function`, `.finalize_types`; function/source attribution, output instruction/slot sizes, layouts and types. Field binding counts distinguish lexical constructor fields from fields requiring frame bindings. The enclosing `compiler.lower` adds native opcode and allocation-plan censuses. |
| Optimization | `optimizer.function` owns function-local passes and records fixed-point iterations and allocation placements. `optimizer.pass.native-*` spans retain function identity, iteration, changed status, before/after sizes and active (non-Nop) instruction counts. Inlining, compaction and register-slot passes run at program scope and retain function attribution. Existing `optimizer.native-*` aggregate events remain available. |
| Liveness and memory facts | `optimizer.analysis.liveness` reports function identity, blocks, slots, instructions, fixed-point block transfers and per-instruction bitset words. Local/global memory passes count `local_opt.fresh_definitions` / `global_memory.fresh_definitions` when a noncyclic, unexposed single-assignment temporary avoids a fact invalidation scan. |
| Effect transfer | `compiler.semantic.apply_plan` records source attribution for the shared call/loop plan interpreter, actual writes, branches, singleton initialization and guard skips. Specialization counts `plan.writes.exported`; private activation writes do not become retained external effects. |
| Native artifact I/O | `compiler.artifact.encode`/`.decode` and `.encode_section`/`.decode_section`; compact section identity and byte counts, string/source-span pool counts; allocation-hint normalization has its own interval. Historical format 6 imports record `compiler.artifact.import_v6` and use the same native verifier. |
| Native verification | `compiler.verify` owns type and per-function validation, including pointer-use and typed-flow intervals. Verification runs at existing admission boundaries; recording does not add validation to trusted compiler output. |
| LLVM checkpoint I/O | `compiler.mllvm.encode` / `.decode` record artifact extent, version/phase, graph census and concrete failure causes. Profile children `.graph` and `.producer` attribute reference reconstruction and layout, callable, Range and Future tables separately. Resumed codegen records the saved phase/level and the HPIR work it actually performs. |
| HIR artifact I/O | `compiler.hir.encode`, `.decode`, `.verify`; decoded HIR census and existing boundary validation. Reading HIR does not invent source parsing or binding. |
| Editor and sessions | The same binder/analyzer/lowerer serves incremental editor prefix work, REPL compilation and prompt analysis. Exact revision reuse, source rebasing and prefix reuse retain their existing editor observations. |

`CompilationTrace` owns synchronous nesting explicitly, not through process-global
or asynchronous ambient state. The host supplies session-unique span IDs and the
exporter preserves their parent relationships. One trace belongs to one synchronous
compilation worker; concurrent workers use separate traces. Attributes, metrics and
concrete exceptions travel through the sink-neutral observation API. Failed and
cancelled phases retain their cause and do not emit successful later stages.
Compiler failures also export `diagnostic.code`, source file/range, optional
label/help/note and indexed related locations; consumers need not parse error
messages to locate a failure or its declaration context.
Editor recovery can return a useful projection after a failed semantic child;
its diagnostic count is separate from successful projection completion.

Durations and thread-local managed allocations are inclusive of nested compiler
work. Do not sum parents and children, or add aggregate optimizer events to the
individual pass intervals. `changed_invocations` means a pass reported a change,
not a count of individual rewrites. HIR node visits count analysis work; native
opcode censuses describe static emitted code, not VM execution. Artifact section
byte counts exclude framing and shared pools; native decode `artifact.bytes`
counts the consumed artifact. Compiler details and census construction only run
with a recorder at the requested level. Use telemetry-off runs for latency.

The shape follows [OpenTelemetry span ownership](https://opentelemetry.io/docs/specs/otel/trace/api/)
and the phase/function-oriented investigation used by
[Clang's time-trace tooling](https://clang.llvm.org/docs/analyzer/developer-docs/PerformanceInvestigation.html).
No collector, LLVM dependency or new persistence schema is required.

Focused acceptance:

```sh
python3 tests/Telemetry/compiler.py PATH_TO_MSYNC
python3 tests/Telemetry/journeys.py PATH_TO_MSYNC --case repl
dotnet run -c Release --project tests/CompilerTelemetry
```

The CLI journey compiles at O0–O3, checks real replay output, validates nested
ownership and failure causes, and exercises HIR/native artifact routes and SQLite.
The in-process companion checks byte-identical artifacts with recording off/on,
persistent mutable sessions, editor revision/rebase/prefix paths, and cancellation
ancestry restoration. That historical telemetry journey uses an in-process
oracle. The later [checkpoint journey](compiler-stage-checkpoints.md) compares
separate processes after standard-module physical field order was made
declaration-ordered instead of hash-dependent.

Local acceptance on 2026-11-13 used .NET 10 Release on Linux x64 and a published
Native AOT CLI in `artifacts/validation/compiler-telemetry-cli`. Source O0–O3
compilation/replay, HIR/SQLite, structured compiler failures and REPL recording
passed. The in-process companion passed artifact equivalence, failed-pass and
invalid-HIR admission attribution, cancellation restoration, incremental editor
and persistent-session checks. The framed LSP observer journey also passed.

## Historical Rust SQLite recording and operator workflow

The current backend is a normalized SQLite database (`rusqlite` with bundled
SQLite), enabled by `--telemetry-db FILE` or `[telemetry]` configuration. Existing
`--trace FILE` / `telemetry::init` JSONL behavior remains supported. Neither backend
is enabled by default; help, version, config inspection, and explicitly disabled
telemetry create no database or writer thread. An explicitly enabled conventional
destination creates its state directory. An invalid observer path reports a
structured warning and does not prevent runtime file effects.

```sh
msync run sync.msync --telemetry-db .temp/sync.sqlite
msync telemetry sessions .temp/sync.sqlite
msync telemetry summary .temp/sync.sqlite
msync telemetry spans .temp/sync.sqlite --limit 20
msync telemetry events .temp/sync.sqlite --trace-id TRACE --span-id SPAN
msync telemetry export .temp/sync.sqlite --trace-id TRACE > .temp/trace.jsonl
```

Operator commands open an existing database read-only and validate its application
identity/schema. They do not enable self-tracing or silently create a missing DB.
`summary` lists counts/outcomes and the most expensive phases. Phase duration
sums overlap and are **not** end-to-end wall time. `spans` sorts completed phases
by duration; `events` selects lifecycle/error observations and span ownership.
`sessions` exposes active/completed/exporter-error recording lifecycles and their trace IDs. Its separate `outcome` derives from root-span outcomes (`panic`, `error`, `cancelled`, `ok`), so a fully recorded failed command is not presented as a successful command. A recording can finish completely while its command fails; absent root completion leaves `outcome` null.
Export rebuilds the schema-1 JSONL protocol from normalized values, including
unsigned metrics larger than SQLite's signed integer range; it does not store an
opaque serialized record as the primary analysis interface.

The pipeline records parsing, compilation, optimization, contextualization,
execution, resource scheduling, tasks, IO, guarded publication, and diagnostics.
Startup before the exporter exists is represented truthfully by monotonic
`startup.arguments_ns`, `startup.configuration_ns`, `startup.exporter_ns`, and
`startup.pre_dispatch_ns` metrics on the session root, not invented historical
spans. Cross-task ownership uses explicit contexts; parent spans may complete
later than children. Backend-specific block/instruction/wait metadata is attached
by the execution owner rather than inferred from record arrival order.

### Ownership, queueing, and failure semantics

Each recording session owns its sink through `Arc`; SQLite has one bounded writer
queue and one connection-owning thread. Producers never open SQL connections or
perform per-record fsync. The worker reuses prepared statements, commits bounded
batches, and sleeps on a blocking receive when idle; flush timers run only with
pending records. Queue admission is nonblocking; overflow increments an explicit
lost-record count reported by flush/shutdown and persisted in session health.
The first exporter error remains observable through `flush`/`take_error`.
Normal CLI shutdown warns about incomplete recording without changing the file
operation's outcome.

`flush` is an ordered queue barrier: all previously accepted records have been
committed before success returns. `disable` stops new global spans but preserves
contexts retaining the old sink, as before. The CLI calls terminal `shutdown`
after its outer span is dropped and work has settled; this closes the writer,
marks the session complete/error, applies completed-session retention and joins
the thread. Retained contexts must not create more records after that terminal
boundary. Library users retaining old contexts can continue using their original
sink across ordinary reconfiguration/disablement, and flush that context directly.

A newly created database is claimed with `BEGIN IMMEDIATE`; application identity,
version and emptiness are re-read under the lock. Two first-use processes cannot
both decide to create the same schema from stale observations. Existing foreign
or newer schemas are rejected, never silently migrated or overwritten.

### Configuration and budgets

Defaults are conventions, overridden by TOML and then explicit CLI options.
`SqliteOptions::validate_keyed` validates policies before a database or worker is
created, letting config diagnostics label the exact TOML key without parsing
English messages.

| SQLite policy | Default | Meaning |
| --- | ---: | --- |
| `queue_capacity` | 4096 | Maximum queued records; overflow is counted, producers do not block |
| `batch_size` | 128 | Records per transaction, also flushed by time/barrier |
| `flush_interval_ms` | 100 | Maximum pending-batch interval, not an idle polling period |
| `busy_timeout_ms` | 1000 | Bounded SQLite writer-lock wait |
| `max_sessions` | DB integer maximum | Optional completed-session retention budget |
| `max_records_per_session` | DB integer maximum | Optional recording budget; never a transfer gate |
| `max_database_bytes` | DB integer maximum | Optional logical page budget; observer-only `SQLITE_FULL` when exhausted |
| `sample_per_million` | 1000000 | Whole-session head sampling; zero creates no database/thread |
| `synchronous_full` | false | `NORMAL` WAL commits by default; opt into `FULL` commit synchronization |

Sampling is decided once per trace so it never independently drops a parent or
child. Sampled-out errors are intentionally not recorded; use full sampling when
diagnosing a failure. Completed-session retention preserves all rows of retained
traces through foreign-key cascading deletion. Crashed/incomplete sessions are
not guessed dead from an ambiguous process ID. When a logical page budget is
explicitly configured, it bounds database growth without deleting a live
writer's trace. Default recorder settings impose no practical size quota. SQLite WAL can
still grow while an external long-lived reader prevents checkpoint recycling.

`NORMAL` WAL provides transactional consistency but is not a power-loss durability
promise. `flush` means commit visibility; `FULL` adds per-commit synchronization.
A passive checkpoint avoids forcibly blocking active readers. JSONL export reads
128-record keyset pages with an initial monotonically allocated record-ID cutoff
and releases each read transaction **before** writing to a possibly slow output
pipe. New records are excluded; concurrent retention can remove older pages, so
use a quiescent database when an immutable complete archive is required.

### Storage and references

See [SQLite storage schema and ownership](telemetry-sqlite.md) for normalized
session/record/attribute/metric/error tables, indexes, budgets and schema details.
Public tests live in `tests/telemetry_sqlite.rs` and
`tests/telemetry_sqlite_cli.rs`, alongside existing JSONL regression targets.

The design follows [SQLite WAL](https://sqlite.org/wal.html),
[SQLite synchronous policy](https://sqlite.org/pragma.html#pragma_synchronous), and
[rusqlite connection ownership](https://docs.rs/rusqlite/0.41.1/rusqlite/struct.Connection.html).
[Dapper](https://research.google/pubs/dapper-a-large-scale-distributed-systems-tracing-infrastructure/)
informs low-overhead common-layer instrumentation and causal trace ownership.
[OpenTelemetry's sampling specification](https://opentelemetry.io/docs/specs/otel/trace/sdk/#sampling)
informs trace-level sampling rather than independently dropping records. Recent
[TraceMesh research](https://arxiv.org/abs/2406.06975) investigates adaptive streaming
sampling, but its distributed-model machinery is not justified for this local
CLI workload: whole-session sampling and explicit recording-health reporting are
simpler and preserve the chosen trace's exact causal structure.


## Contract and integration

`src/telemetry.rs` provides one trace per explicit `init` session and hierarchical
spans shared by CLI, REPL submissions, parsing, compilation, formatting, execution,
and individual copies. Telemetry is disabled by default. The CLI enables it with
`--trace FILE`; `--trace -` writes JSONL to stderr. SQLite recording and operator commands are described above. Trace metadata is intended to
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
errors remain visible through the library API and structured CLI warnings. They
do not replace the actual file-operation status. A failing exporter must not panic in a
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

### Stateful v4 task dispatch

The stateful runtime's real resource admission emits `schedule` below its current
`execute` scope. Records report available slots and buffer bytes, then granted
slots and reserved payload-buffer bytes only after successful permit acquisition.
The admitted lease owns the explicit schedule context. Each physical transfer,
program-port operation and program launch creates a completion-tracked `task`
under that context; these are actual dispatch sites, not compatibility aliases.

The task wrapper attaches its context for exactly one future poll using
`with_context`, then detaches before returning Pending. Lower stream/file phases
therefore retain the task ancestor after Tokio moves a future to another worker,
without leaving a thread-local guard suspended across an await. Aborting an
unfinished future records cancelled/incomplete through the owned linked span.
Explicit cancellation stays cancelled; typed failures retain their causal event
and are not rewritten as cancellation merely because sibling cleanup starts.
No payload bytes are added to telemetry.

Task completion is classified once from its result. Explicit owner-cancellation
branches attach a private typed marker through the existing execution-error
cause; an error message, an absent native cause or a subsequently cancelled token
does not establish that outcome. Expected cancellation emits no `execution.task`
error event and is not passed through a later error-completion setter. Actual IO
failures retain their error status and causal event, even during owner cleanup.
The owner marker also cannot hide retained journals, recovery failures or failed
compensation: those results remain failures and must not become a successful stop.

The finite-task wrapper and byte-flow owner use the same terminal classifier.
A flow's successful `FlowEnd::Cancelled` is a cancellation outcome, but it cannot
override a typed failure. Actual failures emit one `execution.task` error event
on their own task span before completion, preserving the causal path even when
the lower IO layer has already emitted its native error. Trace consumers resolve
an admitted schedule's execution parent by span ID, not by selecting the first
`execute` record: an earlier declarations-only REPL submission can legitimately
have its own recovery/execution scope without admitting a task.

`tests/runtime_task_telemetry_v4.rs` invokes the real CLI against native TCP peers:
successful EOF publication, `/stop` of an idle source, and refused connection.
It checks execute→schedule→task→io.flow ownership, real reservation metrics,
terminal statuses, typed OS cause retention, and absence of file payloads. The
older `repl_jobs::live_stream_keeps_repl_responsive_and_stop_joins_resources`
trace-parent contract also passes with these actual runtime spans.

## Historical structured frontend and failure acceptance (v0.4)

Finite assignment `copy` spans cover immutable staging, not physical publication:
they record `bytes.staged`, transaction/instruction IDs and
`publication.policy=transaction-checkpoint`. The separate `transaction.publish`
span owns final epoch and managed-reader publication scope. Tentative
`file.publish.step` events describe backup/install progress; `file.publish` and
`transaction.commit` appear only after all receipts and the logical store swap.
Known commits with pending cleanup stay successful and retain their cleanup
causes. Failed publication and compensation remain distinct events with original
OS errors; a cancelled attempt cannot report committed bytes.

The default v0.4 frontend is covered directly by
`tests/structured_telemetry_v4.rs`; its macro/literal input cannot be replaced by
a legacy-syntax test. Real output bytes must match before trace assertions run.
The test follows parse, compile, prepare and execute ancestry to the submission,
then verifies that an actual immutable-assignment copy belongs to execute.
There must be one session root, unique span IDs, acyclic span parents and existing
owners for every event. Events reference their owner span, not a separate parent
field or an additional graph vertex.

Failure controls distinguish a frontend error, a detailed preparation error and
an actual HTTP runtime reset. The HTTP peer observes a real GET, sends a short
body against a larger Content-Length and resets its socket; the CLI fails while
the previous destination remains unchanged. This is evidence of a real runtime
transport failure, not a claim that a particular prefix reached unpublished
storage. Execute/submission/session are errors while parse/compile/prepare remain
successful; typed error-chain and source-diagnostic events survive that boundary.
Direct missing-operand preparation records the original half-open byte range
`0..7`, with no fabricated execution span. Raw diagnostic offsets follow the
compiler's owned provenance contract, including intentional bound-value origins.

Both old CLI trace regression filters and the four structured controls passed
on Windows with `CARGO_INCREMENTAL=0` and two Cargo jobs. The focused structured
target passed strict Clippy. Unique literal/body payload markers and unrelated
secret environment values are absent; useful endpoint paths and failure context
remain observable. No privacy filter is used to hide the underlying cause.

### SQLite acceptance on native Linux (2026-10-31)

The root-owned targeted gate passed `telemetry_sqlite` (11 tests),
`telemetry_sqlite_cli` (6 tests), and the established JSONL `telemetry` target
(5 tests). Captured output is `.temp/performance-v6/storage-tooling.log`; the
maintained test sources preserve the reproductions when logs are cleaned.

The gate checks actual CLI bytes and SQL-owned causal records, source-free
payload handling, typed OS diagnostics, exact unsigned metrics, concurrent tasks,
retained old contexts, observer startup warnings without blocked effects, normal completed-session
closure, sampling/disable laziness, capacity failures without corrupting file
publication, logical page limits, and read-only operator queries. A writer inside
the export output callback can update and truncate-checkpoint the same database,
proving the exporter released its page read transaction before blocking output.
An independent concurrent-process review test passed fresh shared-database
initialization/closure over repeated rounds, alongside keyed limit diagnostics.

The cross-domain `performance_journeys` target also passed its five actual-user
journeys, including configuration-selected SQLite cancellation with no partial
publication and normalized execution/query ownership. These are native Linux
results, not a claim of native Windows execution, remote transport coverage,
or a complete historical test-suite rerun. CLI startup/trace-on overhead remains
owned by the root's separate quiet performance comparison window.

Interpreter phase instrumentation inserts explicit owners between execution and
resource admission: `submission -> execute -> interpreter.block ->
interpreter.instruction -> schedule -> task -> io.flow`. The established
`runtime_task_telemetry_v4` regression follows these real parent IDs rather than
requiring obsolete direct `execute -> schedule` adjacency. It still requires the
selected submission to have compiled nonempty work, checks for missing/cyclic or
cross-trace ancestors, and retains exact `task -> schedule` / `io.flow -> task`
ownership plus resource-reservation assertions. This preserves the original
regression against accidentally using an earlier empty binding submission's
context; it is not permission to attach work to an arbitrary session root.

## Windows SSH latency collection (v0.6.5)

A reported native Windows REPL upload of 1,795 bytes took 35,399.5 ms. That is an
end-to-end submission observation, not a measurement of the payload network
rate. The existing Linux loopback SSH benchmark does not establish native
Windows behavior: `benches/ssh_v065.py` imports Unix `pwd` and `resource`, starts
`/usr/sbin/sshd`, and counts clients using POSIX wrappers.

Collect the original user journey rather than replace it with a synthetic large
file. In the same Windows directory, terminal, account, and environment:

```powershell
New-Item -ItemType Directory -Force .temp | Out-Null
msync --version
msync doctor
Get-Command msync, ssh, scp -CommandType Application -All |
    Select-Object Name, Source
ssh -V
$env:MSYNC_SSH_CONFIG
msync repl --trace .temp/ssh-windows.jsonl
# At msync>: repeat the original transfer, then repeat it once in this REPL.
# cmd -> (ssh://forge)::cmd
# Exit normally after both completion notices.
```

`doctor` inventories native executable search locations without starting the
clients. Multiple OpenSSH distributions in the inventory matter: Windows system
OpenSSH, Git's client, and WSL OpenSSH are not interchangeable implementations,
and invoking a PowerShell function called `ssh` is not what Rust's
`Command::new("ssh")` does. Preserve the actual path/version when comparing with
an independently opened SSH terminal. `MSYNC_SSH_CONFIG`, when set, is passed as
`-F` to both SSH and SCP; a direct-shell comparison must use the same file rather
than accidentally compare different aliases, proxy routes, keys, or users.

Existing JSONL records can separate expensive client processes from local work
without additional tooling. Span records are written at completion, so sort by
start time, not file order:

```powershell
$spans = @(Get-Content .temp/ssh-windows.jsonl |
    ForEach-Object { $_ | ConvertFrom-Json } |
    Where-Object kind -eq span)
$spans | Where-Object name -eq transport_process |
    Sort-Object start_unix_ns |
    Select-Object span_id, parent_span_id,
        @{Name='ms';Expression={[math]::Round($_.duration_ns / 1e6, 2)}},
        @{Name='program';Expression={$_.attributes.program}},
        @{Name='pid';Expression={$_.metrics.pid}} |
    Format-Table -AutoSize
$spans | Sort-Object duration_ns -Descending |
    Select-Object -First 20 name, span_id, parent_span_id,
        @{Name='ms';Expression={[math]::Round($_.duration_ns / 1e6, 2)}} |
    Format-Table -AutoSize
```

Use a new trace filename for another independent run: JSONL initialization
appends. A span ID is unique within a trace, not across appended sessions. For
SQLite instead of JSONL, use `msync repl --telemetry-db .temp/ssh-windows.sqlite`,
then `msync telemetry sessions`, `summary`, `spans`, and `export` with that database
and an explicit `--trace-id` if it contains multiple sessions. Do not sum nested
phase durations as elapsed time.

An independent connection control using the same alias/configuration is:

```powershell
$sshConfigArgs = @()
if ($env:MSYNC_SSH_CONFIG) { $sshConfigArgs = @('-F', $env:MSYNC_SSH_CONFIG) }
Measure-Command { ssh @sshConfigArgs forge true }
ssh @sshConfigArgs -vvv -E .temp/ssh-direct.log forge true
```

`true` measures connection/authentication/remote-shell startup without a file
transfer. The debug log explains route, selected authentication methods and
whether the particular direct client reused a connection; it does not provide
separate monotonic timings for every phase. A direct `scp` control should target
a separately named disposable remote file, with the same client/configuration,
source bytes and destination filesystem. It is not equivalent to msync's publication durability and must not be described as such.

### Current attribution limits and instrumentation target

At the v0.6.5 source baseline, `runtime::run_cancellable` records
`transport_process` with program, PID, wait strategy, stdout/stderr byte counts,
and open/exit/error events. It does not record the remote helper's phase, endpoint,
or successful stderr. `inspect_remote_batch` already supplies backend, endpoint,
path count and `client.invocations`; its parent relationship identifies metadata
processes. Other successful SSH helpers are not distinguishable from one another
by their existing process attributes. Do not infer a precise allocate/publish/
settle split merely from an unlabeled `ssh` sequence.

`ssh.session.admit` is emitted inside the Unix-only admission path. Native Windows
returns before that span and does not acquire an authenticated registry session;
its client durations include authentication and any askpass interaction on each
new connection. Counting `transport_process` spans counts only clients using that generic runner,
not all directly launched SSH clients. In particular,
`execution::endpoint::command_output` launches publication allocate/publish/
settle/recovery commands through its own asynchronous process runner and emits no
transport-process span at this baseline. These controls are invisible inside
the enclosing instruction duration. Unix master/check processes also have their
own path outside the generic runner. SFTP subprocesses and server-authenticated
connections are different counts again.

The useful bounded extension is explicit operation identity supplied by the owner:
instrument the asynchronous publication runner with linked spans and name
allocate/publish/settle/recovery phases; distinguish payload and metadata
transport, and record session strategy plus endpoint. Keep process setup,
child lifetime, and pipe-drain durations separate if Windows process orchestration
is implicated. Prefer semantic labels to recording or reverse-parsing whole
remote shell scripts. This provides attribution without changing transport,
credentials, publication or recovery ownership. Authentication internals still
require native-client debug/server observations; a process span alone cannot
separate DNS, proxy connection, key exchange and server execution.

### Follow-up instrumentation implemented

The follow-up working tree closes the missing-publication-runner gap above:

| Record | Meaning |
| --- | --- |
| `remote_publication` | One retained finite publication owner; its explicit context follows the worker and blocking staging task. |
| `transport_process`, `phase=allocate/publish/settle/recover/resolve_origin` | Actual async publication client, including previously invisible SSH invocations. |
| `ssh.upload.stage`, `ssh.upload.payload`, `ssh.download.payload` | Private source staging and actual SCP byte transport, not public-destination publication. |
| `remote_command`, `phase=source.inspect/source.list/source.timestamps/source.finalize/source.copy/remote.helper` | Semantic helper identity. Finalization is in-place normalization, not a second data copy. |
| `spawn_ns`, `child_wait_ns`, `drain_ns` | Native process setup, child lifetime after capture readers start, and remaining reader join after child exit. Whole-span duration also includes other local orchestration. |
| `ssh.session.strategy` | `owned_mux`, `external_mux`, `native_config`, or Windows `native_per_process`; refreshed after lazy client configuration. It describes msync policy, not a measured server-authentication count or a claim about a configured third-party client's own sharing. |

In the PowerShell process table, add:

```powershell
@{Name='phase';Expression={$_.attributes.phase}},
@{Name='spawn_ms';Expression={[math]::Round($_.metrics.spawn_ns / 1e6, 2)}},
@{Name='child_ms';Expression={[math]::Round($_.metrics.child_wait_ns / 1e6, 2)}},
@{Name='drain_ms';Expression={[math]::Round($_.metrics.drain_ns / 1e6, 2)}}
```

For synchronous helpers, find the `remote_command` or SCP parent by
`parent_span_id`; its phase/endpoint belongs to the process below it. Publication
clients carry phase/endpoint directly. Native Windows authentication is still
inside client lifetime; a single process span cannot split its key exchange from
remote shell startup or filesystem flushes. Unix master/check clients remain
outside this process runner and must not be counted as authenticated sessions
merely because a process was launched. The maintained actual-SSH acceptance
runner checks both the phase records and their complete ancestry.

### Native backend and synchronous evaluator transition

- `backend.function` attributes each lowered function and counts instructions,
  fused instructions, u64 results and u64 operand operations.
- Nested `backend.number_analysis` records blocks, fixed-point rounds,
  instructions transferred and alias-exposed slots. These are backend range
  facts, not a repeated escape analysis.
- `vm.function.jit_u64_values` / `jit_u64_operations` count emitted native values
  and operations, not dynamic hits. `heap_escaping_locations` counts individually
  allocated escaping cells; `frame_storage_reuses` still counts frame reuse.
- `connection.resource` records concrete implementation, endpoint and table
  creation/reuse; `connection.command.prepare` reports the requested SSH session
  strategy. Actual authentication reuse requires server evidence, not client
  process counts. `connection.close` records owned-client shutdown outcome.
- `process.synchronous_wait` attributes default external-process completion.
- `vm.demand` identifies the function, instruction opcode and PC at an actual
  interpreter or JIT-helper wait. Guest async continuation helpers have been
  removed in the current transition. Existing pause/continue counters represent
  these waits; they do not imply suspended guest coroutine frames.

The explicit guest-stack loop records `guest_call_pushes`, `guest_call_resumes`
and `guest_max_call_depth` on caller function counters. Depth is the ordinary
caller-record depth within that evaluator entry, not host stack depth or tail
iteration count. `jit_readiness_exits` / `tier.readiness_exits` distinguish an
unavailable operand from `jit_guard_misses` / `tier.guard_misses`; readiness exits
preserve native code and must not increment deoptimization counters. Fixed-slot
stores consult their destination descriptor rather than treating a pending value
elsewhere in the VM as a reason to call the scalar publication helper.

Released process admission now has `process.submit` (source, argument/dependency
counts and causal links), `process.background` (owned job lifetime and actual
outcome/exit code), and `process.launch` (resolved executable and admission).
Launch errors and cancellation explicitly fail the corresponding scopes rather
than leaving successful-looking timing records. The submission Future is
registered before the worker starts, and source diagnostics retain the original
call location after the creating guest frame has been released.

Input submissions use `input.submit` and `input.background`, with source spans,
input-order and captured ordering predecessor links. Cancelling an unconsumed
input marks that job cancelled; a noncooperative host reader may still finish
its already-started physical read. The input turn remains owned by that physical
read until it ends, and its worker does not retain ambient guest execution
context. Process/input submissions share lifetime code while retaining their
different scope-exit rules (join child work versus cancel an unused read).

`backend.work_scope` records function/call-edge counts, required/elided scopes,
propagated requirements, direct-call sites and their maximum compiled callee
depth. These are compilation decisions, not dynamic hit counts. Runtime
`guest_direct_calls` counts actual bounded/context-free direct calls;
`guest_leaf_calls` remains the context-free subset. Recursive/unknown calls
continue to count explicit stack pushes/resumes. Profile mode can allocate a
scope for counters even for a work-free function; do not equate profile-mode
scope allocations with the recording-disabled ownership policy.

### Segmented frame backing and filesystem values

The vm.function metric stack_frames counts segmented frame admissions;
stack_frame_bytes and stack_frame_roots sum admitted layout extents, not peak or
resident memory. stack_segment_allocations counts newly allocated segments
attributed to the function needing them (including initial thread storage).
These distinguish descriptor reuse from actual backing-segment reuse.

Filesystem scalar submissions emit file_read.submit (source, native name,
reader resource keys and matched dependencies), file_read.background (job
lifetime), and file_read.refine (path, concrete resource kind, stream co-start).
Synchronous native writes emit native_io.admit with concrete read/write keys
and dependency counts. A stream can release its ordering lease while its value
Future remains pending; these are deliberately different lifetimes.

Queued Linux tree copies and archive writes keep the owning `file.copy` or
`file.write` span open through completion or retirement. The worker's
`file.sync` and the ordered consumer's `file.publication.wait` and
`file.publish` are children of that file. The wait records queue depth and
whether sync was pending on entry. An abandoned stage has
`file.stage.cleanup` with its actual outcome; later staged files are not
reported as published. Multiple files' sync and copy or write spans can overlap.

Windows local publication records `file.native_copy` around `CopyFileExW`
and `file.sync` around the explicit `FlushFileBuffers` on its retained
handle. Both are children of `file.copy`; the stage is still published only
after native copy and flush have completed successfully.

Observed remote installation receipts report `install.leaves_moved`,
`install.directories_merged`, `install.subtrees_renamed`, and
`install.subtree_fallbacks` on `transport.install.result`. These are actual
remote decisions, not derived file counts: moving a whole tree counts as one
rename. `transport.receipt_wait` measures client stdout drain after upload or
relay input closes (or after a same-endpoint selector submission); it is not
a remote CPU or publication timer. The ordinary unobserved completion receipt
is unchanged.

### Frame descriptors and background callback ownership

Each guest call records one activation_descriptor_allocations or
activation_descriptor_reuses event in vm.function; descriptors now belong to
frames rather than an independent pool. work_forwarded_futures and
work_forwarded_processes attribute late promotion to a closed lexical owner.
transfer.background spans the owned submission; transfer.execute records source,
matched dependency count, callback cleanup and concrete failures. Completion is
published after callback/resource cleanup, not at the first successful I/O result.

vm.function.tier_workspace_leases counts first native workspace acquisition for
a guest call chain; tier_workspace_reuses counts subsequent native entries using
that chain's workspace. Recursive guest calls on the explicit stack do not rent
separate workspaces; nested bounded direct calls have separate active chains.

Direct return telemetry: direct_return_slots counts typed representation/Future
copies into caller-owned return storage; direct_return_scalars counts native
Number/Bool bit publication without a generic result container.

`jit_machine_address_result_sites` counts compiled machine-to-machine CALL
sites whose RET returns a guest reference; `jit_machine_address_results`
counts completed native calls that publish that result in the caller.
`jit_machine_argument_window_sites` and `jit_machine_argument_window_calls`
distinguish the read-only no-copy call window from calls that place arguments
in machine scratch. Readiness exits continue to use the existing demand
attribution; a failed native guard never counts a successful return or window
use. These counters are attributed to the caller and do not invent ordinary
VM frame entries for a native leaf.

At frame entry, call_physical_transfers and call_physical_arguments count
calls and arguments placed using prelinked byte/reference displacements.
call_physical_pending_fallbacks and call_physical_owner_fallbacks count
eligible call entries that used the pending-aware representation path
because an owner had pending state or the owners did not share a native
reference arena. These are actual copies in both paths; they remain included
in call_arguments_copied, not call_arguments_borrowed. The
backend.call_parameters phase records eligible functions and unread
formals, with backend.call_parameters.function rows naming affected
functions. call_physical_omitted_arguments counts omitted frame writes
when execution consumes that compiled proof, not argument evaluations
eliminated.

`backend.call_windows` records eligible and selected writable outgoing
window sites and their argument counts; `backend.call_windows.site` names
the selected caller and instruction. Function-level `call_window.*`
metrics describe candidate, liveness, mixed-placement and shape decisions
after entering the analysis. Selected sites name the callee and record its
capture count and the number of directly placed producers.
`vm.function.call_windows_adopted` and `call_window_arguments`
record completed frame entries and their adopted arguments, separately
from physical copies and read-only machine borrowing. The owner, pending
and capacity miss counters describe failed ownership transitions; the
existing copy/fallback counters describe what occurred after the miss.
`call_window_staged_arguments` is the subset of adopted window arguments
that actually required a store from outside the window. These stores also
count under `call_arguments_copied`; an unread formal skipped by the
prelinked proof is not a staged store.
`call_windows_zero_stage` counts adopted calls that did not enter the
argument mover at all. `jit_frame_call_sites` counts emitted x64 direct
frame CALLs with a static continuation; `jit_frame_calls_continued`
counts successful returns to those continuations, separately from
machine-only leaf calls.
`jit_frame_load_calls_continued` is the subset which enters at a
fused callable read; `jit_frame_callable_copies_elided` counts only
those reads that obtained a ready callable without materializing
its temporary. A host-stack exit before starting the invocation
is reported as `jit_frame_call_activation_exits`.
`jit_fixed_guest_single_frame_returns` counts effectful-capable
fixed calls whose first activation returned without opening a
driver. `jit_fixed_guest_driver_handoffs` counts actual nested or
tail transfers handed to the guest driver with the first activation
already executed. Both are attributed to the original caller.
`jit_fixed_native_entries` counts verified caller-emitted callee
machine entries (seen on their return/exit), `jit_fixed_native_calls`
counts their successful completion, and `jit_fixed_native_frame_resumes`
counts transitions that need to continue the already-started callee
frame after a machine exit, not callee entry retries.
`jit_fixed_native_code_leases` counts acquisitions of a published
callee code lease by the live caller, and
`jit_fixed_native_code_lease_reuses` counts uses of an already held
lease for the same version. `jit_fixed_native_scalar_returns` counts
completed scalar returns authorized for immediate caller-machine
publication after frame retirement. None of these counters count
generic instruction bridge entries or kernel syscalls.
`jit_fixed_native_workless_returns` measures ready scalar fixed
returns that made no ambient work entry. The
`jit_fixed_native_work_{bridge,call,resume}_entries` series records
the first managed work boundary per prepared invocation, attributed
to the caller. With observation enabled a work scope may still
exist for counters; the workless count is not an allocation count.
`jit_location_captures_deferrable` counts location captures proved
absent from emitted frame descriptors at tier compilation.
`jit_fixed_native_capture_bindings_deferred`,
`jit_fixed_native_capture_bindings_materialized`, and
`jit_fixed_native_capture_bindings_avoided` count runtime binding
decisions on the caller: initially skipped, later installed on a
managed boundary, and still skipped at a ready scalar return.
`jit_fixed_native_raw_returns` counts ready scalar completions
without a rented child tier invocation. The
`jit_fixed_native_raw_managed_exits` and
`jit_fixed_native_raw_other_exits` counters cover exact-PC managed
operation boundaries and other reasons to materialize the existing
frame's tier state. These counters do not measure frame allocations.
`jit_fixed_native_reservations` counts successful reservations using
the linked callee extent, before executing the child.
`jit_fixed_native_emitted_frames` counts frames published by the
x64 CALL prologue, observed at either native return or managed exit.
`jit_fixed_native_repeated_admissions` counts same-site admissions
that reuse the retained code and frame recipe without a host call.
`jit_fixed_native_repeated_attachments` counts the subset that later
attach the already-started child at a managed exit.
`jit_fixed_native_unattached_returns` counts scalar returns that
released their physical reservation without a frame view;
`jit_fixed_native_frame_attachments` counts same-FP attachments on
a cold exit. `jit_fixed_native_reservation_misses` counts attempts
that could not reserve both lanes in the current segment, including
a missing native lane or opposite arena. All are caller-attributed.
`jit_fixed_native_inline_returns` is the subset of unattached
scalar returns that skipped the managed completion callback.
The counter is recorded when the caller consumes the completed
native receipt at its next managed entry or tier exit.
`stack_segment_reentries` counts pushes that cross into an existing
cached stack segment after a capacity miss. These are distinct from
`stack_segment_allocations`, which counts frames requiring native
segment or edge-lane allocation. Both retain function attribution.
`jit_fixed_native_argument_sites` counts emitted physical placement
sites. `jit_fixed_native_argument_placements`,
`jit_fixed_native_arguments_placed`, and
`jit_fixed_native_argument_reference_words` count observed caller-machine
placements, live inputs, and copied guest root words respectively, on
callee exit. The placements also count as physical transfers in the
callee. The `jit_fixed_native_argument_*_exits` series describes why an
emitted site used the ordinary frame writer: frame/descriptor binding,
pending caller storage, or address-keyed host reference leases.

`jit_value_materialization_sites` counts emitted private i64
constant, nullable Null and guest callable materialization sites.
`jit_integer64_constants`, `jit_guest_null_constants`, and
`jit_guest_function_composes` count completed emitted operations.
Materialization exits are divided by the presence of pending work
at the guard; a without-pending exit may be a non-Null callable
component, uninitialized input, or storage with a host lease.
Ready function composition totals include the native subset.

`jit_guest_reference_store_sites` and `jit_guest_reference_stores`
distinguish admitted x64 heap reference stores from completed native
writes. Pending and storage exit counters cover branches taken before
mutating the destination. Storage exits include ineligible owner/lane,
uninitialized source or a host token on either side. Scalar guest writes
continue to use their own series.

Native readiness setup: tier_context_pending counts entries with relevant pending
slots. tier_context_unrelated_pending counts entries where the VM had pending
values but the invocation's slot descriptors did not. The coarse native mask now
represents the latter descriptors, not VM-global activity.

guest_stack_limited_calls counts work-scope-free targets that cannot extend the
current native stack. guest_loop_handoffs counts thin direct activations handing
a deep subtree or tail chain to the explicit loop. guest_direct_calls now includes
stack-admitted recursive calls, not only statically bounded sites.

### Captured acquire frontiers and escaped descendants

work.disposition records each owned submission's disposition and whether the
owner is a job scope. Promotion/join decisions include reachable
Future/Process counts; a successful scalar input reports `settled-value`
without claiming that the root graph was traversed. `work.retention_scan`
records the owning function, owned Future/Process counts, and whether activation
exit traversed the roots or closed a batch containing only settled values.
work.escape links
the creator job and work transferred beyond its lifetime. The child list is not
an archive of cancelled/handled internal operations.
acquire_descendant_futures and acquire_descendant_processes count followed
escaped-work edges, not newly launched operations or globally unique tasks.

### Native reference storage and tracing

Guest-heap sweeps report `heap.raw_header_scans` and `heap.raw_descriptor_scans`
separately. `heap.capture_descriptor_reads` counts auxiliary capture-vector
lookups on otherwise header-scanned objects. `heap.host_tokens` counts the
marked native slots that actually cross into the host registry; immediate
source-type words and legacy root slots are reported separately.
`heap.native_stack_frames` and `heap.native_stack_root_slots` count native
headers in the collecting arena and reference slots scanned through those
headers. The caller's adopted suffix is excluded from its published root
range until RET; it is counted under the callee instead.
`vm.function.stack_frame_native_roots` is the native subset of
`stack_frame_roots`; it does not count CLR roots or host handles.
`vm.function.frame_binding_slots` counts reserved indirect cells per
activation; `frame_native_binding_slots` is the subset in native lanes.
This measures placement, not the number of populated bindings or host
adapter lookups. `call_argument_snapshot_bytes` and
`call_argument_snapshot_reference_slots` count typed packet extents at
guest call entry. Ordinary snapshots reuse frame storage, so the existing
`call_argument_snapshot_allocations` series is zero for this path. Tail
handoffs retain their separate buffer and reuse counters.

`vm.storage.pending_index` counts native owner tables opened/resized,
reserved bytes, offset lookup outcomes, new/replaced slots and retirement.
It excludes the external managed allocation path and emits no sample for a
no-pending run. `vm.function.native_builtin_storage_allocations` and
`native_builtin_storage_elements` count guest arena requests from builtin
factories, attributed to the current function when available; they do not
count allocations by the standalone host-library API.

`vm.function.native_indirect_loads` and `native_indirect_stores` count
interpreter/bridge copies that form and use a native location without
materializing the referent's allocation view. `indirect_pending`,
`indirect_non_native_owner`, `indirect_unlinked_layout`,
`indirect_uninitialized`, and `indirect_boundary_carrier` count the
first reason a copy did not take that route. They are independent of
machine-direct field counters. `jit_guest_carrier_adaptations` counts
machine-executed Scope conversions that change between an exact and a
nullable carrier; same-carrier copies are excluded.
`vm.function.vm_scope_copies_forwarded` counts logical Copy sites
absorbed by visited linked field loads, weighted by visits in the interpreter,
bridge and generated tier. The original instruction and edge counters
remain logical, so this metric measures removed work separately.
`native_text_objects` and
`native_text_bytes` count UTF-16 arena blocks and their logical extents.

`native_range_plan_nodes` and `native_range_plan_edges` count plans
actually linked in the executing function. `native_range_cursor_opens`,
`native_range_cursor_bytes`, and `native_range_cursor_yields` report
workspace and demand, including native numeric reductions. A base
sequence can still use the separate `native_sequence_*` counters.

`native_range_callback_requests` and `native_range_callback_resumes`
distinguish dispatch from acknowledgement. `native_range_ordered_nodes`
counts native natural sort/merge plans. `native_path_joins` and
`native_text_comparisons` distinguish raw-lane operations from paths
that materialize values.

`native_range_window_fusions` counts adjacent demand windows absorbed
during linking. `native_range_window_kernels` counts cursor opens whose
root combines a demand window and a numeric sequence in one entry.
Nested kernels can execute below another root without adding to the
root-open counter.

### Completed Future materialization in private tier slots

`vm.function.tier_ready_materializations` counts successful Future payloads
written into compiler-planned private fixed slots during native-context readiness
refresh. It does not count waits, faults, cancellation, or borrowed/external slot
rewrites. NativeTierMemory accumulates the count only when the context is observed
and flushes it through the existing RuntimeProfile counters. This metric is a
mechanism count, not evidence of a throughput improvement; see the ready-slots
paired receipt in native-execution-lowering-v090.md.

### Scalar stream jobs

`stream.submit` records source location, native operation, captured Future
count, and ordering linkage. `stream.background` owns the queued job interval;
`stream.execute` records the native operation, inherited frontier size, and
`callback.work_finished` before result publication. Stream jobs share existing
`work.disposition` and `work.escape` observations for cancellation/promotion and
escaped child work. Ready inline ranges do not allocate submission observations.

`stream.admission` reports the actual caller-release decision: `outcome=completed`
means the ready prefix finished (value or failure), while `outcome=pending` means
an incomplete demanded operation released the caller. The latter links the
specific dependency task. Exactly one outcome wins each stream admission.
Creating a dependent Future alone does not constitute a demand observation.

Direct native child contexts now remain owned by their parent workspace between
sibling calls. Existing `tier_workspace_leases` and `tier_workspace_reuses` count
actual native-entry cache misses/hits for this ownership path too; no new timing
proxy is used. A child remains distinct from a live parent bridge context.

The direct-child workspace cache experiment was subsequently rejected: reduced
lease counts did not establish a throughput benefit. The cache was removed;
lease/reuse counters retain their previous pool/explicit-chain meaning. See the
tier-child-pinned receipt and interpretation in native-execution-lowering-v090.md.

### Whole-function native calls

`jit_ssa_machine_call_sites` and `jit_ssa_machine_calls` include both
machine ABIs. `jit_ssa_register_call_sites` counts emitted register-only call
sites; `jit_ssa_register_calls` counts their executions.
`jit_ssa_call_spill_slots` is a compile-time sum of caller-save slots across
emitted call sites, not a count of guest heap allocations or dynamic spills.
Counters are in `vm.function`; the native counter pointer is null when
profiling is disabled.

### Native bridge representation-copy outcomes

For observed TierBridge Narrow/Convert attempts, vm.function exposes
`tier_copy_fallbacks`, `tier_copy_zero_roots`, `tier_copy_one_root`,
`tier_copy_two_roots`, and `tier_copy_bulk_roots`. Ready counts distinguish the
actual copied representation: equal carrier types copy their full root width;
kind refinement copies the actual destination payload width. Fallback does not
inspect an unready payload. These are a scoped view of bridge attempts, not a
count of every storage copy in the VM. Existing opcode/function/source context
identifies the operations. Core's small-root helper remains sink-independent.
The bridge dispatches linked handlers without widening its entry frame;
observed side-exit classification stays out of line. Direct/fallback counts
and the original instruction/edge mapping do not depend on where the handler
body executes.

### Rendezvous removal

The deletion-first cleanup removes `stream.admission`; it is no longer emitted.
`stream.resolve_ready_producer` records the actual inline constructor resolution
with source and argument index. Existing stream submission/execution/failure and
ownership links remain. `backend.work_scope` adds
`work_scope.deferred_constructors` for the corrected construction-versus-callback
rule in the existing compiler pass. There is no replacement submission-wait hook.

`backend.function` includes `copy.compiled_sites` for copies selected from exact
carrier/fixed-location and compiler-private source facts. `vm.function` includes
`compiled_copies` for actual linked/bridge execution of that handler and frame
Copy/Move and value captures consuming the existing private-slot proof. Argument
transfers use the compiled call contract directly and remain accounted for by
the existing call/snapshot/borrow telemetry, not this counter. Pending
Future forwarding remains a runtime data decision; initialization/type/layout
proofs are not recomputed by this handler.

`vm.function.jit_initialization_checks_elided` records actual native emitter
omissions of private fixed-cell scalar/IsNull initialization guards. It is summed
from compilation statistics, not incremented on each runtime scalar read.
Pending-value and kind guards are not included in this count.

`compiler.storage.calls.unobserved_initialization_slots` counts private slots whose
initialization flags have no borrowed-parameter observer. The decision reuses
the final call/borrow plan. `vm.function.jit_initialization_writes_elided` records
actual native flag-store omissions, including moved-from clearing and register
region outputs; it is summed from emitter statistics rather than incremented
per guest instruction. The corresponding `UnobservedInitialization` bit lives in
the existing slot storage byte.

`vm.function.compiled_reads` counts GetDatum reads selected
by the compiled private-temporary fact. It excludes native scalar loads (reported
by code-generation elision statistics) and generic checked reads. Future payload
resolution remains shared with the checked path.

`vm.function.refinement_copies` and `refinement_fallbacks` count ordinary
interpreter Narrow/Convert direct-copy outcomes. A fallback includes pending
values and conversions requiring representation or semantic work. Native bridge
execution retains its existing `tier_copy_*` counters; the two families are not
double-counted. Counters are attached to the executing function and do no atomic
work when recording is disabled.

Artifact decoding retains section and failure diagnostics but performs no
compatibility work-hint analysis. The former `compat.work_hints.corrected` path
is removed; obsolete prototype revisions report a source-recompilation request.
