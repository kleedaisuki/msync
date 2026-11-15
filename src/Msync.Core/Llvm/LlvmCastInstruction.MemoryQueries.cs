// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/Instructions.cpp CastInst::isNoopCast/isBitCastable/isBitOrNoopPointerCastable.
namespace Msync.Core.Llvm;

public sealed partial class LlvmCastInstruction
{
    public bool IsNoopCast(LlvmDataLayout layout) => IsNoopCast(Opcode, Operand.Type, Type, layout);
    public static bool IsNoopCast(LlvmOpcode opcode, LlvmType source, LlvmType destination, LlvmDataLayout layout) => opcode switch
    {
        LlvmOpcode.BitCast => true,
        LlvmOpcode.PtrToAddr or LlvmOpcode.PtrToInt => layout.GetPointerTypeSizeInBits(source) == destination.GetScalarSizeInBits(),
        LlvmOpcode.IntToPtr => layout.GetPointerTypeSizeInBits(destination) == source.GetScalarSizeInBits(),
        LlvmOpcode.Trunc or LlvmOpcode.ZExt or LlvmOpcode.SExt or LlvmOpcode.FPTrunc or LlvmOpcode.FPExt or
        LlvmOpcode.UIToFP or LlvmOpcode.SIToFP or LlvmOpcode.FPToUI or LlvmOpcode.FPToSI or LlvmOpcode.AddrSpaceCast => false,
        _ => throw new InvalidOperationException("Invalid opcode in source CastInst::isNoopCast.")
    };

    public static bool IsBitCastable(LlvmType source, LlvmType destination)
    {
        if (!source.IsFirstClassType() || !destination.IsFirstClassType()) return false;
        if (source == destination) return true;
        if (source is LlvmVectorType sourceVector && destination is LlvmVectorType destinationVector &&
            sourceVector.ElementCount == destinationVector.ElementCount)
        { source = sourceVector.ElementType; destination = destinationVector.ElementType; }
        if (destination is LlvmPointerType destinationPointer && source is LlvmPointerType sourcePointer)
            return sourcePointer.AddressSpace == destinationPointer.AddressSpace;
        var sourceBits = source.GetPrimitiveSizeInBits();
        var destinationBits = destination.GetPrimitiveSizeInBits();
        if (sourceBits.GetKnownMinValue() == 0 || destinationBits.GetKnownMinValue() == 0) return false;
        return sourceBits == destinationBits;
    }

    public static bool IsBitOrNoopPointerCastable(LlvmType source, LlvmType destination, LlvmDataLayout layout)
    {
        if (source is LlvmPointerType && destination is LlvmIntegerType destinationInteger)
            return destinationInteger.BitWidth == layout.GetPointerTypeSizeInBits(source) && !layout.IsNonIntegralPointerType(source);
        if (destination is LlvmPointerType && source is LlvmIntegerType sourceInteger)
            return sourceInteger.BitWidth == layout.GetPointerTypeSizeInBits(destination) && !layout.IsNonIntegralPointerType(destination);
        return IsBitCastable(source, destination);
    }
}
