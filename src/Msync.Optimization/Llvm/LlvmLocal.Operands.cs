// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Utils/Local.cpp
// canReplaceOperandWithVariable for represented instructions/intrinsic descriptors.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLocal
{
    public static bool CanReplaceOperandWithVariable(LlvmInstruction instruction, int operandIndex)
    {
        var operand = instruction.GetOperand(operandIndex)!;
        if (operand.Type.IsMetadataTy() || operand.Type.IsTokenLikeTy()) return false;
        if (operand is LlvmAllocaInstruction { SwiftError: true }) return false;
        if (operand is not LlvmConstant) return true;
        return instruction switch
        {
            LlvmCallBase call => CanReplaceCallConstant(call, operandIndex),
            LlvmSwitchInstruction or LlvmExtractValueInstruction => operandIndex == 0,
            LlvmInsertValueInstruction => operandIndex < 2,
            LlvmAllocaInstruction allocation => !allocation.IsStaticAlloca,
            LlvmGetElementPtrInstruction gep => CanReplaceGepIndex(gep, operandIndex),
            _ => true
        };
    }

    private static bool CanReplaceCallConstant(LlvmCallBase call, int index)
    {
        if (call.IsBundleOperand(index)) return false;
        bool intrinsic = call.CalledFunction?.IntrinsicDescriptor is not null;
        if (index >= call.ArgumentCount) return !intrinsic;
        // The four represented min/max intrinsics have no immarg parameters,
        // no variadic tail, and none of the source gcroot/threadlocal exceptions.
        if (intrinsic && index >= call.FunctionType.NumParams) return false;
        return true;
    }

    private static bool CanReplaceGepIndex(LlvmGetElementPtrInstruction instruction, int operandIndex)
    {
        if (operandIndex == 0) return true;
        var type = instruction.SourceElementType;
        for (int index = 1; index < operandIndex; ++index)
        {
            if (type is LlvmStructType) return false;
            type = LlvmGetElementPtrInstruction.GetTypeAtIndex(type, instruction.GetIndex(index))!;
        }
        return true;
    }
}
