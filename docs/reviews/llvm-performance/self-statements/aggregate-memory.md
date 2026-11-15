# First-person account: aggregate-memory implementation ownership

## What I actually owned

I was the worker named `aggregate_memory`. This account is based on the task
messages retained in my context, my own edits, and the current repository notes.
It is a summary of assignments and engineering decisions, not a transcript of
private deliberation. The name does not mean I owned every memory or aggregate
change in the final candidate.

My first assignment was a direct-Native aggregate/memory optimization prototype:
scalar replacement of private allocations and local forwarding without the old
global address solver. The direction changed before those prototypes were
integrated. They were quarantined rather than installed as the product optimizer.
I subsequently audited their upstream provenance. I do not attribute the measured
current regression to those unintegrated drafts.

In the LLVM-derived migration I owned substantial initial module/function/block
and naming representation, including the intrusive instruction sequence; the
initial graph-to-existing-Native target adapter; persistent lowering-session
registry and canonical exported-cell relocation; the production SemanticSession
switch; and the shared standard backend emitter/preparation-image repair. Other
workers implemented instruction/type/value families, source production, ABI
annotations, conversions, builtin paths, later Function projection/composition,
carrier inference, machine/vector storage, and many subsequent integration
repairs. The root owned overall integration, default optimization entry, builds,
pipeline choices, and real journey/benchmark execution. My initial target files
were subsequently shared and extended; their present contents are not all my
personal authorship.

For SROA I owned the central data/partition iterator, driver, work queues,
allocation dispatch, split orchestration, deferred WeakVH deletion, readonly
escape propagation through the real SSAUpdater/LoadAndStorePromoter, and selected
alloca promotion. I defined the shared family seams. Peers owned SliceBuilder,
aggregate/PHI/select access rewriting, type/vector/integer helpers, slice
rewriting, presplitting/partition selection, and intrinsic/attribute prerequisites.
The source pin was LLVM a55e64432a516fd6507d3ecfba7407cfe425f940. I did not own
all of SROA's scratch containers or the later Native scalar ABI implementation.

## Why I took those approaches

The assigned target boundary was explicit: emit the established NativeProgram
and execution image from the new graph, with no call back to NativeHirLowerer,
old NativeOptimizer, or the old address/storage solver. Direct producer ABI
annotations supplied facts such as logical type, field layout and capture
location; they were not intended to become another heap analysis. Explicit
HeapAlloc/FileIn/FileOut survived until target emission. Captures remained
addresses of the actual semantic cells, not copied cells or per-call reconstructed
closures.

The initial standard image emitter used existing standard handlers and all-false
RawWritable flags. I did not have direct facts establishing the old raw-write
fast-path contract and was instructed not to fabricate those flags or re-enter
its proof pipeline. This made the new execution path explicit and executable,
but it was a baseline emission choice, not performance parity with the mature
backend. It also was not a substitute for examining downstream consumers of
those flags.

The persistent session work followed the actual external contract: stable native
IDs/table indices, exported canonical cells, pending values that count as
initialized without demand, capture address identity, and partial-failure
reconciliation. A source lexical global relocated to the existing persistent
entry cell rather than creating a second proxy/static resource root. That was
chosen from actual runtime ownership, not an invented identity guard.

The SROA assignment was a faithful translation of the pinned alloca pass, not a
new field-name optimizer and not heap-to-stack conversion. I kept source option
defaults, partition ordering/tails and rewrite queues. I did not add an ABI-based
filter that quietly prevented upstream integer/vector choices. That exposed
real target coverage work instead of hiding it. It did not, by itself, establish
that running every source decision procedure was profitable for the dominant
language workload.

## Assumptions and tradeoffs I missed

### A source algorithm port must preserve its container cost model too

I personally wrote `OrderedSet.ExceptWith` as a `RemoveAll` scan of surviving
values followed by set subtraction. The independent SROA review correctly
identified the mismatch: upstream SetVector subtraction drives the **removed
keys**, membership-tests first, and only scans its vector on a hit. Deleted
original allocas can be absent from a growing promotion list, making my version
perform avoidable scans. This is my concrete translation mistake, not an
unavoidable cost of LLVM or a decision made by a different family owner.

I did consider instruction-list deletion complexity earlier and replaced the
initial List-based block sequence with intrusive links when the root identified
the DCE O(N²) risk. I did not apply the same depth of cost-model comparison to
this worklist operation. Semantic ordering equivalence was insufficient.

I also chose ordinary managed list/hash-backed shared containers without
establishing equivalent allocation behavior to upstream inline small containers.
Some particular scratch-container choices belonged to peers, but as the central
seam owner I should have called out this representation cost across the family,
not treated an algorithmically source-faithful decomposition as enough. The
review's all-unsplittable workload is a particularly important case: decision
paths can reject every candidate while still allocating their scratch state.

### A baseline backend can disable a downstream opportunity yet still pay its setup

I did not trace all-false RawWritable through NativeRegisterPlan far enough.
The current review establishes that CFG/liveness/pending preparation is performed
before discovering that no result is eligible. The raw flags were an explicit
initial emitter choice in my remit; the register planner was not mine, but the
boundary interaction was a missed integration tradeoff. Avoiding the old solver
did not automatically avoid expensive runtime preparation. I should have checked
this ordinary zero-opportunity path while establishing the emitter contract.

The review also shows that generic Function component movement can add value
traffic versus a dedicated typed address path. I owned the initial adapter, not
the later Function SSA/projection/composition implementations. I can explain the
original graph/ABI boundary, but cannot present those later authors' decisions
as my own memory. The broader lesson still applies to my work: emitting valid
standard operations is not evidence that the old hot execution path was retained.

### Correctness journeys did not settle performance

The root-run source, closure/alias/pending-session, Accept/Reconcile, preparation
and Native2 roundtrip journeys validated important real contracts. They found
and drove actual repairs, including an initially duplicated captured session
cell and the stale preparation backend image. Those were genuine defects in
work I owned; I repaired the canonical-cell path and the modified-entry image
ownership rather than adding runtime guards or falling back to the old route.

Those passes did not demonstrate performance parity. My source-ready reports
explicitly did not claim SROA timing or target completeness, but they also did
not provide the missing cross-boundary performance assessment. I should not
confuse accurate status wording with resolving that engineering obligation.
The root, not I, ran and selected the final benchmark pairs.

## What I can conclude now

I accept the concrete worklist cost-model defect and the missed zero-eligible
backend/planner interaction. They are useful, bounded corrections to pursue.
I cannot assign either the entire +4.738 ms telemetry-off compile delta or all
runtime regressions to them: the independent reports explicitly do not establish
that attribution. The isolated long SROA alloca span is not evidence that I can
name a GC pause or a runaway slice traversal. Likewise I did not author the
widened byte-initialization maintenance reviewed in scalar storage, and cannot
supply its author's historical rationale.

My recommendation is to repair the proven source-container mismatch and the
no-op planner boundary, preserve the actual source/alias/session contracts, and
then use the already established focused source/artifact journeys and paired
comparison. This is not a recommendation for another global safety analysis or
for suppressing upstream transforms to make a benchmark appear faster.

For this personal statement I read the existing review reports and relevant
current source. I changed this document only: no product edits, builds, timing
runs, or commits.

## Related records

- [SROA source/cost review](../sroa-cost.md)
- [Frame and allocation review](../frames-gc.md)
- [Scalar storage review](../scalar-storage.md)
- [Target boundary design](../../../design/llvm-native-target-lowering.md)
- [Persistent session design](../../../design/llvm-session-lowering.md)
- [SROA port design](../../../design/llvm-sroa-port.md)
