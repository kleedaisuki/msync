// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/BundleAttributes.cpp
// getAssumeAlignInfo, getAssumeRangeInfo, assumeBundleImpliesNonNull;
// Analysis/AssumeBundleQueries.cpp retained alignment/dereferenceable knowledge.
using System.Numerics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static class LlvmAssumeBundleQueries
{
    public static (LlvmValue Pointer, ulong? Alignment, ulong? Offset) GetAssumeAlignInfo(LlvmOperandBundleUse bundle) =>
        (bundle.GetInput(0), ConstantUInt(bundle.GetInput(1)), bundle.InputCount == 3 ? ConstantUInt(bundle.GetInput(2)) : 0);
    public static (LlvmValue Value, LlvmConstantRange Range) GetAssumeRangeInfo(LlvmOperandBundleUse bundle) =>
        (bundle.GetInput(0), new(((LlvmConstantInt)bundle.GetInput(1)).Value, ((LlvmConstantInt)bundle.GetInput(2)).Value));

    public static bool AssumeBundleImpliesNonNull(LlvmValue value, LlvmOperandBundleUse bundle)
    {
        switch (bundle.Tag)
        {
            case "align":
                var alignment = GetAssumeAlignInfo(bundle);
                return ReferenceEquals(alignment.Pointer, value) && alignment.Alignment is { } align && alignment.Offset is { } offset &&
                    BitOperations.IsPow2(align) && offset % align != 0;
            case "dereferenceable":
                return ReferenceEquals(bundle.GetInput(0), value) && ConstantUInt(bundle.GetInput(1)) is > 0 && value.Type.GetPointerAddressSpace() == 0;
            case "nonnull":
                return ReferenceEquals(bundle.GetInput(0), value);
            case "range":
                var range = GetAssumeRangeInfo(bundle);
                return ReferenceEquals(range.Value, value) && !range.Range.Contains(LlvmAPInt.GetZero(range.Range.BitWidth));
            default:
                return false;
        }
    }

    internal static bool TryGetRetainedAlignmentOrDereferenceable(LlvmOperandBundleUse bundle, out LlvmValue? value,
        out bool isAlignment, out ulong argument)
    {
        value = null; argument = 0; isAlignment = bundle.Tag == "align";
        if (!isAlignment && bundle.Tag != "dereferenceable") return false;
        value = bundle.GetInput(0);
        // getKnowledgeFromBundle's defaults differ from the optional constant facts above.
        argument = bundle.InputCount > 1 ? ConstantUInt(bundle.GetInput(1)) ?? (isAlignment ? 1UL : 0UL) : 0;
        if (isAlignment && bundle.InputCount > 2)
        {
            ulong offset = ConstantUInt(bundle.GetInput(2)) ?? 1;
            ulong merged = argument | offset;
            argument = merged & unchecked(0UL - merged); // MathExtras.h MinAlign.
        }
        return true;
    }

    private static ulong? ConstantUInt(LlvmValue value) => value is LlvmConstantInt integer ? integer.GetZExtValue() : null;
}
