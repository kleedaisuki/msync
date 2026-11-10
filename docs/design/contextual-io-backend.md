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

Implementation and acceptance results are recorded below as they become
available. The contract above is not evidence that an optimization is already
on the default execution path.

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

### Backend resource versions and projected namespaces

`TransferGraph` assigns a separate definition identity to every resource written
by an operation. Surviving resource definitions and demanded results seed
liveness; only reaching read definitions propagate that liveness backwards.
WAW, WAR and directional ordering do not make a dead destination version live.
After pruning, ordering edges are rebuilt over the remaining operations. This
also handles an operation with several physical writes: replacing its ordinary
destination does not discard a surviving write through an out-of-tree alias.
The path index visits relevant ancestors and overlapping descendants rather
than scanning all previous transfers for each ordinary file.

Atomic file/link publication writes a namespace entry. Directory merging can
write through existing directory links. Captured alias-ancestor entry reads
keep those links alive until the operation has consumed the selected physical
path; a later root replacement must not retarget an earlier live write.
Resource separators follow the endpoint grammar: backslash is a separator only
for local Windows resources, not for POSIX/SSH/WSL filenames.

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
