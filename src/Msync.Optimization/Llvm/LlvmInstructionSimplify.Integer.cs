// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionSimplify.cpp,
// simplifyAddInst, simplifySubInst, simplifyMulInst and integer binary dispatch.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmConstantInt IntegerZero(LlvmValue v) => LlvmConstantInt.Get((LlvmIntegerType)v.Type, 0);
    private static LlvmConstantInt IntegerOne(LlvmValue v) => LlvmConstantInt.Get((LlvmIntegerType)v.Type, 1);
    private static LlvmConstantInt IntegerAllOnes(LlvmValue v) =>
        LlvmConstantInt.Get(v.Type.Context, LlvmAPInt.GetAllOnes(((LlvmIntegerType)v.Type).BitWidth));
    private static bool IsIntegerZero(LlvmValue v) => v is LlvmConstantInt { IsZero: true };
    private static bool IsIntegerOne(LlvmValue v) => v is LlvmConstantInt { IsOne: true };
    private static bool IsIntegerAllOnes(LlvmValue v) => v is LlvmConstantInt { IsMinusOne: true };
    private static bool IsIntegerNot(LlvmValue v, LlvmValue other) =>
        v is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } b &&
        ((b.Left == other && IsIntegerAllOnes(b.Right)) || (b.Right == other && IsIntegerAllOnes(b.Left)));

    private static LlvmValue? SimplifyIntegerBinary(LlvmOpcode opcode, LlvmValue left, LlvmValue right,
        bool isNSW, bool isNUW, bool isExact, BinaryQuery query, uint recurse)
    {
        if (left.Type is not LlvmIntegerType) return null;
        Count(query.Metrics, "simplify.integer.queries");
        Count(query.Metrics, opcode switch
        {
            LlvmOpcode.Add => "simplify.integer.add",
            LlvmOpcode.Sub => "simplify.integer.sub",
            LlvmOpcode.Mul => "simplify.integer.mul",
            LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem => "simplify.integer.divrem",
            LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr => "simplify.integer.shift",
            _ => "simplify.integer.bitwise"
        });
        LlvmValue? result = opcode switch
        {
            LlvmOpcode.Add => SimplifyIntegerAdd(left, right, isNSW, isNUW, query, recurse),
            LlvmOpcode.Sub => SimplifyIntegerSub(left, right, isNSW, isNUW, query, recurse),
            LlvmOpcode.Mul => SimplifyIntegerMul(left, right, isNSW, isNUW, query, recurse),
            LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem =>
                SimplifyIntegerDivRem(opcode, left, right, isExact, query, recurse),
            LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr =>
                SimplifyIntegerShift(opcode, left, right, isNSW, isNUW, isExact, query, recurse),
            LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor =>
                SimplifyIntegerBitwise(opcode, left, right, query, recurse),
            _ => null
        };
        Count(query.Metrics, result switch
        {
            null => "simplify.integer.unchanged",
            LlvmPoisonValue => "simplify.integer.poison",
            LlvmUndefValue => "simplify.integer.undef",
            LlvmConstantInt => "simplify.integer.constant",
            _ => "simplify.integer.existing_value"
        });
        return result;
    }

    private static LlvmValue? SimplifyIntegerAdd(LlvmValue lhs, LlvmValue rhs, bool nsw, bool nuw,
        BinaryQuery q, uint depth)
    {
        if (FoldOrCommuteConstant(LlvmOpcode.Add, ref lhs, ref rhs, q) is { } folded) return folded;
        if (rhs is LlvmPoisonValue || q.IsUndef(rhs)) return rhs;
        if (IsIntegerZero(rhs)) return lhs;
        if (LlvmKnownBitsAnalysis.IsKnownNegation(lhs, rhs)) return IntegerZero(lhs);
        if (rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } r && r.Right == lhs) return r.Left;
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } l && l.Right == rhs) return l.Left;
        if (IsIntegerNot(lhs, rhs) || IsIntegerNot(rhs, lhs)) return IntegerAllOnes(lhs);
        if ((nsw || nuw) && rhs is LlvmConstantInt c && c.Value.IsMinSignedValue &&
            lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor && xor.Right == rhs) return xor.Left;
        if (nuw && IsIntegerAllOnes(rhs)) return rhs;
        if (depth != 0 && ((LlvmIntegerType)lhs.Type).BitWidth == 1 &&
            IntegerBin(LlvmOpcode.Xor, lhs, rhs, q, depth - 1) is { } bit) return bit;
        return ReassociateIntegerBin(LlvmOpcode.Add, lhs, rhs, q, depth);
    }

    private static LlvmValue? SimplifyIntegerSub(LlvmValue lhs, LlvmValue rhs, bool nsw, bool nuw,
        BinaryQuery q, uint depth)
    {
        if (FoldOrCommuteConstant(LlvmOpcode.Sub, ref lhs, ref rhs, q) is { } folded) return folded;
        if (lhs is LlvmPoisonValue || rhs is LlvmPoisonValue) return LlvmPoisonValue.Get(lhs.Type);
        if (q.IsUndef(lhs) || q.IsUndef(rhs)) return LlvmUndefValue.Get(lhs.Type);
        if (IsIntegerZero(rhs)) return lhs;
        if (lhs == rhs) return IntegerZero(lhs);
        if (IsIntegerZero(lhs))
        {
            if (nuw) return IntegerZero(lhs);
            var known = LlvmKnownBitsAnalysis.Compute(rhs, context: q.Context, tree: q.Tree, metrics: q.Metrics, assumptions: q.Assumptions, allowEphemerals: q.AllowEphemerals, conditions: q.DC, condContext: q.CondContext);
            if (known.Zero == LlvmAPInt.GetSignedMaxValue(((LlvmIntegerType)lhs.Type).BitWidth))
                return nsw ? IntegerZero(lhs) : rhs;
        }
        if (depth != 0 && ReassociateIntegerSub(lhs, rhs, q, depth - 1) is { } reassoc)
        { Count(q.Metrics, "simplify.binary.reassociate"); return reassoc; }
        if (lhs is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } lp &&
            rhs is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt or LlvmOpcode.PtrToAddr } rp &&
            ComputeIntegerPointerDifference(lp.Operand, rp.Operand, q) is { } difference)
            return LlvmConstantInt.Get(lhs.Type.Context, difference.Value.SExtOrTrunc(((LlvmIntegerType)lhs.Type).BitWidth));
        if (depth != 0 && ((LlvmIntegerType)lhs.Type).BitWidth == 1 &&
            IntegerBin(LlvmOpcode.Xor, lhs, rhs, q, depth - 1) is { } bit) return bit;
        if (SimplifyIntegerByDomEq(LlvmOpcode.Sub, lhs, rhs, q, depth) is { } dominating) return dominating;
        if (nuw && lhs is LlvmConstantInt mask && !mask.Value.IsZero && mask.Value.CountTrailingOnes() == mask.Value.GetActiveBits() &&
            rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor && xor.Right == lhs) return xor.Left;
        return null;
    }

    private static LlvmValue? ReassociateIntegerSub(LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add)
        {
            if (IntegerBin(LlvmOpcode.Sub, add.Right, rhs, q, depth) is { } v &&
                IntegerBin(LlvmOpcode.Add, add.Left, v, q, depth) is { } w) return w;
            if (IntegerBin(LlvmOpcode.Sub, add.Left, rhs, q, depth) is { } v2 &&
                IntegerBin(LlvmOpcode.Add, add.Right, v2, q, depth) is { } w2) return w2;
        }
        if (rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } radd)
        {
            if (IntegerBin(LlvmOpcode.Sub, lhs, radd.Left, q, depth) is { } v &&
                IntegerBin(LlvmOpcode.Sub, v, radd.Right, q, depth) is { } w) return w;
            if (IntegerBin(LlvmOpcode.Sub, lhs, radd.Right, q, depth) is { } v2 &&
                IntegerBin(LlvmOpcode.Sub, v2, radd.Left, q, depth) is { } w2) return w2;
        }
        if (rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } sub &&
            IntegerBin(LlvmOpcode.Sub, lhs, sub.Left, q, depth) is { } sv &&
            IntegerBin(LlvmOpcode.Add, sv, sub.Right, q, depth) is { } sw) return sw;
        if (lhs is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } lt &&
            rhs is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } rt && lt.Operand.Type == rt.Operand.Type &&
            IntegerBin(LlvmOpcode.Sub, lt.Operand, rt.Operand, q, depth) is { } difference)
            return SimplifyIntegerCast(LlvmOpcode.Trunc, difference, lhs.Type, q, depth);
        return null;
    }

    private static LlvmValue? SimplifyIntegerByDomEq(LlvmOpcode op, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        if (depth != 3 || IsImpliedByDomCondition(LlvmCmpPredicate.ICmpEq, lhs, rhs, q) != true) return null;
        Count(q.Metrics, "simplify.integer.dominating_equality");
        return op switch
        {
            LlvmOpcode.Sub or LlvmOpcode.Xor or LlvmOpcode.URem or LlvmOpcode.SRem => IntegerZero(lhs),
            LlvmOpcode.SDiv or LlvmOpcode.UDiv => IntegerOne(lhs),
            LlvmOpcode.And or LlvmOpcode.Or => rhs,
            _ => null
        };
    }

    private static LlvmValue? SimplifyIntegerMul(LlvmValue lhs, LlvmValue rhs, bool nsw, bool nuw,
        BinaryQuery q, uint depth)
    {
        if (FoldOrCommuteConstant(LlvmOpcode.Mul, ref lhs, ref rhs, q) is { } folded) return folded;
        if (rhs is LlvmPoisonValue) return rhs;
        if (q.IsUndef(rhs) || IsIntegerZero(rhs)) return IntegerZero(lhs);
        if (IsIntegerOne(rhs)) return lhs;
        if (q.UseInstructionInfo)
        {
            if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.UDiv or LlvmOpcode.SDiv, IsExact: true } l &&
                l.Right == rhs) return l.Left;
            if (rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.UDiv or LlvmOpcode.SDiv, IsExact: true } r &&
                r.Right == lhs) return r.Left;
        }
        if (((LlvmIntegerType)lhs.Type).BitWidth == 1)
        {
            if (nsw) return IntegerZero(lhs);
            if (depth != 0 && IntegerBin(LlvmOpcode.And, lhs, rhs, q, depth - 1) is { } bit) return bit;
        }
        return ReassociateIntegerBin(LlvmOpcode.Mul, lhs, rhs, q, depth) ??
            ExpandCommutativeIntegerBin(LlvmOpcode.Mul, lhs, rhs, LlvmOpcode.Add, q, depth) ??
            ThreadIntegerBin(LlvmOpcode.Mul, lhs, rhs, q, depth);
    }
}
