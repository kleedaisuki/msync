# Effect-aware planning and bounded execution

## Structured v0.4 preparation

`preparation::prepare` resolves native paths and observes file kinds without
capturing payloads or opening a stream connection. Its `live=false` form never
launches SSH/WSL metadata helpers. A live remote inspection batches stat-like
queries by endpoint; it does not download source data or launch the user's
program. Environment and PATH values are captured as physical local locations.

`prepare_with_context` additionally accepts version-store-owned, epoch-specific
`PlanningContext` premises. Supplied type facts override the live host's absence
when a source exists in the transaction's unpublished view. They are a trust
boundary, not facts inferred from a filename. Opaque program launch invalidates
unproved subsequent filesystem kind assumptions; missing host output is then
unknown rather than an automatic source-not-found diagnostic.

The ordered type interpreter keeps origin and latest-checkpoint states separate.
Bare rollback discards pending type writes; only successful checkpoints and block
completion contribute to named undo. Origin restoration affects the named
transaction's committed write footprint, not unrelated later resources. Successful
assignments establish directory kinds for required parents, while unknown copied
directory descendants retain explicit refinement obligations.

The first existing local parent is captured as an epoch-qualified metadata
frontier. Namespace writes include that directory but not its unchanged
ancestors, so creating files in separate existing directories does not become a
spurious write to the global root. Runtime-provided parent frontiers take priority
over host observations and are retained in the replay certificate.

Regular assignments are optimized to a fixed point inside observation-free
epochs. Explicit commit/rollback, process operations, physical streams, selectors,
containers and directory replacement bound those epochs. The mature copy planner
supplies file-only candidates; typed replay uses the same captured resource keys,
hardlink hints and conservative Windows/WSL overlap relation. Alias hints may
block a rule but never become positive path equality.

The v0.4 metadata model requires guards beyond byte-only dead-store rules:

- An initial missing-destination creator is retained, because it writes parent
  namespace metadata. A later identical regular-file copy may instead disappear
  when its source version and metadata are stable and no intervening operation
  observes or changes the source/destination. Existing-destination dead writes
  and stable regular-file forwarding remain available.
- A literal-backed file is not interchangeable with its literal byte stream.
  A subsequent file copy inherits the producer's mtime; recreating the bytes as
  a fresh literal would lose that correlation. Literal-to-file version propagation
  therefore retains the producer until the IR can represent its metadata origin.
  No synthetic file backend is used to hide this distinction.
- A literal overwrite retains destination permissions, even though its bytes and
  mtime are new. It is a metadata consumer, not a complete file-version overwrite:
  dead-write elimination cannot use it to erase an earlier permission-producing
  assignment. Only a later file-valued write supplies the complete replacement
  metadata required by this rule.

`PreparedProgram` retains the resolved original, optimized program, epoch type
facts, rewrite provenance and graph premises. `verify()` replays using captured
resource comparisons only: deleting a source after preparation does not make the
certificate renderer read data or re-probe the filesystem. Execution must still
validate current resource/version ownership; a certificate is not a TOCTOU or
hostile-filesystem security guarantee. The ordinary-file rules assume quiescent
external resources, as does the existing planner contract below.

A valid conservative graph need not be a complete physical I/O plan.
`is_execution_ready()` remains false for unknown physical types, selector entry
expansion and physical stream ownership/co-start obligations. Process-view version
premises must distinguish an immutable manager input from a private program write
epoch; ordinary location equality alone must not turn a program's own stdin into
an exit prerequisite.

An explicitly pinned input also bounds pathname-based rewrites. A retained older
file version may intentionally differ from the latest reaching write at that
pathname. Preparation preserves that version selection instead of forwarding by
name, while the execution adapter retains the actual reader and its lifetime.

Targeted coverage is `tests/preparation_v4.rs`: metadata-only/no-launch checks,
overwrite and redundant-write rules, fixed-point forwarding, version/rollback
boundaries, independent file-value models, actual reference/optimized mtime
correlations, finite HTTP non-observation, and a Unix FIFO non-opening case.

This structured pass is the correctness baseline for the later integration
profile, not a v3 scaling claim: epoch type-event queries and literal-aware
certificate replay currently scan their histories. Preparation deduplicates
resolved endpoints, but its resource adapter does not yet share the legacy
planner's prefix cache across all structured locations. Measure those costs on
the shared-source/many-destination workload after full execution integration,
then reuse the indexed frontier/cache machinery rather than introduce a second
unverified optimization path.

The new language pipeline separates **frontend compilation**, **planning**, and
**execution**. `Compiler` still resolves declarations into `Program`; `plan.rs`
turns inspected operations into an optimized dependency graph. The scheduler
executes only that graph under explicit resource limits. A plan is an executable
value, not the old sequential listing with a new heading.

```text
source -> tokens/AST -> resolved Program
                            |
                 snapshot locations and kinds
                            |
              regular-file rewrite fixed point
                            |
              effect graph + transitive reduction
                            |
                 bounded topological execution
```

## Public data model

`Plan::build(&Program, &[OperationKinds])` receives the frontend's ordered
operations and inspected source/destination classes:

- `Regular`, `Directory`, and `Stream` describe the three operational file kinds.
- `Missing` is an observation, not a fourth file kind. A missing source may be
  produced by an earlier operation, so the planner propagates exact producer kinds
  before deciding whether an assignment is optimizable.
- `Unknown` records genuinely insufficient inspection, especially offline remote
  endpoints. It is never silently promoted to an ordinary file.

`Plan::offline` supplies unknown kinds. It performs no remote requests. Normal
live preparation can refine those classes with metadata-only local/WSL/SSH
inspection. Environment/PATH/local locations must be resolved and captured by
preparation, so scheduling and execution cannot resolve an ambient variable to
different locations later. If an API caller leaves ENV/PATH unresolved, those
resources conservatively overlap everything and cannot justify a rewrite.

`Plan::build_profiled` is an additive inspection API returning `(Plan,
PlanningStats)`. It reports resource-cache misses, calls to
`std::fs::canonicalize`, ordinary-file identity handles, and dependency candidates.
These are deterministic work counters, not kernel syscall or elapsed-time
estimates. The original `build` API remains unchanged and returns the same plan.

Each retained `PlanNode` owns its `CopyOp`, file classes, read/write/consume
effects, original operation index, and dense node ID. `dependencies` contains
retained predecessor IDs; `edges` explains RAW, WAR, WAW, and consuming-read
hazards. Original indices remain attached to removed and forwarded operations,
making optimization decisions inspectable without losing source provenance.

## What optimization means

For ordinary files, the contract is final filesystem values under a quiescent
external filesystem. An assignment that is overwritten before any observation
does not perform its copy I/O. This is intentionally stronger than merely
reordering every original side effect, and is the reason the user's example can
be reduced:

```text
config_file := ~/.codex/config.toml
des := ~/Desktop

config_file -> des/config.toml
$wsl::config_file -> des/config.toml
```

When both sources are proven ordinary files, only the last assignment survives.
Different source endpoints do not invalidate the proof: what matters is the same
destination version and whether its earlier value was observed.

The rewrite rules deliberately apply only to exact regular-file assignments.
Directory copies, selectors, container insertions, stream transfers, unknown
effects, and invalid same-file assignments are not treated as dead stores.
Existing destination directories without a trailing `/` remain runtime errors,
not implicit merges or values the optimizer can replace arbitrarily. A trailing
`/` retains container semantics and its no-clobber observations.

### Stable-source forwarding

For `a -> b; b -> c`, `b` can be replaced with `a` as the second source only when
the reaching producer is an eligible assignment and no intervening mutation can
change `a`. A later `x -> a` therefore prevents forwarding through the old `b`
snapshot. Forwarding also refuses to create a same-file/hardlink copy.

HTTP snapshots are not duplicated: a second GET can be dynamic. Remote-to-local
snapshots are likewise retained instead of turning one remote download and one
local copy into two expensive remote reads. Local origins and same-endpoint
ordinary-file chains retain the useful forwarding rule.

### Backward dead-store elimination

For each eligible write, indexed future effects identify the first later live
overlapping observation and mutation. A read at or before the first write keeps
the write, including when one later operation both reads and writes it. An
exact eligible overwrite kills it. Any other overlapping mutation blocks the
proof, including a descendant path that depends on the destination being a
directory rather than a file. The backward sweep ignores operations already
removed in the same sweep.

Forwarding and elimination alternate to a fixed point. For example:

```text
a -> b
x -> a       # Dead after the next sweep.
b -> c       # Can forward only after x -> a disappears.
y -> a
z -> b
```

The final plan reads `a` into `c`, then preserves the last writes to `a` and `b`.
No arbitrary iteration cutoff claims a fixed point prematurely. Removed nodes
strictly decrease the live count; forwarding walks to an earlier stable reaching
origin. These finite measures establish termination.

## Dependency and identity model

Resources contain resolved endpoint identity and normalized path components.
Local keys capture current-directory/home expansion once per plan and canonicalize
the nearest existing prefix. Canonical-prefix results are cached for the lifetime
of that plan, including common missing parents; each new child is still probed
before a cached parent is used. Case folding is used only for conservative Windows conflict
detection, never as an elimination proof. File-identity fingerprints add
dependencies for existing hardlinks; a fingerprint collision can only add an
unnecessary dependency and cannot eliminate an operation.

On Windows, WSL `/mnt/<drive>/...` paths and WSL paths relative to the captured
native working directory additionally receive conservative drive projections.
These projections **only add hazards or disqualify a rewrite**: they do not
rewrite an endpoint, prove two paths equal, or infer a produced type across
endpoints. Thus an unusual custom `/mnt` mount can only lose concurrency or an
optimization; it cannot make a false equality eliminate a copy. This covers the
real producer/consumer case of a WSL write to a drive-mounted file followed by a
native read of that file. Arbitrary SSH aliases and custom cross-endpoint mount
layouts still require explicit identity information beyond this model.

Remote filesystem proof keys retain a rooted/relative distinction. `ssh::out`
and `ssh::/out` do not prove the same destination or produced type. Their overlap
comparisons remain conservative because an unknown remote working directory can
make relative and absolute resources alias.

Containment matters even for an exact file access: writing `a` changes whether
`a/b` can exist. Sibling files remain independent, while ancestor paths conflict.
For a single-source container insertion, the effective write is
`container/source-basename`; selectors and directory effects retain broader
hierarchical footprints.
When a remote source's final name cannot be resolved syntactically (home/current
directory/root expressions), the write covers the whole container rather than
inventing names such as `container/~`. Local working-directory/home names are
resolved from their captured native location.

Stream reads consume bytes, so two reads of the same stream conflict even though
ordinary file reads do not. Stream operations are never eliminated or forwarded.
This is an optimization barrier on conflicting effects, **not a global execution
fence**: an independent open socket does not prevent unrelated copies or relays
from progressing.

### Co-start edges are not completion edges

The ordinary read-after-write rule cannot be applied blindly to a pipe:

```text
$pwd::pwd/input -> FIFO
FIFO -> $pwd::pwd/output
```

Waiting for the producer to finish before starting the consumer can deadlock the
producer's open/write. Known stream destinations and consuming sources therefore
form a separate channel graph. Exact matching channels pair producers and
consumers in declaration order. A connected chain becomes one `stream_groups`
co-start cohort, with explicit `stream_connections` provenance. Complementary
effects on a paired channel do not become completion predecessors. All unrelated
file hazards and repeated same-direction stream ordering remain completion
dependencies.

A two-operation transfer and a multi-pipe chain thus start together, while
ordered repeated producer/consumer waves remain separate cohorts. A channel with
no counterpart inside the plan still uses its external peer normally. Mixed
roles with unequal producer/consumer counts are ambiguous and produce a
`PlanIssue`, rather than guessing a pairing and hanging. Self-feedback, closed
stream cycles, internal file-completion dependencies, and cycles introduced by
contracting co-start cohorts are diagnosed too. Native TCP's `/` path marker is
an exact stream endpoint, never a directory container or basename insertion.

The scheduler must admit a cohort atomically only after all its external
completion prerequisites have finished and all its slots/buffer costs fit.
Insufficient `--max-streams` or `--memory-limit` is an error before data I/O, not a
reason to start half a pipeline and wait forever for the unadmitted half. Pipeline
buffers remain bounded; cohort construction does not exempt a stream from limits.
Offline unknown remote kinds cannot establish these channel relationships: use
live scheduler preparation before execution, not an uninspected offline plan.

Every dependency points from an earlier operation to a later operation, so the
operation graph is acyclic by construction. `src/plan/index.rs` indexes the exact
union of may-overlap domains: endpoint/comparison-component prefixes, Windows
drive projections, same-endpoint identity hints, and unresolved wildcard
endpoints. Prefix tries own arena nodes rather than recursively allocated child
objects. Singleton access-ID sets are stored inline; sets with several members
use `BTreeSet`. Latest/earliest queries replace unrelated historical scans in
classification, source-version forwarding, and backward DSE.

Proof-key components, folded comparison components and optional drive projections
are immutable shared `Arc<[String]>` arrays. Scope and location remain owned by
each access; sharing cannot mutate a sibling access. Equality stays value-based,
and non-Windows comparisons reuse the exact-key component array. Resource-cache
hits therefore do not deeply clone every path component into each effect/state.

Dependency discovery uses separate read/write/consume frontiers. Ordinary reads
do not query all previous ordinary reads: many independent destinations sharing
one source therefore stay independent. A new write or consume retires an old
frontier entry only if its conflict domains cover *all* that entry's resources.
Parent-path coverage alone cannot retire an existing descendant file whose
hardlink identity might be referenced outside the parent. Identity/projection
coverage checks and paired-channel exceptions preserve those hazards. Co-start
channels never use ordinary completion-frontier retirement.

The remaining predecessor candidates are transitively reduced by a sparse,
generation-stamped predecessor walk. A one-candidate node requires no reachability
walk. There is no unconditional `N²` ancestor-bit matrix. Retained dependencies
remain the DAG's exact transitive reduction, with the same hazard explanations
and provenance; the scheduler does not rediscover dependency closure.

For `N` operations, path width `P`, and `R` rewrite rounds, indexed nearest-effect
passes perform `O(R N P log N)` work plus resource construction. Graph discovery
is output/frontier-sensitive; sparse reduction may still need substantial
reachability traversal on adversarial graphs. Wildcards, broad hierarchical
effects, hardlink aliases and co-start exceptions can legitimately retain a large
frontier. There is no claim of worst-case linear planning. Storage is
`O(N P + N + E)` for resource/index memberships and the retained graph (apart from
transient candidate/walk storage), rather than an always-allocated `N²` matrix.
The fixed point is still reached without a round cutoff.

Planning never opens a FIFO/socket to obtain identity. Opening one can block or
consume a peer connection. Ordinary-file/directory metadata gates identity
handles, and Windows named-pipe namespaces bypass both handle and path
canonicalization probes. Content snapshots, hostile concurrent source mutation,
arbitrary cross-endpoint mount aliases, and filesystem transactions are outside
the planner's guarantees; runtime safety checks remain in force.

## Verification and precedents

`tests/plan.rs` checks the user's overwrite example, stable-source forwarding,
producer-kind inference, fixed-point exposure, unknown/stream conservatism,
hierarchical dependencies, hardlinks, transitive reduction, paired stream
cohorts, ordered channel waves, and invalid cohort diagnostics. Property tests
generate ordinary-file assignment programs and compare both sequential optimized
execution and reverse-ready topological schedules with an unoptimized state
model. Replanning the optimized program must not change it further.

The indexed implementation additionally passes overlap-index properties against
brute force, a 10,000-program differential comparison against the archived
`d96724d` planner, and independent raw-hazard reachability checks with hierarchical
paths, hardlinks, unresolved resources and WSL projections. See
`planner-proof.md` for the retirement argument and
`performance-planner-v3.md` for workload-specific timing, memory and probe counts.

[LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) distinguishes reaching
memory definitions from observations, which is the relevant idea behind the
file-version model; this small linear DSL does not need LLVM's control-flow
machinery. The upstream
[dead-store elimination implementation](https://llvm.org/docs/doxygen/DeadStoreElimination_8cpp_source.html)
illustrates why an intervening observation or uncertain clobber must stop an
overwrite proof.

Mokhov, Mitchell, and Peyton Jones,
[Build Systems à la Carte](https://www.microsoft.com/en-us/research/publication/build-systems-la-carte/)
(ICFP 2018), separates scheduling from deciding what must run. Here that means
keeping effect analysis and elimination in `Plan`, rather than hiding them inside
worker-launch code. The scheduler can evolve its resource limits without changing
the meaning or provenance of the optimized graph.

## v0.4 shared mutable view epochs

The structured HIR graph in `hir::graph` distinguishes a native process from the
resource epoch that contains it. Two opaque children sharing files cannot be
declared independent merely because they have different argv or independently
captured roots. A pipeline such as `p::stdout -> q::stdin` needs its consumers
admitted before producer completion; serializing every full-root effect until
each child exits would deadlock that pipeline.

`CohortViewProof::shared(owner, epoch, members)` is the trusted adapter boundary.
Each member supplies its complete `ProcessEffects` certificate, with the same
actual provider and ViewId. The runtime retains one mutable native backing/box,
forks leases from it, joins every member and descendant, and imports the shared
delta exactly once. A member may be certified before launch only if its actual
native fork lease already exists, remains retained, and is bound to that
ProcessId's eventual launch. Desired grouping and matching directory contents
are not proofs. The pure constructor validates certificate shape, not the OS.

The graph checks complete/matching membership and keeps transaction, commit,
rollback and ordinary external-filesystem epochs separate. Within a certified
epoch, member launches retain source-order `ProcessStarted` prerequisites.
Their read/write effects do not disappear: `SharedCohort.effects` retains their
aggregate footprint, while foreign managed reads/writes wait for the complete
epoch at the boundary. This is region effect summarization, not a purity or
disjointness assertion; the organization follows the region/effect distinction
described in [the architecture references](architecture-v4.md).

| Event/premise | Meaning |
| --- | --- |
| `ProcessStarted(p)` | The member's launch is acknowledged; its ports can be serviced |
| `ProcessExited(p)` | Member status is available, not permission to export a shared live view |
| `CohortSealed(CohortId(i))` | Every member joined and the view at `graph.cohort_views[i]` was exported/imported once |
| `VersionReadProof` | A retained immutable regular-file reader selected at this instruction's logical epoch |
| Publication dependency | A gate on the managed assignment, not on private pipe draining |

Pinned stdin reads use `Resource::SnapshotFile`; private writes to the original
path cannot mutate their retained bytes. Ordinary unpinned file reads still
wait for the relevant sealed delta. File type, metadata inspection and argv
alone never invent a pin. Program output, including an immediately captured PID,
can be staged while peers launch; its managed-file assignment waits for the
whole shared export. File/namespace WAW and WAR obligations remain recorded on
that publication phase. This also handles an output declaration between member
launches without making a future peer wait for the publication that itself
requires that peer to finish.

A stream-connected group with unknown/overlapping effects but no actual shared
view evidence is a typed `SharedMutableView` refinement: `check`/planning can
return a valid conservative certificate, but `is_execution_ready()` remains
false. Already-proved disjoint effects can still admit separate private views.
Cross-checkpoint live-pipe use and inconsistent provider membership remain hard
issues. Full origin rollback restores only reversible managed file/namespace
writes; it does not reacquire consuming stdin/stdout ports or repeat HTTP GETs.

The focused graph tests cover full-root pipelines without false disjointness,
foreign RAW/WAW boundaries, prior-submission members, actual retained pending
snapshots, output publication order, epoch separation, certificate tampering,
and rollback combined with a shared cohort and a pin. Independent assignment
models still compare every admitted small finite schedule, including reads and
directory metadata. These are graph/proof tests, not substitutes for the actual
Linux/Windows shared-view journeys. Runtime event admission and publication
must consume these phases; graph construction alone does not establish complete
scheduler integration or completion of v0.4.

### Next integration: submission-owned event binding

Status: implementation proposal, not a claim that the current runtime consumes
every node gate. The native shared-view/prelaunch gate is the current baseline.

Keep immutable plans alive with their work rather than consulting a mutable
"latest graph" when an older REPL flow completes. Allocate a monotonic
`SubmissionId` before admitting a source submission and retain an
`Arc<SubmissionPlan>` in every flow, staged assignment and pending region action.
The plan owns prepared HIR, source identities, graph premises and event bindings.

```text
NodeKey          = (SubmissionId, HIR InstructionId)
PublicationOrder = separate source-order ticket, never an HIR node identity
CohortKey        = (provider, actual ViewId, runtime owner, native epoch)
Gate             = (target NodeKey, Start | Publish | RegionCommit, prerequisites)
```

An invocation/session namespace contains those IDs; process IDs remain the
session's globally owned `ProcessId`, because ports outlive source submissions.
Bind `CohortSealed(CohortId(i))` through that plan's retained certificate into a
`CohortKey`. Never use `i` as a cross-submission/global event identity. Likewise,
split the current runtime's fresh publication-order counter from HIR identity;
numeric coincidence is not a mapping. Preserve explicit region/source order
when assigning publication tickets, rather than relying on HIR IDs being sorted.

| Persistent event | Completion condition |
| --- | --- |
| Node service ready | Private IO/stage work is available; this is not logical completion |
| Node logical complete | Its assignment is visible in the correct owner's version view |
| Process started/exited | Actual native acknowledgement/status for that session-owned process |
| Cohort imported | All members joined and the one private delta entered the owner's pending view |
| Owner committed | The epoch is visible to dependent foreign owners under the isolation contract |

The last two events must not be collapsed. Same-owner stdout publication needs
the imported epoch, then writes its assignment into the pending view; it cannot
wait for the commit that itself requires that assignment. A foreign ordinary
file read must not see "sealed" as permission to read an older committed head.
For a first correct scheduler, conservatively finish the preceding implicit
owner before admitting such a foreign start. Explicit transaction child nodes
can complete in their pending owner view; the transaction wrapper completes
only at its actual commit. Manager visibility policy, including read-uncommitted,
remains authoritative rather than being guessed from an event name.

One scheduler owns the ledger and manager mutation. Native/flow tasks send typed
terminal events through a bounded channel; notifications are wakeups, not the
truth of completion. Store success/failure persistently so late subscribers and
dependent cancellation cannot lose an event. Do not build completion from bare
`Notify` counts: Tokio documents its single-permit and registration behavior in
[the Notify contract](https://docs.rs/tokio/latest/tokio/sync/struct.Notify.html).

Admission/pumping contract:

1. Bind and validate all start, publication and region-commit gates before
   effects. Apply them to finite transfers and connected flows, not just spawns.
   Acquire a pinned input at the correct logical version, after its prior
   producers; opening an arbitrary old snapshot is not refinement.
2. Admit the ready frontier within copy/process/stream/buffer limits. Reserve a
   required co-start cohort together; do not wait with half its budget held.
3. Pump other ready work while a gate is blocked. Private drain EOF can enable
   sealing, but cannot mark an output node complete before its publication gate.
   Export/import the cohort once, publish ready assignments in source order,
   and emit owner-visible/committed events at their distinct boundaries.
4. If ready work is exhausted, await actual registered task/native events or
   cancellation. A missing future producer is not a live task. In the REPL,
   reject a new submission before its effects when progress requires future
   human input on an owned stdin; identify the process/resource and offer an
   input connection, `/wait` or `/stop`, rather than hanging the command loop.
5. On failure, terminally fail dependent gates, join/cancel owned work and retain
   the structured cause/source chain. Drop does not silently fulfil a gate.

Retain a submission until no flow, deferred publication or dependent gate owns
it. Retain process scalar receipts separately for later REPL reads. This uses
explicit work ownership/progress rather than timers; the relevant dataflow
precedent is [Naiad's progress-coordination model](https://www.microsoft.com/en-us/research/wp-content/uploads/2013/11/naiad_sosp2013.pdf),
without importing its general timestamp machinery into this structured DSL.

Discriminating follow-up gates: two inputs reusing HIR node zero while the first
still runs; delayed stdout publication versus an unrelated ready transfer;
same-owner read-your-writes before commit versus a foreign committed read;
late completion subscription/cancellation; and a REPL operation blocked by an
unconnected stdin that returns advice without starting any new effects. Actual
native cohort tests remain required; a spawn-only gate does not close this work.
