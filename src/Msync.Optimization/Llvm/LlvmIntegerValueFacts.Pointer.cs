// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp
// isNonZeroAddrPlusOffset, isValidAssumeForContext and isEphemeralValueOf;
// Value.cpp getPointerDereferenceableBytes, Instructions.cpp getAllocationSize.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static bool IsNonZeroAddressPlusOffset(LlvmValue address, LlvmValue offset)
    {
        if (offset is not LlvmConstantInt { IsZero: false } constant ||
            address is not LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } cast ||
            (cast.Opcode == LlvmOpcode.PtrToInt && cast.Type.GetIntegerBitWidth() < 64) || !cast.Operand.Type.IsPointerTy()) return false;
        var pointer = cast.Operand;
        LlvmDataLayout? layout = pointer switch
        {
            LlvmInstruction instruction => instruction.Parent?.Module?.DataLayout,
            LlvmGlobalValue global => global.Parent?.DataLayout,
            _ => null
        };
        if (layout is null) return false;
        ulong bytes = LlvmValueTracking.GetPointerDereferenceableBytes(pointer, layout, out bool canBeNull, out _);
        if (canBeNull) return false;
        return bytes != 0 && constant.Value.TryZExtValue() is { } unsignedOffset && unsignedOffset <= bytes;
    }

    private static bool IsValidNonZeroUseContext(LlvmInstruction use, LlvmInstruction context,
        LlvmDominatorTree tree, Dictionary<string, ulong>? metrics) =>
        LlvmValueTracking.IsValidAssumeForContext(use, context, tree, metrics: metrics);
}
