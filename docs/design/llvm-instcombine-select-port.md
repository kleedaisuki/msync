# InstCombine select source family

Source pin: `a55e64432a516fd6507d3ecfba7407cfe425f940`, Apache-2.0 WITH
LLVM-exception. Primary sources are
[InstCombineSelect.cpp](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/InstCombine/InstCombineSelect.cpp)
and [InstructionCombining.cpp](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/InstCombine/InstructionCombining.cpp).

## Staging and ownership

Incomplete files are staged in `.temp/llvm-instcombine/select/`. They are not
published product implementations, and no build or completed-family claim is
made. The common Work state, target-folder builder, worklist and queries follow
`.temp/llvm-instcombine/INTERFACES.md`. Root serializes integration builds.

The select owner implements select distribution, common operation extraction,
integer compare and min/max canonicalization, nested/boolean select rewrites,
the visitor and select portions of the shared InstructionCombining routines.
The PHI owner implements FoldOpIntoPhi and recurrence/PHI binary folding; the
shared FoldBinOpIntoSelectOrPhi dispatch lives in the select family and calls the
real PHI implementation. No second select-specific optimizer/worklist is added.

## Source-to-managed map in progress

| Source family | Staged file |
| --- | --- |
| InstructionCombining.cpp distributeBinOpOverSelect, SimplifySelectsFeedingBinaryOp | Select.Distribute |
| simplifyOperationIntoSelectOperand, foldOperationIntoSelectOperand, FoldOpIntoSelect, foldBinOpSelectBinOp, foldBinOpIntoSelectOrPhi | Select.Operation |
| simplifyNestedSelectsUsingImpliedCond, foldAndOrOfSelectUsingImpliedCond | Select.Operation |
| InstCombineSelect.cpp getSelectFoldableOperands, foldAddSubSelect, foldSelectNegNot, foldSetClearBits, foldSelectExtConst | Select.Arithmetic |
| foldSelectICmpAnd, foldSelectICmpAndBinOp, foldSelectBitTest | Select.BitTest |
| foldSelectOpOp, scalar CmpPredicate::getMatching carrier adaptation | Select.OpOp |
| foldSelectIntoOp | Select.IntoOp |
| canonicalizeSPF, foldSPFofSPF, canoncalizeSelectICmpMinMax, foldSelectICmpMinMax | Select.MinMax |
| foldSelectOfSymmetricSelect, foldNestedSelects | Select.Nested |
| replaceInInstruction, foldSelectValueEquivalence | Select.Equivalence |
| foldSelectICmpAndZeroShl, foldSelectICmpLshrAshr, foldSelectZeroOrOnes, foldSelectWithExtremeEqCond, foldSelectICmpEq | Select.ICmpBits |
| foldSelectInstWithICmpConst, tryToReuseConstantFromSelectInComparison | Select.ICmpConstants |
| canonicalizeClampLike | Select.Clamp |

Scalar PatternMatch adapters translate the actual source matching order and
commutation behavior, including select-like zext/sext of i1. They do not turn
intrinsic-only patterns into arbitrary select matches. Source CmpPredicate has a
separate SameSign field in this carrier; matching intersects or removes this
flag according to Instructions.cpp rather than copying it unconditionally.

## Shared prerequisites and remaining work

InstructionSimplify query-preserving entry adapters, recursive replacement and
DropFlags collection belong to the central owner. Integer and FP facts consume
the real function cache and context, except source routines explicitly using a
fresh DataLayout-only query. Builder target folding and insertion/worklist
callbacks are centralized. Instruction flags and predicate queries must be
actual core source operations, not family-local approximations.

The full source visitor, boolean rewrite family, constant-operation factoring,
select-to-PHI conversion, saturated/overflow/bit-count idioms and floating-point
leaf families remain under active translation. Required scalar intrinsic
identities are coordinated with scalar_intrinsics and floating_intrinsics;
string matching ordinary call names is not an acceptable substitute. Binary64
APFloat is sufficient for the language Number subset; other floating formats
are not claimed. Source vector/shuffle/masked-vector-load and cmpxchg paths
require their actual absent IR representations, not fake scalar versions.

Detailed work and rewrite counts are recorded through the driver's shared
sink-neutral metrics. The builder and driver own source span/use-role metadata
and queue notification; flags and source profitability/use-count decisions are
retained by each translated transform. No unit-count-only profitability model
or safety gate framework replaces these source decisions.
