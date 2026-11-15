// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp matchClamp,
// isSignedMinMaxIntrinsicClamp; PatternMatch.h m_[SU]Min/Max and m_Intrinsic.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSelectPattern
{
    public static bool TryMatchMinMaxIntrinsic(LlvmValue value, LlvmSelectPatternFlavor expected,
        out LlvmValue? left, out LlvmValue? right, Dictionary<string, ulong>? metrics = null)
    {
        left = right = null;
        Count(metrics, "select_pattern.intrinsic.queries");
        if (value is not LlvmCallInstruction call || call.CalledFunction is not { } function) return false;
        LlvmIntrinsicDescriptor? descriptor = LlvmIntrinsic.GetDescriptor(function);
        if (descriptor is null || IntrinsicFlavor(descriptor.Kind) != expected) return false;
        // Source m_Intrinsic first matches the direct CallInst/Function identity,
        // then applies m_Argument<0/1> in this order; it does not commute operands.
        left = call.GetArgument(0); right = call.GetArgument(1);
        Count(metrics, "select_pattern.intrinsic.matched");
        return true;
    }
    private static LlvmSelectPatternFlavor IntrinsicFlavor(LlvmIntrinsicKind kind) =>
        kind == LlvmIntrinsicKind.SMin ? LlvmSelectPatternFlavor.SMin :
        kind == LlvmIntrinsicKind.SMax ? LlvmSelectPatternFlavor.SMax :
        kind == LlvmIntrinsicKind.UMin ? LlvmSelectPatternFlavor.UMin :
        kind == LlvmIntrinsicKind.UMax ? LlvmSelectPatternFlavor.UMax : LlvmSelectPatternFlavor.Unknown;

    private static LlvmSelectPatternFlavor MatchClamp(LlvmCmpPredicate predicate, LlvmValue cmpLeft, LlvmValue cmpRight,
        LlvmValue whenTrue, LlvmValue whenFalse, Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "select_pattern.clamp.queries");
        if (!ReferenceEquals(cmpRight, whenTrue))
        {
            predicate = LlvmCmpInstruction.GetSwappedPredicate(predicate);
            (whenTrue, whenFalse) = (whenFalse, whenTrue);
        }
        if (!ReferenceEquals(cmpRight, whenTrue) || cmpRight is not LlvmConstantInt first) return LlvmSelectPatternFlavor.Unknown;
        if (TryMatchMinMaxIntrinsic(whenFalse, LlvmSelectPatternFlavor.SMin, out LlvmValue? left, out LlvmValue? right, metrics) &&
            ReferenceEquals(left, cmpLeft) && right is LlvmConstantInt secondMin && first.Value.Slt(secondMin.Value) &&
            predicate == LlvmCmpPredicate.ICmpSlt) return LlvmSelectPatternFlavor.SMax;
        if (TryMatchMinMaxIntrinsic(whenFalse, LlvmSelectPatternFlavor.SMax, out left, out right, metrics) &&
            ReferenceEquals(left, cmpLeft) && right is LlvmConstantInt secondMax && first.Value.Sgt(secondMax.Value) &&
            predicate == LlvmCmpPredicate.ICmpSgt) return LlvmSelectPatternFlavor.SMin;
        if (TryMatchMinMaxIntrinsic(whenFalse, LlvmSelectPatternFlavor.UMin, out left, out right, metrics) &&
            ReferenceEquals(left, cmpLeft) && right is LlvmConstantInt secondUnsignedMin && first.Value.Ult(secondUnsignedMin.Value) &&
            predicate == LlvmCmpPredicate.ICmpUlt) return LlvmSelectPatternFlavor.UMax;
        if (TryMatchMinMaxIntrinsic(whenFalse, LlvmSelectPatternFlavor.UMax, out left, out right, metrics) &&
            ReferenceEquals(left, cmpLeft) && right is LlvmConstantInt secondUnsignedMax && first.Value.Ugt(secondUnsignedMax.Value) &&
            predicate == LlvmCmpPredicate.ICmpUgt) return LlvmSelectPatternFlavor.UMin;
        return LlvmSelectPatternFlavor.Unknown;
    }
    public static bool TrySignedMinMaxIntrinsicClamp(LlvmValue value, out LlvmAPInt low, out LlvmAPInt high,
        Dictionary<string, ulong>? metrics = null)
    {
        low = high = default;
        Count(metrics, "select_pattern.intrinsic_clamp.queries");
        if (value is not LlvmCallInstruction call || call.CalledFunction is not { } function ||
            LlvmIntrinsic.GetDescriptor(function) is not { } descriptor) return false;
        LlvmSelectPatternFlavor outer = IntrinsicFlavor(descriptor.Kind);
        if (outer is not (LlvmSelectPatternFlavor.SMin or LlvmSelectPatternFlavor.SMax)) return false;
        LlvmSelectPatternFlavor inverse = GetInverseMinMaxFlavor(outer);
        if (!TryMatchMinMaxIntrinsic(call.GetArgument(0), inverse, out _, out LlvmValue? innerBound, metrics) ||
            call.GetArgument(1) is not LlvmConstantInt outerConstant || innerBound is not LlvmConstantInt innerConstant) return false;
        low = outerConstant.Value; high = innerConstant.Value;
        if (outer == LlvmSelectPatternFlavor.SMin) (low, high) = (high, low);
        bool matched = low.Sle(high);
        if (matched) Count(metrics, "select_pattern.intrinsic_clamp.matched");
        return matched;
    }
}
