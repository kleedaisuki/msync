// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// distributivity predicates, identity values, factorization and expansion.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool AlgebraCommutative(LlvmOpcode opcode) => opcode is LlvmOpcode.Add or LlvmOpcode.Mul or
            LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.FAdd or LlvmOpcode.FMul;
        private static bool AlgebraLogic(LlvmOpcode opcode) => opcode is LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor;
        private static bool AlgebraShift(LlvmOpcode opcode) => opcode is LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr;
        private static bool LeftDistributesOverRight(LlvmOpcode left, LlvmOpcode right) => left switch
        {
            LlvmOpcode.And => right is LlvmOpcode.Or or LlvmOpcode.Xor,
            LlvmOpcode.Or => right == LlvmOpcode.And,
            LlvmOpcode.Mul => right is LlvmOpcode.Add or LlvmOpcode.Sub,
            _ => false
        };
        private static bool RightDistributesOverLeft(LlvmOpcode left, LlvmOpcode right) => AlgebraCommutative(right)
            ? LeftDistributesOverRight(right, left) : AlgebraLogic(left) && AlgebraShift(right);
        private static LlvmConstant? AlgebraIdentity(LlvmOpcode opcode, LlvmType type) => opcode switch
        {
            LlvmOpcode.Add or LlvmOpcode.Or or LlvmOpcode.Xor => LlvmConstant.GetNullValue(type),
            LlvmOpcode.Mul => LlvmConstantInt.Get(type.Context, new LlvmAPInt(type.GetIntegerBitWidth(), 1)),
            LlvmOpcode.And => LlvmConstantInt.Get(type.Context, LlvmAPInt.GetAllOnes(type.GetIntegerBitWidth())),
            LlvmOpcode.FAdd => LlvmConstantFP.Get(type, LlvmAPFloat.FromBits(0x8000000000000000)),
            LlvmOpcode.FMul => LlvmConstantFP.Get(type, new LlvmAPFloat(1.0)),
            _ => null
        };
        private static LlvmValue? GetIdentityValue(LlvmOpcode opcode, LlvmValue value) =>
            value is LlvmConstant ? null : AlgebraIdentity(opcode, value.Type);
        private LlvmOpcode GetBinOpsForFactorization(LlvmOpcode top, LlvmBinaryOperator instruction,
            out LlvmValue left, out LlvmValue right, LlvmBinaryOperator? other)
        {
            left = instruction.Left; right = instruction.Right;
            if (top is LlvmOpcode.Add or LlvmOpcode.Sub && instruction.Opcode == LlvmOpcode.Shl && right is LlvmConstant constant)
            {
                var one = LlvmConstantInt.Get(instruction.Context, new LlvmAPInt(instruction.Type.GetIntegerBitWidth(), 1));
                right = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Shl, one, constant, DL, Metrics)!;
                Debug.Assert(right is not null); return LlvmOpcode.Mul;
            }
            if (AlgebraLogic(top) && other?.Opcode == LlvmOpcode.AShr && instruction.Opcode == LlvmOpcode.LShr &&
                instruction.Left is LlvmConstantInt { IsNegative: false }) return LlvmOpcode.AShr;
            return instruction.Opcode;
        }
        private LlvmValue? TryFactorization(LlvmBinaryOperator instruction, LlvmOpcode inner,
            LlvmValue a, LlvmValue b, LlvmValue c, LlvmValue d)
        {
            Count("algebra.factorization.queries");
            LlvmValue? value = null, result = null;
            var left = instruction.Left; var right = instruction.Right; var top = instruction.Opcode;
            bool commutative = AlgebraCommutative(inner);
            if (LeftDistributesOverRight(inner, top) && (a == c || commutative && a == d))
            {
                if (a != c) (c, d) = (d, c);
                value = AlgebraSimplifyBin(top, b, d, instruction);
                if (value is null && (left.HasOneUse || right.HasOneUse)) value = Builder.CreateBinOp(top, b, d, right.Name);
                if (value is null) Count("algebra.factorization.left_not_simplified_or_one_use");
                if (value is not null) result = Builder.CreateBinOp(inner, a, value);
            }
            if (result is null && RightDistributesOverLeft(top, inner) && (b == d || commutative && b == c))
            {
                if (b != d) (c, d) = (d, c);
                value = AlgebraSimplifyBin(top, a, c, instruction);
                if (value is null && (left.HasOneUse || right.HasOneUse)) value = Builder.CreateBinOp(top, a, c, left.Name);
                if (value is null) Count("algebra.factorization.right_not_simplified_or_one_use");
                if (value is not null) result = Builder.CreateBinOp(inner, value, b);
            }
            if (result is null) return null;
            Count("algebra.factorized"); result.TakeName(instruction);
            if (result is not LlvmBinaryOperator output) return result;
            bool nsw = AlgebraHasNSW(instruction), nuw = AlgebraHasNUW(instruction);
            if (left is LlvmBinaryOperator lb && AlgebraOverflowing(lb.Opcode))
            { nsw &= lb.HasNoSignedWrap; nuw &= lb.HasNoUnsignedWrap; }
            if (right is LlvmBinaryOperator rb && AlgebraOverflowing(rb.Opcode))
            { nsw &= rb.HasNoSignedWrap; nuw &= rb.HasNoUnsignedWrap; }
            if (top == LlvmOpcode.Add && inner == LlvmOpcode.Mul)
            {
                if (value is LlvmConstantInt constant && !constant.Value.IsMinSignedValue)
                { output.HasNoSignedWrap = nsw; Count(nsw ? "algebra.factorization.nsw_preserved" : "algebra.factorization.nsw_not_preserved"); }
                output.HasNoUnsignedWrap = nuw;
                Count(nuw ? "algebra.factorization.nuw_preserved" : "algebra.factorization.nuw_not_preserved");
            }
            return result;
        }
        private LlvmValue? TryFactorizationFolds(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            var op0 = left as LlvmBinaryOperator; var op1 = right as LlvmBinaryOperator;
            LlvmValue a = null!, b = null!, c = null!, d = null!;
            LlvmOpcode leftOpcode = default, rightOpcode = default;
            if (op0 is not null) leftOpcode = GetBinOpsForFactorization(instruction.Opcode, op0, out a, out b, op1);
            if (op1 is not null) rightOpcode = GetBinOpsForFactorization(instruction.Opcode, op1, out c, out d, op0);
            if (op0 is not null && op1 is not null && leftOpcode == rightOpcode &&
                TryFactorization(instruction, leftOpcode, a, b, c, d) is { } both) return both;
            if (op0 is not null && GetIdentityValue(leftOpcode, right) is { } rightIdentity &&
                TryFactorization(instruction, leftOpcode, a, b, right, rightIdentity) is { } lhs) return lhs;
            if (op1 is not null && GetIdentityValue(rightOpcode, left) is { } leftIdentity &&
                TryFactorization(instruction, rightOpcode, left, leftIdentity, c, d) is { } rhs) return rhs;
            return null;
        }
        private LlvmValue? FoldUsingDistributiveLaws(LlvmBinaryOperator instruction)
        {
            Count("algebra.distributive.queries");
            var left = instruction.Left; var right = instruction.Right;
            if (TryFactorizationFolds(instruction) is { } factored) return factored;
            var query = Query(instruction) with { CanUseUndef = false };
            if (left is LlvmBinaryOperator a && RightDistributesOverLeft(a.Opcode, instruction.Opcode))
            {
                var l = LlvmInstructionSimplify.SimplifyBinOp(instruction.Opcode, a.Left, right, query);
                var r = LlvmInstructionSimplify.SimplifyBinOp(instruction.Opcode, a.Right, right, query);
                LlvmValue? expanded = l is not null && r is not null ? Builder.CreateBinOp(a.Opcode, l, r) :
                    l is not null && l == AlgebraIdentity(a.Opcode, l.Type) ? Builder.CreateBinOp(instruction.Opcode, a.Right, right) :
                    r is not null && r == AlgebraIdentity(a.Opcode, r.Type) ? Builder.CreateBinOp(instruction.Opcode, a.Left, right) : null;
                if (expanded is not null) { Count("algebra.expanded"); expanded.TakeName(instruction); return expanded; }
            }
            if (right is LlvmBinaryOperator b && LeftDistributesOverRight(instruction.Opcode, b.Opcode))
            {
                var l = LlvmInstructionSimplify.SimplifyBinOp(instruction.Opcode, left, b.Left, query);
                var r = LlvmInstructionSimplify.SimplifyBinOp(instruction.Opcode, left, b.Right, query);
                LlvmValue? expanded = l is not null && r is not null ? Builder.CreateBinOp(b.Opcode, l, r) :
                    l is not null && l == AlgebraIdentity(b.Opcode, l.Type) ? Builder.CreateBinOp(instruction.Opcode, left, b.Right) :
                    r is not null && r == AlgebraIdentity(b.Opcode, r.Type) ? Builder.CreateBinOp(instruction.Opcode, left, b.Left) : null;
                if (expanded is not null) { Count("algebra.expanded"); expanded.TakeName(instruction); return expanded; }
            }
            return SimplifySelectsFeedingBinaryOp(instruction, left, right);
        }
    }
}
