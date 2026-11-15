# InstCombine GEP source family

Reference: LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`.
The component is the actual `InstructionCombining.cpp::visitGetElementPtrInst`
family and its source helpers, not a generic pointer-folding substitute.
Root and the InstCombine integration lead own whole-pass publication, build and
real source/artifact execution. The family itself does not select another
optimizer, backend or pass manager.

## Source correspondence and order

| Managed component | Pinned source |
| --- | --- |
| `LlvmInstCombine.Gep.cs` | `InstructionCombining.cpp:3392–3725`, main GEP visitor |
| `.Gep.Helpers.cs` | `shouldMergeGEPs`, `foldSelectGEP`, `canonicalizeGEPOfConstGEPI8`, `combineConstantOffsets`, `shouldCanonicalizeGEPToPtrAdd`, `foldGEPOfPhi` |
| `.Gep.Combine.cs` | `visitGEPOfGEP` |
| `.Gep.Offsets.cs` | `EmitGEPOffset`, `EmitGEPOffsets` |
| `LlvmGepUtils.cs` | `llvm/lib/Analysis/Local.cpp::emitGEPOffset` |
| `LlvmCommonPointerBase.cs` | Shared `InstCombineAddSub.cpp::CommonPointerBase::compute/isExpensive` |
| Core `LlvmGepOperator.Alignment.cs` | `Operator.cpp::getMaxPreservedAlignment`, using the source maximum alignment, struct offsets and worst-case sequential displacements |
| `LlvmValueTracking.VectorSplat.cs` | `VectorUtils.cpp::getSplatValue/isSplatValue`, including their different semantics |

The main visitor preserves the source ordering:

1. Run the GEP simplifier with the actual shared query, then demanded-element
   simplification for fixed-vector results.
2. Normalize sequential index widths and zero-size element displacements;
   preserve the source nneg/nuw/nsw derivation on the created casts.
3. Canonicalize constant offsets, multiply/shift/scalable offsets and structured
   element types through the real builder.
4. Scalarize vector splats, fold PHI-fed GEPs, and combine nested GEPs.
5. Cancel/fold exact index scaling and replace eligible address-only uses of a
   pointer-difference index.
6. Derive the source inbounds/nuw flags, split eligible add/sext-add offsets,
   thread constant GEPs through selects, and rewrite the specified power-of-two
   signed-remainder index to unsigned remainder.

No source family is disabled behind a fake false/null result. The fixed-vector
step calls the shared real demanded-vector component, and splat detection uses
the source two-pattern `getSplatValue` utility. `isSplatValue` is independently
ported with its specified-element behavior, shuffle handling and original
analysis recursion limit; it is not an alias for the narrower extraction query.

## State and target boundary

The existing `LlvmGepOperator` is a typed view over an instruction or constant
expression; it is not another IR value or opcode. Offset arithmetic uses the
actual DataLayout index width, struct offsets, sequential stride and vscale
construction. The shared offset walk is reused rather than adding a second
layout or alias solver. GEP-chain rewrite paths retain the original insertion
point/source context with `InsertPointGuard` and maintain the common InstCombine
worklist/use-count protocol.

Constant-expression and scalar/splat APInt patterns retain the source flag
merging rules. Newly created PHIs copy the relevant operand-role Native ABI
annotations, while cloned GEPs keep the original annotation. Canonical constants
are not mutated. Final replacement carrier selection remains owned by the
existing common target boundary, not by an identity constraint added to these
transforms.

The pointer-difference rule affects only the source-specified PtrToAddr/ICmp
uses and eligible PtrToInt uses. It does not replace unrelated pointer uses.
Dereferenceable-byte queries and flag derivation are optional source optimizer
decisions; no runtime check, heap admission phase or new language rule is added.
Range remains an ordinary builtin object and receives no GEP-specific rule.

## Observability and focused execution

The existing InstCombine visit span supplies source/function/opcode context.
Family counters identify actual normalization, offset canonicalization,
chaining, PHI/select folds, exact-scale rewrites, address-use replacement,
flag derivation and remainder rewrites. The shared offset utility records
visited indices, struct offsets and scaled offsets. Splat utilities distinguish
constant, broadcast, non-splat and depth outcomes. These use the caller's
sink-neutral metrics dictionary; recording-disabled paths create no diagnostic
dictionaries or per-transform timers.

`InstCombineGepProperties` additionally checks struct-offset canonicalization,
exact-scale cancellation, independent splat-query semantics, preserved alignment,
and the source scalar-index requirement in constant-offset accumulation. The
latter requirement matters because ConstantInt can itself hold a vector splat:
the pinned `Operator.cpp` explicitly tests `Type.IsIntegerTy()` before treating
that constant as one scalar displacement. Both the byte fast path and the
general sequential path now retain that source condition.

`InstCombineGepJourney` exercises four real source cases: captured mixed-layout
fields, a first-field Function value, nested shared fields, and a Range of shared
Scope objects mutated in a loop. Each has a known source result and runs with
raw lowering and mem2reg plus full InstCombine, then executes both the emitted
Native program and detached Native2 serialization. The fixture also retains
before/after LLVM text. It snapshots the initially defined functions because
intrinsic declarations may be appended during the pass. No timing measurement
or separate worker build is part of this family; execution receipts are
root-owned and publication alone is not a passing result.
