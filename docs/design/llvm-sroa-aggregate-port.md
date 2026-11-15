# LLVM SROA aggregate accesses and PHI/select memory operations

This family translates `llvm/lib/Transforms/Scalar/SROA.cpp` at
`a55e64432a516fd6507d3ecfba7407cfe425f940`. It is part of the shared
[SROA translation](llvm-sroa-port.md), not a separate optimizer, Range
representation or allocation-lifetime analysis.

## Source correspondence

| Source anchors | Managed family |
| --- | --- |
| `findCommonTypeThroughPHIOrSelect`, `findCommonType` (1515–1605) | `LlvmSroa.Speculation.cs` |
| `isSafePHIToSpeculate`, `getPHIStoreToRewrite`, `speculatePHINodeLoads`, `rewritePHINodeStore` (1629–1842) | `LlvmSroa.Speculation.Phi.cs` |
| `SelectHandSpeculativity`, `isSafeLoadOfSelectToSpeculate`, `isSafeSelectToSpeculateOrPredicate` (1844–1930) | `LlvmSroa.Speculation.cs` and `.Speculation.Select.cs` |
| `speculateSelectInstLoads`, `rewriteMemOpOfSelect`, `rewriteSelectInstMemOps` (1932–2080) | `.Speculation.Select.cs` and `.Speculation.Predicate.cs` |
| `AggLoadStoreRewriter`, recursive `OpSplitter`, load/store scalar leaves (4317–4630) | `.Aggregate.cs` |
| `unfoldGEPSelect`, `unfoldGEPPhi`, pointer-use traversal (4630–4840) | `.Aggregate.Gep.cs` |

## Actual transforms

FCA decomposition walks real struct/array types, emitting every scalar/vector
leaf in source index order. Offsets and per-leaf alignment come from the shared
source DataLayout and `commonAlignment`; packed and nested aggregates are not
substituted with semantic field ordinals. Loads reconstruct the aggregate from
poison using insertvalue; stores extract each leaf before computing its GEP and
store. Atomic or volatile aggregate accesses are not split by this visitor,
matching the source `isSimple` requirement. Real `fake_use` intrinsic users are
replaced with individual component fake uses through the shared intrinsic
registry; ordinary function spelling is never interpreted as an intrinsic.

Pointer traversal uses actual `Use` objects and deduplicates users. It follows
casts, PHIs, selects and GEPs. A GEP with one selected pointer/index (including
an i1 zext) becomes a select of GEPs. A GEP with one PHI pointer/index becomes a
pointer PHI with GEPs in the entry block; only source-eligible arguments,
constants and static allocas may supply those pointer operands. Repeated
predecessors reuse the first generated GEP, exactly as upstream.

PHI load speculation preserves LLVM's same-block, common-load-type,
intervening-write and predecessor-edge rules. Critical edges use the actual
shared Loads.cpp unconditional-load query, not a replacement one-bit local
allocation test. Injected loads are shared for duplicate predecessor edges.
A PHI store is moved only after the source dominance and edge-splitting checks;
critical edges use the real shared SplitBlockPredecessors and DomTreeUpdater.
Exposed allocas return to the central SROA worklist.

Select classification follows addrspacecasts once. Atomic loads must be
predicated, even if both arms happen to be local; volatile accesses are rejected.
PreserveCFG retains the source requirement that both load arms be speculatable
and excludes stores. ModifyCFG uses the real shared if-then/if-then-else CFG
utilities. Each conditional operation clones the original access, preserving its
atomicity. A one-arm speculation uses the source branch-direction swap; loads
merge through a new PHI. The original accesses, casts and pointer select are
erased in the source order.

## Representation and ownership

The central `State` supplies DataLayout, lazy DomTreeUpdater, options, worklists,
source/function trace context and metrics. The shared TransformBuilder remains
LLVM's constant-folder builder; the driver disables GEP canonicalization as the
source does before FCA rewriting. Loads.cpp queries and alloca/offset/alignment
helpers are shared source ports owned by their respective components.

GC/runtime layout annotations are not an optimizer eligibility gate. Newly
created PHIs/selects and their actual operand Uses retain the appropriate target
ABI expectations; context-unique constants are never mutated to attach an ABI.
There is no Function or Range type exclusion to make a convenient target pass.
The target must represent split Function descriptor code/environment/name
components and arbitrary selected scalar/vector partitions. Missing support for
those components is a target representation gap, not grounds for pretending that
FCA rewriting ran or silently using Number as a pointer/integer bit container.

Debug assignment records and AA/profile metadata have no represented storage in
this IR, so their source migration/intersection is empty. Arithmetic poison flags
are not discarded as an invented substitute for absent UB-implying metadata.
Range remains an ordinary builtin pointer and constructor call throughout.

## Telemetry and focused checks

The actual aggregate visitor has an `optimizer.llvm-sroa.aggregate-accesses`
phase with function/instruction/source context. The central driver owns PHI and
select phase spans. Metrics include queued/duplicate/visited users, scalar
loads/stores, FCA/fake-use splitting, GEP distributions, PHI users/prefix scans,
predecessor edges, unconditional-load queries and concrete retained causes,
reused injected loads, speculated/predicated operations and CFG splits.
Disabled recording avoids constructing dynamic retained-cause metric strings.

`SroaAggregateSpeculationProperties` supplies six public-SROA fixture groups:
packed/unpacked nested aggregate leaves and alignment, volatile accesses,
zero-size exclusions, and select/PHI GEP-index unfolding. Escaping-pointer calls
in the decomposition fixtures deliberately keep the memory operations observable
in IR instead of letting mem2reg erase the evidence. These properties supplement
real source/CLI/artifact execution owned by root; they are not a whole-target
acceptance claim.

The six implementation files and properties are staged under
`.temp/llvm-sroa/`. Root publishes the coherent cohort and serializes all builds;
no independent build was started for this family.
