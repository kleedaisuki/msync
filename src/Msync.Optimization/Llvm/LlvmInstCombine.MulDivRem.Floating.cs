// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp
// visitFMul/FDiv/FRem, foldFPSignBitOps and scalar floating reassociation.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MdrSpecificFP(LlvmValue value, double number) => value is LlvmConstantFP fp &&
            fp.Value.BitwiseIsEqual(LlvmConstantFP.Get(fp.Type, number).Value);
        private static LlvmValue? MdrFNeg(LlvmValue value) => value switch
        {
            LlvmUnaryOperator { Opcode: LlvmOpcode.FNeg } unary => unary.Operand,
            LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, Left: LlvmConstantFP { Value.IsZero: true, IsNegative: true } } subtraction => subtraction.Right,
            _ => null
        };
        private static bool MdrNormal(LlvmConstant? value) => value is LlvmConstantFP { Value.IsNormal: true };
        private LlvmValue MdrBuildFP(LlvmOpcode opcode, LlvmValue left, LlvmValue right, LlvmFastMathFlags fmf) =>
            Builder.CreateBinary(opcode, left, right, fastMathFlags: fmf);
        private LlvmInstruction? FoldFPSignBitOps(LlvmBinaryOperator instruction)
        {
            if (MdrFNeg(instruction.Left) is { } a && MdrFNeg(instruction.Right) is { } b)
                return MdrRewrite(MdrBin(instruction.Opcode, a, b, fmf: instruction.FastMathFlags), "instcombine.fp.cancel_negations");
            if (ReferenceEquals(instruction.Left, instruction.Right) && MdrFAbs(instruction.Left) is { } x)
                return MdrRewrite(MdrBin(instruction.Opcode, x, x, fmf: instruction.FastMathFlags), "instcombine.fp.abs_square");
            if (MdrFAbs(instruction.Left) is { } lhs && MdrFAbs(instruction.Right) is { } rhs && (instruction.Left.HasOneUse || instruction.Right.HasOneUse))
                return MdrReplace(instruction, MdrCreateFPIntrinsic(LlvmIntrinsicKind.FAbs,
                    [MdrBuildFP(instruction.Opcode, lhs, rhs, instruction.FastMathFlags)], instruction.FastMathFlags), "instcombine.fp.abs_product");
            return null;
        }
        private LlvmInstruction? VisitFMul(LlvmBinaryOperator instruction)
        {
            Count("instcombine.fmul.visits");
            if (MdrSimplify(instruction) is { } simple) return MdrReplace(instruction, simple, "instcombine.fmul.simplify");
            if (SimplifyAssociativeOrCommutative(instruction)) return instruction;
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            if (FoldBinOpIntoSelectOrPhi(instruction) is { } threaded) return threaded;
            if (FoldMulSelectToNegate(instruction) is { } select) return MdrReplace(instruction, select, "instcombine.fmul.select_negate");
            if (FoldFPSignBitOps(instruction) is { } signs) return signs;
            if (FoldFBinOpOfIntCasts(instruction) is { } casts) return casts;
            var left = instruction.Left; var right = instruction.Right; var flags = instruction.FastMathFlags;
            if (MdrSpecificFP(right, -1)) return MdrRewrite(new LlvmUnaryOperator(left) { FastMathFlags = flags }, "instcombine.fmul.minus_one");
            if (MdrFNeg(left) is { } negated && right is LlvmConstant c && LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, c, DL, Metrics) is { } neg)
                return MdrRewrite(MdrBin(LlvmOpcode.FMul, negated, neg, fmf: flags), "instcombine.fmul.negate_constant");
            if (MdrHas(instruction, LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros))
            {
                if (left is LlvmCastInstruction { Opcode: LlvmOpcode.UIToFP } a && a.Operand.Type.IsIntegerTy(1))
                    return MdrReplace(instruction, Builder.CreateSelect(a.Operand, right, LlvmConstantFP.Get(instruction.Type, 0), fastMathFlags: flags), "instcombine.fmul.uitofp_boolean_left");
                if (right is LlvmCastInstruction { Opcode: LlvmOpcode.UIToFP } b && b.Operand.Type.IsIntegerTy(1))
                    return MdrReplace(instruction, Builder.CreateSelect(b.Operand, left, LlvmConstantFP.Get(instruction.Type, 0), fastMathFlags: flags), "instcombine.fmul.uitofp_boolean_right");
            }
            if (SimplifySelectsFeedingBinaryOp(instruction, left, right) is { } selects)
                return MdrReplace(instruction, selects, "instcombine.fmul.paired_selects");
            if (MdrHas(instruction, LlvmFastMathFlags.AllowReassoc) && FoldFMulReassoc(instruction) is { } reassociated) return reassociated;
            if (FoldFMulIntrinsicPatterns(instruction) is { } intrinsic) return intrinsic;
            if (LlvmValueTracking.MatchSimpleRecurrence(instruction, out _, out var start, out _) &&
                MdrHas(instruction, LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros) && MdrZero(start))
                return MdrReplace(instruction, start, "instcombine.fmul.zero_recurrence");
            if (FoldFMulIntrinsicTail(instruction) is { } tail) return tail;
            if (SimplifyDemandedInstructionFPClass(instruction)) return instruction;
            return null;
        }

        private LlvmInstruction? FoldFMulReassoc(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right;
            if (right is LlvmConstantFP coefficient && !coefficient.Value.IsZero && !coefficient.Value.IsInfinity && !coefficient.Value.IsNaN &&
                left is LlvmBinaryOperator operation && MdrHas(operation, LlvmFastMathFlags.AllowReassoc))
            {
                var fmf = instruction.FastMathFlags & operation.FastMathFlags;
                if (operation.Opcode == LlvmOpcode.FDiv && operation.Left is LlvmConstant dividend && operation.HasOneUse &&
                    LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FMul, coefficient, dividend, DL, Metrics) is { } product && MdrNormal(product))
                    return MdrRewrite(MdrBin(LlvmOpcode.FDiv, product, operation.Right, fmf: fmf), "instcombine.fmul.reassoc_constant_dividend");
                if (operation.Opcode == LlvmOpcode.FDiv && operation.Right is LlvmConstant divisor)
                {
                    var ratio = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FDiv, coefficient, divisor, DL, Metrics);
                    if (MdrNormal(ratio)) return MdrRewrite(MdrBin(LlvmOpcode.FMul, operation.Left, ratio!, fmf: fmf), "instcombine.fmul.reassoc_constant_divisor");
                    var reverse = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FDiv, divisor, coefficient, DL, Metrics);
                    if (operation.HasOneUse && MdrNormal(reverse)) return MdrRewrite(MdrBin(LlvmOpcode.FDiv, operation.Left, reverse!, fmf: fmf), "instcombine.fmul.reassoc_normal_reverse");
                }
                if (operation.HasOneUse && operation.Opcode == LlvmOpcode.FAdd && operation.Right is LlvmConstant addend &&
                    LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FMul, coefficient, addend, DL, Metrics) is { } addProduct)
                    return MdrRewrite(MdrBin(LlvmOpcode.FAdd, MdrBuildFP(LlvmOpcode.FMul, operation.Left, coefficient, fmf), addProduct, fmf: fmf), "instcombine.fmul.distribute_constant_add");
                if (operation.HasOneUse && operation.Opcode == LlvmOpcode.FSub && operation.Left is LlvmConstant subtrahend &&
                    LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FMul, coefficient, subtrahend, DL, Metrics) is { } subProduct)
                    return MdrRewrite(MdrBin(LlvmOpcode.FSub, subProduct, MdrBuildFP(LlvmOpcode.FMul, operation.Right, coefficient, fmf), fmf: fmf), "instcombine.fmul.distribute_constant_sub");
            }
            var sink = SinkDivision(left, right) ?? SinkDivision(right, left);
            if (sink is not null) return sink;
            if (FoldFMulReassocIntrinsics(instruction) is { } intrinsic) return intrinsic;
            var square = FormSquare(left, right) ?? FormSquare(right, left);
            return square;
            LlvmInstruction? SinkDivision(LlvmValue value, LlvmValue other)
            {
                if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv, HasOneUse: true } div || !MdrHas(div, LlvmFastMathFlags.AllowReassoc)) return null;
                var fmf = instruction.FastMathFlags & div.FastMathFlags;
                if ((fmf & LlvmFastMathFlags.AllowReassoc) == 0) return null;
                return MdrRewrite(MdrBin(LlvmOpcode.FDiv, MdrBuildFP(LlvmOpcode.FMul, div.Left, other, fmf), div.Right, fmf: fmf), "instcombine.fmul.sink_division");
            }
            LlvmInstruction? FormSquare(LlvmValue value, LlvmValue factor)
            {
                if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, HasOneUse: true } product ||
                    !MdrMatchFMul(product, factor, out var other) || ReferenceEquals(factor, other)) return null;
                return MdrRewrite(MdrBin(LlvmOpcode.FMul, MdrBuildFP(LlvmOpcode.FMul, factor, factor, instruction.FastMathFlags), other,
                    fmf: instruction.FastMathFlags), "instcombine.fmul.form_square");
            }
        }

        private LlvmInstruction? VisitFDiv(LlvmBinaryOperator instruction)
        {
            Count("instcombine.fdiv.visits");
            if (MdrSimplify(instruction) is { } simple) return MdrReplace(instruction, simple, "instcombine.fdiv.simplify");
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            if (FoldFDivConstantDivisor(instruction) is { } divisor) return divisor;
            if (FoldFDivConstantDividend(instruction) is { } dividend) return dividend;
            if (FoldFPSignBitOps(instruction) is { } signs) return signs;
            if (FoldFSqrtDivIntoFMul(instruction) is { } sqrt) return sqrt;
            var left = instruction.Left; var right = instruction.Right; var fmf = instruction.FastMathFlags;
            if (left is LlvmConstant && right is LlvmSelectInstruction rhs && FoldOpIntoSelect(instruction, rhs) is { } rselect) return rselect;
            if (right is LlvmConstant && left is LlvmSelectInstruction lhs && FoldOpIntoSelect(instruction, lhs) is { } lselect) return lselect;
            if (MdrHas(instruction, LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.AllowReciprocal))
            {
                if (left is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv, HasOneUse: true } a && (a.Right is not LlvmConstant || right is not LlvmConstant))
                    return MdrRewrite(MdrBin(LlvmOpcode.FDiv, a.Left, MdrBuildFP(LlvmOpcode.FMul, a.Right, right, fmf), fmf: fmf), "instcombine.fdiv.reassoc_dividend");
                if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv, HasOneUse: true } b && (b.Right is not LlvmConstant || left is not LlvmConstant))
                    return MdrRewrite(MdrBin(LlvmOpcode.FDiv, MdrBuildFP(LlvmOpcode.FMul, b.Right, left, fmf), b.Left, fmf: fmf), "instcombine.fdiv.reassoc_divisor");
                if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv } inverse && MdrSpecificFP(inverse.Left, 1))
                    return MdrRewrite(MdrBin(LlvmOpcode.FMul, inverse.Right, left, fmf: fmf), "instcombine.fdiv.reciprocal_to_multiply");
            }
            RecordUnavailableFDivTrigLibcall(instruction);
            if (MdrHas(instruction, LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.AllowReassoc) && MdrMatchFMul(right, left, out var factor))
            {
                ReplaceOperand(instruction, 0, LlvmConstantFP.Get(instruction.Type, 1));
                return MdrRewrite(ReplaceOperand(instruction, 1, factor), "instcombine.fdiv.cancel_factor");
            }
            if (MdrHas(instruction, LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoInfs) &&
                (MdrFAbs(right) is { } absolute && ReferenceEquals(left, absolute) || MdrFAbs(left) is { } numerator && ReferenceEquals(right, numerator)))
            {
                var x = MdrFAbs(right) ?? right;
                return MdrReplace(instruction, MdrCreateFPIntrinsic(LlvmIntrinsicKind.CopySign, [LlvmConstantFP.Get(instruction.Type, 1), x], fmf), "instcombine.fdiv.abs_sign");
            }
            if (FoldFDivPowDivisor(instruction) is { } power) return power;
            if (FoldFDivSqrtDivisor(instruction) is { } root) return root;
            if (MdrHas(instruction, LlvmFastMathFlags.AllowReassoc) && left is LlvmCallInstruction { HasOneUse: true } call &&
                MdrFPIntrinsic(call, LlvmIntrinsicKind.Pow) && ReferenceEquals(call.GetArgument(0), right))
            {
                var exponent = MdrBuildFP(LlvmOpcode.FAdd, call.GetArgument(1), LlvmConstantFP.Get(instruction.Type, -1), fmf);
                return MdrReplace(instruction, MdrCreateFPIntrinsic(LlvmIntrinsicKind.Pow, [right, exponent], fmf), "instcombine.fdiv.power_decrement");
            }
            return FoldPowiReassoc(instruction);
        }

        private LlvmInstruction? VisitFRem(LlvmBinaryOperator instruction)
        {
            Count("instcombine.frem.visits");
            if (MdrSimplify(instruction) is { } simple) return MdrReplace(instruction, simple, "instcombine.frem.simplify");
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            var value = StripSignOnlyFPOps(instruction.Right);
            return ReferenceEquals(value, instruction.Right) ? null : MdrRewrite(ReplaceOperand(instruction, 1, value), "instcombine.frem.strip_divisor_sign");
        }
        private static bool MdrMatchFMul(LlvmValue value, LlvmValue factor, out LlvmValue other)
        {
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul } mul)
            {
                if (ReferenceEquals(mul.Left, factor)) { other = mul.Right; return true; }
                if (ReferenceEquals(mul.Right, factor)) { other = mul.Left; return true; }
            }
            other = null!; return false;
        }
    }
}
