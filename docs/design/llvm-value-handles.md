# LLVM value handles and managed IR lifetime

## Source correspondence

The implementation translates LLVM commit
`a55e64432a516fd6507d3ecfba7407cfe425f940`:

- [`IR/ValueHandle.h`](../../vendor/llvm-reference/llvm/include/llvm/IR/ValueHandle.h):
  `ValueHandleBase`, `WeakVH`, `WeakTrackingVH`, `TrackingVH`, and `CallbackVH`.
- [`IR/Value.cpp`](../../vendor/llvm-reference/llvm/lib/IR/Value.cpp):
  handle-list splice/removal, `ValueIsDeleted`, `ValueIsRAUWd`, and the notification
  ordering in `Value::doRAUW` and `Value::~Value`.

| Managed type | RAUW | Explicit IR deletion |
| --- | --- | --- |
| `LlvmWeakValueHandle` | Remains on original value | Becomes null |
| `LlvmWeakTrackingValueHandle` | Follows replacement | Becomes null |
| `LlvmTrackingValueHandle<T>` | Follows replacement through weak-tracking handle | Null/invalid; source typed read assertions remain |
| `LlvmCallbackValueHandle` | Calls `AllUsesReplacedWith` before ordinary use rewriting | Calls `Deleted`; default implementation clears reference |

Callback subclasses may detach themselves, detach pending callbacks, or move to
another value during notification. The implementation retains the upstream
sentinel-cursor algorithm: splice the iterator immediately after the current
entry, dispatch, then advance using the iterator's current successor. It does
not snapshot callbacks or maintain a generic event observer bus. New entries
inserted at the old list head during dispatch are not visited. As upstream,
callbacks must remove references to a deleted value before returning; permanent
new registrations during deletion are not supported.

The required managed surface is `Value`, explicit `Dispose`, and protected
`Deleted` / `AllUsesReplacedWith` overrides. Assignment/registration and disposal
perform constant-time intrusive list operations. A value with no registrations
adds only the head check to its RAUW/deletion path. Optional
`LlvmContext.ValueHandleMetrics` records actual notification counts, follow
operations, weak-null outcomes and callbacks without allocating a disabled sink.

## Managed ownership adaptation

The source embeds list links in native handles and maintains a context map keyed
by raw value pointers. Managed code instead stores the list head on the value.
Each registry node holds its handle weakly, and each handle holds its target
value weakly. Explicit disposal immediately unlinks the node. Collected handles
are skipped and their nodes removed during notification.

```text
value ──strong──> registry nodes ──weak──> handle ──weak──> target value
                        ↑                  │
                        └─────strong───────┘
```

Separating registry nodes from handles is necessary: strong neighboring-handle
links would let an externally retained `WeakVH` root an unrelated neighboring
callback, its analysis cache, and indirectly the entire function/value graph.
There is no global strong value-key registry and no finalizer invoking analysis
callbacks. CLR reclamation is not an IR transformation; a weak target naturally
becomes null when its unmanaged-equivalent IR graph is no longer owned. Analysis
invalidation callbacks run at explicit IR mutation/destruction points instead.

`Instruction.EraseFromParent` notifies after detachment and operand-reference
release, before name destruction. Immutable aggregate-constant canonical merging
notifies after its RAUW and table/reference removal, matching source constant
destruction. `RemoveFromParent` and `DropAllReferences` alone do not imply
value deletion. MemorySSA's source-specific access replacement/deletion adapters
remain separate; these handles do not introduce another access-deletion protocol.

`tests/LlvmIr/ValueHandleProperties.cs` covers a real RAUW/use rewrite and erasure
journey, callback migration and self/neighbor removal, and the managed ownership
property that a weak registry cannot retain a callback/value cycle. This is not
a claim to port unrepresented `AssertingVH`, `PoisoningVH`, or debug metadata
value-handle families.

## Explicit block destruction and mutable block iteration

`LlvmBasicBlock.EraseFromParent` and `DeleteValue` now centralize the represented
`BasicBlock::~BasicBlock` path from pinned `IR/BasicBlock.cpp` (lines 172–211).
Erasure detaches the block first. Destruction drops references from **all**
instructions before clearing the instruction list, so internal PHI/arithmetic
cycles disappear without synthetic replacements. Instruction deletion callbacks
run before the block's own `Value` destructor notification. Block callbacks see
an empty, detached block; its name remains available until the notification ends.
Address-taken `BlockAddress` destruction and debug markers are unrepresented by
this IR carrier and are not inferred from ordinary pointers or names.

Actual deletion in BasicBlockUtils, SCCP and LoopSimplify uses this path;
ordinary block detach, reinsertion and splice do not. DomTreeUpdater retains its
source-specific ordering: eager callback deletion detaches, calls the client
while the temporary unreachable instruction still exists, then destroys the
block. Lazy callback deletion registers the source `CallBackOnDeletion`
`CallbackVH` family and invokes it during block destruction. In particular,
newer weak-handle registrations null before the lazy callback while older ones
null after it, exactly following the shared handle list rather than a separate
manually dispatched callback array. Queued DT/PDT updates still complete before
physical block erasure.

`NextNode` / `PreviousNode` provide the intrusive ilist adjacency used by upstream
mutable CFG traversals. Function insertion, detachment and intra-function splice
maintain these links alongside the existing indexed `BasicBlocks` view. The
canonical iterator saves `NextNode` before simplifying a block; no per-block
`IndexOf` scan or frozen visitation snapshot is introduced. Detachment clears
both links, as removal from the source ilist does.

## Function numbering epochs and dominator synchronization

`LlvmFunction.RenumberBlocks` translates pinned `IR/Function.cpp` lines 64–84:
validate the old numbering in debug builds, reset the allocation counter, assign
consecutive numbers in current block order, and increment the numbering epoch.
`GetMaxBlockNumber` and `GetBlockNumberEpoch` retain their source meanings.
Renumbering never changes control flow, block identity or value-handle targets.

`LlvmDominatorTree.UpdateBlockNumbers` translates
`GenericDomTree.h::updateBlockNumbers`. Upstream reindexes its numbered vector
while keeping all dominator nodes and edges; this implementation's identity-keyed
node dictionary already preserves that mapping. The operation therefore updates
the stored numbering epoch and capacity used by source batch-rebuild selection
without rebuilding the graph, touching DFS intervals, or rediscovering loops.
Both forward and postdominator trees support this synchronization. Existing
source debug epoch assertions catch use before the caller performs the required
update; there is no release-mode validation pass.

`tests/LlvmIr/BlockLifetimeProperties.cs` covers immediate weak loop-header
invalidation without GC, cyclic instruction teardown, BasicBlockUtils deletion,
eager/lazy callback ordering relative to weak handles, block adjacency after
splice/detach/reinsertion, compact renumbering, and preservation of actual
forward/postdominator node objects and DFS intervals. Optional metrics report
renumbered block counts and tree-number synchronization without allocating a
recording-disabled sink.
