// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineMulDivRem.cpp
// foldFDivConstantDivisor/Dividend, foldFDivPowDivisor, foldFDivSqrtDivisor.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? FoldFDivConstantDivisor(LlvmBinaryOperator instruction)
        {
            if (instruction.Right is not LlvmConstant constant) return null;
            var flags = instruction.FastMathFlags;
            if (MdrFNeg(instruction.Left) is { } x &&
                LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, constant, DL, Metrics) is { } negative)
                return MdrRewrite(MdrBin(LlvmOpcode.FDiv, x, negative, fmf: flags), "instcombine.fdiv.negate_constant_divisor");
            if (MdrHas(instruction, LlvmFastMathFlags.NoNaNs) && constant is LlvmConstantFP { Value.IsZero: true } zero &&
                (!zero.IsNegative || MdrHas(instruction, LlvmFastMathFlags.NoSignedZeros)))
                return MdrReplace(instruction, MdrCreateFPIntrinsic(LlvmIntrinsicKind.CopySign,
                    [LlvmConstantFP.Get(instruction.Type, double.PositiveInfinity), instruction.Left], flags), "instcombine.fdiv.zero_to_copysign_infinity");
            if (constant is not LlvmConstantFP divisor || !(divisor.Value.GetExactInverse(out _) ||
                MdrHas(instruction, LlvmFastMathFlags.AllowReciprocal) && divisor.Value.IsNormal))
            { Count("instcombine.fdiv.constant_inverse_unavailable"); return null; }
            var reciprocal = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FDiv,
                LlvmConstantFP.Get(instruction.Type, 1), divisor, DL, Metrics);
            if (!MdrNormal(reciprocal)) { Count("instcombine.fdiv.reciprocal_not_normal"); return null; }
            return MdrRewrite(MdrBin(LlvmOpcode.FMul, instruction.Left, reciprocal!, fmf: flags), "instcombine.fdiv.constant_reciprocal");
        }
        private LlvmInstruction? FoldFDivConstantDividend(LlvmBinaryOperator instruction)
        {
            if (instruction.Left is not LlvmConstant constant) return null;
            var flags = instruction.FastMathFlags;
            if (MdrFNeg(instruction.Right) is { } x &&
                LlvmConstantFolding.ConstantFoldUnaryOpOperand(LlvmOpcode.FNeg, constant, DL, Metrics) is { } negative)
                return MdrRewrite(MdrBin(LlvmOpcode.FDiv, negative, x, fmf: flags), "instcombine.fdiv.negate_constant_dividend");
            if (!MdrHas(instruction, LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.AllowReciprocal) ||
                instruction.Right is not LlvmBinaryOperator { Right: LlvmConstant coefficient } denominator ||
                denominator.Opcode is not (LlvmOpcode.FMul or LlvmOpcode.FDiv)) return null;
            var combined = LlvmConstantFolding.ConstantFoldBinaryOpOperands(
                denominator.Opcode == LlvmOpcode.FMul ? LlvmOpcode.FDiv : LlvmOpcode.FMul, constant, coefficient, DL, Metrics);
            return MdrNormal(combined) ? MdrRewrite(MdrBin(LlvmOpcode.FDiv, combined!, denominator.Left, fmf: flags), "instcombine.fdiv.reassoc_constant_dividend") : null;
        }
        private LlvmInstruction? FoldFDivPowDivisor(LlvmBinaryOperator instruction)
        {
            if (!MdrHas(instruction, LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.AllowReciprocal) ||
                instruction.Right is not LlvmCallInstruction { HasOneUse: true, CalledFunction.IntrinsicDescriptor: { } descriptor } call) return null;
            var kind = descriptor.Kind; var flags = instruction.FastMathFlags;
            LlvmValue[] arguments;
            if (kind == LlvmIntrinsicKind.Pow)
                arguments = [call.GetArgument(0), Builder.CreateFNeg(call.GetArgument(1), fastMathFlags: flags)];
            else if (kind == LlvmIntrinsicKind.PowI)
            {
                if (!MdrHas(instruction, LlvmFastMathFlags.NoInfs)) return null;
                arguments = [call.GetArgument(0), Builder.CreateNeg(call.GetArgument(1))];
            }
            else if (kind == LlvmIntrinsicKind.Exp || kind == LlvmIntrinsicKind.Exp2)
                arguments = [Builder.CreateFNeg(call.GetArgument(0), fastMathFlags: flags)];
            else return null;
            var power = MdrCreateFPIntrinsic(kind, arguments, flags);
            return MdrRewrite(MdrBin(LlvmOpcode.FMul, instruction.Left, power, fmf: flags), "instcombine.fdiv.reciprocal_power");
        }
        private LlvmInstruction? FoldFDivSqrtDivisor(LlvmBinaryOperator instruction)
        {
            var required = LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.AllowReciprocal;
            if (!MdrHas(instruction, required) || instruction.Right is not LlvmCallInstruction { HasOneUse: true } squareRoot ||
                !MdrFPIntrinsic(squareRoot, LlvmIntrinsicKind.Sqrt) || !MdrFPHas(squareRoot, required) ||
                squareRoot.GetArgument(0) is not LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv, HasOneUse: true } divisor ||
                !MdrHas(divisor, LlvmFastMathFlags.AllowReassoc)) return null;
            var swapped = MdrBuildFP(LlvmOpcode.FDiv, divisor.Right, divisor.Left, divisor.FastMathFlags);
            var root = MdrCreateFPIntrinsic(LlvmIntrinsicKind.Sqrt, [swapped], squareRoot.FastMathFlags);
            return MdrRewrite(MdrBin(LlvmOpcode.FMul, instruction.Left, root, fmf: instruction.FastMathFlags), "instcombine.fdiv.sqrt_reciprocal");
        }

        private void RecordUnavailableFDivTrigLibcall(LlvmBinaryOperator instruction)
        {
            if (!MdrHas(instruction, LlvmFastMathFlags.AllowReassoc) || !instruction.Left.HasOneUse || !instruction.Right.HasOneUse) return;
            bool tan = MdrFPIntrinsic(instruction.Left, LlvmIntrinsicKind.Sin) && MdrFPIntrinsic(instruction.Right, LlvmIntrinsicKind.Cos);
            bool cot = !tan && MdrFPIntrinsic(instruction.Left, LlvmIntrinsicKind.Cos) && MdrFPIntrinsic(instruction.Right, LlvmIntrinsicKind.Sin);
            if (!(tan || cot) || !ReferenceEquals(((LlvmCallInstruction)instruction.Left).GetArgument(0), ((LlvmCallInstruction)instruction.Right).GetArgument(0))) return;
            // The represented Native TLI is LLVM disableAllFunctions. No libc ABI
            // or name-based contract is synthesized to replace hasFloatFn's false result.
            string name = instruction.Type.IsFloatTy() ? "tanf" : instruction.Type.IsDoubleTy() ? "tan" : "tanl";
            if (!TLI.IsAvailable(name)) Count("instcombine.fdiv.trig_libcall_unavailable");
        }
    }
}
