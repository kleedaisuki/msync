// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp isSignBitCheck.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool IsSignBitCheck(LlvmCmpPredicate predicate, LlvmAPInt rhs, out bool trueIfSigned)
    {
        trueIfSigned = predicate is LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUge;
        return predicate switch
        {
            LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSge => rhs.IsZero,
            LlvmCmpPredicate.ICmpSle or LlvmCmpPredicate.ICmpSgt => rhs.IsAllOnes,
            LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpUle => rhs.IsMaxSignedValue,
            LlvmCmpPredicate.ICmpUge or LlvmCmpPredicate.ICmpUlt => rhs.IsMinSignedValue,
            _ => false
        };
    }
}
