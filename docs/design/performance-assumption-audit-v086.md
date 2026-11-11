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
| Scope and captures | Fresh anonymous identities, one named instance per program/session, stable field names and types, shared updates through aliases and cells | Field order and addresses, box identity of Numbers, contiguous layout, allocation granularity, and even whether a proved nonescaping object is allocated |
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

## Do not replay portable optimization during binding

Binding is relocation, context-dependent folding, and executable trace
placement. The old path reran the entire machine-independent pipeline after
relocation even when there were no new inputs to that pipeline. A code/constant
feature test now retains the optimized portable functions in that case.
Path constants (including captured/nested ones), context-aware native
folding, path conversion/join/qualification and builtins replaced by known
values remain positive inputs to contextual folding. Relocation, local
transfer selection and trace placement do not depend on this optimization
gate. No proof bit is written to the artifact and the VM consumes neither
the feature test nor the portable optimizer.

For the frozen 8,192-key Heap `.mso`, the old second pass emitted the same
9,138 instructions and all per-function instruction comparisons were equal.
In the managed Release binding component (SDK 10.0.403, TieredCompilation=0,
CPU 5, input loaded outside timing, three warmups and twelve measured
bindings), mean bind cost was 1,775.2 ms and 1,107,287,146 B versus 0.475 ms and
70,672 B after reuse. The frozen Set, Hash and SHA artifacts likewise had
identical instruction sequences before/after bind in each implementation.
These are binding costs, not VM execution measurements. The positive context
control still folds a string into a Path with the bound working directory.
See `artifacts/benchmarks/library-matrix-v086/binding-reuse/` for exact input
identities, probe source and observations. This separation follows the
dependency distinction of [LLVM's analysis invalidation](https://llvm.org/docs/NewPassManager.html):
preserve facts for unchanged inputs rather than rebuild them at a phase boundary.

Contextual preparation is a separate optimization. It does not replay
ordinary execution to learn what it did. A direct metadata/selector site or
a call graph that may emit copies can expose planned work; a closed scalar
entry graph now enters the VM without constructing or evaluating a
preparation frame. The reachability test models the actual copy evaluator,
not source purity. Runtime-dependent inputs still leave residual instructions,
and a selected contextual copy still executes only in the VM.

### Integrated Native results

The complete Linux x86-64 Native candidate (production changes through
`15b6b7f`) was compared with the frozen `5f25145` host. Fresh-process
`bind-ir` pairs used CPU 5, alternating AB/BA order after one untimed pair.
They include startup, load, bind and write. Both sides emitted the same
bytes for each frozen input, and the resulting images passed their actual
numeric result/digest checks.

| Input | Median `bind-ir`, before → after | Faster pairs |
| --- | ---: | ---: |
| 8,192-key Heap | 1,025.3 → 36.5 ms | 4/4 |
| 8,192-key Set | 56.9 → 22.6 ms | 4/4 |
| Hand-written SHA-256 | 37.2 → 15.8 ms | 4/4 |

A separate execution-only control loaded each unchanged input before the
timer (CPU 6, six AB/BA pairs, verified result on every run). Median
milliseconds were 2,064.8 → 1,975.6 for Set (5/6 faster), 890.7 → 846.0 for
Heap (6/6) and 497.3 → 474.6 for SHA-256 (6/6). These are combined-candidate
observations, not attribution of the result to locality alone or estimates
of P95/P99. See the distinct
[Scope layout](object-spatial-layout.md#page-level-descriptors-for-homogeneous-numeric-rows),
[call-frame](managed-runtime-optimization-v080.md#follow-up-source-call-frames-and-numeric-local-storage),
[plan construction](../../tests/Performance/ContextualIo/README.md#instantiated-footprint-index-and-graph-construction)
and [live scheduler](performance-scheduler.md) controls for attribution.

The integrated Native CLI passed object identity/locality and contextual I/O
routes, the 21 process-gated cohort cases, redirected stdio and REPL, and
source-free artifact disassembly. A real VS Code 1.144.0 extension host
passed the artifact suite against that exact CLI. The final Windows x64
managed target built cleanly; the candidate has not run on a Windows host.
Exact binary hashes, samples and journey receipts are under
`artifacts/benchmarks/library-matrix-v086/deep-integrated/`.

## Phase ownership follow-up: no portable optimizer in binding

The previous feature gate still sent the **whole** image to the portable
optimizer when one operation could use new context. That did not fix the
phase boundary. Binding now performs relocation, local contextual folding
in the existing lowering scan, and trace placement. Adjacent constant
operands can fold without building a data-flow graph; a jump into the
interior prevents that rewrite. Other expressions stay in the bytecode,
not on a fallback route through the portable optimizer. Contextual I/O
preparation still owns selected filesystem observations and transfer
dependencies. The VM does not revisit these decisions.

The producer/consumer audit yielded three further concrete changes:

| Phase | Owned facts and removed work |
| --- | --- |
| Artifact handoff | The CLI encodes compiler output or reader-owned images without validating the same module again. Decoders still handle external format/shape errors; the general writer for explicitly assembled modules retains its existing checks. The bytecode reader consumes existing operand slices instead of allocating temporary arrays just to verify them. |
| Lowering and VM | Scope initialization stays in source order, while HIR lowering emits a canonical slot mapping independent of declaration order. Permuted declarations share a layout; small layouts use inline names instead of a per-layout hash map. The compiler/reader establish direct-call arity and construction count. Runtime checks remain for a genuinely indirect callable, not every direct instruction. |
| Session and I/O | Stable REPL entry slots do not prohibit intraprocedural scalar replacement in private callables. Unused `max_streams`/progress settings are parsed but do not gate execution. Owned local replacement cohorts register their source reads and replaced root; only endangered source members are staged. |

This amends C01 (owned local replacement only), C04 (private intraprocedural
passes), C05 and C06 of the concurrent C# audit. Lazy producers and merges
through existing directories still need their own admission footprints;
namespace identity reuse and Windows reparse-tag classification are separate
work. No claim is made that the whole fallback family has disappeared.

For the mixed-control workload, a real literal transfer was prepended to
the frozen 8,192-key Heap source and compiled once with `0c55773`. The same
`.mso` was bound by the two Linux Native AOT CLIs in six alternating
fresh-process pairs on CPU 5, after one untimed pair. The bind median
(including load and output) was **2,027.2 → 36.9 ms**. Both executable
artifacts produced the expected score (8,192) and exact transferred bytes.
The executable bytes need not match; the bound code no longer receives a
second portable transformation. This is not a throughput claim for other
workloads. The phase separation is consistent with
[LLVM's analysis invalidation model](https://llvm.org/docs/NewPassManager.html),
but the performance numbers are specific to msync.

The combined CLI passed 27 gated journeys, including independent replacement
and truly dependent replacement in all three source/object/executable routes.
A residual mixed-source replacement's `openat` trace shows staging only
for the file inside the replaced directory; the unrelated input was opened
directly and the final destination bytes matched. The same fixture ran with
an unused `max_streams = 0` and negative unused progress settings. Raw
timings, binary identities, probe source and acceptance data are in
`artifacts/benchmarks/library-matrix-v086/phase-ownership/`.

## Current VM cost: fewer interpreter actions before a different dispatcher

The release-head investigation uses `025b1da`, SDK 10.0.403, Linux x86-64
Native AOT and O2 artifacts. The original LibraryMatrix's 37.7 s Set, 25.3 s
Heap and 3.42 s SHA are historical results. The current VM has neither
continuation replanning nor BasicBlock dispatch. The earlier 26.03 → 5.30 s
same-binary planner bypass is strong evidence for the historical regression,
not a remaining optimization opportunity. In the earlier integrated table,
Set and SHA were already bound; the Heap input was a preloaded **.mso**, so
the latter still included executable image preparation. See the exact
[inputs](../../artifacts/benchmarks/library-matrix-v086/deep-integrated/execution.py)
and the fresh all-bound control below.

### Scalar causal control

Use `x = x * 1.0000000001 + 0.125`, starting at zero, repeated N times.
Take N from stdin so the compiler cannot precompute the answer. On the same
Native AOT host compare a directly compiled C# loop using the same binary64
recurrence (separate multiply/add) with the production interpreter on
preloaded `.mexe` files. Time includes VM construction, an in-memory stdin
read and result/cleanup, but not parsing, binding, image load or startup.
Verify result bits in every sample and for N = 0, 1, 3, 5, 101 and 1,024
before timing. At N = 1,048,576 the checked result is `0x41000036FA3A7F36`.
Nine measured rounds alternate case order on logical CPU 11.

| Shape | Bytecodes per four updates (hot path) | Median ms | Median allocated bytes |
| --- | ---: | ---: | ---: |
| Native C# recurrence | — | 3.06 | 0 |
| VM scalar loop | 60 | 748.69 | 50,334,064 |
| VM 4× unroll, four assignments | 35 | 359.70 | 31,459,696 |
| VM 4× unroll, one nested expression | 29 | 339.71 | 12,585,328 |
| VM `for _ in iota(0, n)`, same recurrence | 36, plus iterator work | 505.24 | 50,337,136 |

The scalar loop uses a private counter and a private accumulator; its O2
disassembly has no calls in the loop body. The four-way unroll uses
`if i + 4 > n { break; }` and adds four to the counter; its tail loop
performs the remaining updates. The nested form changes only the four
assignments to `x = f(f(f(f(x))))`, with each f written inline as the
expression above; it does not change the FP operation order. Both variants
are compared on admitted nonnegative integer N, not as rewrites for every
possible Number-valued bound. The unroll wins 9/9 pairs over the scalar
loop. The nested form wins 7/9 over the four-assignment form (paired median
−17.0 ms) and removes exactly 18,874,368 B of median allocation, or three
24-byte boxes per four updates. It also removes six load/store opcodes per
group, so it does not measure the isolated elapsed effect of boxing.
The `iota` form still boxes an element even when the bytecode discards it,
but moves loop control out of the VM and is faster here; do not generalize
this to callback-heavy pipelines or different loop bodies.

In the scalar form the observed slope is exactly 48 B/iteration after the
2,416 B fixed cost in the million-update run: two 24-byte NumberValue
allocations, one for each stored result. A second batch on CPU 10 found
708.77 → 2,864.91 ms for 1,048,576 → 4,194,304 iterations and
50,334,064 → 201,329,008 B. A separate fresh-process CLI fixture prints
the scalar result and checks it bit-for-bit through source, `.mso` and
`.mexe`. Those wall times have their own startup and preparation costs.
With seven alternating fresh processes per route, source / object /
executable medians were 21.3 / 17.3 / 18.4 ms at zero updates and
742.0 / 758.8 / 736.7 ms at 1,048,576 updates. These are full CLI route
times, not direct estimates of the difference between phase internals.

The immediately-ready condition still passed through the generic async
resolver. A diagnostic build changed **only** conditional jumps to await a
DeferredValue and otherwise use the same AsBool directly. Eight fresh-process
AB/BA pairs with the same artifacts and 262,144 updates found stock →
diagnostic medians of 169.93 → 162.12 ms (scalar), 95.63 → 84.51 ms (four
assignments) and 78.80 → 77.93 ms (nested). Paired median deltas were −8.36,
−11.14 and +0.45 ms, with 5/8, 6/8 and 4/8 faster pairs, respectively; the
native control had +0.004 ms paired median. This is a diagnostic of ready
branches, not a production change or evidence that generic async plumbing
accounts for most of the gap.

Current mechanics: [dispatch](../../src/Msync.Runtime/Execution/Interpreter.Dispatch.cs)
fetches a general instruction and checks cancellation per opcode; local
loads/stores use `Value[]` and handle Cells. [OperandStack](../../src/Msync.Runtime/Execution/OperandStack.cs)
can retain raw double temporaries, but the generic Pop boxes a number at
the local store. Thus Native AOT compiles the *interpreter*, not the script
loop: type, operand transport, and control work recur for each opcode
even when no frame enters a real suspension. Neither these interventions
nor historical profiles distinguish CPU branch mispredictions from
instruction-pointer dependency, fetch costs, and individual handler work.
Hardware PMU counters were not available on this host.

Native disassembly shows two interface calls (instruction count and index)
and a checked reference assignment before the opcode jump table in
`RunOwnedCodeAsync.MoveNext` (roughly 16 KiB in the clean matrix host).
`Instruction` is a reference record and the module exposes instructions
as `IReadOnlyList<Instruction>`; the artifact reader already produces arrays.
This is runtime instruction representation, not a wire-format requirement.

Two matched experiments test this directly. On the three preloaded scalar
images (N = 262,144, eight AB/BA pairs), a diagnostic assuming
`Instruction[]` and hoisting length changed median time from 171.28 to
128.76 ms (plain), 88.55 to 67.59 ms (four assignments), and 78.10 to
66.62 ms (nested); 7/8, 7/8 and 8/8 pairs were faster. The direct native
control's paired median difference was −0.007 ms. Independently, a
matrix build kept a generic-list fallback and used direct indexing on the
actual array. All eight matched cases were faster on CPU 9: Set
12.1–18.5%, Heap 14.9–18.0%, Hash 12.8–17.7% and SHA 13.8–17.2% (two
pairs each, one warmup and one checked execution per fresh process).
This ablates length and index together; it does not assign the gain to
either one alone or quantify branch mispredictions. Receipts:
`.temp/vm-cost-root/array.csv` and
`.temp/vm-dispatch-agent/direct-fetch/comparison.csv`.

Raw sources, disassemblies, AOT image hashes and CSVs are in the
[benchmark receipts](../../artifacts/benchmarks/vm-execution-head-v086/).
Rebuild using the release CLI to compile O2 and bind the sources, and
publish the matching `scalar/Probe.csproj` there with `dotnet publish -c
Release -r linux-x64`. Run the probe with N, sample count, and artifact
basenames as arguments, with the artifact directory as cwd. The diagnostic
uses the same probe with the recorded dispatch source change.

### Where the work multiplies

A separate uninstrumented Native AOT host measured fresh HEAD-produced
`.mexe` files after loading, with a checked result on each run. The
instrumented managed same-artifact run below is a census, not the timing
build.
Two forks on CPU 9 ran in forward/reverse order, one warmup and four
measurements per case per fork.

| Case | Median ms | Allocated MB (decimal) | Dynamic bytecodes | Source call frames | Generic native invocations |
| --- | ---: | ---: | ---: | ---: | ---: |
| Set, 8,192 keys | 1,921.63 | 212.72 | 23,733,352 | 1,008,030 | 750,282 |
| Heap, 8,192 keys | 812.81 | 83.08 | 10,950,928 | 273,870 | 406,156 |
| Hash, 8,192 keys | 149.67 | 18.68 | 1,793,007 | 24,581 | 91,090 |
| SHA-256, 1 KiB | 468.86 | 62.83 | 7,574,571 | 179,794 | 137,365 |

The Set reaches 424,304 `storage.buffer_get` and 309,586 `ranges.front`
calls; the latter already has the owned snapshot fast path. Heap performs
224,710 gets and 120,960 sets. SHA-256 expands the word bit primitives into
interpreted arithmetic and table operations; 136,272 native calls are
buffer gets. These explain why an isolated VM instruction throughput ratio
is not the cross-language library ratio. Allocation is cumulative GC
traffic, not resident memory or an estimate of time in collection.

Internal source calls already place arguments directly in the new frame,
but still allocate the frame and operand storage and cross the ownership
wrapper. Calls into generic native operations collect Value arguments and
select the registry handler; frequency makes small per-call costs material.
The independent [Range and Set ablations](ranges-v080.md) separate callback
layers, comparator calls and singleton payloads from the dispatch mechanism.
They also expose a host code-generation cost: entering an ordinary native
call eagerly allocates a display class for a deferred-only local function.
Moving closure-producing paths behind helper boundaries on the same images
removes 64 B per Buffer get and 136 B per owned-snapshot index (including
Range dispatcher closures). A stock Set run removes about 60.4 MB of
allocation without changing its bytecode or algorithm. This is not a claim
that asynchronous calls suspend or that the two performance gains add.
For I/O, the [scheduler frontier probe](performance-scheduler.md) uses
real held work and a physically unrelated native observation: a different
problem from scalar code or the historical planner.

Raw fresh images, diagnostic counters and timing receipts:
`.temp/vm-dispatch-agent/{fresh,native}/`, with counters and timings also
in the benchmark receipts above. Use the exact LibraryMatrix inputs and
verified scores/digest in its [README](../../tests/Performance/LibraryMatrix/README.md).

### Next experiment: statically certified scalar regions

First take the measured execution-owned array fetch and the verified common
native call allocation removals as separate small changes. In the array
change preserve the generic in-memory module boundary and avoid copying
bytecode in a callee. For the structural work, start with one register-like
executable region for immediate, private
Number slots, not a global numeric sidecar, full JIT or a new artifact
contract. Select a hot compare/backedge or load/operate/store sequence from
the dynamic data; preserve IEEE evaluation order (no implicit FMA),
cancellation, branch targets, and source attribution. A static Number type
alone does not prove an immediate value or private storage: use the producer
facts for alias/capture, effect and deferred-value boundaries. Keep the
generic path at those boundaries and preserve boxed Value transport where it
is observable. Materialize the execution-only representation once, not in
the dispatch loop or via contextual re-planning. Existing format-5 files
still decode normally. Compare an unboxed-only prototype against a
same-arithmetic fusion and their combination on the scalar controls plus
Set/Heap and the captured/deferred controls. Record dynamic instructions,
boxes and warm time **and** preparation/short-run overhead before generalizing.

This tests independently the representation and instruction-volume axes.
The rejected general sidecar, cache and fusion controls remain informative:
do not charge reference-heavy frames for number-only work. The [Lua 5.4
interpreter](https://www.lua.org/source/5.4/lvm.c.html) illustrates direct
register operands in a production VM. [Ertl and Paysan, ECOOP
2024](https://drops.dagstuhl.de/entities/document/10.4230/LIPIcs.ECOOP.2024.14)
show that serial instruction-pointer updates can limit cheap instructions
independently of misprediction; neither result predicts msync's gain.

## Product SQLite profiles close the measurement loop

The compiler and VM now report through the same opt-in trace. Use
[profile_compare.py](../../tests/Performance/profile_compare.py) to separate
normal Native CLI timing from diagnosis: alternate identical-input runs with
telemetry disabled, then check an additional profiled run on each side.
The runner keeps the SQLite databases and summarizes pass metrics, opcodes,
native callees and total frame activity. For a compiler experiment, run both
prepared artifacts on the same VM. For a runtime experiment, run the same
artifact bytes on both binaries. For compilation costs, time the source
route or the compile command and inspect its optimizer spans rather than
charging compilation to bound execution.

A Linux x86-64 Native AOT product profile compared the release-head O2
compiler with the first integrated O2 candidate using the same runtime
(`3a605f625352`). Every bound run checked exact stdout. Counts from
SQLite, not estimated from the disassembly:

| Source workload | Dynamic VM bytecodes before → after | VM boundary Number boxes before → after | Other observation |
| --- | ---: | ---: | --- |
| 1,048,576-update scalar recurrence | 15,728,667 → 7,602,215 | 2,097,152 → 524,288 | Execute-span allocated traffic 50.37 → 12.62 MB |
| Set, 8,192 keys | 23,733,357 → 20,861,823 | 447,481 → 447,481 | Source frames and 424,304 Buffer gets unchanged |

The scalar normal-path median of seven same-runtime pairs on CPU 10 was
1,125.6 → 710.1 ms on a shared host, with substantial wall-time variation.
The independent tighter in-process [loop experiment](../../tests/LoopOptimization/README.md)
measured 696.1 → 335.5 ms. Set's first five-pair wall-time medians were
1,672 → 1,654 ms, which does not establish a comparable gain to the 12.1%
opcode reduction. Its native-call volume and allocated traffic were unchanged
on this same-runtime comparison. Counts identify what to remove next; they
are not a substitute for elapsed or CPU time.

The first bound indexed-loop run was a useful negative control: a static
diamond rewrite worked, but the actual loop stayed at 393,216 native gets.
The loop-specific reaching-value issue was investigated separately rather
than claiming a win from fewer static sites. Compiler-pass traces likewise
showed that the exact cross-block memory admission fix reduced no-op visits
from 38 to 12 on Heap and 16 to 4 on SHA, but left module-wide memory effect
analysis counts unchanged on both. Its matched serialized bytes were
identical; do not attribute the remaining analysis cost to the admission
check.

The execution receipts and SQLite databases are under
`.temp/goal-profile/compiler-{scalar,set,index}/`. The compiler admission
AB/BA probes are under `.temp/phasecompiler-gate/`.
