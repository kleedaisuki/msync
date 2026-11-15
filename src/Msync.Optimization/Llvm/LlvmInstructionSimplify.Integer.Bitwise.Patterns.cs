// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifyAndCommutative/simplifyOrLogic,
// simplifyLogicOfAddSub and simplifyXorInst's foldAndOrNot.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? BitwiseNotOperand(LlvmValue value) =>
        value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor
            ? IsIntegerAllOnes(xor.Right) ? xor.Left : IsIntegerAllOnes(xor.Left) ? xor.Right : null : null;

    private static bool BitwisePair(LlvmValue value, LlvmOpcode opcode, LlvmValue a, LlvmValue b) =>
        value is LlvmBinaryOperator binary && binary.Opcode == opcode &&
        (binary.Left == a && binary.Right == b || binary.Left == b && binary.Right == a);

    private static bool BitwiseNotArm(LlvmValue value, LlvmOpcode opcode, out LlvmValue notValue,
        out LlvmValue original, out LlvmValue other, bool preferLeft = true)
    {
        notValue = original = other = null!;
        if (value is not LlvmBinaryOperator binary || binary.Opcode != opcode) return false;
        LlvmValue first = preferLeft ? binary.Left : binary.Right;
        LlvmValue second = preferLeft ? binary.Right : binary.Left;
        if (BitwiseNotOperand(first) is { } x)
        { notValue = first; original = x; other = second; return true; }
        if (BitwiseNotOperand(second) is { } y)
        { notValue = second; original = y; other = first; return true; }
        return false;
    }

    private static LlvmValue? SimplifyAndCommutative(LlvmValue lhs, LlvmValue rhs, BinaryQuery q, uint depth)
    {
        if (IsIntegerNot(lhs, rhs)) return IntegerZero(lhs);
        if (MatchCommutative(lhs, LlvmOpcode.Or, rhs) is not null) return rhs;
        if (BitwiseNotArm(lhs, LlvmOpcode.Or, out _, out var y, out var x, false) &&
            BitwisePair(rhs, LlvmOpcode.Or, x, y)) return x;
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Sub } neg && IsIntegerZero(neg.Left) &&
            neg.Right == rhs && BitwisePowerOfTwo(rhs, true, q)) return rhs;
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } minusOne && minusOne.Left == rhs &&
            IsIntegerAllOnes(minusOne.Right) && BitwisePowerOfTwo(rhs, true, q)) return IntegerZero(rhs);
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt n } shift &&
            rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Add } add && IsIntegerAllOnes(add.Right) &&
            add.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl, Right: LlvmConstantInt m } smaller &&
            shift.Left == smaller.Left && BitwisePowerOfTwo(shift.Left, true, q with { Tree = null }) && n.Value.Uge(m.Value))
            return IntegerZero(lhs);
        return SimplifyAndOrWithICmpEq(LlvmOpcode.And, lhs, rhs, q, depth);
    }

    private static LlvmValue? SimplifyLogicOfAddSub(LlvmValue lhs, LlvmValue rhs, LlvmOpcode opcode)
    {
        if (!BitwiseAddAndSub(lhs, rhs) && !BitwiseAddAndSub(rhs, lhs)) return null;
        return opcode == LlvmOpcode.And ? IntegerZero(lhs) : IntegerAllOnes(lhs);
    }

    private static bool BitwiseAddAndSub(LlvmValue lhs, LlvmValue rhs)
    {
        if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Add, Right: LlvmConstantInt a } add ||
            rhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt b } sub) return false;
        return add.Left == sub.Right && ~a.Value == b.Value;
    }

    private static LlvmValue? SimplifyOrLogic(LlvmValue x, LlvmValue y)
    {
        if (IsIntegerNot(y, x)) return IntegerAllOnes(x);
        if (BitwiseNotOperand(y) is { } notY && MatchCommutative(notY, LlvmOpcode.And, x) is not null)
            return IntegerAllOnes(x);
        if (MatchCommutative(y, LlvmOpcode.And, x) is not null) return x;
        if (x is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor && BitwisePair(y, LlvmOpcode.Or, xor.Left, xor.Right))
            return y;
        if (BitwiseNotOperand(x) is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } notXor &&
            BitwisePair(y, LlvmOpcode.Or, notXor.Left, notXor.Right)) return IntegerAllOnes(x);
        if (BitwiseNotArm(x, LlvmOpcode.And, out _, out var b, out var a, false) && BitwisePair(y, LlvmOpcode.Xor, a, b))
            return y;
        if (BitwiseNotArm(x, LlvmOpcode.Xor, out _, out a, out b) && BitwisePair(y, LlvmOpcode.And, a, b)) return x;
        if (BitwiseNotArm(x, LlvmOpcode.Or, out _, out a, out b) && BitwisePair(y, LlvmOpcode.Xor, a, b))
            return IntegerAllOnes(x);
        if (BitwiseNotArm(x, LlvmOpcode.And, out var notA, out a, out b) &&
            BitwiseNotOperand(y) is { } notOr && BitwisePair(notOr, LlvmOpcode.Or, a, b)) return notA;
        if (BitwiseLogicalNotArm(x, true, out notA, out a, out b) && BitwiseNotOperand(y) is { } logicalNotOr &&
            BitwiseLogicalPair(logicalNotOr, false, a, b)) return notA;
        if (BitwiseNotOperand(x) is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } complementXor &&
            BitwisePair(y, LlvmOpcode.And, complementXor.Left, complementXor.Right)) return x;
        if (BitwiseNotOperand(x) is LlvmBinaryOperator { Opcode: LlvmOpcode.And } complementAnd &&
            BitwisePair(y, LlvmOpcode.Xor, complementAnd.Left, complementAnd.Right)) return x;
        return null;
    }

    private static LlvmValue? SimplifyXorAndOrNot(LlvmValue x, LlvmValue y)
    {
        if (BitwiseNotArm(x, LlvmOpcode.And, out _, out var a, out var b) && BitwisePair(y, LlvmOpcode.Or, a, b)) return a;
        if (BitwiseNotArm(x, LlvmOpcode.Or, out var notA, out a, out b) && BitwisePair(y, LlvmOpcode.And, a, b)) return notA;
        return null;
    }

    private static LlvmValue? BitwiseZExtSExt(LlvmValue lhs, LlvmValue rhs, bool isOr)
    {
        if (lhs is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } z &&
            rhs is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } s && z.Operand == s.Operand) return isOr ? rhs : lhs;
        if (rhs is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } rz &&
            lhs is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } ls && rz.Operand == ls.Operand) return isOr ? lhs : rhs;
        return null;
    }

    private static bool BitwiseComplementaryXors(LlvmValue lhs, LlvmValue rhs) =>
        lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt c } a &&
        rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Right: LlvmConstantInt complement } b &&
        a.Left == b.Left && c.Value == ~complement.Value;

    private static bool BitwiseSharedOrXors(LlvmValue lhs, LlvmValue rhs)
    {
        if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor) return false;
        return BitwiseSharedOrXorsArm(xor.Left, xor.Right, rhs) || BitwiseSharedOrXorsArm(xor.Right, xor.Left, rhs);
    }
    private static bool BitwiseSharedOrXorsArm(LlvmValue x, LlvmValue or, LlvmValue rhs) =>
        or is LlvmBinaryOperator { Opcode: LlvmOpcode.Or } binary &&
        ((binary.Left == x && BitwisePair(rhs, LlvmOpcode.Xor, or, binary.Right)) ||
         (binary.Right == x && BitwisePair(rhs, LlvmOpcode.Xor, or, binary.Left)));

    private static bool BitwiseLogicalOperands(LlvmValue value, bool isAnd, out LlvmValue lhs, out LlvmValue rhs)
    {
        lhs = rhs = null!;
        if (!value.Type.IsIntegerTy(1)) return false;
        if (value is LlvmBinaryOperator binary && binary.Opcode == (isAnd ? LlvmOpcode.And : LlvmOpcode.Or))
        { lhs = binary.Left; rhs = binary.Right; return true; }
        if (value is not LlvmSelectInstruction select || !ReferenceEquals(select.Condition.Type, value.Type)) return false;
        if (isAnd ? !IsIntegerZero(select.FalseValue) : !IsIntegerOne(select.TrueValue)) return false;
        lhs = select.Condition;
        rhs = isAnd ? select.TrueValue : select.FalseValue;
        return true;
    }
    private static bool BitwiseLogicalPair(LlvmValue value, bool isAnd, LlvmValue a, LlvmValue b) =>
        BitwiseLogicalOperands(value, isAnd, out var lhs, out var rhs) && (lhs == a && rhs == b || lhs == b && rhs == a);
    private static bool BitwiseLogicalNotArm(LlvmValue value, bool isAnd, out LlvmValue not, out LlvmValue a, out LlvmValue b, bool preferLeft = true)
    {
        not = a = b = null!;
        if (!BitwiseLogicalOperands(value, isAnd, out var lhs, out var rhs)) return false;
        LlvmValue first = preferLeft ? lhs : rhs;
        LlvmValue second = preferLeft ? rhs : lhs;
        if (BitwiseNotOperand(first) is { } x) { not = first; a = x; b = second; return true; }
        if (BitwiseNotOperand(second) is { } y) { not = second; a = y; b = first; return true; }
        return false;
    }

    private static bool BitwiseIsLowMask(LlvmAPInt value) => !value.IsZero && value.CountTrailingOnes() == value.GetActiveBits();
}
