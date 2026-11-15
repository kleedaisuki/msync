// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// foldICmpEquality (6427–6662); source rule order and one-use profitability.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? ICmpEqualityRewrite(LlvmCmpPredicate predicate, LlvmValue a, LlvmValue b, string rule)
        { Count("icmp.equality.rewrite." + rule); return new LlvmICmpInstruction(predicate, a, b); }
        private static bool ICmpImmediate(LlvmValue value) => value is LlvmConstant and not LlvmConstantExpr;
        private LlvmInstruction? FoldICmpEquality(LlvmICmpInstruction instruction)
        {
            if (!ICmpIsEquality(instruction.Predicate)) return null;
            Count("icmp.equality.attempts");
            var op0 = instruction.Left; var op1 = instruction.Right; var predicate = instruction.Predicate;
            LlvmInstruction? Cmp(LlvmValue a, LlvmValue b, string rule) => ICmpEqualityRewrite(predicate, a, b, rule);
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor0)
            {
                var a = xor0.Left; var b = xor0.Right;
                if (a == op1 || b == op1) return Cmp(a == op1 ? b : a, ICmpZero(a), "xor_self");
                if (op1 is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor1)
                {
                    var c = xor1.Left; var d = xor1.Right;
                    if (b is LlvmConstantInt c1 && d is LlvmConstantInt c2 && op1.HasOneUse)
                        return Cmp(a, Builder.CreateXor(c, LlvmConstantInt.Get(c1.Context, c1.Value ^ c2.Value)), "xor_constants");
                    if (a == c) return Cmp(b, d, "xor_cancel");
                    if (a == d) return Cmp(b, c, "xor_cancel");
                    if (b == c) return Cmp(a, d, "xor_cancel");
                    if (b == d) return Cmp(a, c, "xor_cancel");
                }
            }
            if (op1 is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor && (xor.Left == op0 || xor.Right == op0))
                return Cmp(xor.Left == op0 ? xor.Right : xor.Left, ICmpZero(op0), "xor_self");
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and0 && op1 is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and1)
            {
                LlvmValue? x = null, y = null, z = null;
                if (and0.Left == and1.Left) { x = and0.Right; y = and1.Right; z = and0.Left; }
                else if (and0.Left == and1.Right) { x = and0.Right; y = and1.Left; z = and0.Left; }
                else if (and0.Right == and1.Left) { x = and0.Left; y = and1.Right; z = and0.Right; }
                else if (and0.Right == and1.Right) { x = and0.Left; y = and1.Left; z = and0.Right; }
                if (x is not null)
                {
                    bool negativePower = x is LlvmConstantInt cx && y is LlvmConstantInt cy && (-(cx.Value ^ cy.Value)).IsPowerOf2;
                    int uses = (op0.HasOneUse ? 1 : 0) + (op1.HasOneUse ? 1 : 0) + (ICmpImmediate(x) && ICmpImmediate(y!) ? 1 : 0);
                    if (negativePower || uses >= 2) return Cmp(Builder.CreateAnd(Builder.CreateXor(x, y!), z!), ICmpZero(op0), "and_common_mask");
                }
            }
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true, Right: LlvmConstant mask } or0 &&
                op1 is LlvmBinaryOperator { Opcode: LlvmOpcode.Or, HasOneUse: true } or1 && or1.Right == mask)
                return Cmp(Builder.CreateAnd(Builder.CreateXor(or0.Left, or1.Left), LlvmConstantExpr.GetNot(mask)), ICmpZero(op0), "or_common_mask");
            if (op1 is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } ext && (op0.HasOneUse || op1.HasOneUse) &&
                op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.And, Right: LlvmConstantInt lowMask } masked &&
                !lowMask.Value.IsZero && lowMask.Value == LlvmAPInt.GetLowBitsSet(lowMask.Value.BitWidth, lowMask.Value.CountTrailingOnes()) && lowMask.Value.CountTrailingOnes() == ext.Operand.Type.GetScalarSizeInBits())
                return Cmp(ext.Operand, Builder.CreateTrunc(masked.Left, ext.Operand.Type), "zext_mask");
            if (op0 is LlvmBinaryOperator { HasOneUse: true, Right: LlvmConstantInt shift0 } shifted0 &&
                op1 is LlvmBinaryOperator { HasOneUse: true, Right: LlvmConstantInt shift1 } shifted1 && shifted0.Opcode == shifted1.Opcode)
            {
                uint width = shift0.Value.BitWidth; uint amount = (uint)shift0.Value.GetLimitedValue(width);
                if (shifted0.Opcode is LlvmOpcode.LShr or LlvmOpcode.AShr)
                {
                    if (shift0.Value != shift1.Value) return null;
                    if (amount > 0 && amount < width)
                        return ICmpEqualityRewrite(predicate == LlvmCmpPredicate.ICmpNe ? LlvmCmpPredicate.ICmpUge : LlvmCmpPredicate.ICmpUlt,
                            Builder.CreateXor(shifted0.Left, shifted1.Left), LlvmConstantInt.Get(op0.Context, LlvmAPInt.GetOneBitSet(width, amount)), "right_shift");
                }
                if (shifted0.Opcode == LlvmOpcode.Shl && shift0 == shift1 && amount > 0 && amount < width)
                    return Cmp(Builder.CreateAnd(Builder.CreateXor(shifted0.Left, shifted1.Left), LlvmConstantInt.Get(op0.Context, LlvmAPInt.GetLowBitsSet(width, width - amount))), ICmpZero(op0), "left_shift");
            }
            if (op0 is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasOneUse: true, Operand: LlvmBinaryOperator
                { Opcode: LlvmOpcode.LShr, HasOneUse: true, Right: LlvmConstantInt shift } lshr } trunc && op1 is LlvmConstantInt constant && !lshr.Left.HasOneUse)
            {
                uint width = lshr.Left.Type.GetIntegerBitWidth(); ulong amount = shift.Value.GetLimitedValue();
                if (amount < width)
                {
                    var maskBits = LlvmAPInt.GetLowBitsSet(width, trunc.Type.GetIntegerBitWidth()).Shl((uint)amount);
                    return Cmp(Builder.CreateAnd(lshr.Left, LlvmConstantInt.Get(op0.Context, maskBits)),
                        LlvmConstantInt.Get(op0.Context, constant.Value.ZExt(width).Shl((uint)amount)), "trunc_lshr");
                }
            }
            if (FoldICmpIntrinsicWithIntrinsic(instruction) is { } intrinsic) return intrinsic;
            uint bits = op0.Type.GetScalarSizeInBits();
            if (op0 is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, Left: LlvmCastInstruction { Opcode: LlvmOpcode.Trunc } tr, Right: LlvmConstantInt sr } &&
                sr.Value == new LlvmAPInt(sr.Value.BitWidth, bits - 1) && op1 is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, Operand: LlvmBinaryOperator
                { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt sl } upper } && upper.Left == tr.Operand &&
                sl.Value == new LlvmAPInt(sl.Value.BitWidth, bits) && tr.Operand.Type.GetScalarSizeInBits() == bits * 2 && (op0.HasOneUse || op1.HasOneUse))
            {
                var bias = LlvmAPInt.GetOneBitSet(bits * 2, bits - 1);
                return ICmpEqualityRewrite(predicate == LlvmCmpPredicate.ICmpEq ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpUge,
                    Builder.CreateAdd(tr.Operand, LlvmConstantInt.Get(op0.Context, bias)), LlvmConstantInt.Get(op0.Context, bias.Shl(1)), "sign_extension_range");
            }
            if (ICmpAndContains(op0, op1) && IsKnownToBeAPowerOfTwo(op1, false, instruction))
                return ICmpEqualityRewrite(LlvmCmpInstruction.GetInversePredicate(predicate), op0, ICmpZero(op0), "power_bit");
            if (ICmpAndContains(op1, op0) && IsKnownToBeAPowerOfTwo(op0, false, instruction))
                return ICmpEqualityRewrite(LlvmCmpInstruction.GetInversePredicate(predicate), op1, ICmpZero(op1), "power_bit");
            for (int side = 0; side != 2; ++side)
            {
                var a = side == 0 ? op0 : op1; var b = side == 0 ? op1 : op0;
                if (CallsIntrinsic(b, LlvmIntrinsicKind.FShr, out var rotate) && b.HasOneUse && rotate.GetArgument(0) == a && rotate.GetArgument(1) == a)
                    return Cmp(a, Builder.CreateIntrinsic(LlvmIntrinsicKind.FShl, [a, a, rotate.GetArgument(2)]), "rotate_left");
                if (a is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, HasOneUse: true } bx && ICmpImmediate(bx.Right) && !ICmpImmediate(b))
                    return Cmp(Builder.CreateXor(bx.Left, b), bx.Right, "xor_constant_rhs");
            }
            if (FoldICmpEqualityPowerOffset(instruction) is { } power) return power;
            return FoldICmpEqualityWithOffset(instruction);
        }
        private static bool ICmpAndContains(LlvmValue value, LlvmValue operand) => value is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and && (and.Left == operand || and.Right == operand);
        private LlvmInstruction? FoldICmpEqualityPowerOffset(LlvmICmpInstruction instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                var lhs = side == 0 ? instruction.Left : instruction.Right;
                var rhs = side == 0 ? instruction.Right : instruction.Left;
                if (lhs is not LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true } and) continue;
                for (int commuted = 0; commuted != 2; ++commuted)
                {
                    var a = commuted == 0 ? and.Left : and.Right;
                    var value = commuted == 0 ? and.Right : and.Left;
                    bool zero = side == 0 && rhs is LlvmConstantInt { IsZero: true };
                    if (rhs != a && !zero || value is not LlvmBinaryOperator op) continue;
                    LlvmValue? b = op.Opcode is LlvmOpcode.Add or LlvmOpcode.Xor ? op.Left == a ? op.Right : op.Right == a ? op.Left : null
                        : op.Opcode == LlvmOpcode.Sub && op.Right == a ? op.Left : null;
                    if (b is null || !IsKnownToBeAPowerOfTwo(a, true, instruction)) continue;
                    return ICmpEqualityRewrite(instruction.Predicate, Builder.CreateAnd(b, a), zero ? a : ICmpZero(a), "power_offset");
                }
            }
            return null;
        }
    }
}
