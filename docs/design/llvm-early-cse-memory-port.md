# EarlyCSE memory and invariant paths

The implementation follows LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`, primarily
[`EarlyCSE.cpp`](../../vendor/llvm-reference/llvm/lib/Transforms/Scalar/EarlyCSE.cpp).
The compiler selects the explicit MemorySSA configuration; the original
`Run(function, trace, tree)` overload retains upstream generation-only behavior.
This document describes the represented memory paths, not a claim that every
LLVM intrinsic/metadata kind or the complete default LLVM pipeline is available.

## Data and lifetime

The pass walks the dominator tree once with scoped available-value, load, call,
GEP and invariant tables. Each frame retains table insertion marks and a memory
generation; leaving a frame pops all five tables to their previous bindings.
Invariants are exact `MemoryLocation(pointer, size)` keys. There is no global
heap identity graph, overlap theorem or Range classification.

The MemorySSA version uses the source generation-equality fast path first. A
mismatch queries the real caching walker up to ScalarOptions.td's default cap of
500, then uses the later access's defining access. Clobber dominance determines
whether a definition can occur between the two accesses. Every erase updates
MemorySSA with `OptimizePhis=true`; a caller-owned graph is not disposed.

## Newly represented source paths

| Source anchor | Product owner | Behavior |
| --- | --- | --- |
| `ParseMemoryInst`, lines 672–805 | `LlvmEarlyCse.MemoryInst.cs` | Normal loads/stores use matching ID -1; memset/inline-memset use ID 0 and the source stripped destination. Memset has no typed access type. |
| `getMatchingValue`, lines 1110–1181 | `LlvmEarlyCse.Memory.cs` | A constant zero, nonvolatile memset can provide a typed null value to a sufficiently sized, fixed-size, unordered regular load. The source permits unordered atomic loads in this particular path. |
| `isOperatingOnInvariantMemAt`, lines 1032–1052 | `LlvmEarlyCse.Invariants.cs` | Current `!invariant.load` metadata succeeds immediately; otherwise an exact location's invariant-start generation must be no later than the available memory definition. |
| `processNode`, lines 1303–1361 and 1468–1478 | `LlvmEarlyCse.Invariants.cs`, main driver | Assume contributes its simple condition to the scoped value table. Used invariant-start tokens do not create an indefinite scope; unused tokens establish a scope once. Both variants bypass generic call processing as upstream requires. |
| `overridingStores`, lines 1215–1251 | `LlvmEarlyCse.MemoryInst.cs` | Earlier/later pointers, typed accesses, matching IDs and unordered state must match. Memsets do not acquire a fictitious access type to enable extra DSE. |
| Local.cpp `wouldInstructionBeTriviallyDead`, lines 470–491 | shared `LlvmDeadCodeElimination.cs` | Dead launder is removable. A lifetime marker is removable on poison or when every actual pointer Use belongs to a lifetime marker. |
| Local.cpp `combineMetadata`, invariant cases at lines 3045–3052 | `LlvmLocal.Metadata.cs` | A moving merged load retains invariant metadata only when both original paths carry it. A stationary kept load retains its original metadata. Actual SimplifyCFG hoist/sink and EarlyCSE load CSE sites call this helper. |

The memset length stays APInt. The host-size comparison uses its limited unsigned
value against a fixed `TypeSize`, preserving the source comparison even when the
length's active bits exceed 64. Scalable accesses, variable/nonzero fill bytes,
variable lengths, short fills, volatile fills/loads and ordered loads take the
source nonmatching path. This does not substitute Native `ClearMemory` for
`memset`: source fill instructions remain real fill instructions.

Lifetime and `fake.use` have **no special skip in EarlyCSE.cpp** at this pin.
Live markers follow their represented source memory effects. `fake.use` retains
its side effects, consumes `LastStore`, and advances generation; MSSA may still
bypass its inaccessible state for an ordinary load. This distinction is not a
custom marker policy. The source lifetime signature is one pointer argument,
not the historical size-plus-pointer signature.

`LlvmLoadInstruction.HasInvariantLoadMetadata` stores the standard empty
`!invariant.load` marker without a per-load dictionary. Clone preserves it.
The IR writer emits its standard attachment and one module-level empty metadata
node. No new opcode, source-language feature or artifact format is introduced.
The empty-node writer uses the only represented semantic metadata kind; future
metadata node representation must unify its numbering rather than assigning a
second independent `!0` namespace.

## Telemetry and execution

Pass-local counters distinguish invariant insertion/reuse, metadata success,
missing locations/scopes, scope generation mismatch, all actual memset
nonmatching causes, zero forwarding, memory matching-ID mismatch, marker DCE,
and invariant metadata drops during moving CSE. Existing rewrite events retain
function/block/instruction/source context. Disabled recording adds no dictionary
or global state. Core metadata storage and clone are pure data operations.

`tests/LlvmIr/EarlyCseInvariantProperties.cs` exercises these paths through the
actual pass, including dominator sibling scope restoration, finite/late/mismatched
invariant scopes, source atomicity, mixed output types, assume propagation,
lifetime/dead-launder decisions and fake-use boundaries. The class is published
without editing Program during root's integration window. The integration worker
did not build or run it; root owns serialized build and acceptance. Existing
`EarlyCseMemorySsaProperties` remains the accepted configuration control.

Unrepresented constrained-FP state, target-specific/masked load/store intrinsics,
guards, sideeffect/pseudoprobe/noalias-scope descriptors, AA metadata and other
semantic metadata kinds are not simulated with ordinary function-name checks.
