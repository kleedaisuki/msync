# LLVM Local unreachable-block removal

This port translates `changeToUnreachable`, `markAliveBlocks` and
`removeUnreachableBlocks` in `llvm/lib/Transforms/Utils/Local.cpp` at
`a55e64432a516fd6507d3ecfba7407cfe425f940` (source lines 2592–2634,
2724–2926 and 2961–2990). It is a shared transform utility used by the real
SimplifyCFG pass wrapper, not a runtime check or a custom DFS-only substitute.

## Behavior and APIs

`LlvmLocalCfg.RemoveUnreachableBlocks(function, updater, memoryUpdater,
foldInstsToUnreachable, trace, metrics)` keeps the source default
`foldInstsToUnreachable = true`. Reachability uses BasicBlock numbers and the
source LIFO worklist/edge visitation order. Its first scan folds instructions
that establish unreachable control flow and then calls the shared real
`ConstantFoldTerminator` before discovering successors. The source second
iteration may pass `false` to skip both expensive scans and terminator folding;
it does not perform another instruction scan under a different name.

The instruction scan handles registered `llvm.assume` false/undef predicates,
registered `llvm.experimental.guard` false predicates, null/undef indirect
callees, `noreturn` calls except `musttail`, and nonvolatile stores through
undefined/null pointers. Intrinsic identity comes from the registered descriptor,
never an ordinary source function's spelling. The current descriptor registry
has no assume/guard entries; the matching source branches already use the real
identity channel and become reachable only when those intrinsics are represented.
EH Invoke/CatchSwitch classes and their source rewrite paths are not represented.

`LlvmFunction.NullPointerIsDefined` directly translates the source Function.cpp
helper: an explicit `null-pointer-is-valid` function attribute or any nonzero
address space makes null defined. A volatile store is not changed into
unreachable. `noreturn` cuts off instructions after the call, preserving the
call's effects and the source musttail-return contract.

`LlvmLocalCfg.ChangeToUnreachable(first, preserveLcssa, updater, memoryUpdater,
metrics)` uses real shared PHI predecessor removal, preserves one-input PHIs
when requested, inserts unreachable at the source point, replaces surviving uses
of removed values with poison, and submits deduplicated edge deletions to the
real DomTreeUpdater. Optional MemorySSA updating runs before changing the CFG,
as in the source. Unreachable-block removal also removes the selected MemorySSA
blocks before invoking shared `DeleteDeadBlocks`; it skips already-pending lazy
DTU deletions rather than deleting them twice.

## Telemetry

`optimizer.llvm-local.remove-unreachable` reports function and scan-option
context. `optimizer.llvm-local.fold-to-unreachable` records each actual fold's
function, block, instruction opcode, source location and concrete cause. Metrics
record reachable blocks, examined instructions/edges, folded causes, pending
block skips, removed blocks/instructions and dominator updates. Disabled recording
avoids allocating trace dictionaries.

## Focused checks and integration

The staged `UnreachableBlockProperties` checks dead cyclic CFG removal with a
real dominator updater, constant-branch folding before reachability, null-store
folding, the no-scan second-iteration option, volatile/null-valid/nonzero-AS
stores, invalid callees, `noreturn` and the `musttail` exclusion. These properties
supplement the coordinator's real source/CLI/artifact journey; they do not claim
whole-pipeline acceptance on their own.

Implementation is published in
`src/Msync.Optimization/Llvm/LlvmLocalCfg.Unreachable*.cs`; properties are in
`tests/LlvmIr/UnreachableBlockProperties.cs`. The source `NoReturn` call/function
attribute and `null-pointer-is-valid` function attribute are represented directly;
cloning preserves each own call-site flag. Root owns serialized compilation and
execution; no independent build was launched for this utility.
