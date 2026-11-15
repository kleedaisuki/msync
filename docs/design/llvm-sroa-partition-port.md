# SROA presplitting and partition rewriting

This component translates `Scalar/SROA.cpp` at LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`. The source and license are retained
under `vendor/llvm-reference/llvm/`. The parent [SROA note](llvm-sroa-port.md)
records the shared data/driver and concrete target representation dependencies.

## Owned source paths

| Upstream component | Managed component |
| --- | --- |
| `SliceSplitter` and `findOverlappingCopySlice` | `LlvmSroa.Splitter.cs` |
| `presplitLoadsAndStores` | `LlvmSroa.Presplit.cs` |
| `tryCanonicalizeStructToVector`, `selectPartitionType` | `LlvmSroa.PartitionType.cs` |
| `SROA::rewritePartition` | `LlvmSroa.PartitionRewrite.cs` |

The coherent files are initially staged in `.temp/llvm-sroa/`; they are not a
product dependency or an alternative execution route. They use the central
`LlvmSroa.State`, source byte `Slice`/`Partition` views and stable ordered
worklists. No Native-format or source-language type is introduced.

## Presplit invariants

Candidate discovery scans every partition in source order. A splittable access
is a candidate only if it extends past that partition or belongs to an
overlapping load/store copy. Non-simple stored loads, non-store users and
unsplittable peers propagate into the same exclusion set used upstream. Both
passes filtering stores are retained: a later mismatching store can exclude the
load used by an earlier store. Exact relative split vectors must match.

Overlapping copies use the original source construction of split points. Small
overlap creates two boundary points; equal overlap splits in the middle; larger
overlap repeatedly peels equal non-overlapping fragments from both ends. The
points are sorted, not approximated by declared field boundaries.

Rewriting first creates all split loads, then their non-deferred stores, and
only afterward rewrites candidate stores. Deferred stores reuse the exact split
load vector. Other stores cause their source loads to be split separately. Old
instructions remain in the source weak-handle deletion worklist while all
original users are traversed. Slices are killed, the new slices are stably
merged, and affected direct allocas are removed from the promotable set and
requeued. This preserves the memory-copy direction of partial overlap; it does
not turn an overlapping copy into an interleaved load/store sequence.

## Partition choice and promotion

Partition type selection retains upstream ordering: preferred floating vector,
common full-span use type, a subtype of the original alloca, integer widening,
viable vector, largest spanning integer, optional homogeneous-struct vector,
legal integer, and finally byte array. The `AggregateToVector` option only gates
its original source fallback. There is no Native-ABI width filter: an i128
partition remains an i128 partition, not a Number/double reinterpretation.

A matching type at offset zero reuses the original alloca but still runs the
rewriter and PHI/select processing. Other partitions create a correctly aligned
alloca before the original, then use the actual shared alignment enforcement
routine. The rewriter processes split tails as well as the direct slice range.
Tree-structured merges, PHI-load speculation, PHI-store predication and select
rewrites feed the source promotion/requeue decision. Failed promotion discards
only newly appended post-promotion work, while a newly created partition remains
eligible for another refinement iteration.

Source debug assignment fragments, alias metadata and parallel-access metadata
have no represented carrier in the current scalar IR. These components do not
fabricate metadata or reinterpret a missing debug record as a runtime effect.
Memory and lifetime identities use the standard intrinsic descriptor/facade,
not ordinary symbol-name matching.

## Observability and checks

Actual presplit and rewrite-partition spans carry function, alloca and byte
partition context. Counters distinguish discovered candidates, exclusion causes,
relative split mismatches, deferred stores, loads/stores emitted, requeued
allocas, source type-choice branches, promotable outcomes and unchanged
partitions. Maximum partition use count is recorded directly. Recording-disabled
paths do not allocate per-partition attribute dictionaries or type-label strings.

`SroaPartitionChecks.cs` is staged separately under `.temp/llvm-sroa/tests/`.
It constructs the actual `[12 x i8]` overlapping-copy graph, runs public SROA,
then source SCCP/DCE, and requires the resulting SSA value to be three for both
little and big endian layouts. The graph's three original i32 values `[1,2,3]`
become `[1,1,2]` after copying eight bytes from offset zero to offset four. The
check also requires the memory accesses to disappear after promotion. This
fixture has not yet been built or executed: root owns the coherent publication
and serialized build window, including the wide-integer target dependency.
