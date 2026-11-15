# Floating intrinsic and demanded-class integration

The source pin is LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`.
The product uses its own IR objects and native execution, not LLVM's backend or
pass manager. `llvm.frexp`, `llvm.sincos`, and the scalar mathematical functions
are standard intrinsic calls, not project opcode extensions. The language Range
remains an ordinary built-in object; this work adds no Range evaluation function.

## Source map

| Product component | Pinned source |
| --- | --- |
| `LlvmIntrinsic.Floating.cs` | `Intrinsics.td` FP definitions, declaration overload mangling |
| `LlvmAPFloat.Frexp.cs` | `APFloat.cpp::frexp(IEEEFloat)` |
| `LlvmKnownFPClass.IntrinsicTransfers.cs` | `KnownFPClass.cpp` min/max, canonicalize, FMA, exp/log, sqrt, trig, rounding, pow/powi, ldexp |
| `LlvmKnownFPClass.Intrinsics.cs` | `ValueTracking.cpp::computeKnownFPClass` intrinsic call cases |
| `LlvmKnownFPClass.Conversion.cs` | `KnownFPClass.cpp::fpconvert`, `fpext/fptrunc`, `frexp_mant` |
| `LlvmInstCombine.DemandedFPClass*.cs` | `InstCombineSimplifyDemanded.cpp` demanded FP-class entries and structural/arithmetic/intrinsic transfers |
| `LlvmInstCombine.Calls.FPClassPredicate.cs` | `InstCombineCalls.cpp::fpclassTestIsFCmp0` |
| `LlvmConstantFolding.FloatingAggregate.cs` | `ConstantFolding.cpp::ConstantFoldStructCall`, `ConstantFoldScalarFrexpCall` |

The numerical format contract is IEEE half, bfloat16, IEEE single, and IEEE double.
They use the same APFloat exponent/significand engine; conversions do not route
through a host `double` except the host-libm folds explicitly present upstream.
There is no pretend x87, quad, PPC double-double, or arbitrary-format adapter.
All four represented formats have infinity and their sign bit in the most
significant bit, which is the scope of the corresponding APFloat property APIs.

`frexp` obtains the exponent using APFloat `ILogB`, quiets signaling NaNs,
preserves infinities, assigns exponent zero to zero, and rescales finite inputs
by the exact exponent to produce a signed fraction in `[0.5, 1)`. Aggregate
constant folding selects exponent zero for non-finite inputs, matching the
source choice of an unspecified exponent without introducing undef.

`frexp` has result `{FP, Int}` with independent overloads (`.f64.i32`, etc.);
`sincos` has result `{FP, FP}` with one floating overload. The pure
`GetFloatingIntrinsicReturnType` helper shares return-shape construction between
factories and the transform builder without creating a declaration during folding.

## Demanded classes

A demanded class mask is a set of observable result classes, not an additional
safety constraint or alias identity. Return `nofpclass` attributes and fast-math
flags supply the source masks. When a producer has one use, the analysis may
rewrite its operands; with multiple uses, it may replace only the requesting
use. This distinction preserves unrelated uses without a global solver.

Class constants retain NaN payloads for pure sign operations. Canonicalizing
operations may choose a quiet NaN. Empty demanded sets become poison, not eager
runtime checks. The C# flags representation is explicitly bounded to LLVM's
class mask at the operand entry point because C# enum complement does not have
LLVM's bitmask-enum complement semantics.

Function denormal input/output modes remain distinct. IEEE, preserve-sign,
positive-zero, and dynamic handling use the actual function attributes. A
single "flush mode" approximation is not used for the intrinsic transfers.

Telemetry follows the existing sink-neutral compiler metrics: queried/replaced
operands, recursion limits, excluded classes, class-constant folds, and inferred
fast-math flags. Function/source attribution comes from the enclosing InstCombine
pass and work item. Recording-disabled paths do not construct per-query traces.
