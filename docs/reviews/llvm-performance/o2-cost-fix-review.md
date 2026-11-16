# O2 address-cost repair review

## Scope and result

Independent source review of the working-tree repair over `90857bf3`:
`LlvmPointerVisitedSet`, both pointer walkers, DSE address-fact reuse and its
deletion/merge integration, and the focused property fixtures. No substantive
correctness or optimization-policy defect found in this scope.

This review did not build or time the changes. The delivery owner runs the
focused properties, real user journey and matched Native AOT benchmark; those
results, not source review, establish the remaining portion of the measured
37.39 ms regression. See [the causal investigation](o2-compile-cost.md).

## Checked mechanisms

- The pinned LLVM `lib/IR/Value.cpp` uses `SmallPtrSet<const Value *, 4>` for
  both walks. The port now stores four references inline and allocates overflow
  storage only for a fifth distinct reference. Both representations use
  reference identity. Promotion seeds every existing entry; cyclic short and
  long walks retain the prior first-repeated-node return and callback behavior.
  No traversal limit or pointer arithmetic changed.
- DSE reuses structural decompositions, not alias answers or memory contents.
  The reference-keyed table belongs to one function invocation with one fixed
  data layout. The three helpers preserve their original default modes,
  underlying-object lookup budget and zero initial offset. Pointer index width
  still comes from the exact queried pointer type; cached offsets remain
  `LlvmAPInt`, whose arithmetic does not mutate cached operands. Partial
  decompositions retain their original base and displacement.
- `CollectionsMarshal` references are not held across another table lookup or
  mutation: the invoked value-tracking helpers do not call back into DSE.
- Every existing DSE operand mutation is covered: recursive deletion clears
  before poisoning operands, and partial-store merging clears before changing
  the store value. Delayed erasure runs after the analysis phases and after
  deletion's operand rewrite. No global revision/dependency mechanism is added.
- Existing overwrite decisions, scan/walk/definition budgets, phase ordering
  and enabled optimizations are unchanged. Cache metrics distinguish requests
  from actual helper work; the recording-disabled paths do not construct metric
  keys or records. The inline visited-set implementation allocates no overflow
  collection for short walks.
- Focused fixtures cover short/overflow chains and cycles, ConstantExpr and
  address-space behavior, repeated distinct visible fields, negative offsets,
  32-bit index layout, recursive address deletion and actual partial-store
  merging. The focused entry point is additive; existing journeys remain intact.

## Delivery interpretation

Accept the mechanism without reducing DSE work or removing cycle behavior.
Before reporting a latency improvement, compare the same source and release
build settings, and retain the detailed DSE query/decision counts alongside
allocation and timing results. Unchanged optimization outcomes and reduced
helper work are the intended result; fewer optimization candidates are not.

## Follow-up: target PointerTyID failure

The final AOT timing warmup exposed `Type -> StorageFromRoot ->
MemoryStorageType -> Load`. The same stack already exists in the pre-repair
diagnostic receipt `.temp/o2-cost-probe-01/result-second.json`; this target
boundary defect predates the address cache and inline set.

There is a concrete missing consumer of existing metadata:
`MemoryStorageType` reads the pointer Use's `LogicalType`, but not its
`TargetStorageType`. After a zero GEP folds to an opaque base, `LlvmUse.Set`
preserves the original physical structure on that Use. `MemoryAddressSlot`
does consume that structure, but is called only after storage type selection.
For an unannotated pointer payload, the earlier selection falls through
`StorageFromRoot` to `owner.Type(payload)`, which cannot derive a Native pointer
representation from LLVM's opaque `PointerTyID` and throws. Reading the already
declared physical layout at storage selection closes this path without a new
alias constraint, runtime check or fallback analysis.

This establishes a pre-existing deterministic metadata-consumption gap, not the
cause of its intermittent occurrence in the complete workload. Repeated passing
compiles do not close it; a focused zero-GEP pointer-payload fixture is the
appropriate regression case. The target implementation owner is preparing that
fixture and the narrow fix; source review and integration results follow below.

The subsequent frozen target patch consumes the physical Use declaration in
`MemoryStorageType` through the existing `StorageField` selector and uses that
resolved carrier for an otherwise unannotated pointer/aggregate load result.
Both are necessary: repairing storage selection alone leaves the later
`LoadResultType -> owner.Type(load)` failure. Existing logical-ABI selection
remains first; no general inference fallback was introduced.

The physical Use declaration describes a zero-offset view relative to that
Use's current value. This matches the existing `MemoryAddressSlot` and
`Slot(LlvmUse)` behavior. `DeclaredStorageFieldView` still obtains `Slot(value)`,
so a nonzero GEP value retains its own address calculation; selecting field zero
of the declared view does not replace that value with its root. Changing only
storage selection to prefer GEP interpretation would instead risk disagreeing
with existing address selection.

`DeclaredStorageUseJourney` creates the missing case deterministically from a
source-lowered graph: ordinary zero-GEP replacement retains the declaration on
the Use, the Scope base is opaque, and the pointer load has no source ABI hint.
The fixture requires the new decision metric and nested-object result 7, directly
and after Native2 serialization. Source review found no substantive defect in
the frozen patch; build and execution remain owned by the integration agent.

## Final telemetry review

The six new `addresses.memory_storage.*` metrics are decision **counts**, not
presence flags. `CompilationTrace.Metric` assigns its supplied value; each
decision therefore supplies its own incremented `Body` counter. These counters
are fresh per function, and C# conditional access does not evaluate the
increment when `trace` is null. No generic trace API or lowering decision was
changed for this correction.

DSE's new address-fact counters use literal full `dse.address.*` keys through
`CountAddress`. Each call adds to the existing dictionary value when recording
is enabled, without the previous dynamic prefix concatenation. Names and
request/hit/miss/invalidation meanings remain unchanged. Source review found no
substantive defect in either instrumentation adjustment.

The integration owner reported passing real end-to-end and deterministic
physical-Use journeys. Recorded receipts include
`artifacts/diagnostics/o2-cost-fix/address-properties-final.json`,
`dse-rotation-receipt.json` and `function-ssa-final.json` in the same directory.
The earlier balanced 60-block/180-compile comparison preceded the final
instrumentation-only correction; the owner is rebuilding and remeasuring the
exact delivery image. Do not substitute those earlier numbers for the final
image's acceptance result.

## Candidate filtering: avoid work rather than only cheapen it

Preserving 11,128 overwrite queries was useful evidence that the first repair
did not reduce optimization capability; preserving that count is not the
performance objective. Preserve transformations and observable behavior while
removing redundant candidate work.

### Upstream and research comparison

Pinned `DeadStoreElimination.cpp::getDomMemoryDef` (1608 onward) still walks
`getDefiningAccess()`. It computes a location-specific optimized access only
after the traversal establishes it, and consumes that cached edge later in
redundant-existing-value elimination (around 2466). This is not a general jump
edge for another store's location. The current C# split of defining/optimized
operands matches that distinction.

[LLVM's MemorySSA documentation](https://www.llvm.org/docs/MemorySSA.html)
explains the single memory partition, location-query walker and the distinction
between defining and optimized accesses. Consequently, invoking an uncached
walker for every field is not automatically a complexity improvement: it can
move the same disjoint comparisons into another traversal. Reusing a different
store's optimized edge can skip a relevant write or intervening read.

Diego Novillo's author-hosted
[Memory SSA: A Unified Approach for Sparsely Representing Memory Operations](https://www.airs.com/dnovillo/Papers/mem-ssa.pdf),
sections 2.2 and 3, gives the relevant representation principle: aggregate
regions can be represented separately, while more precise partitioning trades
representation size and maintenance work for sparser optimization queries.
The applicable lesson here is to summarize already known byte regions locally,
not introduce whole-program points-to solving or repartition every MemorySSA
edge. This recommendation is an application to this workload, not an algorithm
claimed by that paper.

### Bounded recommendation

Use same-basic-block runs of ordinary stores with a shared decomposed base and
known byte intervals. Maintain the union of prior written intervals. If the
incoming store is disjoint from the whole union, the entire run contains no
overwrite candidate and the candidate walk can jump to its preceding boundary.
Any overlapping interval continues through the existing DSE candidate and
read/use/CFG logic, including partial-store merging. This is general store
geometry, not a namespace, allocation or initializer recognition rule.

The first implementation should stop runs at reads, calls, throwing operations,
unknown writes, fences, nonordinary ordering and control-flow boundaries. A
run whose MemorySSA uses expose a read or PHI is not an unrestricted straight
store chain. Inspect each included definition's relevant uses, not only the run
boundary; an optimized operand is an additional analysis edge rather than a
new executed memory operation. Existing same-block domination must remain true.

Keep interval arithmetic consistent with APInt/index width. A host signed end
comparison must not misclassify a wrapping range as disjoint. Exact-address
maps are insufficient because different offsets can still partially overlap.
Root/layout changes and actual IR mutation end or rebuild affected summaries.
No reduced scan budget, smaller optimization set or generic optimized-edge
shortcut is needed.

A sorted union is proportionate for the observed mostly ordered field writes;
measure build/query work before introducing an interval tree or CFG summaries.
The integration owner is re-profiling the residual stage costs first: if DSE
has become cheap and InstCombine dominates, candidate filtering's expected
benefit must be assessed against that remaining budget rather than query count
alone. No build or timing was run by this reviewer.

### Frozen online-run implementation review

Reviewed `LlvmDeadStoreElimination.Candidates.cs` and its `Eliminate`, `Walk`
and address-invalidation hooks. No substantive source defect found.

- The state is one online run, not a per-function precomputed CFG index.
  Extending requires the exact previous raw defining access, same basic block,
  same reference-identity base and index width. Every extension checks the
  previous definition's real MemorySSA uses and intervening IR effects. Thus
  every admitted member was checked when its successor joined the run.
- Operand 1 of a `MemoryDef` is its extra optimized-analysis edge, not an
  executed observer; ignoring it preserves this distinction. Other uses except
  the next definition's operand 0 terminate the run. Intervening reads, writes
  and throws likewise terminate it. Mutations reset the run before changing IR.
- Interval keys are starts and values are ends. The existing start-key lower
  bound, predecessor/successor disjointness checks and union insertion agree;
  touching intervals merge without losing gaps or overlapping candidates.
  Ranges above 64-bit width or crossing the signed endpoint representation do
  not enter the index. Their ordinary DSE path remains available.
- The jump changes only the initial candidate cursor; `start` remains the
  original access for direct-use read-clobber dominance checks. Overlapping
  ranges take the original walk and partial-overwrite machinery. No scan or
  definition budget changed, and no optimization phase was removed.
- The repeated-fields fixture now checks 66 skipped definitions and zero
  overwrite queries for 12 disjoint visible fields, rather than demanding high
  cache-hit counts. Deletion, actual partial merging and an observing load are
  retained as discriminating cases. Existing loop/CFG journeys remain the
  integration owner's focused execution check.

Skipping already known-disjoint work can let the remaining budget reach more
useful candidates. That is compatible with preserving optimization capability;
the old number of comparisons is not an acceptance condition. Final latency
benefit remains a measurement question for the owner, not a source-review claim.

### Identified-root partitions and unrelated reads

The follow-up frozen index extends the run across multiple roots only when all
are identified objects under the existing BasicAA predicate. Each root keeps
its own interval union; opaque argument roots cannot be treated as disjoint by
identity alone. Unordered loads can remain in such a run only when their
underlying root is also identified and absent from every written partition.
Reads of an already-written root remain boundaries. A read before the first
store to a newly added root does not observe a prior store in that run; adding
that partition makes subsequent reads of it boundaries. This removes actual
literal/global-load fragmentation without ignoring general reads or calls.
Source review found no substantive defect. Focused cases include unrelated
global reads, observing global reads and alternating possibly-aliasing argument
roots; execution and performance remain integration-owned.

## Follow-up: aggregate Function load geometry

A later balanced run failed in the **pre-index control** image `6c49d967...` at
`zero_trip`, preserved in
`artifacts/diagnostics/o2-store-candidates/compile-balanced-60.json`.
The earlier Scope pointer-field fixture established its own narrow fix, not
complete target metadata coverage. Aggregate Function scalarization introduced
new component addresses without carrying the original memory Use declaration.

Reviewed the frozen follow-up producer/consumer repair. A shared InstCombine
helper covers load/store retyping, struct/array unpacking and extract-load
narrowing. An unchanged address copies its Use declaration; a new GEP retains
the original physical root declaration on the GEP, not as a false zero-offset
view on a nonzero component's memory operand. Actual pointer arithmetic and
component offsets remain intact.

`FunctionMemoryComponent` now accepts the actual memory Use. A folded non-GEP
address can therefore provide its existing explicit storage declaration to
`PointerRoot`; load, store, packing and grouped-load callers use the same entry
point. It does not infer a layout from an abstract Scope contract or disable
scalarization. The additional fixture executes the actual three-component
Function load transformation with zero-GEP folding both before and during
InstCombine, then requires result 7 directly and through Native2 serialization.
Source review found no substantive codegen defect; the integration owner must
report execution results for this extended fixture separately from the earlier
pointer-field acceptance. The new physical-use telemetry must publish an
accumulated count rather than repeatedly assigning 1.

The final re-frozen telemetry change does so with a dedicated
`functionMemoryPhysicalUses` Body counter, incremented under conditional trace
access only on the non-GEP path that actually supplies the declaration to
`PointerRoot`. This resolves the source-review telemetry finding.
