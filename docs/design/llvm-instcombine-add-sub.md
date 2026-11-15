# LLVM InstCombine Add/Sub and Negator source port

Source pin: `a55e64432a516fd6507d3ecfba7407cfe425f940`, Apache-2.0 WITH
LLVM-exception. Reference sources live under `vendor/llvm-reference/llvm/`.

## Ownership and implementation map

The family extends the shared `LlvmInstCombine.Work`. It does not run a separate
optimizer, create a second global worklist, or use the LLVM backend/pipeline.

| Reference | Managed family |
| --- | --- |
| `InstCombineNegator.cpp` | `LlvmInstCombine.Negator{,.Fast,.Recursive}.cs` |
| `InstructionCombining.cpp` freely inverted values; header user inversion helpers | `LlvmInstCombine.Negator.{Inversion,Users}.cs` |
| `InstCombineAddSub.cpp` FAddendCoef, FAddend, FAddCombine | `LlvmInstCombine.AddSub.{FAddend,FAddCombine}.cs` |
| checkForNegativeOperand, foldNoWrapAdd, foldAddWithConstant | `LlvmInstCombine.AddSub.{NoWrap,Constant}.cs` |
| MatchMul, MatchRem, MatchDiv, SimplifyAddWithRemainder | `LlvmInstCombine.AddSub.Remainder.cs` |
| matchesSquareSum; integer and floating consumers | `LlvmInstCombine.AddSub.Squares.cs` |
| low-bit mask, shift factoring, signed-division rounding, division ceiling | `LlvmInstCombine.AddSub.Shifts.cs` |
| high-bit sign extension; masked half-width box multiplication | `LlvmInstCombine.AddSub.HighBits.cs` |
| visitAdd, its ordered scalar pattern families | `LlvmInstCombine.AddSub.Add{,.Reassociate,.Idioms,.Counts}.cs` |
| factorizeLerp, factorizeFAddFSub, foldFNegIntoConstant | `LlvmInstCombine.AddSub.Floating.Helpers.cs` |
| visitFAdd, visitFSub, visitFNeg; hoistFNegAboveFMulFDiv | `LlvmInstCombine.AddSub.{FAdd,FSub,FNeg}.cs` |
| OptimizePointerDifference | `LlvmInstCombine.AddSub.Pointer.cs` |
| visitSub and its adjacent min/max and mask helpers | `LlvmInstCombine.AddSub.Sub*.cs` (separate bounded implementation owner) |

`CommonPointerBase` and `EmitGEPOffsets` are shared source utilities owned by the
GEP family. Integer min/max and saturating/counting intrinsics retain actual
intrinsic descriptors; matching ordinary function names is not equivalent.

## Source algorithm invariants

* Negator has an independent TargetFolder builder, created-instruction list and
  per-attempt value cache. Failed attempts erase their own created instructions
  in reverse order. Successful attempts enqueue them in creation order. This
  state is local to a rewrite attempt, not a new compiler pass.
* The freely inverted source helper uses a non-value C++ pointer sentinel for
  analysis-only success. The managed port represents this as `(success, value)`;
  no sentinel or fabricated value enters IR.
* PatternMatch instruction-only binary/cast matchers remain instruction-only.
  Scalar `m_ImmConstant` excludes ConstantExpr, while source `m_Constant` paths
  retain real ConstantExpr factories and constant folding.
* PHI negation checks dominance against each incoming edge, not the definition
  block of the incoming value. Recursion limits and one-use checks are the
  upstream limits and profitability decisions.
* FAddCombine preserves source addend order, coefficient representation and
  instruction quota. Integer-represented coefficients of one/two are different
  from floating-represented coefficients with the same numerical value. APFloat
  performs arithmetic; host floating-point arithmetic does not replace it.
* Floating transformations consume the source fast-math permissions. In
  particular FNeg multiplication absorption unions value flags, intersects
  rewrite flags and separately intersects `ninf`. Select negation does not
  transfer `nsz` across an undef/poison condition unless the source permits it.
* The represented path is scalar integer and scalar floating IR. Vector
  reductions, shuffles and vector-reverse recipes are outside this scalar
  family; their scalar result type alone does not make their vector operands
  represented scalar operations.

Shared facts use the pass SimplifyQuery, including AC, DT, DC and conditional
context when the upstream query uses them. Explicit no-context source queries
stay no-context. Add/Sub pointer arithmetic uses actual layout/GEP offsets; it
adds no object identity constraint, heap solver, or special Range operation.

## Diagnostics and focused journeys

Visits and source rewrite decisions use the shared sink-neutral metric map;
recording-disabled paths do not allocate metric-key strings. Negator records
attempt, recursive depth, cache, failure cleanup and successful creation work.
FAddCombine records grouped coefficients and source quota outcomes. Source and
function context come from the shared instruction-dispatch trace; Negator also
has its own detailed local attempt span.

`tests/LlvmIr/InstCombineAddSubProperties.cs` drives the real pass for modular
square sums and quotient/remainder reconstruction, and checks no-extra-FMF FAdd
canonicalization and the exact FNeg flag combination. Root owns serialized
compilation and test execution. Staging alone is not a passing build claim.
