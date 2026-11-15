// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// factorizeLerp/factorizeFAddFSub/foldFNegIntoConstant.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool AddSubHasFlags(ILlvmFastMathInstruction value, LlvmFastMathFlags flags) => (value.FastMathFlags & flags) == flags;
        private static LlvmValue? MatchAddSubFNeg(LlvmValue value) => value switch
        {
            LlvmUnaryOperator negation => negation.Operand,
            LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, Left: LlvmConstantFP { Value.IsZero: true } zero } subtraction
                when zero.IsNegative || AddSubHasFlags(subtraction, LlvmFastMathFlags.NoSignedZeros) => subtraction.Right,
            _ => null
        };
        private static LlvmBinaryOperator NewAddSubFP(LlvmOpcode opcode, LlvmValue left, LlvmValue right, LlvmFastMathFlags flags) =>
            new(opcode, left, right) { FastMathFlags = flags };
        private LlvmValue BuildAddSubFP(LlvmOpcode opcode, LlvmValue left, LlvmValue right, LlvmFastMathFlags flags) =>
            Builder.CreateBinary(opcode, left, right, fastMathFlags: flags);
        private static bool MatchAddSubCommuted(LlvmValue value, LlvmOpcode opcode, LlvmValue operand, out LlvmValue other)
        {
            other = null!;
            if (value is not LlvmBinaryOperator binary || binary.Opcode != opcode) return false;
            if (ReferenceEquals(binary.Left, operand)) { other = binary.Right; return true; }
            if (!ReferenceEquals(binary.Right, operand)) return false;
            other = binary.Left; return true;
        }
        private LlvmInstruction? FactorizeLerp(LlvmBinaryOperator instruction)
        {
            if (instruction.Opcode != LlvmOpcode.FAdd) return null;
            return FactorizeLerpSide(instruction.Left, instruction.Right, instruction.FastMathFlags) ??
                FactorizeLerpSide(instruction.Right, instruction.Left, instruction.FastMathFlags);
        }
        private LlvmInstruction? FactorizeLerpSide(LlvmValue first, LlvmValue second, LlvmFastMathFlags flags)
        {
            if (first is not LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, HasOneUse: true } multiply || !second.HasOneUse) return null;
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue y = side == 0 ? multiply.Left : multiply.Right;
                LlvmValue candidate = side == 0 ? multiply.Right : multiply.Left;
                if (candidate is not LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, HasOneUse: true, Left: LlvmConstantFP one } subtract ||
                    !one.Value.BitwiseIsEqual(LlvmConstantFP.Get(one.Type, 1).Value) ||
                    !MatchAddSubCommuted(second, LlvmOpcode.FMul, subtract.Right, out var x)) continue;
                var xy = BuildAddSubFP(LlvmOpcode.FSub, x, y, flags);
                return NewAddSubFP(LlvmOpcode.FAdd, y, BuildAddSubFP(LlvmOpcode.FMul, subtract.Right, xy, flags), flags);
            }
            return null;
        }
        private LlvmInstruction? FactorizeFAddFSub(LlvmBinaryOperator instruction)
        {
            if (FactorizeLerp(instruction) is { } lerp) return lerp;
            if (instruction.Left is not LlvmBinaryOperator left || !left.HasOneUse || !instruction.Right.HasOneUse) return null;
            LlvmValue x, y, z;
            LlvmOpcode opcode;
            if (left.Opcode == LlvmOpcode.FMul && MatchAddSubCommuted(instruction.Right, LlvmOpcode.FMul, left.Right, out y))
            { x = left.Left; z = left.Right; opcode = LlvmOpcode.FMul; }
            else if (left.Opcode == LlvmOpcode.FMul && MatchAddSubCommuted(instruction.Right, LlvmOpcode.FMul, left.Left, out y))
            { x = left.Right; z = left.Left; opcode = LlvmOpcode.FMul; }
            else if (left.Opcode == LlvmOpcode.FDiv && instruction.Right is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv } right && ReferenceEquals(left.Right, right.Right))
            { x = left.Left; y = right.Left; z = left.Right; opcode = LlvmOpcode.FDiv; }
            else return null;
            LlvmValue combined = BuildAddSubFP(instruction.Opcode, x, y, instruction.FastMathFlags);
            if (combined is LlvmConstantFP { Value.IsNormal: false }) return null;
            return NewAddSubFP(opcode, combined, z, instruction.FastMathFlags);
        }
        private LlvmInstruction? FoldFNegIntoConstant(LlvmInstruction instruction)
        {
            if (MatchAddSubFNeg(instruction) is not LlvmBinaryOperator { HasOneUse: true } operand) return null;
            LlvmFastMathFlags flags = ((ILlvmFastMathInstruction)instruction).FastMathFlags;
            LlvmFastMathFlags inner = operand.FastMathFlags;
            if (operand is { Opcode: LlvmOpcode.FMul, Right: LlvmConstant constant } &&
                LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, constant, DL, Metrics) is { } negated)
            {
                const LlvmFastMathFlags valueMask = LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs | LlvmFastMathFlags.NoSignedZeros;
                const LlvmFastMathFlags rewriteMask = LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.AllowReciprocal | LlvmFastMathFlags.AllowContract | LlvmFastMathFlags.ApproxFunc;
                LlvmFastMathFlags merged = (flags | inner) & valueMask | flags & inner & rewriteMask;
                merged = merged & ~LlvmFastMathFlags.NoInfs | flags & inner & LlvmFastMathFlags.NoInfs;
                return NewAddSubFP(LlvmOpcode.FMul, operand.Left, negated, merged);
            }
            if (operand.Opcode == LlvmOpcode.FDiv)
            {
                LlvmFastMathFlags combined = flags & ~(LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.NoInfs) |
                    flags & inner & (LlvmFastMathFlags.NoSignedZeros | LlvmFastMathFlags.NoInfs);
                if (operand.Right is LlvmConstant rhs && LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, rhs, DL, Metrics) is { } negatedRight)
                    return NewAddSubFP(LlvmOpcode.FDiv, operand.Left, negatedRight, combined);
                if (operand.Left is LlvmConstant lhs && LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, lhs, DL, Metrics) is { } negatedLeft)
                    return NewAddSubFP(LlvmOpcode.FDiv, negatedLeft, operand.Right, combined);
            }
            if ((flags & LlvmFastMathFlags.NoSignedZeros) != 0 && operand is { Opcode: LlvmOpcode.FAdd, Right: LlvmConstant rhsConstant } &&
                LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, rhsConstant, DL, Metrics) is { } negative)
                return NewAddSubFP(LlvmOpcode.FSub, negative, operand.Left, flags);
            return null;
        }
    }
}
