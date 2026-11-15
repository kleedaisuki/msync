// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SSAUpdater.cpp
// LoadAndStorePromoter live-in rewrite and delayed deletion/replacement chains.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public partial class LlvmLoadAndStorePromoter
{
    private void RewriteLiveInLoads(List<LlvmLoadInstruction> liveInLoads, Dictionary<LlvmValue, LlvmValue> replacedLoads)
    {
        foreach (LlvmLoadInstruction load in liveInLoads)
        {
            LlvmValue value = SSA.GetValueInMiddleOfBlock(load.Parent!);
            ReplaceLoadWithValue(load, value);
            if (ReferenceEquals(value, load)) { value = LlvmPoisonValue.Get(value.Type); Count("loads.self_poisoned"); }
            load.ReplaceAllUsesWith(value);
            replacedLoads[load] = value;
            Count("loads.live_in_replaced");
        }
    }
    private void DeleteInstructions(IReadOnlyList<LlvmInstruction> instructions, Dictionary<LlvmValue, LlvmValue> replacedLoads)
    {
        foreach (LlvmInstruction instruction in instructions)
        {
            if (!ShouldDelete(instruction)) { Count("instructions.client_retained"); continue; }
            if (!instruction.UseEmpty)
            {
                LlvmValue value = replacedLoads.GetValueOrDefault(instruction)!;
                Debug.Assert(value is not null);
                // Intermediate load objects may already be erased; only their
                // stable identity is consulted, as with upstream Value* keys.
                while (replacedLoads.TryGetValue(value, out LlvmValue? replacement))
                { value = replacement; Count("replacement_chain.steps"); }
                ReplaceLoadWithValue((LlvmLoadInstruction)instruction, value);
                instruction.ReplaceAllUsesWith(value);
                Count("loads.final_chain_replaced");
            }
            InstructionDeleted(instruction);
            instruction.EraseFromParent();
            Count("instructions.deleted");
        }
    }
}
