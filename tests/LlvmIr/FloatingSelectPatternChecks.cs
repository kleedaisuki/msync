using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class FloatingSelectPatternChecks
{
    internal static int Run()
    {
        LlvmContext context = new(); LlvmModule module = new("fp-select-patterns", context);
        LlvmType type = LlvmType.GetDoubleTy(context);
        LlvmFunction function = LlvmFunction.Create(LlvmFunctionType.Get(type, [type, type]), LlvmLinkage.InternalLinkage, "patterns", module);
        LlvmBasicBlock block = new(context, "entry", function);
        LlvmValue x = function.Arguments[0], y = function.Arguments[1];
        LlvmConstantFP one = LlvmConstantFP.Get(context, LlvmAPFloat.GetOne(LlvmAPFloatSemantics.IEEEdouble));
        LlvmConstantFP zero = LlvmConstantFP.Get(context, LlvmAPFloat.GetZero(LlvmAPFloatSemantics.IEEEdouble));
        LlvmConstantFP negativeZero = LlvmConstantFP.Get(context, LlvmAPFloat.GetZero(LlvmAPFloatSemantics.IEEEdouble, true));
        LlvmFastMathFlags fast = LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros;
        Require(Pattern(x, y, x, y).Flavor == LlvmSelectPatternFlavor.Unknown, "unqualified min/max preserves signed-zero behavior");
        Require(Pattern(x, y, x, y, LlvmFastMathFlags.NoSignedZeros).Flavor == LlvmSelectPatternFlavor.Unknown, "unknown NaN behavior is not silently ignored");
        LlvmSelectPatternResult minimum = Pattern(x, y, x, y, fast);
        Require(minimum.Flavor == LlvmSelectPatternFlavor.FMinNum && minimum.NaNBehavior == LlvmSelectPatternNaNBehavior.ReturnsAny && minimum.Ordered,
            "nnan+nsz direct select has exact min and ordering descriptor");
        LlvmSelectPatternResult constantMin = Pattern(x, one, x, one);
        Require(constantMin.Flavor == LlvmSelectPatternFlavor.FMinNum && constantMin.NaNBehavior == LlvmSelectPatternNaNBehavior.ReturnsOther && constantMin.Ordered,
            "ordered comparison with safe RHS returns the non-NaN operand");
        LlvmSelectPatternResult inverse = Pattern(x, one, one, x);
        Require(inverse.Flavor == LlvmSelectPatternFlavor.FMaxNum && inverse.NaNBehavior == LlvmSelectPatternNaNBehavior.ReturnsNaN && !inverse.Ordered && inverse.LHS == x && inverse.RHS == one,
            "reversed select flips NaN behavior and required ordering, not output bindings");
        LlvmFCmpInstruction zeros = new(LlvmCmpPredicate.FCmpOlt, x, negativeZero);
        LlvmSelectInstruction chooseZero = new(zeros, x, zero) { FastMathFlags = fast };
        block.Append(zeros); block.Append(chooseZero);
        LlvmSelectPatternResult zeroPattern = LlvmSelectPattern.Match(chooseZero);
        Require(zeroPattern.Flavor == LlvmSelectPatternFlavor.FMinNum && zeroPattern.RHS == zero && zeros.Right == negativeZero,
            "analysis unifies compare-zero with output-zero without mutating the source comparison");
        Require(Pattern(x, zero, x, zero, LlvmFastMathFlags.NoNaNs).Flavor == LlvmSelectPatternFlavor.Unknown,
            "nnan alone does not erase signed-zero requirements");
        Require(Pattern(x, y, x, y, comparisonFlags: fast).Flavor == LlvmSelectPatternFlavor.Unknown,
            "only comparison nnan transfers to select analysis, not comparison nsz");

        LlvmCallInstruction inner = LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.MinNum, [x, one], module);
        block.Append(inner);
        LlvmConstantFP minusOne = LlvmConstantFP.Get(context, one.Value.ChangeSign());
        LlvmSelectPatternResult clamp = Pattern(x, minusOne, minusOne, inner, LlvmFastMathFlags.NoNaNs);
        Require(clamp.Flavor == LlvmSelectPatternFlavor.FMaxNum && clamp.NaNBehavior == LlvmSelectPatternNaNBehavior.ReturnsAny && clamp.LHS == minusOne && clamp.RHS == inner,
            "fast clamp recognizes the source inner minnum call");
        LlvmFCmpInstruction rawCompare = new(LlvmCmpPredicate.FCmpOlt, x, y);
        LlvmSelectInstruction rawSelect = new(rawCompare, y, x);
        block.Append(rawCompare); block.Append(rawSelect);
        Require(LlvmSelectPattern.TryMatchOrdOrUnordFMax(rawSelect, out LlvmValue? rawLeft, out LlvmValue? rawRight) && rawLeft == x && rawRight == y,
            "raw PatternMatch min/max captures comparison operand order exactly");
        LlvmFCmpInstruction beforeCast = new(LlvmCmpPredicate.FCmpOlt, x, one) { FastMathFlags = LlvmFastMathFlags.NoNaNs };
        LlvmCastInstruction converted = new(LlvmOpcode.FPToSI, x, LlvmType.GetInt32Ty(context));
        block.Append(beforeCast); block.Append(converted);
        LlvmSelectPatternResult cast = LlvmSelectPattern.MatchDecomposed(beforeCast, converted, LlvmConstantInt.Get(LlvmType.GetInt32Ty(context), 1), lookThroughCasts: true);
        Require(cast.Flavor == LlvmSelectPatternFlavor.FMinNum && cast.CastOpcode == LlvmOpcode.FPToSI && cast.LHS == x && cast.RHS == one,
            "FP-to-int lookthrough uses actual constant casts and source nsz permission");
        block.Append(new LlvmReturnInstruction(context, zero));
        return 11;

        LlvmSelectPatternResult Pattern(LlvmValue lhs, LlvmValue rhs, LlvmValue whenTrue, LlvmValue whenFalse,
            LlvmFastMathFlags flags = LlvmFastMathFlags.None, LlvmFastMathFlags comparisonFlags = LlvmFastMathFlags.None)
        {
            LlvmFCmpInstruction compare = new(LlvmCmpPredicate.FCmpOlt, lhs, rhs) { FastMathFlags = comparisonFlags };
            LlvmSelectInstruction select = new(compare, whenTrue, whenFalse) { FastMathFlags = flags };
            block.Append(compare); block.Append(select);
            return LlvmSelectPattern.Match(select);
        }
    }

    private static void Require(bool condition, string cause)
    { if (!condition) throw new InvalidOperationException($"Floating select pattern: {cause}"); }
}
