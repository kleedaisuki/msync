# LLVM CycleInfo: static cycle forest for represented CFGs

## Purpose and source

Modern DSE and execution-transfer queries need cycles including irreducible
regions, not only natural loops. `LlvmCycleInfo` is the LLVM IR specialization
of the **pinned** generic cycle algorithm at commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`. It does not delegate to LlvmLoopInfo,
replace cycles with SCCs, require alias analysis, or add an execution prerequisite.

Primary mappings:

- `llvm/include/llvm/ADT/GenericCycleInfo.h`: flat cycle records, membership,
  parent/depth/children, blocks/entries and public queries.
- `llvm/include/llvm/ADT/GenericCycleImpl.h:208–550`: computation records,
  tagLoopHeader, run, flatten and dfs; beginning-of-file exit/exiting-block
  queries and the source smallest-common-cycle parent walk.
- `llvm/include/llvm/IR/CycleInfo.h`, `llvm/lib/IR/CycleInfo.cpp` and
  `llvm/include/llvm/IR/SSAContext.h`: concrete LLVM IR specialization.
- `llvm/include/llvm/Analysis/CycleAnalysis.h` and
  `llvm/lib/Analysis/CycleAnalysis.cpp`: analysis function adapter/reference;
  no LLVM pass manager/backend/pipeline integration is implied.

Implementation is split into `LlvmCycleInfo.cs`, `.Discovery.cs` and `.Layout.cs`.
Product files live under `src/Msync.Optimization/Llvm`; focused checks are
`tests/LlvmIr/CycleInfoChecks.cs`. They construct actual typed IR CFGs rather than
mocking loop/cycle results. Root owns compilation and execution.

## Actual algorithm and ordering

The pinned source has the stable single-DFS header-chain algorithm credited to
Wei et al., *A New Algorithm for Identifying Loops in Decompilation*, SAS 2007.
This is not the historical union-find or SCC decomposition:

1. DFS starts at function entry and visits successors in **reverse terminator
   order**, matching the pinned legacy single-LIFO-stack ordering. Each visited
   block has a one-based path position and innermost-header chain. Retreating
   edges mark headers and weave chains; an edge entering a closed cycle records
   its non-header entry. Leaving a DFS frame weaves into the parent's chain.
2. Scan DFS preorder to number header ranks and count blocks owned directly by
   each cycle. Source sibling heads are prepended; their resulting ordering is
   retained, not sorted into function block order.
3. Euler-traverse the cycle forest to assign flat preorder indexes, parent,
   depth, descendant counts, own regions and inclusive block intervals. Fill
   innermost regions by scanning block preorder backward. Each in-cycle block
   is written once; descendants' intervals are shared by their ancestors.
4. Sort recorded reentries by source header block number and block DFS rank,
   deduplicate identical pairs, and append per-cycle entry lists after the
   Euler block tour, with the header first. These extra entries do not change
   membership intervals. Multiple entries mark an irreducible cycle; unlike
   LoopInfo, they are **not** reduced to natural-loop subsets.

Unreachable CFG components are not analyzed, as in source. The function adapter
expects a definition with a real entry block. No dominator tree, global
fixed-point solver or user-visible validity gate is required. Work is the source
DFS/header-chain processing, linear flattening, and reentry sort—not an extra
pairwise block-reachability or repeated full-block lookup. Existing function
block numbers and GetMaxBlockNumber size the state arrays directly.

## Representation and query contracts

The internal representation retains the source flat cycle array, descendant
counts, ordered block/entry slices, and innermost block-to-cycle map. A stable
`LlvmCycle` object is the managed identity view of a source CycleRef index; one
wrapper per flat index allows DSE's nullable-cycle identity comparisons without
inventing a different tree. Parent/depth/contains forward to those flat records.
No independently constructed mutable block set or per-query tree traversal is
used for containment: inclusive Euler intervals give O(1) cycle/block membership.

Public consumers use:

```csharp
var info = new LlvmCycleInfo(function, trace: trace, metrics: metrics);
LlvmCycle? cycle = info.GetCycle(block);
LlvmCycle? outer = info.GetTopLevelParentCycle(block);
if (cycle is not null) {
    uint depth = cycle.Depth;
    bool reducible = cycle.IsReducible;
    cycle.GetExitBlocks(exits);
}
```

`Cycles` is forest preorder. Children skip each child's descendant subtree.
`Blocks` includes descendants; `Entries` retains header-first reentry order.
`GetCycleDepth`, smallest-common-cycle, exiting blocks and source-style cached
unique exit blocks are implemented. The exit cache deliberately recomputes an
empty result, matching upstream's empty-cache convention; a nonempty result is
reused. Cross-function/stale-result usage is not a supported query contract.

Managed adaptations are one stable identity wrapper per source index and
read-only slices into one final block-layout array. Discovery's growing list is
scratch, not a second retained block-layout copy. The source pointer/bit packing
and position/index union become managed fields with the same lifetimes. Current
Function numbering is stable and exposes maximum number but not LLVM's complete
number-epoch mechanism. Reconstruct this static result after CFG changes that
alter cycle regions; adding out-of-map acyclic blocks returns no cycle, as the
source lookup does. Dynamic addBlockToCycle, splitCriticalEdge, setSingleEntry,
preheader/hoisting-legality utilities and expensive source verification are not
claimed. There is no fake incremental-update facade or automatic reanalysis in
an execution path.

## Observability and focused validation

Detailed phases separate reverse-successor DFS, forest/Euler layout and
irreducible-entry grouping. `cycle_info.*` counters record reachable/unreachable
blocks, edge classes, header-chain and reentry work, discovered cycles/depths,
layout writes/inclusive membership, duplicate reentries and irreducible/unique
entry outcomes. Expensive exit queries record cache hits, scanned blocks/edges
and returned exits with function/header identity. Disabled recording allocates
no diagnostic attributes, metric dictionaries or prefixed counter strings;
core O(1) membership queries add no telemetry allocation or graph traversal.

`CycleInfoChecks` exercises a reachable two-entry irreducible region, unique
exits reached by multiple edges, an unreachable self-loop, and nested reducible
cycles with parent/depth/header/Euler-containment checks. These checks are installed
for the root's focused CFG-component journey, not a new CI gate or blanket suite.
