// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// hoistFNegAboveFMulFDiv/visitFNeg, represented scalar paths.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? HoistFNegAboveFMulFDiv(LlvmValue value, LlvmInstruction source)
        {
            var flags = ((ILlvmFastMathInstruction)source).FastMathFlags;
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.FMul } multiply)
                return (LlvmInstruction)BuildAddSubFP(LlvmOpcode.FMul, multiply.Left, Builder.CreateFNeg(multiply.Right, fastMathFlags: flags), flags);
            if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv } divide)
                return (LlvmInstruction)BuildAddSubFP(LlvmOpcode.FDiv, Builder.CreateFNeg(divide.Left, fastMathFlags: flags), divide.Right, flags);
            if (value is not LlvmCallInstruction call || call.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.Ldexp) return null;
            var merged = flags | call.FastMathFlags;
            LlvmValue negated = Builder.CreateFNeg(call.GetArgument(0), fastMathFlags: merged);
            return Builder.Insert(new LlvmCallInstruction(call.FunctionType, call.CalledOperand, [negated, call.GetArgument(1)]) { FastMathFlags = merged });
        }
        private LlvmInstruction? VisitFNeg(LlvmUnaryOperator instruction)
        {
            Count("instcombine.fneg.visits");
            if (LlvmInstructionSimplify.SimplifyInstruction(instruction, Query(instruction)) is { } simplified)
                return ReplaceInstUsesWith(instruction, simplified);
            if (FoldFNegIntoConstant(instruction) is { } constant) return constant;
            LlvmValue operand = instruction.Operand;
            var flags = instruction.FastMathFlags;
            if (AddSubHasFlags(instruction, LlvmFastMathFlags.NoSignedZeros) && operand is LlvmBinaryOperator { Opcode: LlvmOpcode.FSub, HasOneUse: true } subtraction)
                return NewAddSubFP(LlvmOpcode.FSub, subtraction.Right, subtraction.Left, flags);
            if (!operand.HasOneUse) return null;
            if (HoistFNegAboveFMulFDiv(operand, instruction) is { } hoisted) return ReplaceInstUsesWith(instruction, hoisted);
            if (operand is LlvmSelectInstruction select)
            {
                LlvmValue x = select.TrueValue, y = select.FalseValue;
                if (MatchAddSubFNeg(x) is { } p)
                    return PropagateFNegSelectFlags(new LlvmSelectInstruction(select.Condition, p, Builder.CreateFNeg(y, fastMathFlags: flags)),
                        select, flags, ReferenceEquals(p, y));
                if (MatchAddSubFNeg(y) is { } q)
                    return PropagateFNegSelectFlags(new LlvmSelectInstruction(select.Condition, Builder.CreateFNeg(x, fastMathFlags: flags), q),
                        select, flags, ReferenceEquals(q, x));
                if (IsAddSubImmediateConstant(x) || IsAddSubImmediateConstant(y))
                    return PropagateFNegSelectFlags(new LlvmSelectInstruction(select.Condition,
                        Builder.CreateFNeg(x, fastMathFlags: flags), Builder.CreateFNeg(y, fastMathFlags: flags)), select, flags, true);
            }
            if (operand is LlvmCallInstruction call && call.CalledFunction?.IntrinsicDescriptor?.Kind == LlvmIntrinsicKind.CopySign)
            {
                var intersected = flags & call.FastMathFlags;
                LlvmValue negated = Builder.CreateFNeg(call.GetArgument(1), fastMathFlags: intersected);
                LlvmValue newCopySign = Builder.CreateIntrinsic(LlvmIntrinsicKind.CopySign, [call.GetArgument(0), negated], call.Type);
                if (newCopySign is ILlvmFastMathInstruction floating) floating.FastMathFlags = intersected;
                return ReplaceInstUsesWith(instruction, newCopySign);
            }
            return null;
        }
        private LlvmSelectInstruction PropagateFNegSelectFlags(LlvmSelectInstruction result, LlvmSelectInstruction original,
            LlvmFastMathFlags flags, bool commonOperand)
        {
            result.FastMathFlags = flags | original.FastMathFlags;
            if (!AddSubHasFlags(original, LlvmFastMathFlags.NoSignedZeros) && !commonOperand &&
                !LlvmValueTracking.IsGuaranteedNotToBeUndefOrPoison(original.Condition))
                result.FastMathFlags &= ~LlvmFastMathFlags.NoSignedZeros;
            return result;
        }
    }
}
