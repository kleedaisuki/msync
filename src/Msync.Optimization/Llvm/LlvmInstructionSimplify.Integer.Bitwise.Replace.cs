// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifyAndOrWithICmpEq and
// simplifyWithOpsReplaced's scalar AllowRefinement=true, DropFlags=null path.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    private static LlvmValue? SimplifyAndOrWithICmpEq(LlvmOpcode opcode, LlvmValue lhs, LlvmValue rhs,
        BinaryQuery q, uint depth)
    {
        if (!BitwiseICmpLike(lhs, out var predicate, out var left, out var right) || !BitwiseEqualityPredicate(predicate)) return null;
        BinaryQuery query = q with { CanUseUndef = false };
        if (BitwiseSimplifyWithOpReplaced(rhs, left, right, query, depth) is { } a)
            return BitwiseICmpEqResult(opcode, predicate, lhs, rhs, a);
        if (BitwiseSimplifyWithOpReplaced(rhs, right, left, query, depth) is { } b)
            return BitwiseICmpEqResult(opcode, predicate, lhs, rhs, b);
        return null;
    }

    private static bool BitwiseICmpLike(LlvmValue value, out LlvmCmpPredicate predicate,
        out LlvmValue lhs, out LlvmValue rhs)
    {
        if (value is LlvmICmpInstruction compare)
        { predicate = compare.Predicate; lhs = compare.Left; rhs = compare.Right; return true; }
        if (value.Type.IsIntegerTy(1) && value is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoUnsignedWrap: true } trunc)
        { predicate = LlvmCmpPredicate.ICmpNe; lhs = trunc.Operand; rhs = IntegerZero(lhs); return true; }
        predicate = default;
        lhs = rhs = null!;
        return false;
    }

    private static LlvmValue? BitwiseICmpEqResult(LlvmOpcode opcode, LlvmCmpPredicate predicate,
        LlvmValue lhs, LlvmValue rhs, LlvmValue result)
    {
        bool isAnd = opcode == LlvmOpcode.And;
        LlvmValue absorber = isAnd ? IntegerZero(result) : IntegerAllOnes(result);
        if (predicate == (isAnd ? LlvmCmpPredicate.ICmpEq : LlvmCmpPredicate.ICmpNe))
        {
            if (result == absorber) return absorber;
            if (result == (isAnd ? IntegerAllOnes(result) : IntegerZero(result))) return lhs;
            return null;
        }
        return result == absorber ? rhs : null;
    }

    private static LlvmValue? BitwiseSimplifyWithOpReplaced(LlvmValue value, LlvmValue operand,
        LlvmValue replacement, BinaryQuery q, uint depth) =>
        BitwiseSimplifyWithOpsReplaced(value, [(operand, replacement)], q, true, depth);

    private static LlvmValue? BitwiseSimplifyWithOpsReplaced(LlvmValue value,
        IReadOnlyList<(LlvmValue Operand, LlvmValue Replacement)> replacements, BinaryQuery q,
        bool allowRefinement, uint depth)
    {
        Count(q.Metrics, "simplify.replace.visited");
        foreach (var (operand, replacement) in replacements)
        {
            if (operand is LlvmConstant) return null;
            if (value == operand) return replacement;
        }
        if (depth == 0) return null;
        --depth;
        if (value is not LlvmInstruction instruction || instruction is LlvmPhiInstruction or LlvmFreezeInstruction) return null;
        var newOperands = new LlvmValue[instruction.OperandCount];
        bool anyReplaced = false;
        for (int index = 0; index < newOperands.Length; ++index)
        {
            LlvmValue original = instruction.GetOperand(index)!;
            if (BitwiseSimplifyWithOpsReplaced(original, replacements, q, allowRefinement, depth) is { } substituted)
            {
                newOperands[index] = substituted;
                // Assignment, not OR, follows the pinned source's AnyReplaced update.
                anyReplaced = original != substituted;
            }
            else newOperands[index] = original;
            if (newOperands[index] is LlvmUndefValue && !q.CanUseUndef) return null;
        }
        if (!anyReplaced) return null;
        if (allowRefinement)
        {
            LlvmValue? simplified = SimplifyBitwiseReplacedInstruction(instruction, newOperands, q, depth);
            return simplified == value ? null : simplified;
        }
        if (SimplifyNonRefiningReplacement(instruction, newOperands, replacements, q) is { } exact) return exact;
        return FoldBitwiseReplacedOperands(instruction, newOperands, q, false, true);
    }

    private static LlvmValue? SimplifyNonRefiningReplacement(LlvmInstruction instruction,
        IReadOnlyList<LlvmValue> operands, IReadOnlyList<(LlvmValue Operand, LlvmValue Replacement)> replacements,
        BinaryQuery q)
    {
        if (instruction is LlvmBinaryOperator binary)
        {
            LlvmOpcode opcode = binary.Opcode;
            if (!binary.Type.IsFloatingPointTy())
            {
                if (operands[0] == LlvmConstantFolding.BinOpIdentity(opcode, binary.Type, false)) return operands[1];
                if (operands[1] == LlvmConstantFolding.BinOpIdentity(opcode, binary.Type, true)) return operands[0];
            }
            if (opcode is LlvmOpcode.And or LlvmOpcode.Or && operands[0] == operands[1])
                return opcode == LlvmOpcode.Or && binary.IsDisjoint ? null : operands[0];
            if (opcode is LlvmOpcode.Sub or LlvmOpcode.Xor && operands[0] == operands[1] &&
                replacements.Any(pair => operands[0] == pair.Replacement)) return IntegerZero(binary);
            var absorber = LlvmConstantFolding.BinOpAbsorber(opcode, binary.Type, false);
            if ((operands[0] == absorber || operands[1] == absorber) &&
                replacements.Any(pair => LlvmValueTracking.ImpliesPoison(binary, pair.Operand, q.Metrics))) return absorber;
        }
        if (instruction is LlvmGetElementPtrInstruction && operands.Count == 2 && IsIntegerZero(operands[1])) return operands[0];
        return null;
    }

    private static LlvmValue? SimplifyBitwiseReplacedInstruction(LlvmInstruction instruction,
        IReadOnlyList<LlvmValue> operands, BinaryQuery q, uint depth)
    {
        if (q.Context is null) q = q with { Context = instruction };
        if (instruction is LlvmPhiInstruction phi) return SimplifyPhiCore(phi, q, operands);
        if (instruction is LlvmBinaryOperator binary)
            return binary.Type.IsFloatingPointTy()
                ? SimplifyFloatingBinary(binary.Opcode, operands[0], operands[1], binary.FastMathFlags, q)
                : SimplifyIntegerBinary(binary.Opcode, operands[0], operands[1],
                    q.UseInstructionInfo && binary.HasNoSignedWrap, q.UseInstructionInfo && binary.HasNoUnsignedWrap,
                    q.UseInstructionInfo && binary.IsExact, q, depth);
        if (instruction is LlvmUnaryOperator) return SimplifyFNeg(operands[0], q);
        if (instruction is LlvmICmpInstruction comparison)
            return SimplifyIntegerCompare(comparison.Predicate, operands[0], operands[1], q, depth);
        if (instruction is LlvmCastInstruction cast)
            return SimplifyIntegerCast(cast.Opcode, operands[0], cast.Type, q, depth);
        if (instruction is LlvmFCmpInstruction fpComparison)
            return SimplifyFloatingCompare(fpComparison.Predicate, operands[0], operands[1], fpComparison.FastMathFlags, q, depth);
        if (instruction is LlvmSelectInstruction select)
            return SimplifyScalarSelect(operands[0], operands[1], operands[2], select.FastMathFlags, q, depth);
        return SimplifyOtherInstruction(instruction, operands, q, depth);
    }

    private static LlvmValue? FoldBitwiseReplacedOperands(LlvmInstruction instruction,
        IReadOnlyList<LlvmValue> operands, BinaryQuery q, bool allowNonDeterministic = true,
        bool rejectPoisonCreation = false)
    {
        var constants = new LlvmConstant[operands.Count];
        for (int index = 0; index < constants.Length; ++index)
        {
            if (operands[index] is not LlvmConstant constant) return null;
            constants[index] = constant;
        }
        if (rejectPoisonCreation && LlvmValueTracking.CanCreatePoison(instruction)) return null;
        return LlvmConstantFolding.ConstantFoldInstOperands(instruction, constants, q.Layout, allowNonDeterministic, q.Metrics);
    }
}
