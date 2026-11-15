# LLVM InstCombine scalar shifts

## Source family and staging boundary

The shift family translates the represented scalar paths of
[`InstCombineShifts.cpp`](../../vendor/llvm-reference/llvm/lib/Transforms/InstCombine/InstCombineShifts.cpp)
at LLVM commit `a55e64432a516fd6507d3ecfba7407cfe425f940`. It extends the shared
`LlvmInstCombine.Work` state, builder, worklist, simplification query and analysis
objects. There is no independent bit optimizer, pass runner, target backend or
shift-specific global solver.

The family remains staged under `.temp/llvm-instcombine/` until the coordinator
publishes the complete shared visitor cohort. The source map below describes
implemented source paths, not a claim that an unbuilt staged pass has run.

| Source function / lines | Managed family file |
| --- | --- |
| `canTryToConstantAddTwoShiftAmounts`, `reassociateShiftAmtsOfTwoSameDirectionShifts`, 28–169 | `LlvmInstCombine.Shifts.cs` |
| `dropRedundantMaskingOfLeftShiftInput`, 194–349 | `LlvmInstCombine.Shifts.Masking.cs` |
| `foldShiftOfShiftedBinOp`, 358–412 | `LlvmInstCombine.Shifts.Masking.cs` |
| `commonShiftTransforms`, 415–575 | `LlvmInstCombine.Shifts.Common.cs` |
| `canEvaluateShiftedShift`, `canEvaluateShifted`, 580–727 | `LlvmInstCombine.Shifts.Evaluate.cs` |
| `foldShiftedShift`, `getShiftedValue`, 729–873 | `LlvmInstCombine.Shifts.Evaluate.cs` |
| `canShiftBinOpWithConstantRHS`, `FoldShiftByConstant`, 876–1046 | `LlvmInstCombine.Shifts.Common.cs` |
| `foldLShrOverflowBit`, 1060–1117 | `LlvmInstCombine.Shifts.Common.cs` |
| `setShiftFlags`, 1120–1177 | `LlvmInstCombine.Shifts.cs` |
| `visitShl`, 1179–1425 | `LlvmInstCombine.Shifts.Shl.cs` |
| `visitLShr`, 1427–1782 | `LlvmInstCombine.Shifts.LShr.cs` |
| `foldVariableSignZeroExtensionOfVariableHighBitExtract`, 1785–1849 | `LlvmInstCombine.Shifts.AShr.cs` |
| `visitAShr`, 1851–1988 | `LlvmInstCombine.Shifts.AShr.cs` |

## Retained mechanisms

The source order is retained: InstructionSimplify first, then common transforms,
then opcode-specific rewrites and flag inference at their original positions.
No added numerical search budget replaces the upstream single-use restrictions
on recursive shifted-expression evaluation. The signed, unsigned and lossy shift
semantics remain distinct. Shift-amount reassociation retains the small-type
representability test, optional truncation restrictions, constant folding and
sign-bit-extraction-only mode.

Mask elimination retains all six source patterns, computes masks at twice the
widest participating integer width, and uses the real constant folder for
oversized/poison shifts rather than APInt's saturating shift as a substitute.
The source `MAX_INT_BITS` limit is retained. Scalar undef replacements occur at
the same stage before extension. Scalar poison is not treated as a concrete
APInt by an `AllowPoison` matcher; that upstream option applies to vector splats.

Rewrites preserve or drop NUW/NSW/exact/disjoint flags according to their specific
source branches. Flag inference consults the actual shared KnownBits/sign-bit
queries with the existing instruction, dominator tree and assumption cache.
Subexpression propagation mutates only the source-permitted single-use graph and
queues changed instructions through the shared worklist. Insertions before a
narrowed add retain the source dominance placement; no temporary query instruction
changes use counts during shift-amount simplification.

Represented intrinsic paths include `cttz`, `ctlz`, `ctpop`, comparison intrinsics,
`smulh`/`umulh`, and `bswap` through the coordinated intrinsic-registry cohort.
They use intrinsic identity, never ordinary function-name guesses. Scalar
bool-extension/select, overflow-bit extraction, shifted multiplication splats,
variable high-bit sign extension, and source no-wrap shift cancellation are all
part of the same family rather than special project opcodes.

## Shared dependencies and representation limits

The implementation calls the shared source algorithms directly:
`FoldBinopWithPhiOperands`, `SimplifyDemandedInstructionBits`, `FoldOpIntoSelect`,
`FoldBinOpIntoSelectOrPhi`, and `ShouldChangeType`. They are not implemented here
as `false`/`null` stubs or replaced with a single simplification query. The shared
TargetFolder builder owns folding/insertion, and the central driver owns use
replacement, name transfer, erase notifications and worklist updates.

This map covers scalar paths. `foldVectorBinop`, vector splat/undef-lane merging,
and profile/debug metadata propagation are outside the represented scalar
carrier. Their absence does not manufacture profile facts or relax scalar
poison rules. New intrinsic carriers are coordinated with the registry owner;
there are no invented numeric intrinsic IDs.

## Observability and focused journey

Optional family counters distinguish visitor calls, actual rewrite decisions,
recursive expression visits/rewrites, no-wrap/exact inference, masking outcomes,
constant reassociation and intrinsic lowering paths. The dictionary remains
absent when recording is disabled. Shared analysis and builder counters retain
their owning query contexts rather than opening a second telemetry pipeline.

`InstCombineShiftProperties.cs` is staged with the cohort. It runs the actual
shared pass on scalar functions, then checks all defined small-width results
for opposite shifts, expression propagation, unsigned carry narrowing, variable
high-bit extraction and `cttz` low-bit extraction. The independent APInt evaluator
checks returned SSA expressions; it does not require one brittle final spelling
from the entire InstCombine worklist. The coordinator owns builds and integration
execution receipts.
