# Performance assumptions after thinning VM dispatch

This investigation separates **semantic proof** (what may be changed for every
admitted program) from **workload distribution** (whether an exact fast path
is worth its cost). Repository examples, actual acceptance journeys, and
compute microbenchmarks are separate strata. Their frequencies are not
population telemetry and must not be pooled into a universal default.

For a workload with N uses, let p be the fraction satisfying an exact
precondition, G the per-use cost of establishing it, S the cost saved on
a hit, F the extra cost on a miss beyond the old path, and K the fixed
analysis/materialization cost. The break-even condition is

`p S - (1-p) F > G + K/N`.

Use measured conditional costs when correlated with shape (e.g. local file vs
remote or singleton vs large range), rather than multiplying a global average.
A static certificate has G=0 at execution; a producer-owned tag can amortize
proof over N consumers. If no performance model has stable stratum weights,
report a threshold or a Pareto comparison, not an average that invents weights.
An uncommon case still has its full semantics. Invalidations, aliases and
selected I/O are correctness constraints, not probabilities.

## Executable layout: the backedge veto is broad, but removing it is not the fix

The current executable layout pass rejects a whole function if any explicit
target is at or before its source. An acyclic subregion inside that function
does not logically inherit the veto. To quantify the tradeoff, a managed probe
read exact O2 objects and compared the function tables immediately before and
after layout. The 15 inputs were the four checked-in examples, seven literal
programs extracted from contextual I/O journeys, and four frozen matrix objects.
This probes static code shapes, including duplicated library specializations;
it does not weight instruction counts by actual executions.

| Stratum | Inputs | Functions / instructions | Functions with a backedge | Local forward-jump shapes rejected by the veto | Removals by current pass |
| --- | ---: | ---: | ---: | ---: | ---: |
| Checked-in examples | 4 | 630 / 12,696 | 52 | 21 | 0 |
| Contextual I/O journeys | 7 | 10 / 169 | 2 | 2 | 0 |
| Library matrix | 4 | 179 / 12,674 | 17 | 9 | 0 |

A local shape candidate is a non-adjacent forward Jump into the uniquely
targeted start of a closed trace segment, excluding a jump-only entry and
the Transfer/Pop scheduling hazard. For an individual move, rotate the
target segment T before the gap G between source and target. The order of
remaining explicit edges changes direction iff they cross between T and G;
implicit fallthrough stays inside each segment. All 32 candidates passed
this direction test, but **none** passed the additional sufficient test
that no original backedge interval endpoint is crossed. The existing
contextual finite-loop recognizer uses lexical PC windows, so preserving
branch direction alone does not prove that its preparation opportunities
remain unchanged. The synthetic 12-to-11 fixture in ContextBinding still
shows a real positive case for the existing pass; this corpus is a separate
negative result.

If layout becomes worth improving, work over control-flow identities and
strongly connected components (SCCs), not the accidental intervals of PC
numbering. Prefer a *late* layout after contextual loop/plan discovery, with
serialized plan regions kept atomic and every affected PC reference remapped
(or start with functions that have no plans). Pathwise equality of the
instruction/effect sequence modulo eliminated Jump is the proof invariant;
measure taken-edge frequency before preferring one trace. An objective is
to maximize `sum_e hits(e) * eliminated_dispatch_cost(e) - K_layout`, with
hard constraints for the function entry, implicit fallthrough, open-session
PCs, plan regions, and the Transfer/Pop contract. No runtime rewriter is
needed. Neither static candidate count nor the 12-to-11 fixture establishes
a benefit worth prioritizing over measured VM or scheduler bottlenecks.

This follows the separation of CFG and profitability in
[Pettis–Hansen's chain placement](https://pages.cs.wisc.edu/~fischer/cs701.f05/code.positioning.pdf)
and [LLVM's MachineBlockPlacement](https://llvm.googlesource.com/llvm-project/llvm/+/refs/tags/llvmorg-20.1.4/lib/CodeGen/MachineBlockPlacement.cpp);
their native code cache wins are not a prediction for this VM. Raw
instructions/branch maps, source probe and typed counter are under
`artifacts/benchmarks/library-matrix-v086/conservative-audit/layout/`.

## Frontend: refresh the obligations that changed

`RefreshSessionCalls` revisits historical open-call obligations on each
successful REPL submission. In the selected `HirSessionDispatch` prefix
(O0/O2/O3, 30 submissions, 12 checked semantic results), all **72** refreshes
resolved and none changed the target table. This does not establish that
refresh was useless in general: an assignment can already have changed an
entry, and an identical target table can still conceal a changed effect
summary. A controlled O0 test with 64 independent open slots and unrelated
scalar submissions found 1.15/1.24 ms baseline versus 0.80/0.72 ms for the
proven-unrelated bypass, with about 771–785 KB versus 437–451 KB allocated.
The no-slot control has no consistent gain.

Treat an obligation as a query q over semantic dependencies D(q), including
callable identity/shape, resolved capture or member storage, invocation and
effect facts and relevant argument evidence. A reverse relation maps changed
fact identities to obligations. When a submission changes set S, revisit
`{q | D(q) intersects S}` plus newly created queries. Epochs are useful only
for immutable facts with correct alias invalidation; refresh is still required
if a dependency is unknown or changes, even with the same selected target ID.
Update reverse edges only after the query publishes successfully, preserving
assignment diagnostics, effects and partial-accept/reconcile behavior.
Validate by comparing full and sparse resolutions and diagnostics under
unrelated edits, callable changes, alias writes and change-then-restore. Size
the dependency representation using observed dirty sets and slot counts;
the synthetic bypass is not a production implementation.
This is change propagation, not deferred acceptance of an unproved call;
see [rustc red–green queries](https://rustc-dev-guide.rust-lang.org/queries/incremental-compilation-in-detail.html)
and [Adapton](https://matthewhammer.org/adapton/adapton-pldi2014.pdf).

The editor has a separate whole-document gate: 5 of 31 tracked parseable
scripts pass `EditorPrefix.Independent`'s first check, and all four shipped
examples are excluded. Treating any `$std` syntax as loss of independence is
broader than necessary for a suffix that only consumes exact immutable
embedded declarations. Prove the prefix/suffix dependency closure and
publication ownership rather than deleting the check. This is a source
census, not an edit distribution or measured latency gain. A full REPL
member-alias journey also reproduced an existing mutation-while-enumerating
failure on the unmodified compiler; do not use the successful prefix as
evidence of correctness for that case.

## Middle-end: make available indexed reads edge-sensitive

O2's numeric Heap sift-down reads the left and right children to select the
winner, then reads that chosen index again. Indexed read availability is
killed by any Call and the join intersects facts by identical producers.
For the 8,192-key matrix trace, the modeled sift-down visits the two-child
case 86,206 times versus nine one-child cases. The selected rereads account
for 86,206 of 310,916 modeled whole-workload gets (27.73%); the exercised
shuffled, ascending, descending and duplicate-heavy controls still have
at least 95.24% two-child decisions. These are operation counts for checked
fixtures, not external distributions.

There is also a structural bound. A complete binary heap has one node with
exactly one child only when its current size is even, and sift-down strictly
descends, so it can visit that node at most once per drain step. Draining
`N` values has at most `floor((N-1)/2)` singleton-child visits and at
least `N-3` two-child visits at the root. The descending control reaches
the former bound; the original trace is far from it.

If incoming edge e selected index i_e and already read v_e from buffer
resource B at version m_e, then `get(B, phi(i_e), m)` can become `phi(v_e)`
when every incoming edge has a witness for the same selected index/resource
and no intervening write or alias invalidation. The original read on each
path must still run and be the first operation capable of the original index
failure. A callee may be ignored only with a sound indexed-resource effect
summary of all selected targets; the numeric selector's specialized bytecode
ID alone does not exclude native alternatives. Unknown, effectful, or
mutating comparison policies retain the generic read. Carry the selected
value, not a copy of its referenced object.

A source overlay retaining just the selected child on the default numeric
path produces the identical checksum on the same Native AOT runner. It
changes the exact key trace from 1,024/982 to 895/890 ms and a seeded shuffled
trace from 928/976 to 894/864 ms. This is evidence for removing the read,
not a measured gain for a generic compiler pass. A finite-domain effect
certificate across the HIR boundary and a join test with a Buffer alias
write and an effectful callback discriminate the two important proof risks.
See [LLVM MemorySSA](https://llvm.org/docs/MemorySSA.html) and
[partial redundancy elimination in SSA](https://cgi.cse.unsw.edu.au/~cs4133/Papers/ssapre.toplas97.pdf)
for edge-translated memory availability. The source alias control really reads
99 after a callback write; the improperly retained old value is 10.

## Scheduler: only actual prerequisites may hold a cohort

The residual Range Transfer currently drains all pending work before
starting, even when its members have no dependency on an unrelated live
process. In a gated actual CLI fixture, both source and the two binary routes
were exercised twice: disjoint scalar publication reached the output before
gate release 6/6 times; the disjoint materialized Range did so 0/6. Dependent
scalar and Range copies correctly waited 0/6 before release, and the final
bytes match. The .mexe had no recorded plan; this is a scheduling issue,
not a reason to restore VM planning.

For a certified finite membership with real read/write footprints R and W,
the earliest legal start is `max(completion(d) for d in D(R,W))`, rather
than the maximum over every pending task. Register the cohort as one selected
operation, preserve one native SSH channel, and use real entry/referent
resolution when a preceding namespace publication changes an alias. Unknown
or lazy membership still has to run through its own evaluation protocol.
Do not infer that disjoint strings mean disjoint physical resources.
The negative control holds a *required* producer; a fix must not allow that
cohort to publish early.

For prepared batches, every external dependency query happens before the
new plan registers any work. With an actually empty external live index
the union is exactly empty; omit that scan, not the serialized predecessors
or the ordering fence. Existing accepted 1,024-file independent/selection
plans each have 1,024 node queries and 2,048 footprints; a directory case
has 1,092/2,184 and still needs its 4,229 explicit edges. Exact within-batch
footprint dedup had zero savings on five tested fixtures. Do not allocate a
dedup index for that measured ordinary path. If later reducing graph edges,
distinguish completion-only edges from edges requiring a predecessor's
successful node result; generic transitive reduction is not valid for both.
This parallels access-mode dependency construction in
[StarPU](https://starpu.gitlabpages.inria.fr/features.html) and the separation
of logical regions from physical instances in
[Legion](https://legion.stanford.edu/pdfs/sc2012.pdf), without importing their
resource model or treating path spellings as an alias proof.

A source-linked managed Release component replay of these exact empty
frontier queries gives 2.28 ms / 1.50 MB for 1,024 independent files,
2.27 ms / 1.48 MB for the selection and 2.59 ms / 1.75 MB for the
directory plan (elapsed / allocated). These are costs of the existing
query phase alone, not measured savings in Native AOT or whole CLI latency.

## VM: exploit the representation that actually owns the elements

The stock Set represents keys as singleton Ranges. In the exact checked
8,192-key bound Set, every one of 309,586 front/first operations received
the canonical owned-array representation; the 1,024-key case received it
28,911 times, with no other kind observed. On the same diagnostic Native AOT
binary and unchanged image, direct owned-array front access changes
2,342/2,376 to 2,213/2,230 ms and cumulative allocation from 313.54 to
303.63 MB (exactly 32 bytes per observed call). These are local wins for
this distribution. They do not justify defaulting unknown Range values to
arrays.

Use an explicit representation witness tied to the actual owned Items, not
an independently replaceable SnapshotValues field on a public record.
For an owning array A, first returns the original A[0], retaining object
identity and nullable values; preserve the ordinary empty/error and
cancellation behavior. A live Buffer view, deferred/lazy callback or SSH
selector is not an owned array and must keep the ordinary protocol. The
small test matrix must include empty and nullable snapshots, object
identity, cancellation, changed public record views, and one selected lazy
effect exactly once. This is a narrow representation optimization, not a
new form of runtime planning.
The [.NET LINQ First implementation](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Linq/src/System/Linq/First.cs)
provides a production example of dispatch on an already-indexable
representation, not a proof of msync Range ownership.

## Suggested sequence

1. Remove proven empty prepared-frontier queries first. Then fix the observed
   scheduler wait-all dependency for certified finite cohorts, retaining
   dependent and namespace controls.
2. Add the owned snapshot front path. Its proof is local and its measured
   call rate makes it a better first VM change than global profiling machinery.
3. Implement the Heap availability proof (or a typed library fast path first)
   and sparse REPL refresh in separate branches. Count changed facts, native
   reads and misses before broadening either.
4. Leave general layout lower in priority until dynamic edge counts show
   that recovering those static candidates pays for the additional PC mapping
   and proofs. Keep the existing successful trace pass and the VM boundary.

## First integration

The selected paths now carry their proof to the consumer rather than adding
an interpreter guess:

- An array-backed Range and a private list published by materialize associate
  the snapshot with the actual producer identity. Front/first reuse it, and
  selected local cohort admission can use it without re-enumerating a lazy
  producer. Cloning a Range with a different Items producer does not keep
  this witness.
- Prepared plan admission tests the prior frontier once. A demanded local
  snapshot cohort with overlapping history computes its selected member
  reads and target entry/referent writes. After relevant namespace
  publications it refreshes their projections. It keeps the original cohort
  copy operation and error semantics. An existing member directory can
  merge through descendant links not covered by the footprint, and remote,
  replacement or lazy cohorts with prior work retain the existing path.
- The Heap library asks the semantic producer for a narrow leaf-callable
  certificate. The compiler proves the selected and admitted future
  alternatives have no captures and only operate on scalar parameters.
  The proof is erased before runtime. The two-child path uses the selected
  value when certified; effectful policies re-read, and the one-child path
  still performs its first selected read. This is a library pilot, not a
  general bytecode PRE pass.
- REPL obligations have immutable reverse indexes by root and member name.
  Certified direct no-argument literal thunks are invalidated by semantic
  root writes or changed runtime feedback; reconciliation dirties the full
  frontier. All other obligations retain refresh. Updating the target table
  does not mutate the collection being enumerated for member assignment.

On the selected gated CLI journey, disjoint cohorts now publish before the
held process in all three source/object/executable routes; the three actually
dependent cohorts still wait. Final bytes match. On the checked 8,192-key
Native AOT workloads, the same runner compares Heap bytecode at approximately
1,051 to 930 ms (pooled medians of four observations); the same bound Set on
the baseline and final runners measures approximately 2,338 to 2,235 ms
(two observations per runner). The managed 64-slot REPL control measures
1.246 to 0.784 ms and 783,524 to 449,852 bytes per checked submission
(medians, 112 observations per version). These are selected distributions
and not whole-product latency estimates.

Receipts: `artifacts/benchmarks/library-matrix-v086/decision-integration/`.

## Object representation, control flow, and selected effects

Treat these as separate contracts, not different views of one universal
"effect block":

| Layer | Observable commitment | Implementation freedom |
| --- | --- | --- |
| Scope and captures | Fresh anonymous identities, one named instance per program/session, stable logical names and enumeration order, fixed field types, shared updates through aliases and cells | Field addresses, box identity of Numbers, contiguous layout, allocation granularity, and even whether a proved nonescaping object is allocated |
| Scalar execution | Values, selected failure and effects, strict binary64 behavior | Slot representation, transport of arguments, frame allocation, order of independent non-observable operations |
| Finite external work | Selected endpoint/operand, resource dependencies, directional fences, demanded results and operation failures | Graph shape, batch granularity, number and order of independent workers |

The execution loop has not dispatched by BasicBlock since the earlier VM
thinning. The old BlockBuilder nonetheless split before and after almost every
instruction with any effect bit, including member reads, conversions, calls,
and local captures. These were not useful scheduling boundaries. The
inspection projection now uses branch/iteration targets and terminators; the
effect bit describes the resulting block, not its shape. CLI run, export,
binding and the REPL use instruction-only APIs; textual plans and artifact
disassembly still materialize the projection. Execution-plan region addresses,
dependency edges and bytecode artifact format do not change.

Contextual finite-loop expansion follows the exact source alternatives encoded
in direct/finite dispatch calls, with a per-preparation cache of closed
copy-free subgraphs. Native alternatives cannot add transfer nodes in the
known-call evaluator; this does **not** certify them as pure, interchangeable,
or safe to speculate. Unbounded target sets retain the existing evaluator
fallback, and session calls are not executed by this evaluator. A scalar loop
that can feed a later transfer or
contextual observation is still eligible under the existing rules. A no-copy
proof is not a reason to discard source execution.

Native operation descriptors now distinguish immediate external operations
from the effects of evaluating lazy inputs or invoking callbacks. A pure
enumerator does not wait on the scheduler's external ordering fence merely
because the compiler must conservatively model what its producer might do.
Selected I/O instructions still own their dependencies. The converse matters:
`fs.relative` and `fs.proximate` resolve the filesystem; only the
`lexically_*` variants are pure. Contextual native calls enter the selected
transport at one invocation boundary instead of twice.

For member loads, one storage projection returns either an already-existing
Value (including a NumberValue or the language's Null singleton), or the raw
double in a numeric page. The old two-stage load first attempted numeric
extraction and then read the same reference field a second time; it could
also unbox an existing NumberValue and rebox it as a call argument. Operand
stack bookkeeping lives inline in the invocation state, and argument
collection resolves Cell operands in its existing copying pass. No changes to
object identity, aliasing or physical placement are required.

Follow-up design: publish separate summaries for logical memory and external
resource effects at the same specialization that owns the finite call-target
set. A footprint is a function of arguments/captures and selected endpoint,
not a Boolean on the entire function. Instantiation can use relocatable path
expressions; physical aliases or an opaque child process may still need
selected observations or residual dependencies. Partition execution by true
control dependencies, resource hazards and observed results, and choose
batch size independently. Two admitted operations have no read/write hazard
when both `W_i ∩ (R_j ∪ W_j)` and `W_j ∩ (R_i ∪ W_i)` are empty on
resolved resource identities. Result edges and fences still apply. For a
group of k ready operations, amortizing launch work by roughly `(k-1)h`
is useful only if it outweighs any increase in the critical path from lost
parallelism or waiting to assemble the batch. For compute, prioritize unboxed locals/arguments
and direct internal call frames before adding more layout machinery for
reference-heavy trees. [Lua's call implementation](https://lua.org/source/5.4/ldo.c.html)
is a concrete example of using existing stack arguments for internal calls;
msync still needs its own lifetime boundary for suspending or escaping work.
[LLVM's IR](https://llvm.org/docs/LangRef.html) separates block terminators
from call memory effects. [Choi et al.](https://research.ibm.com/publications/escape-analysis-for-java)
use escape information to make stack placement a representation choice, not
an object semantics change.

The exact same frozen bound Set artifact renders 1,253 inspection blocks
before and 235 after, without changing a byte of the artifact. This is not
an execution-speed measurement. On Linux x86-64 Native AOT, with load outside
the timer, checked checksums, CPU 5 and alternating AB/BA runner order, the
8,192-key Set and Heap medians change from 2,189 to 2,060 ms and 905 to 874 ms,
respectively (four observations per runner). The first SHA-256 batch was
mixed (514 to 533 ms); a separate 12-pair follow-up measures 537 to 503 ms,
with a median paired difference of -33.8 ms and 10/12 faster pairs. Report
both SHA measurements, not a stable throughput or tail guarantee.
Receipts are under `artifacts/benchmarks/library-matrix-v086/object-execution/`.
