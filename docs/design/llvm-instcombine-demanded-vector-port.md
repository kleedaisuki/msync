# InstCombine demanded vector elements and vector binops

## Source and execution ownership

This component follows LLVM commit
[`a55e64432a516fd6507d3ecfba7407cfe425f940`](https://github.com/llvm/llvm-project/tree/a55e64432a516fd6507d3ecfba7407cfe425f940).
The source implementations are `InstCombineSimplifyDemanded.cpp`'s
`SimplifyDemandedVectorElts` and insertion-chain helper,
`InstructionCombining.cpp`'s `foldVectorBinop`, `foldSpliceBinOp`,
`unshuffleConstant` and constant-splat folding, plus
`InstCombiner.h::getSafeVectorConstantForBinop`.

All transforms run in the existing `LlvmInstCombine.Work` instance. They reuse
its worklist, DT, TargetFolder builder, mutation helpers and function-owned
telemetry. There is no secondary optimizer, vector execution interpreter or
runtime check. `Range` remains an ordinary builtin object and is unrelated to
these LLVM vector lane masks.

## Demanded elements

`SimplifyDemandedVectorElts(value, demanded, ref poison, depth = 0,
allowMultipleUsers = false)` retains the source return contract: null for no
rewrite, the original instruction after operand changes, or a replacement value.
The poison mask is analysis output even when there is no rewrite. The recursive
operand helper updates actual SSA Uses through the shared mutation path.

The source paths include:

- Fixed-vector constant lane poisoning, whole poison/undemanded vectors, the
  source depth limit and root/non-root multiple-use handling. Scalable-vector
  analysis returns unchanged at the same upstream boundary.
- Struct-index restrictions and vector operand poison propagation for GEPs.
- Insertelement demand removal, matching extract reuse and the bounded distinct
  insertion-chain shortcut. Its seen-lane mask is the existing APInt carrier.
- Shuffle splats, identity-on-demand, operand demands, discovered poison masks
  and conversion of a single constant-source lane into insertelement.
- Vector and scalar select conditions, lane-ratio bitcasts, FP extensions and
  truncations, elementwise standard intrinsic calls and masked load/gather.
- Dominating shuffled-binop reuse, instruction flag intersection and the
  original integer div/rem/shift exclusions.

Masked pointer demand follows the upstream mask, not the result demand:
removing an undemanded result lane must not remove a pointer access required by
an enabled load lane. The masked intrinsic signatures use three operands at
this pin: pointer(s), mask and passthrough; alignment is not an extra operand.
Elementwise scalar arguments follow the `VectorUtils.cpp` cases in the already
selected trivially-vectorizable domain. Poison propagation uses the actual
intrinsic descriptor, not a blanket property of pure calls.

## Vector binops

`FoldVectorBinop` preserves the source ordering: constant splats through
vector.insert, ordinary reverse, masked VP reverse, left/right splice, the
source speculative-execution query, shuffle motion, select-shuffle cancellation,
constant unshuffling and associative splat reassociation.

Reverse/splice and masked operations use real standard intrinsic identities
and factories. The six registry additions are supplied by the shared Core
integration owner in `LlvmIntrinsic.VectorFamilies.cs`. `GetSplatValue` and
`IsSplatValue` are the separate actual source queries in
`LlvmValueTracking.VectorSplat.cs`; a splat-extraction query is not substituted
for the recursive splat predicate.

Unshuffling preserves poison in unconstrained lanes, rejects conflicting
many-to-one constants and retains the source scalable-vector splat path. For
operations whose undefined constant lanes could introduce UB, the source
identity/absorber constants replace those lanes. FMF and poison-generating
flags follow each original source rewrite rather than a common invented rule.

## Observability and verification

The `demanded_elements.*` and `vector_binop.*` counters report actual queries,
rewritten operands/constants, depth and multiple-use outcomes, shuffle/insert
changes and the concrete binop rewrite families. They are recorded under the
existing function/source owner; disabled recording allocates no counter map.

The root coordinator owns builds and focused graph/execution journeys. Publishing
these source files is not itself a build or execution receipt. Target-specific
intrinsic callbacks remain outside the current standard-intrinsic registry; no
constant-false implementation of a target hook is installed.

## Integer packing and bit-order recognition

`LlvmInstCombine.AndOrXor.IntegerPack.cs` follows pinned
`InstCombineAndOrXor.cpp:3744–3877`: combine scalar extract/zero-extend/shift
pieces, or bitcast vector shuffle/zero insertion pieces, into one vector shuffle
and bitcast. The shared mask retains contributions from both operands and uses
the module's endianness for scalar packing positions.

`LlvmLocal.BitParts.cs` ports `Utils/Local.cpp:3568–3954` and
`LlvmInstCombine.AndOrXor.BitOrder.cs` ports the source consumer at lines
3010–3026. A per-query value cache records bit provenance, including failed
subexpressions; arrays are copied when a source optional value is copied, so
masking one use cannot modify another cached use. Source recursion (48) and
scalar-element width (128) limits are retained. Successful recognition creates
standard `llvm.bswap` or `llvm.bitreverse` calls, with the source's narrowing,
masking and extension sequence. Optional counters cover queries, cache hits,
visits, provenance conflicts and emitted operations without creating a metrics
map when recording is disabled.

`InstCombineBitOrderProperties.Run()` covers real dynamic-provider byte and bit
permutations through the shared recognizer and adjacent vector-byte packing
through the public InstCombine pipeline. Execution belongs to the root's
serialized build/test window; publication itself is not an execution result.
