// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/Instructions.cpp AllocaInst::isArrayAllocation/getAllocationSize/getAllocationSizeInBits.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmAllocaInstruction
{
    public bool IsArrayAllocation => ArraySize is not LlvmConstantInt { IsOne: true };
    public LlvmTypeSize GetAllocationBaseSize(LlvmDataLayout layout) => layout.GetTypeAllocSize(AllocatedType);

    public LlvmTypeSize? GetAllocationSize(LlvmDataLayout layout)
    {
        var size = GetAllocationBaseSize(layout);
        if (size.IsZero || !IsArrayAllocation) return size;
        if (ArraySize is not LlvmConstantInt count || count.Value.TryZExtValue() is not { } elements) return null;
        Debug.Assert(!size.IsScalable, "Array elements cannot have a scalable size");
        if (elements != 0 && size.KnownMinValue > ulong.MaxValue / elements) return null;
        return LlvmTypeSize.Fixed(unchecked(size.KnownMinValue * elements));
    }

    public LlvmTypeSize? GetAllocationSizeInBits(LlvmDataLayout layout)
    {
        if (GetAllocationSize(layout) is not { } size || size.KnownMinValue > ulong.MaxValue / 8) return null;
        return LlvmTypeSize.Get(size.KnownMinValue * 8, size.IsScalable);
    }
}
