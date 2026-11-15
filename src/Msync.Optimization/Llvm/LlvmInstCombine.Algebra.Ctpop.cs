// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstructionCombining.cpp
// tryFoldInstWithCtpopWithNot. Inversion uses the shared source InstCombiner helper.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmInstruction? TryFoldInstWithCtpopWithNot(LlvmInstruction instruction)
        {
            int constantIndex = 1;
            switch (instruction.Opcode)
            {
                case LlvmOpcode.Sub: constantIndex = 0; break;
                case LlvmOpcode.ICmp:
                    if (((LlvmICmpInstruction)instruction).Predicate is LlvmCmpPredicate.ICmpSgt or LlvmCmpPredicate.ICmpSge or
                        LlvmCmpPredicate.ICmpSlt or LlvmCmpPredicate.ICmpSle) return null;
                    break;
                case LlvmOpcode.Or:
                    if (instruction is not LlvmBinaryOperator { IsDisjoint: true }) return null;
                    break;
                case LlvmOpcode.Add: break;
                default: return null;
            }
            if (instruction.GetOperand(1 - constantIndex) is not LlvmCallInstruction { HasOneUse: true } pop ||
                pop.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.CTPop ||
                instruction.GetOperand(constantIndex) is not LlvmConstant constant) return null;
            var operand = pop.GetArgument(0);
            var width = LlvmConstantInt.Get(operand.Context, new LlvmAPInt(operand.Type.GetIntegerBitWidth(), operand.Type.GetScalarSizeInBits()));
            if (instruction is LlvmICmpInstruction compare && compare.Predicate is not (LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe))
            {
                var greater = LlvmConstantFolding.ConstantFoldCompareInstOperands(LlvmCmpPredicate.ICmpUgt, constant, width, DL, F, Metrics);
                if (greater is null || !greater.IsNullValue) return null;
            }
            if (!IsFreeToInvert(operand, operand.HasOneUse, out bool consumes) || !consumes) return null;
            var inverted = GetFreelyInverted(operand, operand.HasOneUse, Builder);
            Debug.Assert(inverted is not null);
            var population = Builder.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [inverted!]);
            LlvmValue result;
            switch (instruction.Opcode)
            {
                case LlvmOpcode.Sub:
                    var difference = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Sub, constant, width, DL, Metrics)!;
                    result = Builder.CreateAdd(population, difference); break;
                case LlvmOpcode.Or:
                case LlvmOpcode.Add:
                    var sum = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Add, constant, width, DL, Metrics)!;
                    result = Builder.CreateSub(sum, population); break;
                default:
                    var offset = LlvmConstantFolding.ConstantFoldBinaryOpOperands(LlvmOpcode.Sub, width, constant, DL, Metrics)!;
                    result = Builder.CreateICmp(LlvmCmpInstruction.GetSwappedPredicate(((LlvmICmpInstruction)instruction).Predicate), population, offset); break;
            }
            Count("algebra.ctpop_not_fold"); return ReplaceInstUsesWith(instruction, result);
        }
    }
}
