# LLVM InstCombine floating comparisons and select patterns

The implementation follows LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`. Upstream files are retained under
`vendor/llvm-reference/llvm/`; translated files use Apache-2.0 WITH LLVM-exception.
The FCmp partials extend the shared `LlvmInstCombine.Work` state and worklist.
They do not introduce another pass, a replacement simplifier, LLVM codegen, or
any language-level Range behavior.

## Source mapping

| Managed file | Pinned source |
| --- | --- |
| `LlvmInstCombine.FCmp.cs` | `InstCombineCompares.cpp:9421–9761`, scalar `visitFCmpInst` ordering and dispatch |
| `.FCmp.IntToFP.cs` | `foldFCmpIntToFPConst`, including exactness, mantissa/exponent limits, signed/unsigned bounds and fractional predicate changes |
| `.FCmp.Arithmetic.cs` | reciprocal-and-zero, negation/common operand, subtraction comparison folds |
| `.FCmp.Intrinsic.cs` | fabs/sqrt zero tests, exact integer-distance tests, floor/ceil comparisons, redundant min/max clamps |
| `.FCmp.Format.cs` | `foldFCmpFpTrunc`, exact wider-format midpoint construction and the FPExt constant path |
| `.FCmp.Select.cs` | shared ICmp/FCmp `foldCmpSelectOfConstants` truth-table folding |
| `LlvmSelectPattern.Floating.cs` | `ValueTracking.cpp:9050–9145,9440–9789`, FP min/max/clamp matching; scalar `PatternMatch.h::FMaxMin_match` |
| `LlvmConstantFolding.FPFormats.cs` | `ConstantFold.cpp` scalar FP cast/bitcast paths |
| `LlvmConstantFolding.Denormal.cs` | `ConstantFolding.cpp` scalar `FlushFPConstant` and the input/output denormal-mode boundary |

Shared algebraic symmetry, truth-table synthesis, select/PHI operation folding,
indexed-global comparison folding, exact integer-to-FP analysis, and the source
worklist are consumed from their respective shared implementations. They are not
replaced by null/unknown stubs or a single InstructionSimplify call. The visitor
performs the upstream initial simplification query, then its actual rewrite
families in upstream order.

## Numerical and FP-environment mechanisms

Integer-to-FP comparison folding uses APFloat integer conversion status and
`IsExact`, round-to-integral, exponent limits, and rounded integer boundaries.
No host floating-point comparison decides a transformation. The fractional
predicate adjustments preserve negative and positive bounds separately. Inputs
whose conversion precision can affect the comparison retain the source cast.

A truncating FP comparison is rewritten using the exact boundary between the
comparison constant and its adjacent representable value. The wider-format
midpoint is computed using APFloat addition and `scalbn`, then corrected for the
source format's nearest-ties-to-even result. Overflow endpoints use the source's
finite extrapolated neighbour and both representability checks. For example,
`fcmp olt (fptrunc double X to float), float 1.0` becomes a double comparison
against `0x3feffffff0000000` (`1 - 2^-25`), not against a host-computed estimate.
FPExt comparisons distinguish lossy constants from lossless non-denormal ones.

The numerical carrier supplies actual IEEE half, bfloat, single and double
semantics. Wider unsupported formats are not represented by relabelled binary64
numbers. FP constant casts and half/bfloat raw bitcasts use the same carrier and
context uniquing as arithmetic and comparisons.

Denormal conditions read `Function.GetDenormalMode` and its actual input/output
pair, including the single-precision override. Subtraction-to-comparison needs
the source IEEE pair; the integer bitcast zero test is disabled when inputs may
flush to zero; fabs smallest-normalized tests use the actual flushing input
mode. Scalar constant folding now flushes inputs and outputs through the same
source mechanism. Without a function, a denormal constant has dynamic mode and
is not guessed to be IEEE. This also removes the old binary64 exponent-mask
classification from format-generic folding.

Fast-math flags follow each source operation, rather than being copied
indiscriminately. In particular the `FCmpInst(..., FlagsSource)` constructor's
last argument is a flags source, not an insertion point; only those source
constructor sites copy the old comparison's flags. Sqrt comparisons clear `ninf`
when the sqrt lacks it; subtraction comparisons adopt the source subtraction's
`ninf` and propagate its `nnan` as prescribed. Sign-only matching includes the
source `fsub +0, X` form only when `nsz` permits it.

## FP select-pattern contract

The existing result constructor and four-value deconstruction are retained.
`NaNBehavior` and `Ordered` are additional initialized properties, carrying
upstream `SPNB_NA`, returns-NaN, returns-other, and returns-any distinctions.
Matching normalizes compare zeros to a selected zero without mutating IR,
retains signed-zero requirements, distinguishes ordered/unordered NaN outcomes,
and inverts the NaN/order descriptor when the select arms are reversed.

The source's local constant/FMF non-NaN and nonzero tests are used here; this
matcher does not invent a broader proof query. FP-to-integer cast lookthrough
adds the source's permission to ignore the zero sign. Fast floating clamps match
both the exact ordered/unordered compare-select forms and registered minnum/
maxnum intrinsics. The public raw `TryMatchOrdOrUnordFMin/FMax` helpers retain
comparison-operand capture order and are deliberately different from the more
qualified select-pattern analysis.

## Execution and telemetry

`InstCombineFCmpChecks` drives the shared actual InstCombine entry on scalar IR
functions: fractional and integer-bound rewrites, reciprocal signs, subtraction/
negation, real denormal-mode gates, fabs/sqrt/floor forms, lossy extension and
exact truncation midpoints. It also covers format-aware constant casts and the
input flushing boundary. `FloatingSelectPatternChecks` covers the NaN/order/
zero descriptor, raw pattern capture order, clamp calls and cast lookthrough.
The integration owner serializes their execution with the compiler's existing
end-to-end journeys.

Per-family telemetry counts queries, rewrites, source precision-limit retention,
format loss, clamp/NaN/zero-pattern decisions, and concrete denormal actions.
Function/source context and causal ownership remain with the shared work/pass
scope; recording-disabled helper paths do not construct dynamic outcome keys.
