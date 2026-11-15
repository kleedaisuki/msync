// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// visitFSub. Vector reduction transformations require unrepresented vector IR.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitFSub(LlvmBinaryOperator instruction)
        {
            Count("instcombine.fsub.visits");
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction)) is { } simple)
                return ReplaceInstUsesWith(instruction, simple);
            if (FoldVectorBinop(instruction) is { } vector) return vector;
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            var flags = instruction.FastMathFlags;
            if (MatchAddSubFNeg(instruction) is { } negated)
                return new LlvmUnaryOperator(negated) { FastMathFlags = flags };
            if (FoldFNegIntoConstant(instruction) is { } constant) return constant;
            if (FoldFBinOpOfIntCasts(instruction) is { } casts) return casts;
            LlvmValue left = instruction.Left, right = instruction.Right;
            var query = Query(instruction);
            if (AddSubHasFlags(instruction, LlvmFastMathFlags.NoSignedZeros) ||
                LlvmKnownFPClasses.CannotBeNegativeZero(left, instruction, DT, Metrics, assumptions: AC, allowEphemerals: query.AllowEphemerals, conditions: query.DC, condContext: query.CondContext))
                if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, HasOneUse: true } subtraction)
                    return NewAddSubFP(LlvmOpcode.FAdd, left, BuildAddSubFP(LlvmOpcode.FSub, subtraction.Right, subtraction.Left, flags), flags);
            if (AddSubHasFlags(instruction, LlvmFastMathFlags.NoSignedZeros) && left.HasOneUse &&
                left is not LlvmConstantExpr && MatchAddSubFNeg(left) is { } x)
                return new LlvmUnaryOperator(BuildAddSubFP(LlvmOpcode.FAdd, x, right, flags)) { FastMathFlags = flags };
            if (left is LlvmConstant && right is LlvmSelectInstruction selected && FoldOpIntoSelect(instruction, selected) is { } select) return select;
            if (right is LlvmConstant rhs && IsAddSubImmediateConstant(rhs) &&
                LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, rhs, DL, Metrics) is { } negative)
                return NewAddSubFP(LlvmOpcode.FAdd, left, negative, flags);
            if (MatchAddSubFNeg(right) is { } y) return NewAddSubFP(LlvmOpcode.FAdd, left, y, flags);
            if (right is LlvmCastInstruction { Opcode: LlvmOpcode.FPTrunc or LlvmOpcode.FPExt, HasOneUse: true } cast &&
                MatchAddSubFNeg(cast.Operand) is { } unnegated)
                return NewAddSubFP(LlvmOpcode.FAdd, left, Builder.CreateCast(cast.Opcode, unnegated, instruction.Type), flags);
            if (MatchAddSubNegatedProduct(right, out var first, out var second, out var productOpcode))
                return NewAddSubFP(LlvmOpcode.FAdd, left, BuildAddSubFP(productOpcode, first, second, flags), flags);
            if (SimplifySelectsFeedingBinaryOp(instruction, left, right) is { } selectedArms)
                return ReplaceInstUsesWith(instruction, selectedArms);
            if (!AddSubHasFlags(instruction, LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoSignedZeros)) return null;
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } sub && ReferenceEquals(sub.Left, right))
                return new LlvmUnaryOperator(sub.Right) { FastMathFlags = flags };
            if (MatchAddSubCommuted(right, LlvmOpcode.FAdd, left, out var addend))
                return new LlvmUnaryOperator(addend) { FastMathFlags = flags };
            var one = LlvmConstantFP.Get(instruction.Type, 1);
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, Right: LlvmConstant coefficient } mul && ReferenceEquals(mul.Left, right) &&
                LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FSub, coefficient, one, DL, Metrics) is { } reduced)
                return NewAddSubFP(LlvmOpcode.FMul, right, reduced, flags);
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, Right: LlvmConstant coefficient2 } mul2 && ReferenceEquals(mul2.Left, left) &&
                LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FSub, one, coefficient2, DL, Metrics) is { } reduced2)
                return NewAddSubFP(LlvmOpcode.FMul, left, reduced2, flags);
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.FAdd, HasOneUse: true } outer)
            {
                LlvmBinaryOperator? nested = outer.Left as LlvmBinaryOperator;
                LlvmValue z = outer.Right;
                if (nested is not { Opcode: LlvmOpcode.FSub, HasOneUse: true }) { nested = outer.Right as LlvmBinaryOperator; z = outer.Left; }
                if (nested is { Opcode: LlvmOpcode.FSub, HasOneUse: true })
                    return NewAddSubFP(LlvmOpcode.FSub, BuildAddSubFP(LlvmOpcode.FAdd, nested.Left, z, flags),
                        BuildAddSubFP(LlvmOpcode.FAdd, nested.Right, right, flags), flags);
            }
            if (FactorizeFAddFSub(instruction) is { } factored) return factored;
            if (new FAddCombine(Builder, Metrics).Simplify(instruction) is { } reassociated) return ReplaceInstUsesWith(instruction, reassociated);
            if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, HasOneUse: true } last)
                return NewAddSubFP(LlvmOpcode.FSub, last.Left, BuildAddSubFP(LlvmOpcode.FAdd, last.Right, right, flags), flags);
            return null;
        }
        private static bool MatchAddSubNegatedProduct(LlvmValue value, out LlvmValue first, out LlvmValue second, out LlvmOpcode opcode)
        {
            first = null!; second = null!; opcode = default;
            if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.FMul or LlvmOpcode.FDiv, HasOneUse: true } binary) return false;
            opcode = binary.Opcode;
            if (MatchAddSubFNeg(binary.Left) is { } x)
            { first = x; second = binary.Right; return true; }
            if (MatchAddSubFNeg(binary.Right) is not { } y) return false;
            first = opcode == LlvmOpcode.FMul ? y : binary.Left;
            second = opcode == LlvmOpcode.FMul ? binary.Left : y;
            return true;
        }
    }
}
