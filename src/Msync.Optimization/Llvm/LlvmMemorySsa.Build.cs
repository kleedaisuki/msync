// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: MemorySSA.cpp
// buildMemorySSA/placePHINodes/createNewAccess/isOrdered and createMemoryPhi.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmMemorySsa
{
    private void BuildMemorySsa()
    {
        LlvmBatchAAResults batch = new(AA);
        HashSet<LlvmBasicBlock> definingBlocks;
        if (Trace is null) definingBlocks = ScanInstructions(batch);
        else definingBlocks = Trace.Measure("optimizer.memoryssa.instruction-scan", (Graph: this, Batch: batch), static state => state.Graph.ScanInstructions(state.Batch));
        if (Trace is null) PlacePhiNodes(definingBlocks);
        else Trace.Run("optimizer.memoryssa.idf-placement", (Graph: this, Blocks: definingBlocks), static state => state.Graph.PlacePhiNodes(state.Blocks));
        var visited = new HashSet<LlvmBasicBlock>();
        if (Trace is null) RenamePass(visited);
        else Trace.Run("optimizer.memoryssa.domtree-rename", (Graph: this, Visited: visited), static state => state.Graph.RenamePass(state.Visited));
        if (Trace is null) MarkUnreachable(visited);
        else Trace.Run("optimizer.memoryssa.unreachable", (Graph: this, Visited: visited), static state => state.Graph.MarkUnreachable(state.Visited));
    }
    private HashSet<LlvmBasicBlock> ScanInstructions(LlvmBatchAAResults batch)
    {
        var defining = new HashSet<LlvmBasicBlock>();
        foreach (LlvmBasicBlock block in Function.BasicBlocks)
        {
            Count("blocks.scanned");
            foreach (LlvmInstruction instruction in block.Instructions)
            {
                Count("instructions.scanned");
                LlvmMemoryUseOrDef? access = CreateNewAccess(instruction, batch);
                if (access is null) continue;
                Accesses(block).AddLast(access);
                if (access is LlvmMemoryDef) { Definitions(block).AddLast(access); defining.Add(block); }
            }
        }
        Count("blocks.defining", (ulong)defining.Count);
        return defining;
    }
    private LlvmMemoryUseOrDef? CreateNewAccess(LlvmInstruction instruction, LlvmBatchAAResults batch, LlvmMemoryUseOrDef? template = null)
    {
        if (!instruction.MayReadFromMemory() && !instruction.MayWriteToMemory())
        { Count("instructions.no_memory_effects"); return null; }
        bool definition, use;
        if (template is not null) { definition = template is LlvmMemoryDef; use = template is LlvmMemoryUse; }
        else
        {
            Count("aa.classification_queries");
            LlvmModRefInfo modref = batch.GetModRefInfo(instruction);
            definition = (modref & LlvmModRefInfo.Mod) != 0 || IsOrdered(instruction);
            use = (modref & LlvmModRefInfo.Ref) != 0;
        }
        if (!definition && !use) { Count("instructions.aa_no_modref"); return null; }
        LlvmMemoryUseOrDef access;
        if (definition)
        {
            access = new LlvmMemoryDef(instruction.Parent!, instruction, nextId++);
            Count("access.definitions");
        }
        else
        {
            access = new LlvmMemoryUse(instruction.Parent!, instruction);
            Count("access.uses");
            if (instruction is LlvmLoadInstruction load)
            {
                Count("aa.constant_memory_queries");
                if ((batch.GetModRefInfoMask(LlvmMemoryLocation.Get(load)) & LlvmModRefInfo.Mod) == 0)
                { access.SetOptimized(LiveOnEntryDef); Count("uses.constant_memory_live_on_entry"); }
            }
        }
        valueToAccess[instruction] = access;
        return access;
    }
    private static bool IsOrdered(LlvmInstruction instruction) => instruction switch
    {
        LlvmLoadInstruction load => !load.IsUnordered,
        LlvmStoreInstruction store => !store.IsUnordered,
        _ => false
    };
    private void PlacePhiNodes(HashSet<LlvmBasicBlock> defining)
    {
        LlvmForwardIdfCalculator calculator = new(DominatorTree, Metrics) { DefiningBlocks = defining };
        var frontier = new List<LlvmBasicBlock>(); calculator.Calculate(frontier);
        foreach (LlvmBasicBlock block in frontier) CreateMemoryPhi(block);
    }
    internal LlvmMemoryPhi CreateMemoryPhi(LlvmBasicBlock block)
    {
        System.Diagnostics.Debug.Assert(GetMemoryAccess(block) is null);
        LlvmMemoryPhi phi = new(block, nextId++);
        Accesses(block).AddFirst(phi); Definitions(block).AddFirst(phi);
        numberingValid.Remove(block); valueToAccess.Add(block, phi); Count("access.phis");
        return phi;
    }
}
