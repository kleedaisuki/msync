// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantRange.cpp
// getEquivalentICmp overloads.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    public void GetEquivalentICmp(out LlvmCmpPredicate predicate, out LlvmAPInt rhs, out LlvmAPInt offset)
    {
        offset = LlvmAPInt.GetZero(BitWidth);
        if (IsFullSet || IsEmptySet)
        {
            predicate = IsEmptySet ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge;
            rhs = LlvmAPInt.GetZero(BitWidth);
        }
        else if (GetSingleElement() is { } only)
        { predicate = LlvmCmpPredicate.ICmpEq; rhs = only; }
        else if (GetSingleMissingElement() is { } missing)
        { predicate = LlvmCmpPredicate.ICmpNe; rhs = missing; }
        else if (Lower.IsMinSignedValue || Lower.IsZero)
        { predicate = Lower.IsMinSignedValue ? LlvmCmpPredicate.ICmpSlt : LlvmCmpPredicate.ICmpUlt; rhs = Upper; }
        else if (Upper.IsMinSignedValue || Upper.IsZero)
        { predicate = Upper.IsMinSignedValue ? LlvmCmpPredicate.ICmpSge : LlvmCmpPredicate.ICmpUge; rhs = Lower; }
        else
        { predicate = LlvmCmpPredicate.ICmpUlt; rhs = Upper - Lower; offset = -Lower; }
        Debug.Assert(MakeExactICmpRegion(predicate, rhs) == Add(new LlvmConstantRange(offset)));
    }

    public bool GetEquivalentICmp(out LlvmCmpPredicate predicate, out LlvmAPInt rhs)
    {
        GetEquivalentICmp(out predicate, out rhs, out var offset);
        return offset.IsZero;
    }
}
