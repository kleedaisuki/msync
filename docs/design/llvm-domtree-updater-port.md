# LLVM dominator updater

`LlvmDomTreeUpdater` translates LLVM's coordinator for the already ported
incremental forward/postdominator trees. The pinned source is
[`a55e64432a516fd6507d3ecfba7407cfe425f940`](https://github.com/llvm/llvm-project/tree/a55e64432a516fd6507d3ecfba7407cfe425f940):

- `llvm/include/llvm/Analysis/GenericDomTreeUpdater.h`
- `llvm/include/llvm/Analysis/GenericDomTreeUpdaterImpl.h`
- `llvm/include/llvm/Analysis/DomTreeUpdater.h`
- `llvm/lib/Analysis/DomTreeUpdater.cpp`

## Source correspondence

| Source operation | Managed implementation |
| --- | --- |
| `applyUpdates` | Eager submission to both existing trees, or a lazy mixed-update queue with self-edge elimination |
| `applyUpdatesPermissive` | First-occurrence edge deduplication and comparison with the already mutated CFG; cancelling/no-op submissions do not become tree work |
| `applyUpdatesImpl<IsForward>` | Independent forward/postdominator queue cursors; consecutive normal and critical-split runs keep their source batching |
| `splitDTCriticalEdges` | Collect predecessor dominance before mutation, substitute original predecessors for newly split blocks, insert new nodes, then change successor immediate dominators |
| `splitPDTCriticalEdges` | Source insert/insert/conditional-delete batches passed to actual incremental postdominator updates |
| `dropOutOfDateUpdates` | Retire only the prefix consumed by both available trees; erase queued blocks only after both are current |
| `deleteBB`, `callbackDeleteBB` | Replace dead instructions with poison as required, retain one unreachable terminator while queued, detach/erase after tree updates, invoke source deletion callbacks |
| `recalculate` | Explicit caller-requested rebuild; lazy deletion is completed with per-tree erasure suspended before both trees are reconstructed |

Ordinary mutations never use unconditional reconstruction as an adapter. Existing
`LlvmDominatorTree.ApplyUpdates` retains its own LLVM rebuild selection. Explicit
`Recalculate` can change the associated function, matching the source API.

## Ownership and integration

The client mutates the CFG before submitting edges. A block scheduled for lazy
deletion remains in its function until pending tree updates no longer need it.
`GetTree()` and `GetPostTree()` flush only that tree; `Flush()` and `Dispose()`
flush both. Use deterministic `using`/`Dispose`, rather than a GC finalizer, for
the source destructor's flush contract. The managed IR has no common CallbackVH
infrastructure, so callbacks supplied to this updater are held explicitly and
invoked at the corresponding block-deletion point.

Canonical shared API names are `ApplyUpdates`, `ApplyUpdatesPermissive`,
`SplitCriticalEdge`, `DeleteBlock`, `CallbackDeleteBlock`, `GetTree`,
`GetPostTree`, `Recalculate`, `Flush`, and `IsBlockPendingDeletion`. Update
records use the existing `LlvmCfgUpdate`/`LlvmCfgUpdateKind`.

This is optional optimizer infrastructure, not execution admission, heap
validation, or a language Range mechanism. It introduces no IR operation.

## Observability and focused properties

Detailed spans identify update submission, permissive submission, critical-edge
split, deletion/callback deletion, flush and explicit recalculation. Counts
separate submitted/accepted/skipped edges, queue/retirement, normal versus
critical batches in each tree, dominance work, deleted instructions/blocks and
callback delivery. Context includes the function, strategy, available trees,
and block/source for block operations. With recording and caller metrics off,
no diagnostic dictionaries or counter-key strings are constructed.

`DomTreeUpdaterProperties` exercises eager changes and permissive filtering,
lazy dual-tree acquisition and delayed callbacks, both strategies' critical-edge
splits, explicit recalculation (including a different function), and no-tree lazy
deletion. Updated trees are compared against fresh source Semi-NCA construction,
including all-pairs dominance. These properties supplement the real CFG
transformations' source/target execution journeys; they do not establish pass
performance by themselves. The root agent owns builds and integrated execution.
