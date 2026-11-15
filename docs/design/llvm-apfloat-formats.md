# Exact one-word LLVM floating semantics

The managed LLVM numeric carrier uses the source algorithms pinned at
`a55e64432a516fd6507d3ecfba7407cfe425f940`. It does not call an LLVM runtime
library, use the LLVM backend, or introduce a new source-language number kind.
Native `Number` and its artifact ABI remain IEEE binary64.

## Representation and source correspondence

`LlvmAPFloat` retains the raw bits together with the source semantics ID. Its
CLR-default value remains binary64 positive zero. Equality and constant interning
include the semantics: binary16 and BFloat values with identical raw bits are
not the same constant. The public APInt bitcast has the actual format width.

| Semantics | Storage bits | Precision | Normal exponent range |
| --- | ---: | ---: | ---: |
| IEEEhalf | 16 | 11 | -14 .. 15 |
| BFloat | 16 | 8 | -126 .. 127 |
| IEEEsingle | 32 | 24 | -126 .. 127 |
| IEEEdouble | 64 | 53 | -1022 .. 1023 |

Source files are under `vendor/llvm-reference/llvm/`. The relevant source map is:

| Managed implementation | Pinned upstream implementation |
| --- | --- |
| Formats, pack/unpack | `lib/Support/APFloat.cpp` semantics definitions and `initFromIEEEAPInt` / `convertIEEEFloatToAPInt` |
| `Convert` | `IEEEFloat::convert`, lines 2554–2704 |
| `Next` | `IEEEFloat::next`, lines 4533–4664 |
| `Scalbn` | `scalbn(IEEEFloat, ...)`, lines 4738–4756 |
| `GetExactInverse` | `APFloat::getExactInverse`, lines 5987–6043 |
| Fused multiply-add | `IEEEFloat::fusedMultiplyAdd`, lines 2347–2389; `multiplySignificand(addend)`, lines 1310–1434 |
| Six min/max variants | `include/llvm/ADT/APFloat.h`, lines 1752–1836 |
| Format losslessness | `APFloatBase::isLosslesslyConvertibleTo`, lines 238–312 |
| Integer predicate | `IEEEFloat::isInteger`, lines 1194–1201 |
| Text bit-string fallback | `lib/IR/AsmWriter.cpp::writeAPFloatInternal`, lines 1594–1651 |

The existing one-word arithmetic engine now unpacks and packs the selected
semantics instead of assuming double. FMA uses an APInt temporary holding the
source's `2 * precision + 1` significand and rounds only once. Ordinary arithmetic
continues to use its existing one-word path. No product operation rounds through
host `float`, `double`, `Half`, or decimal arithmetic.

`Category.Normal` is the source finite-nonzero storage category and includes
subnormals. Public `IsNormal` follows upstream: finite nonzero **and not**
`IsDenormal`. Exact inverse uses the source power-of-two/exponent construction
and excludes denormal inputs and denormal inverse results.

## API contracts

- Factory methods take `LlvmAPFloatSemantics`; optional sign arguments preserve
  signed zero and infinity.
- `Convert(semantics, rounding)` returns `Value`, `Status`, and `LosesInfo`.
  NaN payload loss and signaling-NaN invalid status remain separate, as in source.
- `Next(down)` and `Scalbn(exponent, rounding)` return immutable value/status
  pairs; `GetExactInverse(out inverse)` is the source boolean consumer adapter.
- `FromAPInt(semantics, integer, isSigned, rounding)` selects the destination
  format. The existing overload without semantics remains binary64.
- `GetFltSemantics()` maps actual scalar LLVM half/BFloat/float/double types.
- `IsLosslesslyConvertibleTo` checks normalized exponent range, precision, and
  the actual smallest subnormal conversion. Distinct IEEE formats are only
  accepted when NaN representation changes may be ignored: source conversion
  quiets signaling NaNs.

`ConstantFP` chooses the scalar type from these semantics, and typed construction
requires matching semantics. Vector splats remain the ConstantFP carrier owned
by the constant-vector implementation, not a second floating representation.
The writer can use upstream's exact `f0x...` fallback directly, avoiding a host
widening conversion that would quiet signaling NaNs.

## Integration and focused properties

The staged `ApFloatFormatProperties.Run()` integrates into the existing numeric
property harness through its `Program.Require`. It covers all 65,536 binary16
and BFloat bit patterns through single extension/narrowing, signaling and quiet
NaN statuses, signed-zero/infinity boundaries, format-specific inverse rules,
rounding ties, and full-precision FMA cancellation. Independent host IEEE
conversion/FMA is used only as a test oracle, never as product implementation.
The root integration owner runs the shared build and property harness once the
cohort is published; the numeric worker does not launch competing MSBuild jobs.
Numeric operations retain existing caller-owned fold/transform telemetry rather
than adding a separate sink or timing layer to immutable values.

## Remaining representation work

This is the one-word IEEE tranche, not a claim that every upstream floating
semantics is complete. x87 extended, IEEE quad, PPC double-double, and newer
small finite-only formats require their actual source representations and
special-value rules. They must not be mapped to binary64 or accepted by a
hardcoded all-64-bit fallback. Extending those formats requires a coherent
multiword public carrier before consumers can claim support.
