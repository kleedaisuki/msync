# LLVM TargetFolder and transform-builder strategy

Reference: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`,
`llvm/include/llvm/Analysis/TargetFolder.h`, `IR/IRBuilder.h` and
`llvm/lib/IR/IRBuilder.cpp`. The target folder is a distinct strategy, not a name
for ConstantFolder and not adoption of an LLVM backend or pass manager.

Publication status: the folder and builder strategy are published with the
ConstantExpr, numeric/vector and shared intrinsic-folding prerequisites. Root
owns the coherent build and acceptance receipt; publication alone is not an
execution result. The APInt bit-field helpers, Analysis bit-field folders and
insertion-point guard remain shared dependencies.

## Source behavior

`LlvmTargetFolder` owns the caller-supplied DataLayout reference. Desirable
constant-expression binary operations are created through the real Core
ConstantExpr factory and then passed to `ConstantFoldConstant`. Other binary
operations use the Analysis constant folder directly. Exact and nowrap variants
preserve the source flag dispatch; source literal folding itself is not replaced
with a custom overflow rule. FP folder entry points retain the source treatment
of FMF rather than inventing fast-math permission.

Comparisons use `ConstantFoldCompareInstOperands`, not the target-independent
`ConstantFoldCompareInstruction`. This matters for context-dependent denormals.
GEPs use the supported-element-type test, require constant pointer/indices, use
the DataLayout-aware ConstantExpr GEP factory and then fold the expression.
Aggregate extract/insert and select keep their source constant-operand gates.
Standard intrinsic folding is attempted only for constant operands and uses the
real intrinsic identity and Analysis constant-folding implementation.

Pointer cast helpers retain the source identical-type early return and otherwise
use the corresponding ConstantExpr factory before target folding. Constant
uniquing, RAUW and symbolic expressions belong to the shared Core/Analysis
component; this folder does not introduce a second expression cache.

## Builder integration and compatibility

The existing constructors gain an optional final `targetFolder` argument:

```csharp
var builder = new LlvmTransformBuilder(
    layout, metrics, onInsert,
    targetFolder: new LlvmTargetFolder(layout, metrics));
```

Existing callers retain their default ConstantFolder behavior. In particular,
default intrinsic construction emits calls rather than folding intrinsic calls.
The existing `foldConstants: false` mode continues to emit ordinary instructions.
The explicit TargetFolder strategy selects the source target-dependent folders.
No execution-time mode or admission check is introduced.

Builder intrinsic creation returns `LlvmValue`, matching the source API because
the result can be a folded constant. The static `LlvmIntrinsic.Create*` factories
remain call factories. `CreateBinaryIntrinsic` preserves the source declaration
creation before folding; generic `CreateIntrinsic` does not manufacture a detached
call before a successful fold. Consequently no abandoned call keeps phantom Use
edges alive. Only actual inserted instructions invoke the insertion callback.
Canonical constants are never assigned source or logical-ABI annotations.

`InsertPointGuard` restores both the insertion point and current source location.
It does not restore fast-math flags: the source has a separate guard for that
state. A private Negator builder can collect created instructions without
immediately enqueueing them in the InstCombine worklist.

## Bit-field dependencies

The target-specific `bitinsert` and `bitextract` folders translate the source
ConstantFolding.cpp routines. They preserve undefined-offset and out-of-range
poison, whole-base overwrite as a bitcast, undef refinement, and the source
ConstantByte-only partial-bit representation. They do not reinterpret a generic
pointer or an arbitrary aggregate as raw bits.

Core immutable `LlvmAPInt.InsertBits` and `ExtractBits` translate the source
APInt.cpp algorithms: whole-value replacement, single-word masks, aligned word
copies, cross-word extraction, and the source general insertion bit loop.
Mutation is represented by returning a new APInt, following the established
managed APInt ownership contract. BigInteger is used only by the focused test
oracle, never by product folding.

## Observability and acceptance

The folder records family queries, folds and non-fold outcomes in the existing
caller-owned metric dictionary. Shared constant folders report their concrete
decision paths. The builder records actual inserted opcodes and successful
constant returns; its existing caller supplies source/function/pass context.
Recording-disabled paths allocate no telemetry dictionaries.

`TargetFolderProperties` checks target versus default intrinsic behavior,
insertion-callback ownership, retained NoFolder behavior, the denormal-comparison
distinction, insertion-point/source restoration, and word-boundary bit-field
properties against an independent integer oracle. Root owns the coordinated
build and real source/Native artifact acceptance; this component does not launch
an independent MSBuild.

The vector factory/constant-expression paths are coordinated with the Core
vector-instruction/constant owners. Missing representations are not approximated
as scalar operations or advertised as completed vector folding.

## Shared intrinsic integration

The shared dispatcher now includes the represented integer families, floating
families and launder-invariant-group folder. The source
`canConstantFoldIntrinsic` switch runs before scalar poison/undef evaluation.
Absence of a context function means strictfp for this entry point. An actual
call passes its own attributes and caller to the scalar folder. The direct
TargetFolder context function affects the strictfp gate only, just as upstream:
it does not fabricate a Call or make context-dependent canonicalize folds.
Fixed-vector folding gathers each source lane, retaining scalar immediate
arguments for abs/ctlz/cttz/is.fpclass/powi, then invokes the same scalar folder.
Registered identities such as tan and atan2 are not automatically foldable:
the pinned switch intentionally excludes them.

Exact APFloat handles rounding, min/max, FMA, ldexp and powi. The source host-libm
paths use Linux glibc x64/arm64 fenv and errno with the actual host ABI constants;
only FE_INEXACT is ignored. Availability resolves the actual glibc libraries and
required fenv/errno exports once, not merely an OperatingSystem.IsLinux guess.
Other hosts leave these host-libm calls unfolded; the exact APFloat families
continue working. No managed Math replacement bypasses source exception behavior.
This host adapter is compiler infrastructure, not a target runtime dependency.

GEP canonical construction now retains zero vector indices when needed to splat
a scalar pointer. Scalable element strides construct the source vscale/multiply
sequence, preserving GEP nuw/nusw flags on the multiply, rather than rejecting
the now-represented operation. This is upstream IR construction, not a new pass.
