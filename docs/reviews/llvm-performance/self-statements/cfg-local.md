# cfg_local: original contributor self-statement

## My actual role

My own work started with the `/root/cfg_local` assignment to port LLVM local CFG
and critical-edge utilities. Earlier root discussion about frontend slowness,
Range element thunks, captures, heap proofs and the migration's initial design
was inherited context, not decisions I personally made or can reconstruct as
first-person recollection. I was subsequently assigned base block splitting,
then SROA pointer-use traversal and slice construction. I am an original author
of those pieces, not the independent SROA performance reviewer.

This statement uses my assignment history and the current source, alongside the
existing [SROA cost review](../sroa-cost.md) and
[integrated review](../../../design/llvm-performance-regression-review.md).
I have not changed product code, built, executed new timing runs, or committed
anything for this follow-up.

## What I wrote, and what I inherited

| My contribution | Boundary |
| --- | --- |
| `LlvmLocalCfg.Terminator.cs`, `LlvmLocalCfg.EmptyBlock.cs` | Constant terminator folding, recursive dead-instruction deletion, empty-block elimination, PHI value/edge redirection and source growth limits |
| `LlvmCriticalEdge.cs` | Critical-edge options, edge multiplicity, splitting, duplicate-edge merge, LCSSA exit PHIs and analysis updates |
| `LlvmBasicBlockUtils.SplitPredecessors.cs`, `LlvmBasicBlockUtils.Split.cs` | Public DT/DTU predecessor-split wrappers and base block splitting; I extended the existing LoopSimplify shared implementation with the actual DTU update branch rather than duplicating its PHI/LoopInfo policy |
| `LlvmPtrUseVisitor.cs`, `LlvmSroa.SliceBuilder.cs`, `LlvmSroa.SliceBuilder.Memory.cs` | Per-Use pointer traversal, byte offsets, slice construction, dead users/operands, PHI/select discovery, memory intrinsic/lifetime handling and call escape outcomes |
| `LocalCfgProperties.cs`, `PtrUseVisitorProperties.cs` | Focused properties handed to root for runner integration; I did not personally run the root-owned builds or claim their results |
| Initial local-CFG design note and the SROA slice-builder note | Source mapping and implementation boundaries; later alignment sections in the local-CFG note are another participant's work |

I did not write the existing DT/MemorySSA algorithms, DTU wrapper, basic block
merge/deletion implementation, SROA partition iterator/driver/type selection,
allocation-size helpers, capture attributes or intrinsic facades. I requested
and consumed those owners' APIs. In particular, `OrderedSet.ExceptWith` is not my
implementation. I did not select the default pass sequence, design Function leaf
SSA, change the Native register planner/runtime dispatch, or produce the frozen
benchmark images and performance measurements.

## Why I took this approach

I treated the pinned LLVM source as the transformation authority. For CFG
utilities that meant preserving PHI edge multiplicity and updating the existing
DT/PDT, LoopInfo and MemorySSA instead of adding a replacement graph solver.
I shared predecessor-split and base block-splice implementations so users did
not accumulate subtly different versions of the same rewrite.

For SROA I preserved its per-Use traversal, not a per-instruction shortcut:
one instruction can use two differently displaced pointers. Offsets use the
DataLayout index width and physical byte layout. A variable-offset load retains
the source read-only escape outcome, while an unknown-offset store aborts that
optional scalarization. I specifically requested the real union of capture
components rather than substituting the stricter `DoesNotCapture` predicate.
Those outcomes do not reject a source program or impose a runtime heap proof.
Range remained outside these utilities as an ordinary object/runtime call.

This was a useful source-fidelity basis. It was not evidence of equal C++/.NET
allocation cost, faster compilation, or better resulting Native execution.

## Costs and integration obligations I missed

**I concentrated on algorithm and represented-path coverage and underweighted
the cost of the managed representation.** Each new slice builder constructs a
base worklist and visited set, two additional dictionaries and a dead-user set,
even when the alloca has only ordinary loads/stores. Empty .NET containers may
delay their backing-array allocation, but their objects are still constructed.
The upstream stack-local visitor and inline small containers do not have that
same small-workload cost. Virtual visitor dispatch also replaces CRTP dispatch;
I did not inspect generated code or establish its cost. Neither fact currently
quantifies a share of the regression.

My CFG translation likewise uses managed snapshots and sets, including
`ToArray`/`ToHashSet` in rewrites. Some snapshots are required while mutating use
lists, and upstream also materializes containers, but I did not establish that
my chosen representation preserves its allocation profile. My initial handoffs
should have called this out, not merely said the source algorithms were intact.

There is also a concrete recording-disabled-path oversight in my code:
`SliceBuilder.Abort` forms `"sroa.slice.abort." + reason` before `Count` checks
whether `Metrics` is null. An aborted visit therefore constructs that key even
without recording. Root should make key construction conditional or use fixed
keys; I have not made that product edit in this review-only task. I have not
established that this path explains a material part of the measured regression.

My counters identify visited Uses, offsets, intervals and outcomes, but I did
not provide a dedicated slice-construction phase span. The existing reviewed
13.1667 ms alloca interval, with only about 0.046 ms in its direct children,
therefore cannot be causally assigned by my telemetry to visitation, sorting,
GC or scheduling. I should have identified that attribution gap in the handoff.
Adding more checks or calling the whole interval "necessary LLVM analysis"
would not resolve it.

I also left a representation-extension obligation unclosed. The initial CFG
port omitted lifetime skipping because lifetime intrinsics were then absent.
They are now represented, yet `SplitKnownCriticalEdge` still tests
`FirstNonPhiForCriticalEdge` for `IgnoreUnreachableDests`, while the pinned source
uses `getFirstNonPHIOrLifetime`. A destination beginning with a lifetime marker
then `unreachable` can therefore take a different split decision. The old
"lifetime intrinsics not represented" wording in the local-CFG note is stale.
Root should reconcile this helper and the note now that the actual intrinsic
facade exists. I have not shown that this option/path ran in the regression.

## Corrections root should preserve in the final account

1. My "complete source body" handoff meant the assigned represented operations,
   not full LLVM O2, performance parity, or an end-to-end speedup. Source fidelity
   and green properties cannot be substituted for the user's performance result.
2. Preserve the measured compile regression. The supplied +4.738 ms paired
   telemetry-off delta compares a cohort; the separate 31.098 ms detailed SROA
   observation is inclusive and instrumented. Neither its full duration nor its
   difference from mem2reg is my demonstrated causal contribution.
3. Correct my disabled-telemetry key allocation and lifetime integration gap.
   Review per-alloca small-container costs while retaining source traversal,
   ordering and offset semantics. Do not pool/reuse visitors casually: the source
   visitor's visited/worklist state is one-visit state, not a reusable analysis
   cache with a complete reset contract.
4. Keep my compile-side implementation costs separate from runtime regression
   attribution. I did not inspect enough generated Native output to explain the
   bound-artifact slowdown. My transformations can change that output; absence of
   runtime edits does not prove absence of downstream effects.
5. Root-owned serialized builds and timing were the agreed execution discipline,
   not a reason for me to leave representation costs implicit. My responsibility
   was to surface those costs and integration obligations before presenting the
   implementation as ready for production evaluation.
