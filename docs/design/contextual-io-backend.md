# Contextual I/O backend and execution plans

## Contract (2026-11-10)

This work replaces the assumption that executable artifacts are reusable
environment-independent programs. `.mso` retains the machine-independent
program. `.mexe` is an inspectable, resubmittable carrier for a contextualized
execution plan. Its observations may cease to describe the environment at any
time; repeat submission is another attempt to execute its recorded operations.

The contextual backend computes known values and control flow, observes selected
filesystem/endpoint state, expands finite selections, builds effect dependencies,
eliminates dead work and forms executable batches. An opaque external call ends
the region whose effects can currently be computed; continuation inputs describe
what must become available before the next region can be contextualized.

The VM executes received operations and dependencies. It owns parallel execution,
admission, processes, transport lifetimes, cancellation and concrete errors. It
does **not** revalidate the whole plan, compare environment snapshots, classify
plans as stale, automatically replan, or fall back to another program because
an observation changed. Reading/decoding an artifact is not an environment
validity check. Native operations perform the work requested and report actual
failures with their source operation, endpoint and paths.

This separation is the user's explicit correction to the initial proposal of
"validating at submission". It also supersedes earlier design statements that
prohibited all filesystem observations in executable artifacts. There is no
promise to preserve older IR encodings, and no permission to rewrite published
release assets or tags.

## Observable work and dependencies

The motivating case is two discarded transfers to the same exact file:

```msync
~/AGENTS.md -> ~/.codex/AGENTS.md;
~/.temp/AGENTS.md -> ~/.codex/AGENTS.md;
```

When no operation observes the first result or destination between these writes,
the first destination version is dead. Its file transfer disappears, not merely
its scheduling annotation. An eliminated operation does not run and therefore
does not contribute an operational error or transfer count. Operand computations
with separately observable effects are not implicitly discarded with it.

Relevant observations include a demanded transfer Bool, a read of the written
file or affected namespace, an escaping result, and an opaque external effect.
Overlapping directory and alias resources require their actual semantics:
copying two directory trees into one destination is not necessarily complete
overwrite, because the first tree may contribute surviving members. Expand known
members and eliminate overwritten leaf versions, or retain the still-needed
subtree work. Do not treat every same-spelled directory write as replacement.

Model read-after-write, write-after-read and write-after-write edges independently
of execution order. Last-writer elimination operates on destination versions and
their observers. Independent live nodes may reorder and run concurrently; dead
nodes and their exclusively dead edges disappear before scheduling. A batch is
an execution grouping of live graph work, not a new transactional guarantee.

Scope/cell mutation remains under the scalar/control owner. Capturing transfer
operands fixes the values consumed by a task; it does not permit worker tasks to
execute arbitrary mutable source-language callbacks concurrently.

## Directional barriers

The source spellings are `acquire` and `release` (including the user's informal
`aquire` spelling when describing the intent). They are directional scheduling
constraints, not unconditional bidirectional epoch boundaries:

| Marker | Forbidden movement | Movement not forbidden by the marker alone |
| --- | --- | --- |
| acquire | An earlier operation moving after it | A later independent operation moving before it |
| release | A later operation moving before it | An earlier independent operation moving after it |

Data/resource/result dependencies can still forbid the otherwise permitted
movement. Opaque external calls are distinct knowledge/effect boundaries.
Neither a normal arithmetic operation nor every transfer needs its own effect
block. Block coalescing and batching must consume these directional edges rather
than turning both markers into global drains.

## Transport and normal-path cost

Selected finite remote work owns its authentication and channel lifetime. Reuse
the native OpenSSH client's configured aliases, agents and prompts. Do not open
connections for unselected branches, or authenticate separately for the metadata
and payload of one finite operation. Independent batches can share an owned
connection without making remote namespace knowledge globally valid.

Local publication keeps same-directory staging and atomic replacement, with at
most one file fsync. Cohort failure may leave completed files and supports retry;
there is no journal, rollback registry or recovery scan. Grouping small files
should amortize planning, transport and scheduling rather than introduce a
buffer, Task and global dependency scan for every bytecode instruction.

## Engineering and research basis

* [LLVM MemorySSA](https://www.llvm.org/docs/MemorySSA.html) provides the useful
  distinction between resource definitions, uses and reaching clobbers. The
  adaptation here is filesystem namespace/content versions, not pointer memory
  and not a dependency on LLVM itself.
* [MLIR effects and speculation](https://mlir.llvm.org/docs/Rationale/SideEffectsAndSpeculation/)
  separates effect ordering from permission to speculate an operation. Endpoint
  discovery/authentication must not be executed just because a graph can express
  its dependency.
* [MLIR async tokens](https://mlir.llvm.org/docs/Dialects/AsyncDialect/)
  distinguish completion dependencies from returned values. The VM needs both
  for demanded Bool results and independent finite I/O.
* [OpenSSH configuration](https://man.openbsd.org/ssh_config) supplies native
  connection multiplexing through ControlMaster/ControlPersist. Ownership and
  cleanup must distinguish msync-created resources from user-owned masters.
* [Diffuse (ASPLOS 2025)](https://research.nvidia.com/publication/2025-03_composing-distributed-computations-through-task-and-kernel-fusion)
  uses an intermediate representation to analyze and fuse distributed tasks
  across library boundaries. The applicable mechanism is an explicit dependency
  graph before task fusion, not its GPU/JIT implementation or its reported
  speedups. For msync, amortize per-file admission/channel overhead without
  collapsing an independent frontier into a single serial worker. VM-wide job
  and buffer budgets still apply inside batches; nested per-batch parallelism
  must not multiply the configured concurrency by the number of batches.

The following sections describe the implemented default execution path and its
acceptance results. Earlier rejected candidates remain historical observations,
not substitutes for the final Native and installed-editor evidence.

## Representation and phase ownership

The bytecode container uses format 5. Core owns concrete `ExecutionPlan` nodes,
their `Reads` and `Writes` resource vectors, predecessor IDs, independent
execution batches, destination policy, source origins and demanded Bool outputs.
A resource vector is uniformly empty, singleton or multiple: a directory merge
can reach more than one physical subtree through existing links, and a finite
replacement cohort can read several unrelated source roots. A single nominal
path is not an adequate effect summary for either operation.

The operation kind is distinct from its complete footprint. A concrete leaf
destination retains `Exact`, `Merge` or `Replace`; flattening a container into
its final child path must not accidentally change a directory merge into an
exact-directory rejection. Directory preparation emits creation, member-copy
and postorder metadata operations. Creation with a Null source means an
unconditional directory action; a Path source additionally owns the native
source-directory operation. This is a typed opcode distinction, not a magic
pathname or a new language type.

`ContextualBackend` owns selected partial evaluation and namespace discovery.
Frozen selections have an explicit portable `RangeValue.SnapshotValues` carrier;
ordinary lazy ranges retaining producer inputs are not evidence of an expanded
member list. Known metadata conditions become fixed scalar/control decisions.
Only selections demanded by prepared work are enumerated; an unconsumed or
escaping lazy Range is not an authentication request.

## Resource versions and liveness

Each written resource has its own definition/version, even when one operation
writes several resources. Overwriting one definition cannot remove the producer
while another written root remains observable. For example, replacing a local
tree does not erase a previous merge's surviving writes through a link into an
outside directory.

Read-after-write uses and demanded results drive operation liveness. Mere
write-after-write, write-after-read and barrier ordering are not liveness roots;
otherwise the final writer would resurrect every dead writer preceding it.
After dead definitions are pruned, the backend rebuilds ordering frontiers among
the surviving operations. Prefix-indexed resources avoid comparing every pair
of ordinary independent files.

Atomic file/link publication writes a namespace entry, not the old referent of
an existing destination link. A following read observes the entry version before
resolving its content resource. Directory merging instead follows destination
directory links. Namespace reads of traversed link ancestors keep their
resolution lifetime explicit: a leaf writing outside a tree must finish before
a later replacement removes the link it used. Separators and case comparison
follow the endpoint grammar; a POSIX backslash is filename data.

## Continuations and runtime ownership

The remaining typed bytecode is the continuation program after an opaque effect
or an unresolved input. Runtime orchestration supplies the selected frame's
actual inputs to the backend; the resulting residual code, constants and plans
belong to that frame, not the shared source Module. Rewrites retain instruction
provenance so backedges and opaque-result boundaries resume the original
continuation with current values. This prevents the first loop iteration or
first closure invocation from freezing subsequent invocations. It is not shared
bytecode patching or a stale-plan recovery path.

`ContextualResources` has an explicit run/session owner. Artifact/inspection
factories close their own metadata resources before returning a pure Module;
live orchestration lends its resource owner to preparation and execution so
selected metadata and payload can use the same native channel. No live lease is
hidden in a Module-keyed side table or serialized with an artifact.

The VM receives concrete operations and predecessor completions. One completion
per batch can back several resource registrations. Local independent frontiers
are partitioned across the existing global job/buffer permits, and each local
worker amortizes its native loop and buffer across its chunk. Remote commands,
metadata and payload share the selected owned native session, with per-item
outcomes rather than rollback. A failed stream frame closes that channel;
completed members remain completed.

Demanded Bool results retain their actual per-operation outcomes. Owner failure
aggregation checks only undemanded members, so batching an observed false result
with an independent successful discarded copy does not invent an exception.
Directory metadata finalization is blocked when its required copy predecessors
failed; it does not seal a partially copied directory's permissions or count as
another copied file. Directory creation/metadata have distinct diagnostic and
trace actions, with their source spans and concrete paths retained.

## Projected namespaces and selected computation

Backend filesystem facts have two owners inside one preparation: initial
observations and predicted namespace state after preceding graph operations.
They are not interchangeable. In particular, enumerating an existing source
directory after a planned new-member write must include that new member; a
directory generated by a preceding known copy must not be mistaken for a
currently existing directory and enumerated before it is created. Namespace
projection consumes EnsureDirectory/leaf/finalization operations; a genuinely
unresolved producer creates a dependency-driven continuation instead.

Selection construction remains lazy. A merely bound or escaping selector does
not enumerate files or authenticate an endpoint. A selected transfer or finite
loop consumes the selector, and its recorded snapshot is the membership used
by the residual operation. Remote selection obtains member names, kinds and
canonical namespace paths with one owned-session directory query; subsequent
target namespace resolution is batched, not one network round trip per file.
These caches belong to the current preparation, never to a reusable semantic
module or a VM plan-validity registry.

Known finite loops use a scratch scalar state and concrete operand list before
publishing graph operations. Residual local stores preserve initialization
versus mutation, and unproved loops retain their continuation. Ordinary scanning
stops before a backedge header, so the first iteration cannot freeze later
iteration values. Changed-function and append-only constant/plan views avoid
copying unrelated module tables for each dynamic preparation.

### Known source calls

A resolved source call is not an opaque external effect. The bounded evaluator
consumes typed `FunctionCode`, current known arguments and supported captures;
it does not replay syntax or call the source compiler. It first collects local
copy descriptions and symbolic transfer results. Only a completed proof publishes
those operations into the temporary graph. A discarded caller result does not
make the callee's tail transfer Bool an observer, so helper composition exposes
the same last-writer elimination as direct arrows.

Actual closure construction remains in residual code to preserve function
identity. Runtime captures are resolved through current frame/local/field shadow
facts, not a permanently cached first caller. Unknown callbacks, unproved
recursion, captured mutation and escaping processes retain continuations.
An observed constant return from an I/O-owning function also retains its
completion-dependent continuation: knowing the scalar Null/Bool is not proof
that the callee's owned I/O completed successfully. Direct demanded transfer
Bool results and genuinely pure constant-return calls have separate proofs.

### Preparation cost and allocation attribution

The local component control measures `ContextualBackend.Prepare` separately from
compilation, context binding, fixture creation and VM execution. It uses Debian
Linux, .NET 10.0.14, `DOTNET_TieredCompilation=0`, three warmups and five recorded
samples. The unchanged 1,024-independent-copy source has SHA256
`d8d5ba611926a161b31964699e02e54780980f603d82d89f215a447853834cd3`.
Each preparation returns 1,024 live nodes, one batch, three residual instructions
and three blocks, without creating destination files. Allocation is construction
traffic from `GC.GetAllocatedBytesForCurrentThread`, not retained heap size.

One allocation-only diagnostic recording uses the exact common4 Runtime
`587c19ba80656badc1c46361202084235f2ecf34e8f8b5e688548cc2d2d8cdbe`.
A test-only attach gate places source compilation, binding and three warmups
before recording. Twelve preparations of the 1,024-copy input produce 2,082
GC allocation ticks, all with stacks, weighted by `AllocationAmount64`.
The approximately 226.2 MB of sampled weights attribute 36.4% to the nearest
`PlanResourceIndex` frame, 31.8% to `PlanFileSystem`, 15.4% to `TransferGraph`
and 9.0% to preparation code. These are disjoint nearest-owner groups, not
inclusive stacks to sum again. The recording's shorter fixed fixture pathname
differs from the timing control's scratch pathname; its absolute allocation
counter is not used as the before/after control.

Repeated path-segment `String.Split` calls and full local-path construction
before filesystem-cache lookups account for substantial traffic. The correction
uses span splitting and the platform's
[alternate dictionary lookup](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2.getalternatelookup?view=net-10.0)
to allocate trie-edge strings only on insertion. A standalone zero-warning
check compares 5,000 generated add/remove sequences and all associated queries
against the previous index, passing 480,002 comparisons including literal POSIX
backslashes and empty/repeated separators. Filesystem spelling and canonical
parent reuse belong to one preparation; predicted namespace changes remain
separate from those initial observations.

The following combined control uses common5 Runtime
`327aa67b6de8efe36a4003fd4712a46ab60b30d51cbb0d3d34e8dbdc36d6e239`.
The source, fixture-path length policy, warmup/sample counts and component
boundary match. Common5 includes both the index and filesystem changes and
other backend integration corrections; this is not single-patch attribution.

| 1,024-copy preparation | common4 | common5 |
| --- | ---: | ---: |
| Each recorded allocation, B | 21,549,808 | 12,255,272 |
| Raw milliseconds | 68.9410, 89.0105, 84.0829, 96.9402, 79.5116 | 82.6646, 82.7042, 78.1468, 39.0218, 53.0479 |
| Median milliseconds | 84.0829 | 78.1468 |
| Live nodes / batches / instructions / blocks | 1,024 / 1 / 3 / 3 | 1,024 / 1 / 3 / 3 |

The stable allocation reduction is 9,294,536 B (43.1%). Wall-clock samples vary;
their descriptive medians do not establish a Native end-to-end speedup or a
latency benefit attributable to the index patch alone. There is no timing gate,
input quota or global filesystem cache. The trace and its phase/source/hash
receipt are under `.temp/backend-prepare-perf/`; the unchanged before control and
single after control are `.temp/backend-acceptance/common4-preparation-cost.json`
and `common5-preparation-1024.json`. No second allocation trace or repeated
three-size timing batch is required by these findings.

### Windows directory-link timestamp application

The first Native candidate (`f668ca0`) passed the Linux directory journeys but
failed Windows `metadata-order/source`: the merged directory referent retained
its recent write time instead of the second source directory's recorded time.
This is a native operation target issue, not missing graph ordering. In
[.NET 10's Windows path timestamp setter](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Private.CoreLib/src/System/IO/FileSystem.Windows.cs#L212-L232),
`OpenHandleToWriteAttributes` always uses `FILE_FLAG_OPEN_REPARSE_POINT`.
Consequently `Directory.SetLastWriteTimeUtc` updates the final directory link
itself, whereas the merge and its effect footprint concern the referent.

Directory metadata now uses the existing native attributes handle with
`FILE_FLAG_BACKUP_SEMANTICS` and without `FILE_FLAG_OPEN_REPARSE_POINT`; explicit
link metadata retains the reparse-point flag. Both paths share the timestamp
write implementation, with one handle open and one write and no extra discovery,
snapshot check or graph serialization. Windows execution acceptance belongs to
the next Native candidate; the Linux result alone does not prove this repair.

### Native consumers and captured Range producers

A native consumer's `Pure` classification does not describe the computation
hidden behind a runtime Range. Enumerating `Items` or invoking `Resolve` can
enter its captured interpreter directly, independently of the callback argument
supplied to the native registry. The rejected `f668ca0` Native candidate exposed
this distinction: a delayed producer incremented its captured `calls` cell at
first demand, but backend consumption retained the old scalar fact. The reduced
program produced before/first/second/after values `0/1/1/0`, rather than
`0/1/1/1`, on the actual Linux Native image; the full journey failed on both OSes.

`NativeEvaluation.CanEnumerate` is the shared capability boundary for the main
partial evaluator, known source calls and finite-loop evaluation. A native with
`Enumerates` behavior may consume a Range during preparation only when explicit
`SnapshotValues` membership exists and both `Resolve` and `Selection` are absent.
Other `Invokes` and `LocalMemory` restrictions remain independent. Literal and
recorded snapshots still support partial computation; a live producer stays in
the ordinary native continuation, whose execution boundary supplies fresh
mutable-location facts afterwards. No standard-library check, global
partial-evaluation disable switch or VM plan-validity mechanism is involved.
The existing `hir_semantic_journey.py` delayed-demand case remains the end-to-end
regression contract, including source and source-free object/executable routes.

Managed common8 passes that complete O2 journey through source, relocatable
and executable routes: 28 exact outputs per route, including `0/1/1/1`, and six
negative diagnostics. The scoped result is retained at
`.temp/backend-acceptance/common8-hir-o2.json`; both-platform Native acceptance
of the corrected producer remains a separate delivery responsibility.

### Captured field identity and current preparation facts

Spatial-layout acceptance exposed an existing backend error independently of
packed storage. A Scope method captured its `x` field, an alias wrote 19 through
the method, and a later direct `object::x = 23` updated the field read while the
captured getter still returned 19. The accepted pre-layout Native image produced
this result at O0 and O2. `ResolveKnownCapture` had compared a Cell identity with
`scope.Members[name]`, but that public member accessor already dereferences the
Cell. The comparison could never find the pending virtual field write.

`ScopeValue.TryGetFieldCell` exposes only the exact backing-cell identity without
materializing numeric payloads. Preparation owns a lazy reference-identity map
from Cell to its latest known value. Member stores, active incoming-local stores,
member reads and known-call capture reads use that same location fact. Unknown
values remain unknown. A fresh `InitializeLocal` detaches the previous incoming
location when a physical slot is reused; finite-loop restore publication uses
the same local-write policy. No real Cell or Scope is mutated during this
analysis, and no cache crosses the preparation lifetime.

The normal non-cell field path resolves its logical offset once and then reads
the existing field/shadow value. It does not allocate a cell map or perform a
second member-name lookup. This correction is distinct from numeric read caching
or the layout allocator.

Immutable spatial common2 (Runtime SHA256
`a7de776ea20ca431d470c252faf32e32772f81ff14b60beee4c106821bf93fe5`)
restores the exact original getter/field outputs to 23/23. The real object-layout
journey passes source, source-deleted `.mso` and `.mexe` routes with 12 exact
outputs each, including a discriminator where the creating function's frame
still contains the captured field cell. Its direct and captured reads both
observe 23 after the alias store. The scoped report and six-assembly identities
are retained in `.temp/spatial-validation/common2-object.json`.

## Final contextual Native candidate and workload results

The corrected validation-only source is
`746f6b9319e336751e5486ef3071928f8a0dbcad`.
[Candidate run 42792178206](https://github.com/kleedaisuki/msync/actions/runs/42792178206)
passes all nine selected Native phases on Linux and Windows and all three actual
editor-host suites. The separate real installed-VSIX receipt also passes source
LSP, HIR and format-5 executable graph viewing, outline/hover, read-only behavior
and absence of execution effects. These results close the earlier deferred-Range,
Windows directory-link timestamp and terminal completion failures; the rejected
candidate's results are not substituted for them.

The exact measured Linux Native SHA256 is
`7d1d2856e75631f162bdded1f7258dc7b1d71620afcd2ae28ae42939a08865f4`.
The comparison baseline is the published 0.8.1 Native executable,
`105e29503c74422831c8481281c617deba5356eb56f38ec9da3b23fbd89c5732`.
This backend task does not publish a new release or replace existing 0.8.1
packages. Format 5 belongs to this candidate; the public release remains intact.

### Paired actual CLI I/O

`tests/Performance/ContextualIo/paired.py` runs one explicitly selected workload
per invocation. Each comparison uses seven alternating fresh-process pairs after
one untimed invocation per image, the same existing output paths, `NO_COLOR=1`
and disabled user configuration/telemetry. Inputs receive a new fixed-length
generation before every run. Every output filename and byte is checked after
timing. The ordinary directory case additionally checks an empty directory,
Unix executable/data modes and file/directory modification times. Fixture setup,
verification and graph inspection are outside timing; CLI startup, frontend,
backend preparation and actual I/O are inside it.

The host is Linux x86-64, kernel `6.12.113+deb13-amd64`, glibc 2.41; the harness
uses Python 3.15.2, not a product Python dependency. Each large case has 1,024
files of 4,096 bytes. Development builds, other timing jobs and installed-editor
hosts are paused during the paired window. Raw samples are retained without
filtering or repeated batches.

| Actual source-run workload | Published 0.8.1 median, ms | Candidate median, ms | Median paired difference, ms | Candidate faster pairs |
| --- | ---: | ---: | ---: | ---: |
| One independent copy | 22.26 | 23.55 | +2.23 | 1/7 |
| Ordinary nested directory, 1,024 files | 1,306.67 | 552.05 | -713.76 | 7/7 |
| 1,024 independent source statements | 658.62 | 693.60 | +36.83 | 2/7 |
| 1,024 writes to one destination | 1,632.64 | 212.29 | -1,419.90 | 7/7 |
| One regex selection, 1,024 files | 1,326.51 | 553.23 | -744.89 | 7/7 |

Ordinary directory and regex synchronization improve by approximately 2.37x
and 2.40x on these descriptive medians; overwritten work improves by 7.69x.
The independent-statement workload is about 5.3% slower, and the one-copy paired
cost is approximately +2.23 ms. These are retained costs of this implementation,
not parity or a universal improvement. The paired-difference median need not
equal the difference between the two independent medians. No per-patch timing
attribution follows from this combined backend change.

### Actual prepared work

After timing, the same candidate compiles, prepares and disassembles each exact
generated source without executing transfers. The resulting graph evidence is:

| Workload | Prepared nodes | Backend batches | Eliminated transfers |
| --- | ---: | ---: | ---: |
| One independent copy | 1 CopyFile | 1 | 0 |
| Ordinary directory | 1,024 CopyFile + 34 EnsureDirectory + 34 ApplyDirectoryMetadata | 100 | 0 |
| Independent statements | 1,024 CopyFile | 1 | 0 |
| Repeated overwrite | 1 CopyFile | 1 | 1,023 |
| Regex selection | 1,024 CopyFile | 1 | 0 |

Every row has one plan. The directory's 100 batches retain its actual namespace
and metadata ordering; this is not a claim that the whole folder is one batch.
The overwritten nodes actually disappear, and the regex member list is explicit
rather than recomputed by the VM. Full read-only graph JSON, ephemeral `.mexe`
carriers, source/harness/native receipt identities and raw timing observations
are retained under `artifacts/candidates/contextual-io/746f6b9/performance/`.
The native installation evidence is under the same candidate's
`installed-smoke/` directory.

## Delivery closure and requirement evidence

The accepted production source is `746f6b9319e336751e5486ef3071928f8a0dbcad`.
Later documentation-only commits do not alter its payloads or measurements.
The implementation changes the current reader to format 5; it does not retain
an old-format execution path. The released 0.8.1 tag and asset bytes remain
unchanged. This goal requested backend implementation, not a new public release;
the complete validated Native packages and matching VSIX are retained as the
candidate delivery rather than substituted for that existing release.

| Requested property | Concrete implementation and acceptance |
| --- | --- |
| Ephemeral, inspectable, resubmittable `.mexe` | Format-5 concrete plans plus continuation bytecode; frozen membership/metadata-branch and failure/retry journeys run on both Native platforms. No timestamp comparison, stale-plan classification or automatic replan is present in the VM. |
| Only the last meaningful write survives | Per-resource versions and data-use liveness; actual admission tests, safe copy forwarding, and the exact Native 1,024-write graph retaining one copy and eliminating 1,023. Observed Bool, alias and read-result cases preserve their logical outputs. |
| Actual backend computation | Metadata-dependent branches, concrete path kinds and selections, finite loops and proven lexical calls are computed before submission. Unsupported inputs and effectful callbacks retain explicit continuations, not guessed values. |
| Meaningful graph ordering and directional markers | RAW/WAR/WAW frontiers are rebuilt after pruning; acquire/release follow the table above. Actual marker-fusion and demanded-result tests check executed work, not only printed annotations. Opaque effects remain knowledge boundaries. |
| Fewer blocks and amortized small-file work | The 1,024-file preparation control has one batch and three residual instructions/blocks; ordinary directories expand into file/create/final-metadata work with real dependency ordering, rather than a serial recursive-copy placeholder. |
| Parallel execution and resource management in VM | Prepared dependencies, per-batch completion, global job/buffer permits, source-owned error reporting and explicit run/session transport ownership. Captured scalar mutation stays on its control owner. |
| Connection reuse without extra authentication | Real managed transport tests cover one-client batches and two-client ordered duplex relays. Exact accepted Native CLI filtering confirms one client/authentication/session; the prior candidate's unchanged relay code confirms two clients for three cross-endpoint files. No WAN throughput claim follows from the owned loopback fixture. |
| Correct platform and mutable-language behavior | Linux and Windows each pass nine selected Native phases, including complete HIR source/object/executable semantics and directory-link ordering/timestamps. The rejected delayed-Range and Windows-link failures have root fixes, not library bypasses. |
| Editor, REPL and actual installation | Native PTY/ConPTY, three real editor-host suites, then a separate CLI-installed exact VSIX/native pair. A plan-containing format-5 `.mexe` has read-only custom/text views, highlighting, outline, hover and no execution effects. |
| Optimizer overhead and useful real I/O effects | One allocation trace guides bounded span/path reuse; preparation allocation falls 43.1%. The final matched Native matrix above includes both dominant-workload improvements and independent/single-copy regressions. |

The decision is to retain the measured graph-preparation cost: the dominant
directory/selection cases improve about 2.4x and repeated overwritten work about
7.7x, while explicit independent statements are about 5.3% slower and one-copy
paired latency increases about 2.23 ms. These costs remain visible; they are not
hidden by a pooled overall speedup or represented as eliminated overhead.

`artifacts/candidates/contextual-io/746f6b9/delivery.json` links the exact source,
Native jobs, all four package digests, installed receipt and payload audit.
`performance/index.json` links every source, harness, raw paired observation and
post-timing graph; `transport-cli/receipt.json` records the final selected-channel
CLI proof. Both-platform execution and installed activation are completed
evidence, not requirements deferred to a future release.

## Preparation regions and allocation-linear emission (2026-11-10)

The tail investigation distinguishes function-wide applicability from a useful
entry position. A preparation stops at backward-edge targets. In an 8,192-row
reference scan, a counted private Runtime mirror found 8,197 preparations, of
which 8,192 stopped before executing any instruction. The initial positional
fix removes those empty entries without changing any Step count. Resource waits
remain outside the eligibility decision, so an unprepared region does not lose
its submitted-work dependencies.

The generalization caches one immutable linear region map per FunctionCode.
Backward targets and Return stop propagation; forward conditional successors
union opportunities. Call, GetIterator, metadata natives, Transfer, LocalTransfer
and SelectPath remain opportunities. In particular, GetIterator can expose a
finite I/O batch even though its backedge header is a stop. This is a bounded
intraprocedural reachability fact, not a callee-effect system, value cache or
filesystem validity check. The initial internal module path now shares the
public path's no-preparation decision for functions with no such work.

A distinct-reference manifest exposed a second cost: successful pure filename
calls repeatedly rewrite a function. The old emitter created singleton arrays,
LINQ filters, materialized arrays and Repeat enumerators even for unchanged
instructions. PreparationCode now appends directly into pre-sized output and
provenance lists, then performs the same PC remapping. A frozen 32-row/four-scan
allocation calibration falls from 5,910,640 to 2,923,616 bytes with this emission
change alone. Cold managed/JIT timings are not a latency comparison. The final
combined implementation's one 8,192-row manifest check returns the exact 32,768
result and allocates 625,415,320 bytes; remaining conservative per-row Call
specialization is visible, not claimed eliminated. Setup and the separate
1,000-sample reference-control tail window are in
`tests/Performance/ScopeSpatial/Tails/README.md`.

Rejected duplicate constructors stop speculative evaluation and stay in the
executable path, including inside KnownCalls. TryAdd replaces the existing
Dictionary.Add duplicate check; there is no extra validation pass. The VM owns
the concrete MsyncException, inner cause and source span, and earlier selected
transfers can complete before that failure. The source-linked emission oracle
checks 1,505 cases/12,047 contracts; an independent DFS region oracle checks
1,213 cases/90,515 contracts. The selected-duplicate journey checks 18 direct,
portable and prepared executable cases/93 contracts, including prior bytes and
unreachable later writes. Existing ScopeLayout 109 contracts and all 24 real
source/object/executable loop continuation journeys pass on the final Runtime.

### Exact source calls without a planning continuation

The distinct-reference manifest's actual call is a statically selected source
`filename` wrapper whose body has no contextual preparation opportunity. The VM
may execute that exact source call directly when the caller's following region
also has no planning opportunity. This does not assert purity or elide the call:
local mutation and opaque-native effects still execute under ordinary invocation
and ownership. Existing B/Text target selection is reused; native, unresolved
and open-session targets keep their original path. GetIterator, metadata and
later I/O remain opportunities, and resource waits stay outside this decision.
The single 8,192-row/four-scan allocation control on identical bound bytecode
falls from 625,415,320 to 86,723,704 bytes. Cold managed timings are budget
observations, not tail estimates; the accepted 1,000-sample window was not rerun.

### Selected observation admission

A focused caller-publish/callee-read test exposed a real race: entry preparation
could start at LoadLocal and cross into `exists` before the caller's discarded
publication completed. Waiting only at the entry instruction was too late once
the backend had frozen a false branch. PlanFileSystem now admits physical/cache
observations at their actual selected frontier, including transfer classification,
directory/regex membership, batched prefetch and optional forwarding proofs.

Admission belongs to the active Interpreter and is passed into each preparation,
never retained in Module, an artifact or session-shared transport ownership. Its
staged readiness probe first checks namespace producers; only a subsequent ready
probe resolves referents. A returned task waits existing work only and does not
continue into filesystem observation on its own. Ready reads complete
synchronously. Pending metadata resumes at the exact frontier after its prefix;
speculative call/loop evaluation falls back to the real invocation instead of
waiting before its earlier captured writes. Unresolved process-flow transfers
retain their ordinary schedule/defer path rather than becoming mandatory waits.

An outer observation scope prevents nested prefix classification from becoming
parent-wide waits. Entry/namespace resources retain their non-subtree scope;
actual tree/membership observations include descendants. A scheduler-owned
writer-endpoint summary filters unrelated endpoints before remote resolution,
and a resolver is reused only within that one preparation. It is not a filesystem
validity cache. A transfer's append-only draft is merged only after complete
preparation, so blocking partway through a cohort cannot publish an unregistered
partial operation alongside earlier sites. There is no execution rollback,
global drain, stale-plan check or automatic replan.

Final focused admission validation uses Runtime SHA256
`4abb200b538daab27b4679c1199202d7cf9d57d2d37b6a16930afacdddac1de7`:
22 actual VM journeys/143 contracts plus 24 real loop and 12 selected namespace,
selection and overwrite CLI routes pass. Independent held-producer oracles check
prefix progress without replay, no partial cohort admission, unrelated ready
reads, unselected branches and replacement through an intermediate path. These
are ordering/ownership checks, not another latency measurement window.

Selected read admission has three ordered stages: namespace publication, exact
entry publication without following the leaf, then current referent/subtree
resolution. An entry replacement can make the old symlink referent irrelevant;
collecting both dependencies before waiting would incorrectly retain that old
wait. A controlled Jobs=1 oracle holds an old-target producer and queues a
prepared WriteText replacement of the symlink entry. Releasing only the job
permit must let the observer return `new` before the unrelated old producer is
released. This passes on Runtime
`cb9080b66b196ba29f2d0a747456f53f2e999f7a067891959577c3abe444955d`;
no task continues filesystem work after a pending admission stage.

## Compute-only continuation admission (0.8.6 investigation)

The library matrix exposed a runtime cost that survives initial binding. A
pre-bound image bypasses the initial relocatable preparation, but not the
execution-time admission at frame entry and call boundaries. Applicability
currently treats any Call as an opportunity; the direct-call bypass needs
both a statically resolved eligible callee and no adjacent preparation demand.
Nested numerical container operations therefore repeatedly allocate a planning
context, replay known prefixes and sometimes rebuild instruction/block maps,
even in a closed program with no possible transfers or metadata reads.

A diagnostic Native AOT build of the 0.8.6 VM adds a counter and an opt-out at
that runtime admission point. The same binary executes the same bound modules
with and without the opt-out; the modules have no plans, and all return values
or digests match. Two fresh-process AB/BA rounds, 512 numeric keys and three
timed executions per arm give:

| Workload | Continuation preparations per run | Stock / bypass, ms | Stock / bypass, allocated MB |
| --- | ---: | ---: | ---: |
| Red-black Set | 40,144 | 933 / 201 | 398.1 / 18.4 |
| Heap | 35,243 | 621 / 104 | 293.2 / 9.9 |
| Linear-probing hash set | 2,056 | 49.2 / 17.8 | 19.6 / 2.2 |
| Handwritten SHA-256, one padded block | 4,192 | 172 / 56.7 | 55.6 / 6.5 |

One checked pair with the exact original 8,192-key Set image gives 26.03 s
versus 5.30 s, 985,902 versus zero preparations and 9.73 GB versus 0.45 GB
cumulative allocation, not peak memory. Independent interrupted Native AOT
stacks also put preparation and block reindexing on the hot path. The bypass
is an experimental intervention for these known compute-only programs, not
an execution mode or a safe general optimization. An empty serialized plan
table does not prove that a future continuation is free of I/O.

A production fix should prove a negative: retain a compact, immutable
contextual-effect and downstream-demand summary of the code, not a cache of
argument values, object contents, filesystem observations, or previous frames.
Propagate effects through proven call/dispatch targets, and keep unknown or
effectful callbacks eligible. A call may be locally compute-only but still
produce an input to a downstream transfer, so check result demand as well as
the callee's own effects. Start with the closed computation case; test a
mixed program that computes its destination, a late-bound effectful callback,
and existing namespace/observation waits before broadening the proof.
