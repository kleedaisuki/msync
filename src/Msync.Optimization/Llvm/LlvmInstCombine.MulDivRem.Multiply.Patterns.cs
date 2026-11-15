// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp visitMul subpatterns.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldMulSelectConstants(LlvmBinaryOperator instruction)
        {
            return FoldSide(instruction.Left, instruction.Right) ?? FoldSide(instruction.Right, instruction.Left);
            LlvmInstruction? FoldSide(LlvmValue other, LlvmValue value)
            {
                if (!other.HasOneUse || value is not LlvmSelectInstruction { HasOneUse: true, TrueValue: LlvmConstant yes, FalseValue: LlvmConstant no } select ||
                    other is not LlvmBinaryOperator { Opcode: LlvmOpcode.Mul or LlvmOpcode.Shl, Right: LlvmConstant coefficient } operation) return null;
                var tv = LlvmConstantFolding.ConstantFoldBinaryOpOperands(operation.Opcode, yes, coefficient, DL, Metrics);
                var fv = LlvmConstantFolding.ConstantFoldBinaryOpOperands(operation.Opcode, no, coefficient, DL, Metrics);
                if (tv is null || fv is null) return null;
                var replacement = Builder.CreateSelect(select.Condition, tv, fv);
                return MdrRewrite(MdrBin(LlvmOpcode.Mul, operation.Left, replacement,
                    instruction.HasNoUnsignedWrap && operation.HasNoUnsignedWrap,
                    instruction.HasNoSignedWrap && operation.HasNoSignedWrap && tv is LlvmConstantInt { Value.IsMinSignedValue: false } &&
                    fv is LlvmConstantInt { Value.IsMinSignedValue: false }), "instcombine.mul.select_constants");
            }
        }
        private LlvmInstruction? FoldMulDivisionPair(LlvmBinaryOperator instruction)
        {
            LlvmValue factor = instruction.Right;
            var division = instruction.Left as LlvmBinaryOperator;
            if (division?.Opcode is not (LlvmOpcode.UDiv or LlvmOpcode.SDiv))
            { factor = instruction.Left; division = instruction.Right as LlvmBinaryOperator; }
            if (division?.Opcode is not (LlvmOpcode.UDiv or LlvmOpcode.SDiv) || !division.HasOneUse) return null;
            var opposite = DynCastNegVal(factor);
            bool same = ReferenceEquals(division.Right, factor), negated = ReferenceEquals(division.Right, opposite);
            if (!same && !negated) return null;
            if (division.IsExact)
                return same ? MdrReplace(instruction, division.Left, "instcombine.mul.exact_division_pair") :
                    MdrRewrite(MdrBin(LlvmOpcode.Sub, MdrInt(instruction, 0), division.Left), "instcombine.mul.exact_negative_division_pair");
            var frozen = MdrFreeze(division.Left);
            var remainder = Builder.CreateBinary(division.Opcode == LlvmOpcode.UDiv ? LlvmOpcode.URem : LlvmOpcode.SRem, frozen, division.Right);
            return MdrRewrite(MdrBin(LlvmOpcode.Sub, same ? frozen : remainder, same ? remainder : frozen), "instcombine.mul.division_pair_to_remainder");
        }
        private LlvmInstruction? FoldMulAbs(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (ReferenceEquals(left, right) && MdrAbs(left, out var x, out _))
                return MdrRewrite(MdrBin(LlvmOpcode.Mul, x, x), "instcombine.mul.abs_square");
            if (instruction.HasNoSignedWrap && left.HasOneUse && right.HasOneUse &&
                MdrAbs(left, out var a, out bool aPoison) && aPoison && MdrAbs(right, out var b, out bool bPoison) && bPoison)
                return MdrReplace(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.Abs,
                    [Builder.CreateMul(a, b, nsw: true), LlvmConstantInt.GetTrue(instruction.Context)]), "instcombine.mul.abs_product");
            return null;
        }
        private static bool MdrAbs(LlvmValue value, out LlvmValue operand, out bool minPoison)
        {
            if (value is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } call && descriptor.Kind == LlvmIntrinsicKind.Abs)
            { operand = call.GetArgument(0); minPoison = call.GetArgument(1) is LlvmConstantInt { IsOne: true }; return true; }
            operand = null!; minPoison = false; return false;
        }
        private LlvmInstruction? FoldMulBoolean(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } a &&
                right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } b && a.Operand.Type.IsIntegerTy(1) &&
                ReferenceEquals(a.Operand.Type, b.Operand.Type))
            {
                bool same = a.Opcode == b.Opcode;
                if (a.HasOneUse || b.HasOneUse || same && ReferenceEquals(a.Operand, b.Operand))
                    return MdrRewrite(new LlvmCastInstruction(same ? LlvmOpcode.ZExt : LlvmOpcode.SExt,
                        Builder.CreateAnd(a.Operand, b.Operand, "mulbool"), instruction.Type), "instcombine.mul.extended_boolean_pair");
            }
            if (left is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } lhs && lhs.Operand.Type.IsIntegerTy(1))
                return MdrReplace(instruction, Builder.CreateSelect(lhs.Operand, right, MdrInt(instruction, 0)), "instcombine.mul.zext_boolean_left");
            if (right is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt } rhs && rhs.Operand.Type.IsIntegerTy(1))
                return MdrReplace(instruction, Builder.CreateSelect(rhs.Operand, left, MdrInt(instruction, 0)), "instcombine.mul.zext_boolean_right");
            var select = FoldSigned(left, right) ?? FoldSigned(right, left);
            if (select is not null) return MdrReplace(instruction, select, "instcombine.mul.sext_boolean");
            if (right is LlvmConstant constant && left is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } cast && cast.Operand.Type.IsIntegerTy(1) &&
                LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Sub, MdrInt(instruction, 0), constant, DL, Metrics) is { } neg)
                return MdrReplace(instruction, Builder.CreateSelect(cast.Operand, neg, MdrInt(instruction, 0)), "instcombine.mul.sext_boolean_constant");
            return null;
            LlvmValue? FoldSigned(LlvmValue value, LlvmValue other) => value is LlvmCastInstruction { Opcode: LlvmOpcode.SExt, HasOneUse: true } sext &&
                sext.Operand.Type.IsIntegerTy(1) ? Builder.CreateSelect(sext.Operand, Builder.CreateNeg(other, nsw: instruction.HasNoSignedWrap), MdrInt(instruction, 0)) : null;
        }
        private LlvmInstruction? FoldMulSignExtract(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right; uint width = instruction.Type.GetIntegerBitWidth();
            if (right is LlvmConstant constant && left is LlvmBinaryOperator { Opcode: LlvmOpcode.AShr, HasOneUse: true, Right: LlvmConstantInt amount } shr &&
                amount.Value == new LlvmAPInt(width, width - 1) &&
                LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Sub, MdrInt(instruction, 0), constant, DL, Metrics) is { } negative)
                return MdrReplace(instruction, Builder.CreateSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, shr.Left, MdrInt(instruction, 0)), negative, MdrInt(instruction, 0)), "instcombine.mul.signmask_constant");
            var extract = FoldExtract(left, right) ?? FoldExtract(right, left);
            if (extract is not null) return MdrReplace(instruction, extract, "instcombine.mul.sign_or_lowbit_select");
            var magnitude = FoldAbs(left, right) ?? FoldAbs(right, left);
            return magnitude is null ? null : MdrReplace(instruction, magnitude, "instcombine.mul.signmask_to_abs");
            LlvmValue? FoldExtract(LlvmValue value, LlvmValue other)
            {
                if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.LShr, Right: LlvmConstantInt c } ls && c.Value == new LlvmAPInt(width, width - 1))
                    return Builder.CreateSelect(Builder.CreateICmp(LlvmCmpPredicate.ICmpSlt, ls.Left, MdrInt(instruction, 0)), other, MdrInt(instruction, 0));
                if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true, Right: LlvmConstantInt { IsOne: true } } lowBit)
                    return Builder.CreateSelect(Builder.CreateTrunc(lowBit.Left, LlvmType.GetInt1Ty(instruction.Context)), other, MdrInt(instruction, 0));
                return null;
            }
            LlvmValue? FoldAbs(LlvmValue value, LlvmValue other)
            {
                if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.Or, Right: LlvmConstantInt { IsOne: true }, Left: LlvmBinaryOperator
                    { Opcode: LlvmOpcode.AShr, Right: LlvmConstantInt c } sign } || !ReferenceEquals(sign.Left, other) || c.Value != new LlvmAPInt(width, width - 1)) return null;
                return Builder.CreateIntrinsic(LlvmIntrinsicKind.Abs, [other, LlvmConstantInt.GetBool(instruction.Context, instruction.HasNoSignedWrap)]);
            }
        }
    }
}
