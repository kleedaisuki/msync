# Shared LLVM CFG utility translation

Pinned source is LLVM `a55e64432a516fd6507d3ecfba7407cfe425f940`, with the
Apache-2.0 WITH LLVM-exception license retained in the reference tree and staged
translation files. The initial aggregate-memory-owned cohort is staged under
`.temp/llvm-cfg-utils/`; it is not an invented CFG cleanup algorithm.

| Upstream method | Managed source translation |
| --- | --- |
| `BasicBlock.cpp` predecessor/successor queries, `removePredecessor` (434–460) | Edge-count-sensitive single predecessor, duplicate-edge-tolerant unique predecessor/successor, PHI incoming removal, optional one-input preservation and constant PHI replacement |
| `BasicBlockUtils.cpp` `emptyAndDetachBlock`, `detachDeadBlocks`, `DeleteDeadBlock(s)` (62–172) | Remove successor PHI inputs once per CFG edge; unique DT edge deletion; poison dead instruction uses; leave unreachable before applying real DTU updates; defer actual deletion to DTU where supplied |
| `BasicBlockUtils.cpp::FoldSingleEntryPHINodes` (195–213) | Replace leading PHIs by first incoming value, or poison for self input, then erase |
| `BasicBlockUtils.cpp::MergeBlockIntoPredecessor` (237–402) | Unique-predecessor legality, special/side-effecting terminator rejection, two-successor conditional mode, PHI-loop rejection, PHI folding, definition movement, predecessor replacement, terminator movement, DT/DTU/LI/MemorySSA observer updates and old block deletion |
| `Local.cpp::EliminateDuplicatePHINodes` (1431–1556) | Exact ordered incoming values/blocks comparison; <=32-PHI triangular search, larger-PHI hash set; intersect IR flags, RAUW, restart after changed keys, delayed PHI destruction |

The merge DTU list intentionally adds inserted edges before removed edges, as
upstream does to avoid transient unreachability and pathological update cost.
Direct DT mode reparents the merged node's children and erases its node; it does
not rebuild or claim the tree preserved. MemorySSA receives its existing
MoveAllAfterMergeBlocks and moved-terminator MoveToPlace operations. LoopInfo
removes the merged block using its actual mutation API.

Managed adaptations are ordered intrusive instruction moves with the existing
parent/symbol hooks, GC-backed block destruction after references are dropped,
and reference-identity PHI hashing in place of LLVM pointer hashes. Source and
logical-use annotations follow the existing instruction movement and RAUW
contracts. Caller phases own timing and source/function attribution; helper work
counts report removed blocks/instructions, PHI changes/comparisons/hashes, merge
attempts and moved definitions. No DT/MemorySSA recomputation or per-caller heap
snapshot is substituted for source updates.

## Explicit partial coverage

The current IR does not represent BlockAddress constants, funclet pad/return
classes or convergence entry/loop intrinsic descriptors. Their corresponding
source address-taken, EH co-deletion and dual-convergence-token branches are not
claimed implemented or replaced by name matching. MemoryDependenceResults and
AssertingVH diagnostics are also not represented; their optional cache/debug
observers are not a homemade replacement analysis. The represented branch,
switch, return, PHI and memory operations use the actual source algorithms.
Local terminator folding/empty-block handling and critical-edge/LCSSA utilities
have separate owners sharing this public utility layer and true DomTreeUpdater.
