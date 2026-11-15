// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM MemorySSAUpdater.cpp getNewDefiningAccessForClone, cloneUsesAndDefs,
// updateForClonedLoop / updateForClonedBlockIntoPred / updateExitBlocksForClonedLoop.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsaUpdater
{
    private LlvmMemoryAccess GetNewDefiningAccessForClone(LlvmMemoryAccess access,
        IReadOnlyDictionary<LlvmValue, LlvmValue> valueMap, Dictionary<LlvmMemoryPhi, LlvmMemoryAccess> phiMap,
        Func<LlvmBasicBlock, bool> isInClonedRegion)
    {
        if (access is LlvmMemoryDef definition)
        {
            if (MemorySsa.IsLiveOnEntryDef(definition)) return definition;
            var instruction = definition.MemoryInstruction!;
            if (!isInClonedRegion(instruction.Parent!)) return definition;
            var cloned = valueMap.GetValueOrDefault(instruction) as LlvmInstruction;
            LlvmMemoryAccess? result = cloned is not null ? MemorySsa.GetMemoryAccess(cloned) : null;
            return result is null or LlvmMemoryUse
                ? GetNewDefiningAccessForClone(definition.DefiningAccess!, valueMap, phiMap, isInClonedRegion) : result;
        }
        return phiMap.GetValueOrDefault((LlvmMemoryPhi)access) ?? access;
    }

    private void CloneUsesAndDefs(LlvmBasicBlock block, LlvmBasicBlock newBlock,
        IReadOnlyDictionary<LlvmValue, LlvmValue> valueMap, Dictionary<LlvmMemoryPhi, LlvmMemoryAccess> phiMap,
        Func<LlvmBasicBlock, bool> isInClonedRegion, bool cloneWasSimplified = false)
    {
        if (MemorySsa.GetBlockAccesses(block) is not { } accesses) return;
        foreach (var access in accesses)
        {
            if (access is not LlvmMemoryUseOrDef useOrDef ||
                valueMap.GetValueOrDefault(useOrDef.MemoryInstruction!) is not LlvmInstruction instruction) continue;
            var cloned = MemorySsa.CreateDefinedAccess(instruction,
                GetNewDefiningAccessForClone(useOrDef.DefiningAccess!, valueMap, phiMap, isInClonedRegion),
                cloneWasSimplified ? null : useOrDef, creationMustSucceed: false);
            if (cloned is not null)
            { MemorySsa.InsertIntoListsForBlock(cloned, newBlock, LlvmMemorySsaInsertionPlace.End); Count("clone.accesses"); }
            else Count("clone.no_memory_access");
        }
    }

    public void UpdateForClonedLoop(IReadOnlyList<LlvmBasicBlock> loopBlocksRpo, IReadOnlyList<LlvmBasicBlock> exitBlocks,
        IReadOnlyDictionary<LlvmValue, LlvmValue> valueMap, bool ignoreIncomingWithNoClones = false)
    {
        var blocks = loopBlocksRpo.Concat(exitBlocks).Distinct().ToArray();
        var region = blocks.ToHashSet();
        var phiMap = new Dictionary<LlvmMemoryPhi, LlvmMemoryAccess>();
        foreach (var block in blocks)
        {
            if (valueMap.GetValueOrDefault(block) is not LlvmBasicBlock newBlock) continue;
            Debug.Assert(MemorySsa.GetBlockAccesses(newBlock) is null);
            if (MemorySsa.GetMemoryAccess(block) is { } phi) phiMap.Add(phi, MemorySsa.CreateMemoryPhi(newBlock));
            CloneUsesAndDefs(block, newBlock, valueMap, phiMap, region.Contains);
        }
        foreach (var block in blocks)
        {
            if (MemorySsa.GetMemoryAccess(block) is not { } phi || phiMap.GetValueOrDefault(phi) is not LlvmMemoryPhi newPhi) continue;
            var newPredecessors = Predecessors(newPhi.Block).ToHashSet();
            for (int index = 0; index < phi.IncomingCount; ++index)
            {
                var incomingBlock = phi.GetIncomingBlock(index);
                if (valueMap.GetValueOrDefault(incomingBlock) is LlvmBasicBlock mapped) incomingBlock = mapped;
                else if (ignoreIncomingWithNoClones) continue;
                if (!newPredecessors.Contains(incomingBlock)) continue;
                newPhi.AddIncoming(GetNewDefiningAccessForClone(phi.GetIncomingValue(index), valueMap, phiMap, region.Contains), incomingBlock);
            }
            if (OnlySingleValue(newPhi) is not { } single) continue;
            phiMap[phi] = single; RemoveMemoryAccess(newPhi);
        }
        Count("clone.blocks", (ulong)blocks.Length);
    }

    public void UpdateForClonedBlockIntoPred(LlvmBasicBlock block, LlvmBasicBlock predecessor,
        IReadOnlyDictionary<LlvmValue, LlvmValue> valueMap)
    {
        var phiMap = new Dictionary<LlvmMemoryPhi, LlvmMemoryAccess>();
        if (MemorySsa.GetMemoryAccess(block) is { } phi) phiMap.Add(phi, phi.GetIncomingValueForBlock(predecessor));
        CloneUsesAndDefs(block, predecessor, valueMap, phiMap, candidate => ReferenceEquals(candidate, block), cloneWasSimplified: true);
    }
}
