// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: CloneFunction.cpp
// CloneBasicBlock and Cloning.h ClonedCodeInfo (represented diagnostic fields).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmClonedCodeInfo
{
    public bool ContainsCalls { get; set; }
    public bool ContainsMemProfMetadata { get; set; }
    public bool ContainsDynamicAllocas { get; set; }
}

public static class LlvmCloning
{
    public static LlvmBasicBlock CloneBasicBlock(LlvmBasicBlock block, IDictionary<LlvmValue, LlvmValue> valueMap,
        string nameSuffix = "", LlvmFunction? function = null, LlvmClonedCodeInfo? codeInfo = null, bool mapAtoms = true)
    {
        LlvmBasicBlock result = new(block.Context, "", function);
        if (block.HasName) result.SetName(block.Name + nameSuffix);
        bool calls = false, dynamicAllocas = false;
        foreach (var instruction in block.Instructions)
        {
            var clone = instruction.Clone();
            if (instruction.HasName) clone.SetName(instruction.Name + nameSuffix);
            result.Append(clone);
            valueMap[instruction] = clone;
            if (instruction is LlvmCallInstruction) calls = true;
            if (instruction is LlvmAllocaInstruction allocation && !allocation.IsStaticAlloca) dynamicAllocas = true;
        }
        if (codeInfo is not null)
        {
            codeInfo.ContainsCalls |= calls;
            codeInfo.ContainsDynamicAllocas |= dynamicAllocas;
        }
        return result;
    }
}
