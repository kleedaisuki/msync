# LLVM InstCombine boolean comparison families

Source pin: `a55e64432a516fd6507d3ecfba7407cfe425f940`.
New pass-family files are staged under `.temp/llvm-instcombine/` for the shared
InstCombine coordinator. They do not constitute a second pass, an independent
worklist, or an execution prerequisite.

## Source mapping

| Managed component | Upstream source |
| --- | --- |
| `LlvmInstCombine.BooleanCompare.Common.cs` | `InstCombineAndOrXor.cpp::getNewICmpValue`, `getFCmpValue`, `insertRangeTest`; scalar PatternMatch adapters |
| `BooleanCompare.Masks.cs` | `MaskedICmpType`, `getMaskedICmpType`, `conjugateICmpMask`, `decomposeBitTest`, `getMaskedTypeForICmpPair`, `getReconstructedMaskedICmp` |
| `BooleanCompare.MaskedFolds.cs` | `foldLogOpOfMaskedICmps_NotAllZeros_BMask_Mixed`, `foldLogOpOfMaskedICmpsAsymmetric`, `foldLogOpOfMaskedICmps` |
| `BooleanCompare.PowerAndMasks.cs` | `foldAndOrOfICmpsWithPow2AndWithZero`, `foldSignedTruncationCheck`, `foldIsPowerOf2OrZero`, `foldIsPowerOf2`, `foldNegativePower2AndShiftedMask`, `foldPowerOf2AndShiftedMask`, `foldUnsignedUnderflowCheck` |
| `BooleanCompare.Parts.cs` | `IntPart`, `matchIntPart`, `extractIntPart`, `foldEqOfParts` |
| `BooleanCompare.Ranges.cs` | `simplifyRangeCheck`, `foldAndOrOfICmpsWithConstEq`, `foldAndOrOfICmpsUsingRanges`, `foldAndOrOfICmpEqConstantAndICmp` |
| `BooleanCompare.Floating.cs` | `matchIsNotNaN`, `matchUnorderedInfCompare`, `matchIsFiniteTest`, `foldLogicOfFCmps`, `matchIsFPClassLikeFCmp`, `foldLogicOfIsFPClass`, `reassociateFCmps`; `InstCombiner.h::stripSignOnlyFPOps` |
| `BooleanCompare.AndOr.cs` | `foldAndOrOfICmps`, `foldBooleanAndOr`, preserving upstream transformation order |
| `BooleanCompare.Xor.cs` | `foldXorOfICmps` |
| `LlvmCmpInstAnalysis.Codes.cs` | `CmpInstAnalysis.h/.cpp::getICmpCode`, `getFCmpCode`, `getPredForICmpCode`, `getPredForFCmpCode`, `predicatesFoldable` |
| `LlvmValueTracking.SignBitCheck.cs` | `ValueTracking.cpp::isSignBitCheck` |
| `LlvmKnownFPClass.PredicateUtils.cs` | `GenericFloatingPointPredicateUtils.h::fcmpToClassTest/fcmpImpliesClass` overloads; SSA matchers from `FloatingPointPredicateUtils.cpp`; `FloatingPointMode.cpp::inverse_fabs` |

The integer families retain actual APInt masks and modular ConstantRange unions,
intersections, offsets and complements. `m_AddLike` includes disjoint OR;
`m_Not` is commutable; signed-truncation and extracted-part matching retain the
source one-use and width conditions. No integer facts are approximated as source
Number/binary64 arithmetic.

The logical-select paths retain the source freeze insertion/reconstruction
rules, rather than treating a poison-blocking select as an unconditional bitwise
operation. Ctpop rewrites drop the actual poison-generating annotations through
the shared Core instruction API and push the affected instruction to the source
worklist. XOR implication folding preserves other users when it inverts a
comparison, excluding the newly created inversion from its own replacement.

The floating families preserve predicate bit encodings, operand swaps,
fast-math intersections/unions, ordered/unordered NaN distinctions, finite tests,
class tests and absolute-value range checks. `StripSignOnlyFPOps` performs the
source sequence once (negation, fabs, copysign), not a new recursive stripping
algorithm. The generic predicate utility uses the actual Function denormal input
mode and declines the zero-class identity when inputs are not IEEE. Its fabs
look-through and smallest-normal/negative/positive/subnormal/infinity branches
are the pinned source branches, not the previous no-fabs approximation.

## Integration and representation

- These methods extend the single `LlvmInstCombine.Work` owned by the pass
  driver. They consume its SQ/DT/AC, TargetFolder builder, metrics and real
  InstructionWorklist; target-specific profitability is not invented here.
- Main comparison dispatch retains the upstream call order, one-use checks and
  source distinction between logical and bitwise operands. Known SameSign flags
  are copied where source predicates retain them, and dropped from a surviving
  RHS exactly where the logical substitution branch requires it.
- The original source CmpPredicate pack also carries SameSign into shared
  InstructionSimplify. The coordinator has been notified that the older enum-only
  query API does not express this fully; that shared dependency must be resolved
  before claiming full predicate-pack coverage. A second ad-hoc signed query is
  not substituted for the source implementation.
- Source spans and per-Use logical ABI annotations are preserved by the shared
  builder/replacement machinery. Derived names use raw UTF-8 bytes rather than
  decoding and re-encoding arbitrary LLVM value names.
- Current frontend scalar floating representation is IEEE binary64. Other
  APFloat formats, vector constants/splats and vector extraction/materialization
  are representation work outside this scalar family, not silently emulated.
  Comparison-code constant creation explicitly reports absent vector splat
  representation instead of constructing a scalar with the wrong result type.
- Debug/profile metadata graphs have no represented carrier here; no fabricated
  branch probabilities or source attribute promises are introduced.
- Range objects are ordinary runtime pointers and calls. LLVM ConstantRange in
  this component means an integer modular interval, not the language Range.

## Evidence and telemetry

Counters identify actual boolean-comparison queries and the source rewrite
family selected: masked checks, interval composition, power-of-two tests,
unsigned underflow, extracted integer parts, NaN/class/finite/absolute-value
checks, and XOR comparison transformations. Function/instruction/source spans
are owned by the real shared pass driver. No diagnostic map is allocated by
these family helpers when the existing recorder is disabled.

`BooleanCompareProperties.cs` is staged for coordinator execution. It checks
comparison-code truth tables, sign-bit classification, and exact/implied FP class
masks against independent raw IEEE classification and host comparison relations,
including signed zero, both NaN classes, infinities, normal/subnormal boundaries,
fabs inputs and the non-IEEE denormal-input refusal. It is supplementary to the
coordinator's real source/Native artifact journeys. No build, execution count or
performance result is claimed until the coordinator runs the integrated cohort.
