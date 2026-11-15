# InstCombine integer comparisons with constants

## Source and ownership

This is the scalar source port of
[`InstCombineCompares.cpp`, lines 950–3407](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/lib/Transforms/InstCombine/InstCombineCompares.cpp#L950),
LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`. It extends the shared
`LlvmInstCombine.Work`; it is not a second pass or a wrapper around InstSimplify.
The complete family is staged under `.temp/llvm-instcombine` pending coherent
publication of the whole source pass. The [common port contract](llvm-instcombine-port.md)
describes insertion, replacement, analysis ownership, and driver behavior.

| Staged file suffix | Source functions |
|---|---|
| `ICmp.Constant.Core.cs` | `foldICmpAddOpConst`, `foldICmpShrConstConst`, `foldICmpShlConstConst`, `processUGT_ADDCST_ADD`, `foldIRemByPowerOfTwoToBitTest`, `foldSignBitTest`, `foldICmpWithZero`, `foldIsMultipleOfAPowerOfTwo`, `foldICmpWithConstant`, `foldICmpWithDominatingICmp` |
| `ICmp.Constant.Casts.cs` | `foldICmpTruncConstant`, `foldICmpTruncWithTruncOrExt`; scalar `m_Signum` from `PatternMatch.h` |
| `ICmp.Constant.Xor.cs` | `foldICmpXorConstant`, `foldICmpXorShiftConst` |
| `ICmp.Constant.And.cs` | `foldICmpAndShift`, `foldICmpAndConstConst`, `foldICmpAndConstant` |
| `ICmp.Constant.OrMul.cs` | `foldICmpOrXorSubChain`, `foldICmpOrConstant`, `foldICmpMulConstant` |
| `ICmp.Constant.Shifts.cs` | `foldICmpShlLHSC`, `foldICmpShlConstant`, `foldICmpShrConstant` |
| `ICmp.Constant.Div.cs` | `foldICmpSRemConstant`, `foldICmpUDivConstant`, `foldICmpDivConstant` |
| `ICmp.Constant.AddSub.cs` | `foldICmpSubConstant`, `foldICmpAddConstant` |
| `ICmp.Constant.TruthTable.cs` | `createLogicFromTable` (all 16 cases), `foldICmpBinOpWithConstantViaTruthTable`; scalar `m_SelectLike` |

## Mechanics retained from the source

The transforms use immutable APInt values of the actual IR width. Mask,
constant-range, product-overflow, and division-bound computations do not narrow
to a host integer. LLVM's `nuw`, `nsw`, `exact`, `samesign`, one-use checks,
preferred integer widths, and canonical predicate choices are retained at their
source decision sites. These are optimizer algebra preconditions, not execution
admission checks and not a global heap or alias-identity solver.

Signed and unsigned division are replaced by the source half-open interval
construction. Low/high overflow are separately retained as `-1/0/+1`, including
negative divisor predicate reversal and signed minimum boundaries. Multiplication
uses the source square-root and rounded-division reductions. The truth-table
construction constant-folds all four input combinations before constructing the
source-selected boolean expression; it retains the source one-use policy.

The dominating-condition fold consults the existing condition cache and actual
edge dominance. It does not build or iterate a new global analysis. Range and
KnownBits queries retain instruction, dominator, assumption, ephemeral, and
condition-cache context through the shared query helpers. The indexed-global
fold uses the same helper as the main comparison visitor.

The shared utility `LlvmValueTracking.NullTest.cs` ports
`ValueTracking.cpp` 11265–11294 (`stripNullTest`): the shifted-value/nonzero-lowbits
idiom and select of a value and its negation. It is one pattern helper with no
recursive solver or extra work budget.

## APInt prerequisites

`numerics/LlvmAPInt.NumberTheory.cs` ports these exact `Support/APInt.cpp`
algorithms:

- `GreatestCommonDivisor`, 826–873: modified Stein algorithm, retaining the
  common power of two inside both operands; signed mode first takes absolute
  values using modular APInt negation.
- `sqrtFloor`, 1244–1300: 32-entry lookup, the upstream hardware/libc-style
  square-root shortcut only below 52 active bits, then APInt Babylonian iteration.
- `RoundingUDiv` and `RoundingSDiv`, 2810–2855: division/remainder followed by
  the source floor/ceiling correction. No host BigInteger dependency is added.

The public managed names are `LlvmAPInt.GreatestCommonDivisor(a,b,isSigned=false)`,
`value.SqrtFloor()`, and static `RoundingUDiv`/`RoundingSDiv` taking
`LlvmAPIntRounding.Down`, `.TowardZero`, or `.Up`.

## Telemetry and focused execution

The family records actual pattern queries, shift-mask rewrites, division-range
construction and each bound overflow direction, square-root queries, rounded
multiplication reductions, constant-PHI input work, dominating conditions visited,
truth-table construction, and opcode-specific rewrite reasons. Existing builder,
query, and worklist counters retain their own causal work. Dynamic reason-key
construction occurs only when metrics are enabled.

`InstCombineICmpConstantProperties.cs` is staged with the family. It exercises the
actual shared worklist pass on signed/unsigned i8 division comparisons, including
negative and signed-minimum divisors and extreme comparison bounds, evaluating
every input against the original expression. Its numeric checks cover all i8
GCD/rounded-division operand pairs and sqrt boundaries at widths 8, 64, 128, and
257. This file is not an execution receipt: the root owns the serialized build
and will run it after the shared source cohort is coherent.

## Representation boundary

This is the complete represented scalar slice, not the complete comparison file:
other ICmp/pointer/visitor and FCmp source slices have separate owners. Vector
splat and scalable-vector branches require vector instruction/value carriers and
are not represented by scalar rewrites. The FP-exponent-mask fold calls the
actual format-aware `GetInf(type.GetFltSemantics()).BitcastToAPInt()` and
`CreateIsFPClass`; it is not approximated by a double-only constant. Those numeric
format and intrinsic dependencies are supplied by their owning source cohorts.
