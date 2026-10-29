# Effect-aware planning and bounded execution

> Historical v0.3/v0.4 planning notes. The current design specializes only the
> next ordered basic block, without transaction epochs or filesystem-wide
> prebinding; see [barrier execution](barrier-execution.md) and
> [block specialization](block-specialization.md).

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

Live processes retained from earlier submissions also bound host observations.
Their certified write footprints invalidate overlapping initial host file kinds
until the correct seal/import event gate; a missing footprint is conservatively
opaque. A read-only or disjoint footprint does not invalidate unrelated kinds.
This prevents premature directory/missing-source errors and byte-only DSE based
on a host file that a pending native import may replace. Own ordered assignments
still establish their normal postconditions. Runtime-supplied epoch facts remain
trusted only after the runtime has selected the correct managed version at its
event gate. An actual matching retained `VersionReadProof` proves its input is a
regular file independently of subsequent replacements of the original pathname;
preparation neither discards that pin nor fabricates one from host metadata.

`PlanningContext.pending_effects` carries the actual retained write domains of
earlier asynchronous flows/deferred nodes as the same conservative premise. It
invalidates host classifications even without a live process (for example, a TCP
flow that has not yet published its missing target). These effects neither mint
external node identities nor prove that a namespace insertion's bytes already
exist. The runtime keeps the real producer/`NodeCommitted` gate in its event
ledger and refines the source after that gate; the preparer leaves overlapping
types unknown. No namespace WAW exemption follows from this field.

`host_unstable: BTreeSet<EndpointKey>` is a separate, per-instruction/per-operand
premise. `EndpointRole` distinguishes transfer source/destination and executable;
argv path values are not promoted to required file inputs. A logically known
future regular file/directory can still require deferred physical enrollment:
an earlier unpublished directory replacement may leave the current host ancestor
as an ordinary file. Inspection walks ordered writes before probing each next
endpoint and retains that instability across checkpoints (and conservatively
across rollback). It keeps raw `..` and does not downgrade correct logical kinds
to encode this condition. Runtime/domain uses the marker to enroll an envelope,
then resolves/revalidates only after the actual producer start/publication gate.
An unrelated unknown file's scoped write does not defer independent bad paths.

A verified physical-stream destination is not a future filesystem namespace
replacement. Writing bytes to a FIFO/socket therefore preserves the next
reader's observed `Stream` kind and does not add `host_unstable`; otherwise a
file-to-FIFO-to-file chain would enroll the FIFO's parent as ordinary snapshot
data before its co-start protocol could run. This rule applies only while that
destination observation is stable. A cached stream type under an earlier native
replacement still requires the normal deferred endpoint gate. Physical stream
ownership and co-start refinement remain mandatory in either case.

The structured executor discharges FIFO rendezvous with explicit
`physical_rendezvous` premises: opposite source/destination `EndpointKey`s,
exact local FIFO paths, and a native provider/inode identity. The pure graph
checks stable `Stream` facts, same-region membership and exclusive endpoint
ownership. Only the matched FIFO Write/Consume conflict waits for `Admitted`;
unrelated file/namespace hazards and same-direction consumers remain ordered.
Repeated waves have separate groups. Contracted completion cycles, internal
completion hazards and closed FIFO-only cycles are rejected before IO.

Execution preparation retains metadata-only inode leases (`O_PATH` on Linux),
not FIFO read/write connections. These prevent inode-number reuse without
satisfying a reader/writer rendezvous. Current-submission endpoint leases are
owned by the runtime; admitted flows retain their own clones through descriptor
opening. Admission revalidates the pathname, and descriptor `fstat` validates the
same retained inode before byte IO. Writer retries also check the path, so
replacing a FIFO with another unopened FIFO cannot cause a stale ENXIO loop.
Ordinary compiler check/format entry points do not acquire these execution leases.

Connected groups reserve every stream slot and two configured buffers per flow
before the first endpoint opens. Static insufficient capacity is rejected before
payload capture; dynamic partial reservations are dropped on failure. Remaining
reservations are released when the owning submission exits or is cancelled.
Metadata leases are released on shutdown. The transport retains its relay,
close watcher, cancellation and file-sink publication policy; FIFOs never become
immutable filesystem snapshot entries.

Focused acceptance on WSL Ubuntu with fixtures on a private project-local tmpfs:
the full physical endpoint target passed 14 cases (one explicitly native-process
case ignored), including two-end FIFO co-start, repeated waves and a three-flow
chain, stream/memory capacity refusal before capture, sockets and EOF/stop.
Graph replay/hazard tests passed 67/67; the FIFO type-stability regression passed
1/1; real inode replacement/metadata-lease tests passed 3/3. The same library and
three integration targets passed strict Clippy. Reproduction is the disposable
`.temp/check-fifo-preparation-v4.sh`; these are correctness results, not latency
benchmarks or a replacement for provisioned native-child gates.

Inspection uses the same pending-write predicate before probing a leaf or its
ancestors. A currently non-directory ancestor or denied host lookup must not
preempt a native import that may replace it. Deferred paths retain their raw
absolute spelling, including `..`; unverified symlink traversal is not collapsed
into a positive path/alias proof. No physical-identity resource is captured for
these endpoints, so even later inferred types cannot accidentally enable rewrites
using pre-import aliases. Syntax/backend validation still runs (including NUL
rejection); execution revalidates the actual type/ancestry after its event gate.

Unverified absolute remote paths may retain a pure logical resource key without
capturing a host alias. This preserves dead-store proofs between a native source
and an explicitly typed remote source. WSL drive projections (`/mnt/...`), parent
traversal, relative paths and native unverified operands remain excluded: their
canonical/physical alias premises cannot be recovered from the old host. No
filesystem probe is hidden in the logical remote resource constructor.

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
that publication phase. A managed-file assignment is not a native process
effect: if a subsequent program may read that path, the old epoch must seal,
publish the assignment and, where required, commit before preparing its fresh
view. Reusing the old backing would expose the old file even when owner/ViewId
strings match. Direct port-to-port connections remain true shared co-start.

`shared_native_launch_order` classifies only a native-Spawn to native-Spawn
witness whose complete actual effects match a real shared-view certificate.
It never relaxes a deferred file assignment or mutable file read, and callers
must check every other pending witness independently. Multiple existing-peer
output drains can continue in one epoch; a later native launch after a managed
assignment starts a new one. This keeps private IO progress separate from the
visibility that a later program's file argument actually requires.

Native launch epochs and live-port checkpoints are distinct. A finite managed
assignment separates later native launches from an earlier mutable backing, but
does not stop an earlier process's private stdout capture. Only an actual
commit/rollback boundary makes that live service cross a checkpoint. Likewise,
an unknown program-output destination permits private capture while retaining
`DestinationType`/`OutputPublication` refinement; its resource writes remain
publication-gated and its actual selected-view kind must be checked there. This
does not assert that the destination is a regular file or waive a known socket,
directory, mutable-input or filesystem hazard. `same_native_epoch` lets admission
reject an incompatible speculative fork before constructing its real cohort
certificate, rather than treating an invalid certificate as normal scheduling.

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

### Named regions share the execution frontier

The live runtime uses root/named `Frame` continuations rather than recursively
awaiting an entire named body. A frame owns pending instruction references and
its immutable transaction-boundary `WorkNode`; it does not own another Manager
or reinterpret the lexical environment. A named frame is created only after
the wrapper's real Start gate succeeds and the Manager transaction begins. Its
whole body is owner-bound before any child retains a plan. The wrapper emits
LogicalComplete/Committed only after its children, owner IO and RegionCommit
obligations settle. Conflicting or unknown summaries still fence later wrappers.

Ready frames are visited cooperatively. Commit/closing frames wait for their
owned IO readiness instead of suspending the outer frontier on one child.
Untaken stdin is closed only when that frame has no pending producer in the
current checkpoint prefix; a future epoch must not be hoisted to complete it,
and another frame's current input must not be lost. Explicit checkpoints retain the
named origin, and local reset remains the existing owner/epoch cancellation
protocol. A submission failure joins/discards its still-active newly begun named
scopes and restores their origins; already finished scopes and older background
owners are not silently rewound. The Manager's active-ID inventory also keeps
explicit stop/shutdown able to recover an interrupted frontier future.

Literal-to-ordinary-file materialization is one finite job path for both named
and top-level statements. It retains the immutable `Arc<str>`, writes configured
chunks into an exclusively owned private file, and retains one jobs permit and
one buffer through main-thread immutable snapshot capture. It does not give a
worker mutable Manager access. `Source::MaterializedLiteral` preserves literal
permission/basename semantics instead of pretending that a private temporary is
an ordinary copy source. Top-level submission completion drains its own finite
materializations before returning a REPL prompt; existing external/program flows
retain their explicit background lifetimes and source-owned pending effects.

Actual `literal.capture` write-start events occur after opening the private file,
and write-complete events follow its flush. They are not queue/admission events.
Tokio documents that [`File` writes can remain pending until flush](https://docs.rs/tokio/latest/tokio/fs/struct.File.html);
cancellation therefore joins the issued bounded write before returning. The
control/IO split follows the useful production distinction in
[DuckDB's asynchronous execution design](https://duckdb.org/2026/07/31/asynchronous-io),
without importing its execution framework or adding a per-region actor layer.

Snapshot capture and physical transaction publication still belong to the
single Manager/control owner. Existing native programs receive the complete
managed resource view; different argv or cwd spellings do not establish
disjoint native write footprints. This change neither narrows those grants nor
adds remote transactional peers. Genuine overlap is demonstrated for proven
disjoint literal materializations, not asserted for arbitrary named programs.

`named_execution_v4` checks actual write-interval overlap and exact final bytes,
permissions and origin modification times: Serializable/jobs=2, a jobs=1 serial
negative control, and ReadCommitted existing-file siblings in the same parent.
Checkpoints, pending rollback and both named origin undos are exercised. Named
sibling creations retain the existing strict parent gate rather than acquiring
an unsupported namespace-merge premise. An actual REPL literal/read pair checks
that finite completion is not made invisible behind an early prompt. Windows
and WSL pass the five focused named journeys. The positive IO workload is 4.5 MiB
per large literal so it discriminates real overlap from control-plane dispatch
overhead; arbitrarily short IO is not promised to overlap. A deterministic unit
also completes an older A boundary after B has been bound and verifies that B's
dependent read still awaits its own LogicalComplete, not foreign Committed.
Boundary gate witnesses never replace the current submission binding plan.
The actual Linux program lifecycle gate passes all fourteen cases on the same
uninstrumented implementation. These correctness intervals are not performance
benchmarks, and Windows native-provider acceptance remains a separate CI gate.

### Bounded namespace receipt algebra

An idle stream and a ready sibling file must not be ordered merely because both
eventually insert names into the same existing directory. Treating the whole
directory metadata record as an ordinary long-lived WAW dependency introduces
head-of-line blocking. Omitting that metadata effect entirely is also wrong:
directory reads, predicates, permission changes and origin undo observe it.

The supported abstraction is a narrowly certified Manager operation, not general
metadata commutativity. An active `NamespaceMergeEpoch` binds one actual native
parent identity, unchanged attributes/permissions, its canonical `NativeNameIndex`
keys, and reversible publication receipts. `NamespaceMergeProof::insert_file`
binds one direct-child ordinary file insertion and its preallocated logical
`PublicationOrder`. The real Manager permit must remain retained and publication
must use that permit's receipt method; the pure graph certificate is not a native
capability by itself. Neither a basename guess nor ReadCommitted parent refresh
alone proves this algebra.

For successful native mutation receipts `r_i`, the operation is:

```text
r_i = (canonical child key, immutable file value, logical order, observed mtime)
names      = disjoint-key union of successful inserted file values
winner     = successful receipt with greatest logical order
parent mtime = winner.observed_mtime, or the exact before-state if none succeeded
parent attributes/permissions = unchanged before-state
```

The stamp comes from the successful native data installation, not a synthetic
counter converted to wall-clock time. Selection is by logical order, **not**
maximum wall-clock value and not maximum with the old baseline. A future-dated
baseline or a backward clock step must not silently win. For example, an earlier
source instruction may finish later with stamp 200 while a later instruction
already finished with stamp 100; receipt order still selects that later source
instruction's actual stamp 100. The fixed point replays the same per-write
receipt values in logical source order. This is not a globally monotonic mtime
promise or equality with a different POSIX execution at another wall-clock time.

Native installation, metadata application, Store publication and receipt-ledger
advancement must share the Manager's guarded commit/compensation boundary.
Failed or cancelled/compensated mutations do not advance the winner. A committed
mutation with cleanup still pending remains committed. Origin restoration keeps
the exact observed before-state and remains a strict boundary, not a general
remove-contribution CRDT operation.

The HIR represents certified bytes/name binding as `Resource::NamespaceEntry`
and the parent delta as `Resource::NamespaceChange` with `AccessKind::NamespaceMerge`.
Only distinct canonical child keys with distinct logical tickets in the same
actual merge epoch commute. The native index, not portable Unicode/case folding,
decides distinctness. Same-name aliases, different epochs and duplicate tickets
cannot acquire a disjointness proof. A normal `DirectoryMetadata` read/write,
directory/tree or predicate read, permission mutation and rollback still fence
these changes. Plain existing sibling file-byte reads do not accidentally become
directory-mtime observers.

Scope is implicit, exact, missing local file destinations in an already existing
parent, for literal/ordinary finite sources or physical EOF file publication.
`FileClass::Regular` is not itself a local immutable-reader capability. A finite
HTTP/WSL/SSH source still requires its import adapter; it must not enter the
local-file namespace source path solely because its eventual payload is finite.
Only an already supported literal/local source or actual physical-flow capture
uses that fast path. Namespace publication retains a logical `copy` span with
source/destination metadata, `publication.policy=namespace-receipt`, and assigned
file/byte metrics. The nested transaction publisher still owns committed-byte
metrics; an omitted span or fabricated staging copy is not an optimization.
Named transactions, selectors, directory replacements, new-parent creation and
program-view exports remain on their existing strict contracts. A barrier closes
the active Manager merge epoch; a blocked REPL barrier must offer actionable
advice before effects rather than hiding the conflict or hanging behind an idle
peer. The default remains ordinary metadata WAW until an actual permit exists.

This follows the useful distinction between abstract operation conflicts and
representation-level writes in
[transactional boosting](https://www.cs.brandeis.edu/~cs220/reads/p207-herlihy.pdf).
The metadata and observation preconditions matter as much as distinct names:
[invariant-confluence analysis](https://www.vldb.org/pvldb/vol8/p185-bailis.pdf)
motivates checking the chosen merge and application invariants together rather
than calling every set insertion coordination-free. We do not import a general
CRDT runtime or weaken the four named-transaction isolation levels.

### Physical FIFO rendezvous is admission, not file publication

A FIFO writer cannot wait for its reader's EOF (or conversely) before opening:
[Linux FIFO semantics](https://www.man7.org/linux/man-pages/man7/fifo.7.html)
require both sides for progress. A generic consuming-file hazard therefore
needs a narrow physical protocol refinement, not a blanket independence claim.

`FifoEndpointProof` binds an exact local source/destination operand to an actual
provider/inode observation. `FifoRendezvousProof` connects opposite endpoints of
that same native FIFO. Preparation/graph replay checks the original instruction,
stream kind, stable host premise and same region. The provider must retain and
revalidate the identity at actual open. Only the corresponding exact
`Write`/`Consume` hazard becomes `Admitted`; other byte/namespace hazards,
same-side consuming readers and opaque program effects remain strict. The proof
is retained in `PlanningContext` and `PlanGraph`; cloning a pure certificate is
not an OS capability, and no old submission is silently assigned a new proof.

All observed FIFO operands, including unpaired ones, additionally access a
`StreamIdentity(provider, inode-generation)` resource. Original pathname hazards
remain: unlink/replacement and opaque filesystem effects are not channel bytes.
The identity resource prevents distinct hard-link names from being declared
independent channels or allowing competing readers to split successive waves.
Only the certified opposite-endpoint pair changes its identity hazard to
admission; same-side and cross-wave accesses still require completion.

`StreamPath` keeps byte-channel pathname overlap separate from a `File` mutation.
It still conflicts with replacing that path or an ancestor tree, but writing
stream bytes does not invalidate the filesystem kind in this or a later input.
Opaque/native file writes remain genuine type-invalidating effects. Positive
FIFO generation certificates require a held metadata-only native lease:
Linux/Android/FreeBSD use `O_PATH`; an inode number without a lease is not that
capability. The unverified macOS `O_EVTONLY` reader-open path is not used.
Legacy external-peer byte IO remains separate from this structured certificate.

Self-contained FIFO rendezvous currently joins statements within one submission.
When a later REPL input conflicts with an already admitted half, it is rejected
before opening its endpoint with a `/stop` then `/load` or single-macro hint.
`/wait` alone cannot manufacture a missing peer. This does not restrict external
FIFO peers or the existing program-port connections across REPL inputs.

Focused acceptance on WSL UID 1000 with project `.temp` privately mounted tmpfs:
41 preparation cases, 68 graph cases, 21 event-ledger cases, four FIFO identity
cases, 17 ordinary physical journeys and two actual native-program journeys all
passed. The latter include reader-first FIFO input to an already retained `cat`
space and closure of an idle physical input after native child exit. The cat
declaration is a prior input so its actual mediated footprint is established;
this does not claim that a new opaque Spawn followed by an unclassified external
FIFO in the same input can bypass the conservative deferred-domain policy.
Hard-linked FIFO waves, all-member external prerequisites, insufficient group
capacity, inode replacement, cross-input refusal/recovery and existing Unix
socket behavior are explicit controls. Strict library/graph/preparation/physical
Clippy passed. These are correctness gates, not performance measurements.

Before reserving a connected co-start group's slots, every member's external
Start prerequisites must hold. The event binder unions already-bound external
events across members, excluding only internal `Admitted` facts. It does not
retarget a `Completion` edge: doing so would reinterpret the original target's
foreign `Committed` prerequisite as another member's owner-local completion.
Thus a ready FIFO writer cannot occupy the reader's slots while an earlier TCP
producer still needs those slots to publish the reader's other dependency.

Each source/destination endpoint has one paired owner; repeated FIFO waves keep
their ordinary same-side completion order. Contracting each connected co-start
group must leave an acyclic completion graph, and a group cannot contain its own
completion hazard. The directed channel graph also rejects closed cycles with
no independent source. Thus ordered writer/reader waves remain supported, while
two reads followed by two writes cannot slip through pairwise certificates and
deadlock after admission. Admission remains
distinct from EOF, logical assignment, import and commit. This separation is
consistent with the safety discipline of
[verified dataflow progress tracking](https://matthias-brun.ch/assets/publications/progress_tracking_itp2021.pdf):
progress information must reflect actual outstanding work rather than confusing
an activated operator with a completed computation. We use that distinction,
not Timely's distributed timestamp protocol.

### Occupied capacity is not an impossible cohort

The live admission layer distinguishes a request that fits now, a feasible
request whose permits are occupied, and one exceeding the configured total.
An independent batch node in the second category stays on the ready frontier:
the runtime pumps owned tasks/publications and retries after actual capacity is
released. The concurrent ceiling does not cap the total number of operations.
The frontier compares available slot/buffer counts as well as ledger progress,
because a completed IO task can release its lease before its native owner can
import or commit. Releasing a permit does not fabricate logical completion.

Flow reservations are acquired before input-version pinning, port extraction or
endpoint open. An inseparable FIFO group reserves all its members together and
retains their real external Start prerequisites. Static FIFO capacity checks
also count native stdin consumers that must stay live with their peers, not only
the two buffers per physical flow. Buffered producer output and replayable
scalars do not themselves require that simultaneous native space. A group that
cannot fit fails before new-input payloads;
an owner retaining capacity required by its own pending stdin produces explicit
ordering/limit advice instead of an indefinite self-wait.

Interactive submissions do not block the editor on occupied resources. They
return a located `/wait`/`/stop` diagnostic before the blocked operation starts,
preserve the earlier live flow, and can be retried after it finishes. The focused
physical-runtime regressions use two real TCP peers and distinct existing output
directories to check serialization with one stream slot or two shared buffers,
plus interactive refusal/retry and the existing impossible-FIFO-group control.
Their bounded peer delays establish ordering and liveness, not performance.

### Local rollback is an epoch reset, not a seal

Commit joins and seals admitted native work before publishing. Bare local
rollback instead stops the current uncommitted epoch, joins/discards its owned
native and stdio work, and restores the Manager checkpoint. Requiring a sleeping
child's import, or a child's stdin/output EOF, before that cancellation would
make the control action wait for exactly the work it is meant to stop.

`PlanGraph.epoch_resets` records each controller, transaction, previous commit
node (or transaction start), all reverted definitions, the still-pending work
eligible for cancellation, and its native process IDs. The controller waits for
actual `ProcessStarted` acknowledgements and flow `Admitted` facts, not private
stage EOF or file publication. Admission is a separate fact from `ServiceReady`.
Successful `EpochReset` is emitted only after joins and checkpoint restoration.

Cancelled child definitions never become successful `LogicalComplete` or
`Committed` events. Previously completed observations remain historical facts,
but an epoch-discard marker prevents using them as current resource versions.
Region commit replaces reverted-child visibility obligations with that
controller's successful reset; checkpoint-protected nodes stay untouched.
Following file reads depend on the restored checkpoint/reset, not on a canceled
producer's import or unpublished output. Restoration effects cover managed
file/namespace writes, never a second acquisition of stdin/stdout or a repeated
HTTP request. Each runtime reset must also leave a usable fresh cancellation
scope for the next epoch rather than poisoning the session. Its cancellation
authority is owner/epoch-local; a future independent transaction is not cancelled
merely because it shares the session's top-level interruption token.

Rollback remains source ordered. Ordinary finite assignments, HTTP consumption
and explicit exit-status observations before the control action must really
complete; `ObservationComplete` preserves that factual completion even when
later resource visibility is discarded. Rollback is not time travel through an
earlier ordered blocking statement. Thus `spawn sleep; rollback; literal` can
cancel promptly, while `spawn opaque; conflicting finite assignment; rollback`
may first wait for the preceding assignment. A REPL dependency on future human
stdin must return constructive connection/`/wait`/`/stop` advice before effects,
not hang the command loop. A separate future dead-epoch proof could eliminate
unobserved pure managed writes, but this control mechanism never silently drops
observable IO in order to reach the rollback.

### Native Windows / WSL namespace separation

A regular-file kind is not an alias-separation proof. After a native assignment,
WSL input metadata is normally deferred because native and WSL names may denote
the same projected object. Preparation can discharge only a specific managed
write/input pair with actual filesystem evidence: the Windows metadata handle
must belong to a local disk and resolve to an actual `HarddiskVolume` device,
with NTFS/ReFS reported by
[GetVolumeInformationByHandleW](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumeinformationbyhandlew).
Mapped/UNC redirectors, reparse ancestors and unresolved parent traversal prove
nothing. The WSL source must resolve without symlink traversal to a native
ext-family filesystem (`statfs` magic `0xef53`), not DrvFs, overlay or FUSE.
[statfs](https://man7.org/linux/man-pages/man2/statfs.2.html) observes the resolved
mounted filesystem, so a bind mount cannot gain this proof merely by looking
like a `/home` or `/tmp` pathname.

The preparation witness retains concrete native/WSL location pairs and the
observed canonical WSL path. Tilde and absolute names therefore use the same
resource key without changing the original operand/provenance. Only managed
assignment invalidations and their own type postconditions can use the pair;
opaque effects and retained native imports remain conservative. The HIR's general
native/WSL hazard relation is not weakened. Offline preparation launches no
helper, ordinary WSL-only inspection keeps its original batch, and a successful
namespace probe supplies its file kind without a duplicate stat invocation.

Actual Windows/WSL acceptance uses owned native-home data: both existing and
initially missing NTFS targets produce one optimized assignment and exact WSL
bytes. The default CLI also plans one operation and executes the original
`config_file := ~/...; ...; $wsl::config_file -> des/...` form. A real `/mnt`
projection remains two ordered nodes, and an opaque program does not manufacture
regular-file or separation facts. The explicit environment-dependent target is
`wsl_plan_proof_v4`; ordinary CI must not count its ignored cases as WSL passes.
