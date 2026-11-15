// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.h
// minnum/maxnum/minimum/maximum/minimumnum/maximumnum.
namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    public bool IsInteger() => IsFinite &&
        Compare(RoundToIntegral(LlvmRoundingMode.TowardZero).Value) == LlvmFloatCompare.Equal;
    public LlvmAPFloat ClearSign() => FromBits(Semantics, bits & ~Format.SignMask);
    public LlvmAPFloat CopySign(LlvmAPFloat sign) => FromBits(Semantics,
        (bits & ~Format.SignMask) | (sign.IsNegative ? Format.SignMask : 0));
    public LlvmAPFloat MakeQuiet() => IsNaN ? FromBits(Semantics, bits | (1UL << (Format.Precision - 2))) : this;
    public static LlvmAPFloat MinNum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsSignaling) return left.MakeQuiet();
        if (right.IsSignaling) return right.MakeQuiet();
        if (left.IsNaN) return right;
        if (right.IsNaN) return left;
        return OrderedMinimum(left, right);
    }
    public static LlvmAPFloat MaxNum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsSignaling) return left.MakeQuiet();
        if (right.IsSignaling) return right.MakeQuiet();
        if (left.IsNaN) return right;
        if (right.IsNaN) return left;
        return OrderedMaximum(left, right);
    }
    public static LlvmAPFloat Minimum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsNaN) return left.MakeQuiet();
        if (right.IsNaN) return right.MakeQuiet();
        return OrderedMinimum(left, right);
    }
    public static LlvmAPFloat Maximum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsNaN) return left.MakeQuiet();
        if (right.IsNaN) return right.MakeQuiet();
        return OrderedMaximum(left, right);
    }
    public static LlvmAPFloat MinimumNum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsNaN) return right.IsNaN ? right.MakeQuiet() : right;
        if (right.IsNaN) return left;
        return OrderedMinimum(left, right);
    }
    public static LlvmAPFloat MaximumNum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsNaN) return right.IsNaN ? right.MakeQuiet() : right;
        if (right.IsNaN) return left;
        return OrderedMaximum(left, right);
    }
    private static LlvmAPFloat OrderedMinimum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsZero && right.IsZero && left.IsNegative != right.IsNegative) return left.IsNegative ? left : right;
        return right.Compare(left) == LlvmFloatCompare.LessThan ? right : left;
    }
    private static LlvmAPFloat OrderedMaximum(LlvmAPFloat left, LlvmAPFloat right)
    {
        if (left.IsZero && right.IsZero && left.IsNegative != right.IsNegative) return left.IsNegative ? right : left;
        return left.Compare(right) == LlvmFloatCompare.LessThan ? right : left;
    }
}
