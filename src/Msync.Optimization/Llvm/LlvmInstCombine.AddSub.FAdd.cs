// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp visitFAdd.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? VisitFAdd(LlvmBinaryOperator instruction)
        {
            Count("instcombine.fadd.visits");
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction)) is { } simple) return ReplaceInstUsesWith(instruction, simple);
            if (SimplifyAssociativeOrCommutative(instruction)) return instruction;
            if (FoldVectorBinop(instruction) is { } vector) return vector;
            if (FoldBinopWithPhiOperands(instruction) is { } phi) return phi;
            if (FoldBinOpIntoSelectOrPhi(instruction) is { } selected) return selected;
            var flags = instruction.FastMathFlags;
            LlvmValue left = instruction.Left, right = instruction.Right;
            if (instruction.HasOneUse && right is LlvmConstantFP { Value.IsZero: true } &&
                LlvmValueTracking.CanIgnoreSignBitOfZero(instruction.Uses.First())) return ReplaceInstUsesWith(instruction, left);
            if (MatchAddSubFNeg(left) is { } negativeLeft) return NewAddSubFP(LlvmOpcode.FSub, right, negativeLeft, flags);
            if (MatchAddSubFNeg(right) is { } negativeRight) return NewAddSubFP(LlvmOpcode.FSub, left, negativeRight, flags);
            if (MatchAddSubNegatedProduct(left, out var x, out var y, out var opcode))
                return NewAddSubFP(LlvmOpcode.FSub, right, BuildAddSubFP(opcode, x, y, flags), flags);
            if (MatchAddSubNegatedProduct(right, out x, out y, out opcode))
                return NewAddSubFP(LlvmOpcode.FSub, left, BuildAddSubFP(opcode, x, y, flags), flags);
            if (FoldFBinOpOfIntCasts(instruction) is { } casts) return casts;
            if (SimplifySelectsFeedingBinaryOp(instruction, left, right) is { } selectArms) return ReplaceInstUsesWith(instruction, selectArms);
            if (AddSubHasFlags(instruction, LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoSignedZeros))
            {
                if (FactorizeFAddFSub(instruction) is { } factored) return factored;
                if (FoldSquareSumFP(instruction) is { } squares) return squares;
                if (FoldAddSubFPMulConstant(left, right, instruction) is { } multiply ||
                    (multiply = FoldAddSubFPMulConstant(right, left, instruction)) is not null) return multiply;
                if (FoldAddSubCancelFP(left, right, flags) is { } cancellation ||
                    (cancellation = FoldAddSubCancelFP(right, left, flags)) is not null) return cancellation;
                if (new FAddCombine(Builder, Metrics).Simplify(instruction) is { } reassociated) return ReplaceInstUsesWith(instruction, reassociated);
            }
            if (MatchAddSubMinMaxPair(left, right, out x, out y) || MatchAddSubMinMaxPair(right, left, out x, out y))
            {
                var result = NewAddSubFP(LlvmOpcode.FAdd, x, y, flags);
                if (!AddSubHasFlags(result, LlvmFastMathFlags.NoNaNs)) result.FastMathFlags &= ~LlvmFastMathFlags.NoInfs;
                return result;
            }
            return null;
        }
        private LlvmInstruction? FoldAddSubFPMulConstant(LlvmValue multiply, LlvmValue value, LlvmBinaryOperator instruction)
        {
            if (multiply is not LlvmBinaryOperator { Opcode: LlvmOpcode.FMul, Right: LlvmConstant coefficient } binary ||
                !IsAddSubImmediateConstant(coefficient) || !ReferenceEquals(binary.Left, value)) return null;
            if (LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.FAdd, coefficient,
                LlvmConstantFP.Get(instruction.Type, 1), DL, Metrics) is not { } combined) return null;
            return NewAddSubFP(LlvmOpcode.FMul, value, combined, instruction.FastMathFlags);
        }
        private static LlvmInstruction? FoldAddSubCancelFP(LlvmValue subtraction, LlvmValue addition, LlvmFastMathFlags flags)
        {
            if (subtraction is not LlvmBinaryOperator { Opcode: LlvmOpcode.FSub } sub || MatchAddSubFNeg(sub.Left) is not { } x ||
                !MatchAddSubCommuted(addition, LlvmOpcode.FAdd, x, out var z)) return null;
            return NewAddSubFP(LlvmOpcode.FSub, z, sub.Right, flags);
        }
        private static bool MatchAddSubMinMaxPair(LlvmValue maximum, LlvmValue minimum, out LlvmValue x, out LlvmValue y)
        {
            x = null!; y = null!;
            if (maximum is not LlvmCallInstruction max || max.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.Maximum ||
                minimum is not LlvmCallInstruction min || min.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.Minimum) return false;
            x = max.GetArgument(0); y = max.GetArgument(1);
            return ReferenceEquals(min.GetArgument(0), x) && ReferenceEquals(min.GetArgument(1), y) ||
                ReferenceEquals(min.GetArgument(1), x) && ReferenceEquals(min.GetArgument(0), y);
        }
    }
}
