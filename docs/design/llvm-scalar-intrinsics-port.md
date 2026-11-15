# Scalar bit-count, funnel-shift and three-way comparison intrinsics

## Source and representation

The implementation follows LLVM commit
[`a55e64432a516fd6507d3ecfba7407cfe425f940`](https://github.com/llvm/llvm-project/tree/a55e64432a516fd6507d3ecfba7407cfe425f940).
The exact definitions are in
[`Intrinsics.td`](https://github.com/llvm/llvm-project/blob/a55e64432a516fd6507d3ecfba7407cfe425f940/llvm/include/llvm/IR/Intrinsics.td):

| Intrinsic | Scalar signature | Overloaded name | Distinct source properties |
| --- | --- | --- | --- |
| `fshl` | `iN (iN, iN, iN)` | `llvm.fshl.iN` | Shift count modulo N; no newly created undef/poison |
| `ctpop` | `iN (iN)` | `llvm.ctpop.iN` | No newly created undef/poison |
| `cttz` | `iN (iN, i1)` | `llvm.cttz.iN` | Argument 1 is immediate; true makes zero input poison; **not** `nocreateundeforpoison` |
| `scmp`, `ucmp` | `iR (iN, iN)` | `llvm.scmp.iR.iN`, `llvm.ucmp.iR.iN` | Independent result and operand overloads; return range [-1, 2) |

These remain ordinary LLVM Call instructions with source semantic string identities,
not fabricated TableGen numeric IDs, project opcodes, native function names or
language syntax. The represented registry remains scalar; this change does not
claim vector intrinsic overloads or vector constant construction. Comparison
results must have at least two bits, as required by upstream `Verifier.cpp`.
There is no source language Range handling in this component. LLVM integer
`ConstantRange` is an analysis interval, not a language Range object.

The default builder creates a real call, matching the source default
`ConstantFolder::FoldIntrinsic`; it does not secretly execute a new simplifier.
`GetDeclaration(module, kind, resultType, argumentType)` handles independent
comparison overloads. Existing min/max callers keep their original three-argument
shape and module declaration reuse. `CreateIntrinsic` can infer the result type
from argument zero, or accept an explicit result integer type.

## Source correspondence

| Product path | Upstream source mechanisms |
| --- | --- |
| `Core/Llvm/LlvmIntrinsic.cs` | `Intrinsics.td`, `Intrinsics.cpp::getOrInsertDeclaration`, `IRBuilder` intrinsic call construction |
| `Optimization/Llvm/LlvmConstantFolding.Intrinsic.cs` | `ConstantFolding.cpp::ConstantFoldScalarCall`, `ConstantFoldScalarCall1`, `ConstantFoldIntrinsicCall2`, `ConstantFoldScalarCall3` |
| `Optimization/Llvm/LlvmInstructionSimplify.ScalarIntrinsics.cs` | `InstructionSimplify.cpp::simplifyIntrinsic`, represented `ctpop`, `cttz`, `scmp`, `ucmp`, `fshl` branches |
| `Optimization/Llvm/LlvmConstantRange.BitCount.cs` | `ConstantRange.cpp::cttz`, `ctpop`, `getUnsignedCountTrailingZerosRange`, `getUnsignedPopCountRange` |
| `Optimization/Llvm/LlvmConstantRange.Intrinsic.cs` | `ConstantRange.cpp::isIntrinsicSupported`, `intrinsic` dispatch |
| `Optimization/Llvm/LlvmConstantRangeAnalysis.cs` | `ValueTracking.cpp::getRangeForIntrinsic` and call return range attribute consumption |
| `Optimization/Llvm/LlvmSccpSolver.Intrinsic.cs` | `SCCPSolver.cpp::handleCallResult` interval transfers and `handleCallOverdefined` constant-argument call folding |

Poison propagation precedes undef-specific folds, as it does upstream. In
particular `ctpop(undef)` selects zero; `cttz(undef, true)` is poison and
`cttz(undef, false)` selects zero. A funnel shift with an undefined shift selects
its first input, both undefined inputs stay undefined, and a constant shift is
reduced modulo width before combining limbs. The implementation uses the
existing arbitrary-width APInt operations, not host fixed-width arithmetic.

The new call arities require existing min/max-only consumers to test the actual
intrinsic kind before reading argument 1 or asking for min/max predicates.
This applies to PHI recurrence matching, shared min/max pattern folding and
constant-range analysis. Unary bit-count calls must not be mistaken for min/max.

## Native target

The existing target has a Boolean `i1` ABI and source Number is binary64.
The target therefore lowers `fshl.i1` and `ctpop.i1` to existing typed Copy,
`cttz.i1(x, false)` to Boolean Not, and `cttz.i1(x, true)` to false. The last
mapping returns the sole defined result when x is true; zero is poison, so it
adds no runtime check. These are target operation identities, not additional
optimization passes. Existing min/max lowering is unchanged.

General `iN` target slots, including the at-least-two-bit result of `scmp` and
`ucmp`, are not represented by this existing Native ABI. Those limitations remain
explicit rather than reinterpreting integers as binary64 or claiming an
unsupported call is executable. No LLVM backend or pipeline is adopted.

## Focused execution and observability

`tests/LlvmIr/ScalarIntrinsicChecks.cs` covers canonical declarations and two
independent overload types, exact source attributes, undef/poison behavior,
exhaustive small-width scalar folding, interval-transfer containment and
min/max/unary consumer interactions. `ScalarIntrinsicExecution.RunAsync` constructs
real `i1` call graphs and executes raw and InstSimplify forms through the existing
Native target and detached Native2 artifact roundtrip with public InvokeAsync.
The coordinator owns builds and execution; the presence of these fixtures is not
an execution receipt.

Existing sink-neutral counters record intrinsic declaration queries/reuse,
concrete folds and simplification reasons, interval transfers and SCCP constant
call outcomes. Native lowering records actual intrinsic mapping sites under the
existing function/source owner. Recording-disabled paths create no counter maps.

## InstCombine integer extension work

The next integer tranche is staged in `.temp/llvm-integer-intrinsics/` while
FP and memory owners provide their source family dispatches. It adds independent
partials for actual `abs`, `ctlz`, `fshr`, bit permutations, saturation,
overflow aggregates and high multiplication; it does not replace the source
algorithms with generic native calls. The central descriptor now has source
property fields rather than a growing list of inferred name rules. The staged
APInt byte swap and reverse use the exact upstream multiword algorithms and
portable bit-reversal table, with platform byte swapping for host words.

`LlvmIntrinsic.Vectorizable.cs` is already published: it ports
`VectorUtils.cpp::isTriviallyVectorizable`'s actual semantic-ID switch, not an
assumption that every pure intrinsic is vectorizable. The canonical string is
`LlvmIntrinsicKind`'s identity; it is never an ordinary source function name.
`LlvmIntrinsicDescriptor.IsCommutative` likewise follows
`IntrinsicInst.h::isCommutative` for the represented descriptors.

Demanded-bit simplification is staged separately under
`.temp/llvm-instcombine/LlvmInstCombine.DemandedBits*.cs`; source methods preserve
single-use mutation versus multiple-use replacement, the six-level analysis
limit, exact flag removal and insertion-point guards. The staged family is not
an execution receipt or a claim that the full InstCombine pass is integrated.
