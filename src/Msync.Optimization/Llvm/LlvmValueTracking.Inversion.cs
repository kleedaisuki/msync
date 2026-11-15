// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp isKnownInversion.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool IsKnownInversion(LlvmValue left, LlvmValue right)
    {
        if (left is not LlvmICmpInstruction a || right is not LlvmICmpInstruction b || a.SameSign != b.SameSign) return false;
        var predicate = b.Predicate;
        LlvmValue other;
        if (ReferenceEquals(b.Left, a.Left)) other = b.Right;
        else if (ReferenceEquals(b.Right, a.Left)) { other = b.Left; predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate); }
        else return false;
        if (ReferenceEquals(a.Right, other)) return a.Predicate == LlvmCmpInstruction.GetInversePredicate(predicate);
        if (a.Right is not LlvmConstantInt c1 || other is not LlvmConstantInt c2) return false;
        if (a.SameSign && c1.Value.IsNegative != c2.Value.IsNegative) return false;
        return LlvmConstantRange.MakeExactICmpRegion(a.Predicate, c1.Value).Inverse() == LlvmConstantRange.MakeExactICmpRegion(predicate, c2.Value);
    }
}
