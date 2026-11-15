// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/ValueTracking.cpp getRangeForSelectPattern.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantRangeAnalysis
{
    private static LlvmConstantRange RangeForSelectPattern(LlvmSelectInstruction select, bool useInfo, Dictionary<string, ulong>? metrics)
    {
        uint width = select.Type.GetIntegerBitWidth();
        var pattern = LlvmSelectPattern.Match(select, metrics);
        var zero = LlvmAPInt.GetZero(width);
        var signedMin = LlvmAPInt.GetSignedMinValue(width);
        if (pattern.Flavor == LlvmSelectPatternFlavor.Unknown) return LlvmConstantRange.Full(width);
        if (pattern.Flavor == LlvmSelectPatternFlavor.Abs)
        {
            bool noSignedWrap = pattern.RHS is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, HasNoSignedWrap: true } negation
                && IsNegationOf(negation, pattern.LHS!) && useInfo;
            return LlvmConstantRange.GetNonEmpty(zero, noSignedWrap
                ? LlvmConstantRange.Inc(LlvmAPInt.GetSignedMaxValue(width)) : LlvmConstantRange.Inc(signedMin));
        }
        if (pattern.Flavor == LlvmSelectPatternFlavor.NAbs) return LlvmConstantRange.GetNonEmpty(signedMin, new(width, 1));
        var constant = pattern.LHS as LlvmConstantInt ?? pattern.RHS as LlvmConstantInt;
        if (constant is null) return LlvmConstantRange.Full(width);
        var value = constant.Value;
        return pattern.Flavor switch
        {
            LlvmSelectPatternFlavor.UMin => LlvmConstantRange.GetNonEmpty(zero, LlvmConstantRange.Inc(value)),
            LlvmSelectPatternFlavor.UMax => LlvmConstantRange.GetNonEmpty(value, zero),
            LlvmSelectPatternFlavor.SMin => LlvmConstantRange.GetNonEmpty(signedMin, LlvmConstantRange.Inc(value)),
            LlvmSelectPatternFlavor.SMax => LlvmConstantRange.GetNonEmpty(value, LlvmConstantRange.Inc(LlvmAPInt.GetSignedMaxValue(width))),
            _ => LlvmConstantRange.Full(width)
        };
    }
}
