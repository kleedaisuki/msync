// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/DataLayout.cpp S/F specifications and getPreferredAlign.
namespace Msync.Core.Llvm;

public enum LlvmFunctionPointerAlignType { Independent, MultipleOfFunctionAlign }

public sealed partial class LlvmDataLayout
{
    private LlvmAlign? stackNaturalAlignment, functionPointerAlignment;
    private LlvmFunctionPointerAlignType functionPointerAlignmentType;
    public LlvmAlign? GetStackAlignment() => stackNaturalAlignment;
    public LlvmAlign? GetFunctionPtrAlign() => functionPointerAlignment;
    public LlvmFunctionPointerAlignType GetFunctionPtrAlignType() => functionPointerAlignmentType;

    private bool ParseNaturalAlignmentSpecification(string specification)
    {
        if (specification[0] == 'S')
        {
            if (specification.Length == 1) throw new FormatException("malformed specification, must be of the form \"S<size>\"");
            stackNaturalAlignment = ParsePointerAlignment(specification[1..], "stack natural");
            return true;
        }
        if (specification[0] != 'F') return false;
        if (specification.Length == 1) throw new FormatException("malformed specification, must be of the form \"F<type><abi>\"");
        functionPointerAlignmentType = specification[1] switch
        {
            'i' => LlvmFunctionPointerAlignType.Independent,
            'n' => LlvmFunctionPointerAlignType.MultipleOfFunctionAlign,
            char kind => throw new FormatException($"unknown function pointer alignment type '{kind}'")
        };
        functionPointerAlignment = ParsePointerAlignment(specification[2..], "ABI");
        return true;
    }

    public LlvmAlign GetPreferredAlign(LlvmGlobalVariable global)
    {
        LlvmAlign? explicitAlignment = global.Alignment;
        if (explicitAlignment is { } sectionAlignment && global.HasSection) return sectionAlignment;
        LlvmAlign alignment = GetPrefTypeAlign(global.ValueType);
        if (explicitAlignment is { } requested)
            alignment = requested >= alignment ? requested : LlvmAlign.Max(requested, GetABITypeAlign(global.ValueType));
        if (global.HasInitializer && explicitAlignment is null && alignment.Value < 16
            && GetTypeSizeInBits(global.ValueType).KnownMinValue > 128) return new(16);
        return alignment;
    }
}
