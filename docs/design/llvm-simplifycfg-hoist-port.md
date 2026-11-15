# LLVM SimplifyCFG common-successor hoisting

This component translates `llvm/lib/Transforms/Utils/SimplifyCFG.cpp` at
`a55e64432a516fd6507d3ecfba7407cfe425f940`. It belongs to the full SimplifyCFG
port, not a separate custom optimization or an execution prerequisite.

## Source correspondence

| Source family | Managed component |
| --- | --- |
| `skippedInstrFlags`, `isSafeToHoistInstr`, `shouldHoistCommonInstructions` (1550–1636) | `HoistSkipFlags`, `SkippedInstructionFlags`, `IsSafeToHoistInstruction`, `ShouldHoistCommonInstructions` |
| `areIdenticalUpToCommutativity` (1706–1726) | Same operand/predicate/commutativity order, using the shared source instruction equivalence APIs |
| `hoistCommonCodeFromSuccessors` (1894–2110) | `HoistCommonCodeFromSuccessors` and the lockstep successor cursors in `LlvmSimplifyCfg.Hoist.cs` |
| `hoistSuccIdenticalTerminatorToSwitchOrIf` (2112–2240) | `LlvmSimplifyCfg.Hoist.Terminator.cs` |
| `isSafeCheapLoadStore` (1864–1889) | `LlvmSimplifyCfg.Hoist.ConditionalMemory.cs` |
| `LockstepReverseIterator<true>` | Reverse predecessor pointers for the source all-instructions-equal precheck; it stops when any block runs out |

The forward scan retains LLVM's default skip limit of 20, the distinction
between skipped reads, side effects and implicit control flow, the same-block
operand ordering restriction, and the musttail-plus-return restriction. It does
not replace memory operations or calls with a pure-instruction-only subset.
Calls use the source musttail, `nomerge`, `convergent`, profitability and
attribute-intersection rules. Intersected IR flags are handled by the shared
`AndIRFlags` port.

Identical terminators are cloned rather than moved. Conditional-branch PHI
inputs that differ are represented by shared, nonfolded selects before the new
terminator. PHI fast-math flags and per-use Native ABI annotations are preserved.
Successor PHIs gain an incoming edge for every represented CFG edge, including
repeated switch destinations. Dominator updates use the real shared
`DomTreeUpdater`, with deduplicated insertions and the original unique-successor
deletions. The source helper erases the original terminator and recursively
removes its dead condition.

## Represented-state boundaries

The represented IR has no `BlockAddress`, EH invoke/CallBr instruction classes,
MMRA/AA metadata, branch profile metadata or debug records. The corresponding
source properties are empty/absent; no dummy metadata storage or fake debug
merging is introduced. UB-implying parameter/return attributes are not currently
represented, so their source drop operation has no attributes to remove. This
does not clear arithmetic poison flags: the source does not do that here.

The pinned generic `TargetTransformInfoImplBase` returns false for
`hasConditionalLoadStoreForType`. Therefore the source's masked-vector
conditional-faulting emitter cannot be reached with the existing target.
`HoistConditionalLoadsStores` reports the missing masked-vector-intrinsic
representation explicitly if invoked; it never pretends to perform a rewrite.
Enabling conditional faulting on a future target requires implementing those
intrinsics and their target lowering, not changing this predicate to guess true.

The language Range object is unrelated to this optimizer's CFG or type queries.
Its constructor remains an ordinary call returning a pointer. This component
contains no Range dispatch, thunk, capture mode or new opcode.

## Observability and integration

The real hoist invocation uses `optimizer.llvm-simplifycfg.hoist-common` with
function, block and source context. The shared pass metric map records visited
successors, lockstep comparisons/skips, all-equal comparisons, concrete retained
causes, instructions and terminators hoisted, PHI selects created/reused,
dominator update counts and conditional-memory target queries. Recording-disabled
paths do not allocate trace attributes.

The three `LlvmSimplifyCfg.Hoist*.cs` files are initially staged under
`.temp/llvm-simplifycfg/` for the owner to integrate with the complete driver and
shared utilities. No independent build was launched; root serializes integration
builds and end-to-end execution.
