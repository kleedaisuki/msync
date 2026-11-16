// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: DeadStoreElimination.cpp eliminateDeadDefs/storeIsNoop.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmDeadStoreElimination
{
    private sealed partial class State
    {
        private bool EliminateDefinitions()
        {
            bool changed = false;
            for (int index = 0; index < definitions.Count; ++index)
            {
                var killing = definitions[index];
                if (skipped.Contains(killing)) continue;
                var location = GetWriteLocation(killing.MemoryInstruction!);
                if (location is null) continue;
                PrepareStoreCandidates(killing, location.Value);
                var underlying = Underlying(location.Value.Ptr!);
                var (removed, killedKilling) = EliminateKilledDefinitions(killing, location.Value, underlying);
                changed |= removed;
                if (!killedKilling && StoreIsNoop(killing, underlying))
                {
                    Delete(killing.MemoryInstruction!);
                    Count("stores.redundant");
                    changed = true;
                }
                // No memset/malloc/calloc IDs or C-library availability exist.
            }
            return changed;
        }
        private (bool Changed, bool KillingDeleted) EliminateKilledDefinitions(LlvmMemoryDef killing, LlvmMemoryLocation killingLoc, LlvmValue underlying)
        {
            bool changed = false, killingDeleted = false;
            uint scan = options.ScanLimit, walk = options.WalkLimit, partial = options.PartialStoreLimit;
            var work = new List<LlvmMemoryAccess>();
            var membership = new HashSet<LlvmMemoryAccess>();
            var deleted = new HashSet<LlvmMemoryAccess>();
            int originalSkipped = skipped.Count;
            void Add(LlvmMemoryAccess access) { if (membership.Add(access)) work.Add(access); }
            Add(killing.DefiningAccess!);
            for (int index = 0; index < work.Count; ++index)
            {
                var current = work[index];
                if (deleted.Contains(current)) continue;
                var dead = GetDominatingDefinition(killing, current, killingLoc, underlying, ref scan, ref walk, ref partial);
                if (dead is null) continue;
                if (dead is LlvmMemoryPhi phi)
                {
                    for (int incoming = 0; incoming < phi.IncomingCount; ++incoming)
                    {
                        var definition = phi.GetIncomingValue(incoming);
                        if (postOrder.GetValueOrDefault(definition.Block) > postOrder.GetValueOrDefault(dead.Block)) Add(definition);
                    }
                    continue;
                }
                var deadDefinition = (LlvmMemoryDef)dead;
                var deadInstruction = deadDefinition.MemoryInstruction!;
                var deadLoc = GetWriteLocation(deadInstruction)!.Value;
                Add(deadDefinition.DefiningAccess!);
                var overwrite = IsOverwrite(killing.MemoryInstruction!, deadInstruction, killingLoc, deadLoc, out long killingOffset, out long deadOffset);
                if (overwrite == Overwrite.MaybePartial)
                    overwrite = PartialOverwrite(killingLoc, deadLoc, killingOffset, deadOffset, deadInstruction);
                if (options.PartialStoreMerging && overwrite == Overwrite.PartialEarlierWithFullLater
                    && deadInstruction is LlvmStoreInstruction deadStore && killing.MemoryInstruction is LlvmStoreInstruction killingStore
                    && dominators.Dominates(deadStore, killingStore))
                {
                    if (TryMergeStores(killingStore, deadStore, killingOffset, deadOffset) is { } merged)
                    {
                        InvalidateAddressFacts(storeMerge: true);
                        deadStore.ValueOperand = merged;
                        changed = killingDeleted = true;
                        Count("stores.merged");
                        Delete(killingStore, deleted);
                        RemoveIntervals(deadStore);
                        break;
                    }
                }
                if (overwrite == Overwrite.Complete)
                {
                    Delete(deadInstruction, deleted);
                    Count("stores.overwritten");
                    changed = true;
                }
            }
            Debug.Assert(skipped.Count - originalSkipped == deleted.Count);
            return (changed, killingDeleted);
        }
        private bool StoreIsNoop(LlvmMemoryDef definition, LlvmValue underlying)
        {
            if (definition.MemoryInstruction is not LlvmStoreInstruction store || !IsRemovable(store)) return false;
            if (store.ValueOperand is LlvmUndefValue and not LlvmPoisonValue
                && underlying is LlvmAllocaInstruction or LlvmHeapAllocInst)
            {
                // MemoryBuiltins.cpp returns undef for uninitialized allocations;
                // the first-class HeapAlloc adapter has that same explicit contract.
                return memory.IsLiveOnEntryDef(memory.GetSkipSelfWalker().GetClobberingMemoryAccess(definition, alias, Metrics));
            }
            if (store.ValueOperand is not LlvmLoadInstruction load) return false;
            bool same = ReferenceEquals(load.PointerOperand, store.PointerOperand)
                || alias.Alias(LlvmMemoryLocation.Get(load, layout), LlvmMemoryLocation.Get(store, layout)) == LlvmAliasKind.MustAlias;
            if (!same) return false;
            var loadAccess = memory.GetMemoryAccess(load)!.DefiningAccess;
            if (ReferenceEquals(loadAccess, definition.DefiningAccess)) return true;
            var current = memory.GetWalker().GetClobberingMemoryAccess(definition, alias, Metrics);
            var work = new List<LlvmMemoryAccess> { definition };
            var seen = new HashSet<LlvmMemoryAccess> { definition };
            void Add(LlvmMemoryAccess access) { if (seen.Add(access)) work.Add(access); }
            Add(current);
            for (int index = 1; index < work.Count; ++index)
            {
                current = work[index];
                if (current is LlvmMemoryPhi phi)
                {
                    for (int incoming = 0; incoming < phi.IncomingCount; ++incoming) Add(phi.GetIncomingValue(incoming));
                    continue;
                }
                Debug.Assert(current is LlvmMemoryDef);
                if (!ReferenceEquals(loadAccess, current)) return false;
            }
            return true;
        }
    }
}
