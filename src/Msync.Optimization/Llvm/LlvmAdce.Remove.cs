// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADCE.cpp removeDeadInstructions.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmAdce
{
    private sealed partial class Work
    {
        private LlvmAdceChanged RemoveDeadInstructions()
        {
            bool controlChanged = trace is null ? UpdateDeadRegions() :
                trace.MeasureDetailed("optimizer.llvm-adce.update-dead-regions", this, static state => state.UpdateDeadRegions());
            for (int blockIndex = function.BasicBlocks.Count - 1; blockIndex >= 0; --blockIndex)
                for (var instruction = function.BasicBlocks[blockIndex].LastInstruction; instruction is not null; instruction = instruction.Previous)
                {
                    Count("removal.instructions_scanned");
                    if (liveInstructions.Contains(instruction)) continue;
                    worklist.Add(instruction); Count("removal.dead_instructions");
                }
            bool nonDebugChanged = worklist.Count != 0;
            // Break references for the entire dead set first, including SSA cycles.
            foreach (var instruction in worklist)
            { Count("removal.operands_detached", (ulong)instruction.OperandCount); instruction.DropAllReferences(); }
            foreach (var instruction in worklist)
            {
                if (trace is null) instruction.EraseFromParent();
                else trace.Run("optimizer.llvm-adce.erase", instruction, static dead => dead.EraseFromParent(), Attributes(instruction));
                Count("removal.instructions_erased");
            }
            return new(controlChanged || nonDebugChanged, nonDebugChanged, controlChanged);
        }
    }
}
