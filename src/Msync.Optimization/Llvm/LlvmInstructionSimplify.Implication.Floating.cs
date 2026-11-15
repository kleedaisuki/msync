// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp isImpliedCondFCmps.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static bool? ImpliedFloatingCompares(LlvmCmpPredicate lp, LlvmValue l0, LlvmValue l1,
        LlvmCmpPredicate rp, LlvmValue r0, LlvmValue r1, bool lhsIsTrue)
    {
        if (!lhsIsTrue) lp = LlvmCmpInstruction.GetInversePredicate(lp);
        if (l0 == r1) { (r0, r1) = (r1, r0); rp = LlvmCmpInstruction.GetSwappedPredicate(rp); }
        if (r0 == l1) { (l0, l1) = (l1, l0); lp = LlvmCmpInstruction.GetSwappedPredicate(lp); }
        if (l1 == r1 && (l0 != r0 || l0 is LlvmConstantFP))
        {
            (l0, l1) = (l1, l0); lp = LlvmCmpInstruction.GetSwappedPredicate(lp);
            (r0, r1) = (r1, r0); rp = LlvmCmpInstruction.GetSwappedPredicate(rp);
        }
        if (l0 == r0 && l1 == r1)
        {
            if (((byte)lp & (byte)rp) == (byte)lp) return true;
            if (((byte)lp & ~(byte)rp) == (byte)lp) return false;
        }
        if (l0 == r0 && l1 is LlvmConstantFP lc && r1 is LlvmConstantFP rc &&
            LlvmConstantFPRange.MakeExactFCmpRegion(lp, lc.Value) is { } dom)
        {
            if (LlvmConstantFPRange.MakeExactFCmpRegion(rp, rc.Value) is { } implied && implied.Contains(dom)) return true;
            if (LlvmConstantFPRange.MakeExactFCmpRegion(LlvmCmpInstruction.GetInversePredicate(rp), rc.Value) is { } inverse && inverse.Contains(dom)) return false;
        }
        return null;
    }
}
