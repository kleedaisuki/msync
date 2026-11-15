# InstCombine PHI family

Pinned upstream: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`.
This is a family of the shared `LlvmInstCombine.Work` visitor/worklist, not a
second pass or a custom SSA propagation algorithm. Incomplete shared integration
is staged in `.temp/llvm-instcombine`; the parent owns pipeline selection/builds.

## Code-to-source map

| Managed file | Exact upstream routines |
| --- | --- |
| `LlvmInstCombine.Phi.cs` | `InstCombinePHI.cpp::visitPHINode`, `PHIsEqualValue`, `getAnyNonZeroConstInt` |
| `.Phi.Operations.cs` | `PHIArgMergedDebugLoc`, `foldDeadPhiWeb`, PHI-of-insertvalue/extractvalue/binop/GEP/load/op, `isSafeAndProfitableToSinkLoad` |
| `.Phi.Casts.cs` | `foldIntegerTypedPHI`, `foldPHIArgIntToPtrToPHI`, `foldPHIArgZextsIntoPHI` |
| `.Phi.Slicing.cs` | `PHIUsageRecord`, `LoweredPHIRecord`, `SliceUpIllegalIntegerPHI` |
| `.Phi.ControlFlow.cs` | `simplifyUsingControlFlow`, `foldDependentIVs` |
| `.Phi.PushOperation.cs` | `InstructionCombining.cpp::simplifyInstructionWithPHI` (1923–1961), `foldOpIntoPhi` (2007–2187), `foldBinopWithPhiOperands` (2279–2390) |
| `.Phi.Recurrence.cs` | `InstructionCombining.cpp::foldBinopWithRecurrence` |
| `LlvmValueTracking.Recurrence.cs` | `ValueTracking.cpp::matchTwoInputRecurrence`, both `matchSimpleRecurrence` overloads |
| `LlvmLocal.OverflowTracking.cs` | `Local.h::OverflowTracking`, `Local.cpp::mergeFlags/applyFlags` |
| `LlvmValue.DoPHITranslation` | `Value.cpp::DoPHITranslation` (1117–1122), minimal insertion into existing Core Value |

## Preserved mechanisms

The visitor order follows the source: generic simplification; zero-extension and
pointer cast folds; common incoming-operation sinking; pointer-cast merging;
dead PHI-web removal; integer-to-pointer PHI conversion; dead induction cycles;
nonzero-input simplification; equality cycles; predecessor canonicalization/CSE;
illegal integer slicing; dominating-condition replacement; dependent IV folding.
The 16-PHI limits, source `MaxNumPhis` budget, one-user versus one-use distinction,
constant-index GEP rejection, and operation/flag intersections are retained.

Operation pushing uses actual per-predecessor simplification and PHI translation.
It permits one unsimplified incoming path only under the source unconditional
branch/reachability/backedge decisions; source comparison-intrinsic and boolean
zext opportunities have their separate WillFold branch. Clones are deduplicated
by predecessor, and UB-implying attributes/metadata are dropped through the
shared source Core helper. Identical users are collected before replacement.
The implied-condition fallback uses the source layout-only query, not invented
heap facts or an extra proof engine.

Illegal integer slicing keeps the source ordered PHI inspection and sorted
(PHI id, shift, trunc width) work records, predecessor-value reuse, extraction
cache and dynamic appended work for cyclic PHIs. The source extraction-cache
lookup key is preserved, including its use of the current PHI rather than
silently substituting the incoming PHI. Interleaved recurrence reduction uses
actual source OverflowTracking flag conditions, not blanket flag clearing or
invented arithmetic identities. Binary/FP constant work goes through the shared
source ConstantFolding component.

## Representation adaptation and boundaries

Managed collections replace SmallVector/SmallPtrSet/DenseMap; graph ownership and
use traversal remain the existing Core IR contracts. This family does not update
CFG or reconstruct aliases. Ordinary calls, HeapAlloc and FileIn/FileOut remain
ordinary represented operations; no Range recognition exists.

LLVM DILocation/MD_prof/TBAA/access-group metadata are not represented in this
Core slice. Equal project SourceSpan locations survive incoming-operation
merging; conflicting locations are dropped, rather than falsely selecting one
arm. This is a diagnostic-carrier adaptation, not a port of DILocation's inline
scope-merging algorithm. The source load CSE metadata combination is therefore
not claimed as implemented. Actual project ABI LogicalType annotations follow
operand roles and definitions; no globally uniqued constant is retagged and no
new type/alias constraint is introduced. PHI operand reordering swaps the
per-use annotations with their values. New result instructions inherit the
replacement hint only through the shared replacement/insertion mechanism.

ConstantExpr and vector-constant carriers are still outside the represented
slice. The source constant-expression rejection is checked by exact ValueKind,
not replaced by an assumption that every constant is an immediate. No missing
represented helper is substituted with false/null: cast profitability, lossless
truncation, Core flag operations, query dispatch and Builder TargetFolder are
shared dependencies with explicit owners.

## Telemetry and end-to-end opportunities

The shared driver supplies function/source context and disabled-lean metric
storage. Family metrics count actual incoming/user scans, dead-web work/limit,
created operand PHIs, merged operation kinds, load-sink memory scans, pointer
conversion outcomes, incoming simplifications, clone insertion, rejected
backedges/multiple unsimplified operands, slicing/extract rewrites, CSE,
condition replacement and recurrence reduction. There is no separate outer-only
stopwatch masquerading as transform evidence.

Useful real journeys include if-arms computing the same binary operation with
one common operand, aggregate insert/extract merges, an integer address PHI with
an existing pointer counterpart, nonzero PHI inputs used only for zero equality,
and mutually cyclic PHIs. Compare actual source→new IR→Native output and
rewrite reasons; do not infer benefit from unit-test counts or add a new CLI
optimization gate for this family.
