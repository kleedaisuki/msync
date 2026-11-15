# Source-derived LLVM transform builder

`Msync.Optimization.Llvm.LlvmTransformBuilder` adapts
[`IRBuilder.h`](../../vendor/llvm-reference/llvm/include/llvm/IR/IRBuilder.h),
[`IRBuilder.cpp`](../../vendor/llvm-reference/llvm/lib/IR/IRBuilder.cpp) and
[`ConstantFolder.h`](../../vendor/llvm-reference/llvm/include/llvm/IR/ConstantFolder.h)
at LLVM revision `a55e64432a516fd6507d3ecfba7407cfe425f940`. It is shared
instruction-construction infrastructure for translated transforms, not an
optimizer, LLVM backend or additional IR pipeline. It lives in Optimization
because Core must not depend on the constant-folding implementation.

## Ownership and insertion

Construct with an instruction to insert before, or a block to append to. The
explicit context/layout constructor also supports detached construction.
`SetInsertPoint(instruction)` adopts its SourceSpan (the represented debug-location
adapter); block-end insertion preserves the current source. Insertion assigns a
name and uses the current source if present, otherwise retaining an instruction's
existing source. Saved insertion points retain block and before-instruction
anchors. An instruction anchor follows its current parent when a split moves
it; only a block-end insertion point retains the explicit block. Clearing the
insertion point creates detached instructions, as the
source default inserter does.

`Insert` and an optional caller `onInsert` callback run only for instructions.
Constants returned by folding are never inserted, renamed or annotated. In
particular, a canonical constant's `LogicalType` is not a builder scratch field.
Logical result and operand-use ABI annotations remain the transform caller's
responsibility; the callback can attach them to real instructions/uses without
mutating shared constants. The builder does not assume that an operation's
result has its source operand's logical ABI.

## Source correspondence

| Adapter path | Upstream mechanism | Folding condition |
| --- | --- | --- |
| `CreateBinary` / `CreateBinOp` and arithmetic wrappers | CreateBinOpFMF / no-wrap/exact constructors; FoldBinOp | Both operands constant |
| `CreateFNeg` | CreateFNegFMF; FoldUnOpFMF | Constant operand |
| `CreateICmp` / `CreateFCmp` | CreateICmp / CreateFCmpHelper; FoldCmp | Both operands constant |
| `CreateSelect` | CreateSelectFMF; FoldSelect | Condition **and both arms** constant |
| `CreateLogicalAnd` / `CreateLogicalOr` | Poison-blocking select construction | Same select-folder rules |
| `CreateNot` | XOR with an all-ones constant | Same binary-folder rules |
| `CreateCast` and integer-width helpers | CreateCast; FoldCast | Identity type returns operand; otherwise constant operand |
| `CreateGep` / pointer-add helpers | CreateGEP / CreatePtrAdd / CreateScaledPtrAdd; FoldGEP | Constant pointer and all constant indices, within the existing represented folder |
| `CreateExtractValue` / `CreateInsertValue` | FoldExtractValue / FoldInsertValue | Aggregate, and inserted value when applicable, constant |
| `CreateLoad` / `CreateStore` | CreateAlignedLoad / CreateAlignedStore | **No builder constant folding**; absent alignment uses DataLayout ABI alignment |
| `CreateFreeze`, PHI and terminators | Direct IRBuilder insertion | No folder |
| `CreateBinaryIntrinsic` | CreateBinaryIntrinsic; default FoldIntrinsic returns null | Creates the actual registered intrinsic call |

The default strategy is `ConstantFolder`, using existing source-translated
`LlvmConstantFolding` functions. A caller whose upstream code explicitly uses
`IRBuilder<NoFolder>` selects `foldConstants: false`; this skips folder requests,
not builder-level identity casts or the builder's construction rules. Neither
strategy invokes InstructionSimplify, algebraic identities, dominance queries or
a custom simplifier. Floating math flags apply only to source floating math
operators. Unrepresented LLVM metadata/constrained-FP intrinsic families are not
synthesized.

## Pointer canonicalization

The pinned IRBuilder defaults `CanonicalizeGEPs` to true. The adapter follows its
scalar algorithm: constant struct offsets and scaled constant sequential indices
become byte GEPs, dynamic sequential indices are sign-extended/truncated to the
index width and use the source scaled pointer-add array GEP, and multiple indices
produce a chain of pointer additions. A nonconstant base with all-zero indices
still gets at least one GEP. No-wrap flags follow each source pointer addition.
`DisableGEPCanonicalization()` exposes the upstream opt-out; it is not an
execution prerequisite or a replacement for target pointer arithmetic support.
Scalable-vscale and vector-splat construction have no current represented helper
and are explicitly outside this scalar adapter.

The target lowerer must consume these actual pointer operations using producer
DataLayout and logical ABI information. The builder does not replace canonical
pointer arithmetic with runtime layout proofs, heap identity checks or bespoke
object accesses. Core's current constant representation also has no general
ConstantExpr: an existing folder's unsupported ConstantExpr opportunity remains
an actual instruction, rather than a fabricated folded value.

## Observability and focused evidence

Caller-provided metrics count real folder requests, folded results, inserted
opcodes and GEP construction decisions. Existing constant-folding counters remain
visible in the same dictionary. Disabled recording creates no telemetry
collection and does not construct opcode metric names. Source/function context
belongs to the surrounding transform observation and emitted instruction source.

`tests/LlvmIr/TransformBuilderProperties.cs` checks insertion order/source,
per-use versus constant annotations, ConstantFolder versus InstructionSimplify,
NoFolder, scalar/aggregate folds, no-load-fold behavior, ABI alignment, default
GEP canonicalization and its source opt-out, detached construction and restored
insertion points, including an anchor moved by block splitting. Builds and
execution are coordinator-owned.
