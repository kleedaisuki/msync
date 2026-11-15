// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SROA.cpp getAdjustedPtr/getAdjustedAlignment/canConvertValue.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSroa
{
    internal static LlvmValue GetAdjustedPtr(LlvmTransformBuilder builder, LlvmDataLayout layout, LlvmValue pointer,
        LlvmAPInt offset, LlvmType pointerType, string prefix)
    {
        if (!offset.IsZero)
            pointer = builder.CreatePtrAdd(pointer, LlvmConstantInt.Get(pointer.Context, offset), prefix + "sroa_idx", LlvmGepNoWrapFlags.InBounds);
        if (ReferenceEquals(pointer.Type, pointerType)) return pointer;
        var oldScalar = (LlvmPointerType)pointer.Type.GetScalarType();
        var newScalar = (LlvmPointerType)pointerType.GetScalarType();
        return builder.CreateCast(oldScalar.AddressSpace == newScalar.AddressSpace ? LlvmOpcode.BitCast : LlvmOpcode.AddrSpaceCast,
            pointer, pointerType, prefix + "sroa_cast");
    }

    internal static LlvmAlign GetAdjustedAlignment(LlvmInstruction instruction, ulong offset) =>
        LlvmAlign.CommonAlignment(instruction switch
        {
            LlvmLoadInstruction load => load.Alignment,
            LlvmStoreInstruction store => store.Alignment,
            _ => throw new InvalidOperationException("SROA adjusted alignment requires a load or store.")
        }, offset);

    internal static bool CanConvertValue(LlvmDataLayout layout, LlvmType oldType, LlvmType newType, uint vscale = 0)
    {
        if (ReferenceEquals(oldType, newType)) return true;
        if (oldType.IsIntegerTy() && newType.IsIntegerTy())
        {
            Debug.Assert(oldType.GetIntegerBitWidth() != newType.GetIntegerBitWidth());
            return false;
        }
        var newSize = layout.GetTypeSizeInBits(newType);
        var oldSize = layout.GetTypeSizeInBits(oldType);
        if (newType is LlvmScalableVectorType && oldType is LlvmFixedVectorType ||
            oldType is LlvmScalableVectorType && newType is LlvmFixedVectorType)
        {
            if (vscale == 0) return false;
            var oldVectorType = oldType.IsPtrOrPtrVectorTy() ? layout.GetIntPtrType(oldType) : oldType;
            var newVectorType = newType.IsPtrOrPtrVectorTy() ? layout.GetIntPtrType(newType) : newType;
            if (newType is LlvmScalableVectorType)
            {
                if (LlvmVectorType.GetWithSizeAndScalar((LlvmVectorType)newVectorType, oldVectorType) is null) return false;
                newSize = LlvmTypeSize.Fixed(unchecked(newSize.KnownMinValue * vscale));
            }
            else
            {
                if (LlvmVectorType.GetWithSizeAndScalar((LlvmVectorType)oldVectorType, newVectorType) is null) return false;
                oldSize = LlvmTypeSize.Fixed(unchecked(oldSize.KnownMinValue * vscale));
            }
        }
        if (newSize != oldSize || !newType.IsSingleValueType() || !oldType.IsSingleValueType()) return false;
        oldType = oldType.GetScalarType();
        newType = newType.GetScalarType();
        if (newType.IsPointerTy() || oldType.IsPointerTy())
        {
            if (newType is LlvmPointerType newPointer && oldType is LlvmPointerType oldPointer)
                return newPointer.AddressSpace == oldPointer.AddressSpace ||
                    !layout.IsNonIntegralAddressSpace(oldPointer.AddressSpace) && !layout.IsNonIntegralAddressSpace(newPointer.AddressSpace)
                    && layout.GetPointerSize(oldPointer.AddressSpace) == layout.GetPointerSize(newPointer.AddressSpace);
            if (oldType.IsIntegerTy()) return !layout.IsNonIntegralPointerType(newType);
            if (!layout.IsNonIntegralPointerType(oldType)) return newType.IsIntegerTy();
            return false;
        }
        return !oldType.IsTargetExtTy() && !newType.IsTargetExtTy();
    }

    private static void CountType(Dictionary<string, ulong>? metrics, string name, ulong amount = 1)
    {
        if (metrics is null) return;
        string key = "sroa.types." + name;
        metrics[key] = metrics.GetValueOrDefault(key) + amount;
    }

    private static bool RejectType(Dictionary<string, ulong>? metrics, string reason)
    {
        CountType(metrics, reason);
        return false;
    }
}
