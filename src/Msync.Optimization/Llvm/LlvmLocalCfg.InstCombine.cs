// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Local.cpp handleUnreachableTerminator/removeAllNonTerminatorAndEHPadInstructions.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLocalCfg
{
    public static bool HandleUnreachableTerminator(LlvmInstruction instruction, ICollection<LlvmValue> poisonedValues,
        Dictionary<string, ulong>? metrics = null)
    {
        bool changed = false;
        foreach (LlvmUse use in instruction.OperandUses)
        {
            if (use.Value is not LlvmInstruction operand || operand.Type.IsTokenTy()) continue;
            use.Set(LlvmPoisonValue.Get(operand.Type));
            poisonedValues.Add(operand);
            changed = true;
            if (metrics is not null) metrics["local.unreachable.terminator-operands-poisoned"] =
                metrics.GetValueOrDefault("local.unreachable.terminator-operands-poisoned") + 1;
        }
        return changed;
    }
    public static uint RemoveAllNonTerminatorAndEHPadInstructions(LlvmBasicBlock block, Dictionary<string, ulong>? metrics = null)
    {
        uint count = 0;
        LlvmInstruction end = block.GetTerminator();
        List<LlvmValue> uses = [];
        HandleUnreachableTerminator(end, uses, metrics);
        while (end != block.FirstInstruction)
        {
            LlvmInstruction instruction = end.Previous!;
            if (!instruction.UseEmpty && !instruction.Type.IsTokenTy()) instruction.ReplaceAllUsesWith(LlvmPoisonValue.Get(instruction.Type));
            if (instruction.IsEHPad || instruction.Type.IsTokenTy()) { end = instruction; continue; }
            ++count;
            instruction.EraseFromParent();
        }
        if (metrics is not null) metrics["local.unreachable.nonterminators-erased"] =
            metrics.GetValueOrDefault("local.unreachable.nonterminators-erased") + count;
        return count;
    }
}
