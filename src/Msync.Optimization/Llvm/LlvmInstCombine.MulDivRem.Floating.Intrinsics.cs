// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp
// foldPowiReassoc and the intrinsic-pattern branches of visitFMul/foldFMulReassoc.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MdrFPIntrinsic(LlvmValue value, LlvmIntrinsicKind kind) =>
            value is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor: { } descriptor } && descriptor.Kind == kind;
        private static bool MdrFPHas(LlvmInstruction instruction, LlvmFastMathFlags flags) =>
            instruction is ILlvmFastMathInstruction fp && (fp.FastMathFlags & flags) == flags;
        private static LlvmValue? MdrFAbs(LlvmValue value) => MdrFPIntrinsic(value, LlvmIntrinsicKind.FAbs) ? ((LlvmCallInstruction)value).GetArgument(0) : null;
        private static LlvmValue? MdrSqrt(LlvmValue value) => MdrFPIntrinsic(value, LlvmIntrinsicKind.Sqrt) ? ((LlvmCallInstruction)value).GetArgument(0) : null;
        private LlvmValue MdrCreateFPIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments, LlvmFastMathFlags flags)
        {
            var call = Builder.CreateIntrinsic(kind, arguments);
            if (call is LlvmCallInstruction intrinsic) intrinsic.FastMathFlags = flags;
            return call;
        }
        private LlvmInstruction? FoldPowiReassoc(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right; var flags = instruction.FastMathFlags;
            if (instruction.Opcode == LlvmOpcode.FMul)
            {
                var single = Increment(left, right) ?? Increment(right, left);
                if (single is not null) return single;
                if ((left.HasOneUser || right.HasOneUser) && MdrFPIntrinsic(left, LlvmIntrinsicKind.PowI) && MdrFPIntrinsic(right, LlvmIntrinsicKind.PowI))
                {
                    var a = (LlvmCallInstruction)left; var b = (LlvmCallInstruction)right;
                    if (MdrFPHas(a, LlvmFastMathFlags.AllowReassoc) && MdrFPHas(b, LlvmFastMathFlags.AllowReassoc) &&
                        ReferenceEquals(a.GetArgument(0), b.GetArgument(0)) && ReferenceEquals(a.GetArgument(1).Type, b.GetArgument(1).Type) &&
                        WillNotOverflowSignedAdd(a.GetArgument(1), b.GetArgument(1), instruction))
                        return MdrReplace(instruction, Pow(a.GetArgument(0), a.GetArgument(1), b.GetArgument(1)), "instcombine.fmul.powi_add_exponents");
                }
            }
            if (instruction.Opcode != LlvmOpcode.FDiv || !MdrHas(instruction, LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoNaNs) ||
                left is not LlvmCallInstruction { HasOneUse: true } numerator || !MdrFPIntrinsic(numerator, LlvmIntrinsicKind.PowI) ||
                !MdrFPHas(numerator, LlvmFastMathFlags.AllowReassoc)) return null;
            var x = numerator.GetArgument(0); var y = numerator.GetArgument(1);
            if (!WillNotOverflowSignedSub(y, MdrInt(y, 1), instruction)) return null;
            if (ReferenceEquals(x, right))
                return MdrReplace(instruction, Pow(x, y, MdrInt(y, LlvmAPInt.GetAllOnes(y.Type.GetIntegerBitWidth()))), "instcombine.fdiv.powi_decrement");
            if (right is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul } denominator && MdrHas(denominator, LlvmFastMathFlags.AllowReassoc) &&
                MdrMatchFMul(denominator, x, out var z))
                return MdrRewrite(MdrBin(LlvmOpcode.FDiv, Pow(x, y, MdrInt(y, LlvmAPInt.GetAllOnes(y.Type.GetIntegerBitWidth()))), z, fmf: flags), "instcombine.fdiv.powi_product_divisor");
            return null;
            LlvmValue Pow(LlvmValue a, LlvmValue b, LlvmValue c) => MdrCreateFPIntrinsic(LlvmIntrinsicKind.PowI,
                [a, Builder.CreateAdd(b, c, nsw: true)], flags);
            LlvmInstruction? Increment(LlvmValue value, LlvmValue other)
            {
                if (value is not LlvmCallInstruction { HasOneUse: true } call || !MdrFPIntrinsic(call, LlvmIntrinsicKind.PowI) ||
                    !MdrFPHas(call, LlvmFastMathFlags.AllowReassoc) || !ReferenceEquals(call.GetArgument(0), other)) return null;
                var exponent = call.GetArgument(1); var one = MdrInt(exponent, 1);
                return WillNotOverflowSignedAdd(exponent, one, instruction) ?
                    MdrReplace(instruction, Pow(other, exponent, one), "instcombine.fmul.powi_increment") : null;
            }
        }
        private LlvmInstruction? FoldFMulIntrinsicPatterns(LlvmBinaryOperator instruction)
        {
            if (!MdrHas(instruction, LlvmFastMathFlags.Fast)) return null;
            LlvmCallInstruction? logarithm = null; LlvmValue? x = null, y = null;
            Match(instruction.Left, instruction.Right); Match(instruction.Right, instruction.Left);
            if (logarithm is null) return null;
            var log = MdrCreateFPIntrinsic(LlvmIntrinsicKind.Log2, [x!], instruction.FastMathFlags);
            return MdrRewrite(MdrBin(LlvmOpcode.FSub, MdrBuildFP(LlvmOpcode.FMul, log, y!, instruction.FastMathFlags), y!, fmf: instruction.FastMathFlags), "instcombine.fmul.log2_half");
            void Match(LlvmValue value, LlvmValue other)
            {
                if (value is not LlvmCallInstruction { HasOneUse: true } call || !MdrFPIntrinsic(call, LlvmIntrinsicKind.Log2) ||
                    call.GetArgument(0) is not LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, HasOneUse: true } mul || !MdrSpecificFP(mul.Right, 0.5)) return;
                logarithm = call; x = mul.Left; y = other;
            }
        }
        private LlvmInstruction? FoldFMulIntrinsicTail(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right; var flags = instruction.FastMathFlags;
            var extrema = MinMax(left, right) ?? MinMax(right, left);
            if (extrema is not null) return extrema;
            if (MdrHas(instruction, LlvmFastMathFlags.AllowContract))
            {
                var sine = Trig(left, right) ?? Trig(right, left);
                if (sine is not null) return MdrReplace(instruction, sine, "instcombine.fmul.tan_cos");
            }
            if (MdrHas(instruction, LlvmFastMathFlags.AllowReassoc))
            {
                var ldexp = Scale(left, right) ?? Scale(right, left);
                if (ldexp is not null) return MdrReplace(instruction, ldexp, "instcombine.fmul.ldexp_one");
            }
            return null;
            LlvmInstruction? MinMax(LlvmValue a, LlvmValue b)
            {
                if (!MdrFPIntrinsic(a, LlvmIntrinsicKind.Maximum) || !MdrFPIntrinsic(b, LlvmIntrinsicKind.Minimum)) return null;
                var max = (LlvmCallInstruction)a; var min = (LlvmCallInstruction)b;
                var x = max.GetArgument(0); var y = max.GetArgument(1);
                if (!((ReferenceEquals(min.GetArgument(0), x) && ReferenceEquals(min.GetArgument(1), y)) ||
                    (ReferenceEquals(min.GetArgument(1), x) && ReferenceEquals(min.GetArgument(0), y)))) return null;
                var fmf = MdrHas(instruction, LlvmFastMathFlags.NoNaNs) ? flags : flags & ~LlvmFastMathFlags.NoInfs;
                return MdrRewrite(MdrBin(LlvmOpcode.FMul, x, y, fmf: fmf), "instcombine.fmul.minimum_maximum");
            }
            LlvmValue? Trig(LlvmValue a, LlvmValue b)
            {
                if (!a.HasOneUse || !b.HasOneUse || !MdrFPIntrinsic(a, LlvmIntrinsicKind.Tan) || !MdrFPIntrinsic(b, LlvmIntrinsicKind.Cos)) return null;
                var tan = (LlvmCallInstruction)a; var cos = (LlvmCallInstruction)b;
                return ReferenceEquals(tan.GetArgument(0), cos.GetArgument(0)) ? MdrCreateFPIntrinsic(LlvmIntrinsicKind.Sin, [tan.GetArgument(0)], flags) : null;
            }
            LlvmValue? Scale(LlvmValue x, LlvmValue value)
            {
                if (value is not LlvmCallInstruction { HasOneUse: true } call || !MdrFPIntrinsic(call, LlvmIntrinsicKind.Ldexp) ||
                    !MdrFPHas(call, LlvmFastMathFlags.AllowReassoc) || !MdrSpecificFP(call.GetArgument(0), 1)) return null;
                return MdrCreateFPIntrinsic(LlvmIntrinsicKind.Ldexp, [x, call.GetArgument(1)], flags);
            }
        }
        private LlvmInstruction? FoldFMulReassocIntrinsics(LlvmBinaryOperator instruction)
        {
            var left = instruction.Left; var right = instruction.Right; var flags = instruction.FastMathFlags;
            if (MdrHas(instruction, LlvmFastMathFlags.NoNaNs) && left.HasOneUse && right.HasOneUse &&
                MdrSqrt(left) is { } a && MdrSqrt(right) is { } b)
                return MdrReplace(instruction, MdrCreateFPIntrinsic(LlvmIntrinsicKind.Sqrt,
                    [MdrBuildFP(LlvmOpcode.FMul, a, b, flags)], flags), "instcombine.fmul.sqrt_product");
            if (MdrHas(instruction, LlvmFastMathFlags.NoSignedZeros))
            {
                var root = SqrtReciprocal(left, right) ?? SqrtReciprocal(right, left);
                if (root is not null) return root;
            }
            if (MdrHas(instruction, LlvmFastMathFlags.NoNaNs | LlvmFastMathFlags.NoSignedZeros) && ReferenceEquals(left, right) && left.HasNUses(2) && left is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv } division)
            {
                if (MdrSqrt(division.Right) is { } y)
                    return MdrRewrite(MdrBin(LlvmOpcode.FDiv, MdrBuildFP(LlvmOpcode.FMul, division.Left, division.Left, flags), y, fmf: flags), "instcombine.fmul.square_sqrt_divisor");
                if (MdrSqrt(division.Left) is { } x)
                    return MdrRewrite(MdrBin(LlvmOpcode.FDiv, x, MdrBuildFP(LlvmOpcode.FMul, division.Right, division.Right, flags), fmf: flags), "instcombine.fmul.square_sqrt_dividend");
            }
            var increment = IncrementPow(left, right) ?? IncrementPow(right, left);
            if (increment is not null) return increment;
            if (FoldPowiReassoc(instruction) is { } powi) return powi;
            if (left.HasOneUser || right.HasOneUser)
            {
                if (MdrFPIntrinsic(left, LlvmIntrinsicKind.Pow) && MdrFPIntrinsic(right, LlvmIntrinsicKind.Pow))
                {
                    var lhs = (LlvmCallInstruction)left; var rhs = (LlvmCallInstruction)right;
                    if (ReferenceEquals(lhs.GetArgument(0), rhs.GetArgument(0)))
                        return MdrReplace(instruction, MdrCreateFPIntrinsic(LlvmIntrinsicKind.Pow,
                            [lhs.GetArgument(0), MdrBuildFP(LlvmOpcode.FAdd, lhs.GetArgument(1), rhs.GetArgument(1), flags)], flags), "instcombine.fmul.pow_add_exponents");
                    if (ReferenceEquals(lhs.GetArgument(1), rhs.GetArgument(1)))
                        return MdrReplace(instruction, MdrCreateFPIntrinsic(LlvmIntrinsicKind.Pow,
                            [MdrBuildFP(LlvmOpcode.FMul, lhs.GetArgument(0), rhs.GetArgument(0), flags), lhs.GetArgument(1)], flags), "instcombine.fmul.pow_multiply_bases");
                }
                if (MdrFPIntrinsic(left, LlvmIntrinsicKind.Exp) && MdrFPIntrinsic(right, LlvmIntrinsicKind.Exp))
                    return MergeExp(LlvmIntrinsicKind.Exp);
                if (MdrFPIntrinsic(left, LlvmIntrinsicKind.Exp2) && MdrFPIntrinsic(right, LlvmIntrinsicKind.Exp2))
                    return MergeExp(LlvmIntrinsicKind.Exp2);
            }
            return null;
            LlvmInstruction? MergeExp(LlvmIntrinsicKind kind) => MdrReplace(instruction, MdrCreateFPIntrinsic(kind,
                [MdrBuildFP(LlvmOpcode.FAdd, ((LlvmCallInstruction)left).GetArgument(0), ((LlvmCallInstruction)right).GetArgument(0), flags)], flags), "instcombine.fmul.exp_add_arguments");
            LlvmInstruction? SqrtReciprocal(LlvmValue value, LlvmValue x)
            {
                if (value is not LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv } div || !MdrSpecificFP(div.Left, 1) ||
                    MdrSqrt(div.Right) is not { } argument || !ReferenceEquals(argument, x)) return null;
                return MdrRewrite(MdrBin(LlvmOpcode.FDiv, x, div.Right, fmf: flags), "instcombine.fmul.sqrt_reciprocal");
            }
            LlvmInstruction? IncrementPow(LlvmValue value, LlvmValue x)
            {
                if (value is not LlvmCallInstruction { HasOneUse: true } call || !MdrFPIntrinsic(call, LlvmIntrinsicKind.Pow) || !ReferenceEquals(call.GetArgument(0), x)) return null;
                var y = MdrBuildFP(LlvmOpcode.FAdd, call.GetArgument(1), LlvmConstantFP.Get(instruction.Type, 1), flags);
                return MdrReplace(instruction, MdrCreateFPIntrinsic(LlvmIntrinsicKind.Pow, [x, y], flags), "instcombine.fmul.pow_increment");
            }
        }
    }
}
