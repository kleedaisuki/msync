// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// PromoteMemoryToRegister.cpp AllocaInfo, LargeBlockInfo and promotion fast paths.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmPromoteMemoryToRegister
{
    private static IEnumerable<LlvmUser> EarlyUsers(LlvmValue value)
    {
        for (var use = value.FirstUse; use is not null;)
        {
            var current = use; use = use.Next;
            yield return current.User;
        }
    }
    private sealed class AllocaInfo
    {
        internal readonly List<LlvmBasicBlock> DefiningBlocks = [];
        internal readonly List<LlvmBasicBlock> UsingBlocks = [];
        internal LlvmStoreInstruction? OnlyStore;
        internal LlvmBasicBlock? OnlyBlock;
        internal bool OnlyUsedInOneBlock = true;
        internal LlvmType ValueType = null!;
        internal AllocaInfo(LlvmAllocaInstruction alloca, Dictionary<string, ulong>? metrics)
        {
            foreach (var user in alloca.Users)
            {
                Count(metrics, "alloca.users_analyzed");
                var instruction = (LlvmInstruction)user;
                LlvmType type;
                if (instruction is LlvmStoreInstruction store)
                { DefiningBlocks.Add(store.Parent!); OnlyStore = store; type = store.ValueOperand.Type; }
                else
                { var load = (LlvmLoadInstruction)instruction; UsingBlocks.Add(load.Parent!); type = load.Type; }
                if (ValueType is null) ValueType = type;
                else Debug.Assert(ReferenceEquals(ValueType, type));
                if (!OnlyUsedInOneBlock) continue;
                if (OnlyBlock is null) OnlyBlock = instruction.Parent;
                else if (OnlyBlock != instruction.Parent) OnlyUsedInOneBlock = false;
            }
        }
    }
    private sealed class LargeBlockInfo(Dictionary<string, ulong>? metrics)
    {
        private readonly Dictionary<LlvmInstruction, int> numbers = [];
        private static bool Interesting(LlvmInstruction instruction) =>
            instruction is LlvmLoadInstruction { PointerOperand: LlvmAllocaInstruction } or
                LlvmStoreInstruction { PointerOperand: LlvmAllocaInstruction };
        internal int GetIndex(LlvmInstruction instruction)
        {
            Debug.Assert(Interesting(instruction));
            if (numbers.TryGetValue(instruction, out int index))
            { Count(metrics, "large_block.cache_hits"); return index; }
            Count(metrics, "large_block.scans");
            index = 0;
            foreach (var candidate in instruction.Parent!.Instructions)
            {
                Count(metrics, "large_block.instructions");
                if (Interesting(candidate)) numbers[candidate] = index++;
            }
            return numbers[instruction];
        }
        internal void Delete(LlvmInstruction instruction) => numbers.Remove(instruction);
        internal void Clear() => numbers.Clear();
    }
    private sealed partial class Promotion
    {
        private bool RewriteSingleStore(LlvmAllocaInstruction alloca, AllocaInfo info, LargeBlockInfo blockInfo)
        {
            var onlyStore = info.OnlyStore!;
            LlvmValue replacement = onlyStore.ValueOperand;
            bool requireDominatingStore = replacement is LlvmInstruction ||
                !LlvmValueTracking.IsGuaranteedNotToBePoison(replacement, metrics: metrics, assumptions: assumptions);
            var storeBlock = onlyStore.Parent!;
            int storeIndex = -1;
            info.UsingBlocks.Clear();
            foreach (var user in EarlyUsers(alloca))
            {
                if (ReferenceEquals(user, onlyStore)) continue;
                var load = (LlvmLoadInstruction)user;
                if (requireDominatingStore)
                {
                    if (load.Parent == storeBlock)
                    {
                        if (storeIndex == -1) storeIndex = blockInfo.GetIndex(onlyStore);
                        if (storeIndex > blockInfo.GetIndex(load))
                        { info.UsingBlocks.Add(storeBlock); Count(metrics, "single_store.load_before_store"); continue; }
                    }
                    else if (!tree.Dominates(storeBlock, load.Parent!))
                    { info.UsingBlocks.Add(load.Parent!); Count(metrics, "single_store.store_not_dominating"); continue; }
                }
                if (ReferenceEquals(replacement, load)) replacement = LlvmPoisonValue.Get(load.Type);
                ReplaceLoad(load, replacement, "single_store");
                blockInfo.Delete(load);
            }
            if (info.UsingBlocks.Count != 0) return false;
            onlyStore.EraseFromParent(); blockInfo.Delete(onlyStore);
            alloca.EraseFromParent();
            Count(metrics, "fastpath.single_store"); Count(metrics, "removed.stores"); Count(metrics, "removed.allocas");
            return true;
        }
        private bool PromoteSingleBlock(LlvmAllocaInstruction alloca, LargeBlockInfo blockInfo)
        {
            var stores = new List<(int Index, LlvmStoreInstruction Store)>();
            foreach (var user in alloca.Users)
                if (user is LlvmStoreInstruction store) stores.Add((blockInfo.GetIndex(store), store));
            stores.Sort(static (a, b) => a.Index.CompareTo(b.Index));
            foreach (var user in EarlyUsers(alloca))
            {
                if (user is not LlvmLoadInstruction load) continue;
                int loadIndex = blockInfo.GetIndex(load);
                int begin = 0, end = stores.Count;
                while (begin < end)
                {
                    int middle = begin + (end - begin) / 2;
                    if (stores[middle].Index < loadIndex) begin = middle + 1;
                    else end = middle;
                    Count(metrics, "single_block.store_search_steps");
                }
                LlvmValue replacement;
                if (begin == 0)
                {
                    if (stores.Count != 0) { Count(metrics, "single_block.load_before_store"); return false; }
                    replacement = LlvmUndefValue.Get(load.Type);
                }
                else replacement = stores[begin - 1].Store.ValueOperand;
                if (ReferenceEquals(replacement, load)) replacement = LlvmPoisonValue.Get(load.Type);
                ReplaceLoad(load, replacement, "single_block"); blockInfo.Delete(load);
            }
            while (!alloca.UseEmpty)
            {
                var store = (LlvmStoreInstruction)alloca.Users.Last();
                store.EraseFromParent(); blockInfo.Delete(store); Count(metrics, "removed.stores");
            }
            alloca.EraseFromParent(); Count(metrics, "removed.allocas"); Count(metrics, "fastpath.single_block");
            return true;
        }
    }
}
