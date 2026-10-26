# Effect-aware planning and bounded execution

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

For each eligible write, the planner scans later live effects until the first
overlapping observation or mutation. An overlapping read keeps the write. An
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
Local keys capture current-directory/home expansion and canonicalize the nearest
existing prefix. Case folding is used only for conservative Windows conflict
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
$pwd/input -> FIFO
FIFO -> $pwd/output
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

Every raw dependency points from an earlier operation to a later operation, so
the operation graph is acyclic by construction. Compact ancestor bitsets remove
transitive edges before exposing scheduler predecessors. For example, repeated
consumers of one stream form a dependency chain rather than retaining every
historical pair. The scheduler need not rediscover dependency closure.

The current builder checks operation pairs to find hazards. For `N` operations,
path width `P`, and `R` rewrite rounds, the straightforward rewrite bound is
`O(R N² P)`. Hazard discovery is quadratic, with ancestor bitset unions for
transitive reduction; ancestor storage is `N²` bits, not a dense matrix of edge
objects. This is deliberately simple for configuration scripts, not a claim of
an industrial million-node build engine. Location normalization/identity work is
cached and comparison keys are precomputed, avoiding allocations per pair.

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
