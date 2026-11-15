// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp:6000-6480, scalar default FP environment;
// PatternMatch.h FNeg_match/m_FNegNSZ; APFloat.cpp makeQuiet (binary64 layout).
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    public static LlvmValue? SimplifyUnOp(LlvmOpcode opcode, LlvmValue operand,
        LlvmFastMathFlags flags, LlvmDataLayout layout, LlvmInstruction? context = null,
        Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false)
    {
        if (opcode != LlvmOpcode.FNeg) throw new InvalidOperationException("Expected FNeg.");
        return SimplifyFNeg(operand, new(layout, context, metrics, Assumptions: assumptions, AllowEphemerals: allowEphemerals));
    }

    private static LlvmValue? SimplifyFNeg(LlvmValue operand, BinaryQuery query)
    {
        Count(query.Metrics, "simplify.fneg.queries");
        if (operand is LlvmConstant constant &&
            LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, constant, query.Layout, query.Metrics) is { } folded)
            return folded;
        return MatchFNeg(operand);
    }

    private static LlvmValue? SimplifyFloatingBinary(LlvmOpcode opcode, LlvmValue left, LlvmValue right,
        LlvmFastMathFlags flags, BinaryQuery query)
    {
        Count(query.Metrics, "simplify.fp.queries");
        if (FoldOrCommuteConstant(opcode, ref left, ref right, query) is { } folded)
        {
            Count(query.Metrics, "simplify.fp.constant_fold");
            return folded;
        }
        if (SimplifyFPOperands(left, right, flags, query) is { } common)
        {
            Count(query.Metrics, "simplify.fp.operand_special");
            return common;
        }
        LlvmValue? result = opcode switch
        {
            LlvmOpcode.FAdd => SimplifyFAdd(left, right, flags, query),
            LlvmOpcode.FSub => SimplifyFSub(left, right, flags, query),
            LlvmOpcode.FMul => SimplifyFMul(left, right, flags, query),
            LlvmOpcode.FDiv => SimplifyFDiv(left, right, flags),
            LlvmOpcode.FRem => Has(flags, LlvmFastMathFlags.NoNaNs) && IsFPZero(left)
                ? LlvmConstantFP.Get(left.Type, IsFPNegativeZero(left) ? LlvmAPFloat.FromBits(0x8000000000000000UL) : new LlvmAPFloat(0.0)) : null,
            _ => throw new InvalidOperationException("Expected a floating-point binary opcode.")
        };
        Count(query.Metrics, result is null ? "simplify.fp.unchanged" : "simplify.fp.identity_or_pattern");
        return result;
    }

    private static LlvmConstant? SimplifyFPOperands(LlvmValue left, LlvmValue right,
        LlvmFastMathFlags flags, BinaryQuery query)
    {
        if (left is LlvmPoisonValue || right is LlvmPoisonValue) return LlvmPoisonValue.Get(left.Type);
        return SimplifyFPOperand(left, flags, query) ?? SimplifyFPOperand(right, flags, query);
    }

    private static LlvmConstant? SimplifyFPOperand(LlvmValue value, LlvmFastMathFlags flags, BinaryQuery query)
    {
        bool nan = MatchFPClassConstant(value, true);
        bool infinity = MatchFPClassConstant(value, false);
        bool undef = query.IsUndef(value);
        if (Has(flags, LlvmFastMathFlags.NoNaNs) && (nan || undef) ||
            Has(flags, LlvmFastMathFlags.NoInfs) && (infinity || undef))
            return LlvmPoisonValue.Get(value.Type);
        if (undef) return CanonicalNaN(value.Type);
        if (nan) return PropagateNaN((LlvmConstant)value);
        return null;
    }

    private static LlvmValue? SimplifyFAdd(LlvmValue left, LlvmValue right, LlvmFastMathFlags flags,
        BinaryQuery query)
    {
        if (IsFPNegativeZero(right)) return left;
        if (IsFPPositiveZero(right) && (Has(flags, LlvmFastMathFlags.NoSignedZeros) || CannotBeNegativeZero(left, query)))
            return left;
        if (Has(flags, LlvmFastMathFlags.NoNaNs))
        {
            if (right is LlvmConstantFP { Value.IsInfinity: true }) return right;
            if (IsFSubFromZero(left, right) || IsFSubFromZero(right, left) ||
                ReferenceEquals(MatchFNeg(left), right) || ReferenceEquals(MatchFNeg(right), left))
                return LlvmConstantFP.Get(left.Type, 0.0);
        }
        if (Has(flags, LlvmFastMathFlags.NoSignedZeros) && Has(flags, LlvmFastMathFlags.AllowReassoc))
        {
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } lhs && ReferenceEquals(lhs.Right, right)) return lhs.Left;
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } rhs && ReferenceEquals(rhs.Right, left)) return rhs.Left;
        }
        return null;
    }

    private static LlvmValue? SimplifyFSub(LlvmValue left, LlvmValue right, LlvmFastMathFlags flags,
        BinaryQuery query)
    {
        if (IsFPPositiveZero(right)) return left;
        if (IsFPNegativeZero(right) && (Has(flags, LlvmFastMathFlags.NoSignedZeros) || CannotBeNegativeZero(left, query)))
            return left;
        if (IsFPNegativeZero(left) && MatchFNeg(right) is { } negated) return negated;
        if (Has(flags, LlvmFastMathFlags.NoSignedZeros) && IsFPZero(left))
        {
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } subtraction && IsFPZero(subtraction.Left))
                return subtraction.Right;
            if (right is LlvmUnaryOperator negation) return negation.Operand;
        }
        if (Has(flags, LlvmFastMathFlags.NoNaNs))
        {
            if (ReferenceEquals(left, right)) return LlvmConstantFP.Get(left.Type, 0.0);
            if (left is LlvmConstantFP { Value.IsInfinity: true }) return left;
            if (right is LlvmConstantFP { Value.IsInfinity: true } infinity)
                return LlvmConstantFP.Get(left.Type, infinity.Value.ChangeSign());
        }
        if (Has(flags, LlvmFastMathFlags.NoSignedZeros) && Has(flags, LlvmFastMathFlags.AllowReassoc))
        {
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } subtraction && ReferenceEquals(subtraction.Left, left))
                return subtraction.Right;
            return MatchCommutative(left, LlvmOpcode.FAdd, right);
        }
        return null;
    }

    private static LlvmValue? SimplifyFMul(LlvmValue left, LlvmValue right, LlvmFastMathFlags flags,
        BinaryQuery query)
    {
        if (IsFPOne(left) || IsFPZero(left)) (left, right) = (right, left);
        if (IsFPOne(right)) return left;
        if (!IsFPZero(right)) return null;
        if (Has(flags, LlvmFastMathFlags.NoNaNs) && Has(flags, LlvmFastMathFlags.NoSignedZeros))
            return LlvmConstantFP.Get(left.Type, 0.0);
        var known = KnownFiniteSign(left, flags, query);
        if (!known.Finite) return null;
        if (Has(flags, LlvmFastMathFlags.NoSignedZeros)) return LlvmConstantFP.Get(left.Type, 0.0);
        if (known.Negative == false) return right;
        if (known.Negative == true && right is LlvmConstantFP zero)
            return LlvmConstantFP.Get(right.Type, zero.Value.ChangeSign());
        return null;
    }

    private static LlvmValue? SimplifyFDiv(LlvmValue left, LlvmValue right, LlvmFastMathFlags flags)
    {
        if (IsFPOne(right)) return left;
        bool noNaNs = Has(flags, LlvmFastMathFlags.NoNaNs);
        if (noNaNs && Has(flags, LlvmFastMathFlags.NoSignedZeros) && IsFPZero(left))
            return LlvmConstantFP.Get(left.Type, 0.0);
        if (!noNaNs) return null;
        if (ReferenceEquals(left, right)) return LlvmConstantFP.Get(left.Type, 1.0);
        if (Has(flags, LlvmFastMathFlags.AllowReassoc) && MatchCommutative(left, LlvmOpcode.FMul, right) is { } factor)
            return factor;
        if (IsFSubFromZero(left, right) || IsFSubFromZero(right, left)) return LlvmConstantFP.Get(left.Type, -1.0);
        if (Has(flags, LlvmFastMathFlags.NoInfs) && IsFPZero(right)) return LlvmPoisonValue.Get(right.Type);
        return null;
    }

    private static LlvmValue? MatchFNeg(LlvmValue value)
    {
        if (value is LlvmUnaryOperator negation) return negation.Operand;
        if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } subtraction) return null;
        bool zero = Has(subtraction.FastMathFlags, LlvmFastMathFlags.NoSignedZeros)
            ? IsFPZero(subtraction.Left) : IsFPNegativeZero(subtraction.Left);
        return zero ? subtraction.Right : null;
    }

    private static bool IsFSubFromZero(LlvmValue value, LlvmValue operand) =>
        value is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } subtraction &&
        IsFPZero(subtraction.Left) && ReferenceEquals(subtraction.Right, operand);

    private static LlvmValue? MatchCommutative(LlvmValue value, LlvmOpcode opcode, LlvmValue operand)
    {
        if (value is not LlvmBinaryOperator binary || binary.Opcode != opcode) return null;
        if (ReferenceEquals(binary.Left, operand)) return binary.Right;
        return ReferenceEquals(binary.Right, operand) ? binary.Left : null;
    }

    private static bool CannotBeNegativeZero(LlvmValue value, BinaryQuery query) =>
        LlvmKnownFPClasses.CannotBeNegativeZero(value, query.Context, query.Tree, query.Metrics, assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC, condContext: query.CondContext);

    private static (bool Finite, bool? Negative) KnownFiniteSign(LlvmValue value, LlvmFastMathFlags flags,
        BinaryQuery query)
    {
        const LlvmFPClassTest classes = LlvmFPClassTest.NaN | LlvmFPClassTest.Infinity;
        LlvmKnownFPClass known = LlvmKnownFPClasses.Compute(value, flags, classes,
            query.Context, query.Tree, query.Metrics, assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC, condContext: query.CondContext);
        return (known.IsKnownNever(classes), known.SignBit);
    }

    private static bool Has(LlvmFastMathFlags flags, LlvmFastMathFlags bit) => (flags & bit) != 0;
    private static bool IsFPZero(LlvmValue value) => value is LlvmConstantFP { IsZero: true };
    private static bool IsFPNegativeZero(LlvmValue value) => value is LlvmConstantFP { IsZero: true, IsNegative: true };
    private static bool IsFPPositiveZero(LlvmValue value) => value is LlvmConstantFP { IsZero: true, IsNegative: false };
    private static bool IsFPOne(LlvmValue value) => FMAConstant(value, out var number) && number.BitwiseIsEqual(LlvmAPFloat.GetOne(number.Semantics));
}
