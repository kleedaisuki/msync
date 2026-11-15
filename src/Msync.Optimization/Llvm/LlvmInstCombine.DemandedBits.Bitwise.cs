// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstCombineSimplifyDemanded.cpp SimplifyDemandedUseBits And/Or/Xor source cases.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmValue? SimplifyDemandedBitwise(LlvmBinaryOperator instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            uint width = demanded.BitWidth;
            LlvmKnownBits left = new(width), right = new(width);
            if (SimplifyDemandedBits(instruction, 1, demanded, ref right, query, depth + 1) ||
                SimplifyDemandedBits(instruction, 0, instruction.Opcode == LlvmOpcode.And ? demanded & ~right.Zero
                    : instruction.Opcode == LlvmOpcode.Or ? demanded & ~right.One : demanded, ref left, query, depth + 1))
            {
                if (instruction.Opcode == LlvmOpcode.Or) instruction.DropPoisonGeneratingFlags();
                return instruction;
            }
            if (instruction.Opcode == LlvmOpcode.Xor && demanded.IsOne &&
                instruction.Left is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var lhsKind } lhsCount && lhsKind == LlvmIntrinsicKind.CTPop &&
                instruction.Right is LlvmCallInstruction { CalledFunction.IntrinsicDescriptor.Kind: var rhsKind } rhsCount && rhsKind == LlvmIntrinsicKind.CTPop)
            {
                using var guard = new LlvmTransformBuilder.InsertPointGuard(Builder);
                Builder.SetInsertPoint(instruction);
                return Builder.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [Builder.CreateXor(lhsCount.GetArgument(0), rhsCount.GetArgument(0))]);
            }
            known = AnalyzeDemandedBitwise(instruction, left, right, query, depth);
            if (DemandedSubset(demanded, known.Zero | known.One)) return DemandedConstant(instruction.Type, known.One);
            if (instruction.Opcode == LlvmOpcode.And)
            {
                if (DemandedSubset(demanded, left.Zero | right.One)) return instruction.Left;
                if (DemandedSubset(demanded, right.Zero | left.One)) return instruction.Right;
                return ShrinkDemandedConstant(instruction, 1, demanded & ~left.Zero) ? instruction : null;
            }
            if (instruction.Opcode == LlvmOpcode.Or)
            {
                if (DemandedSubset(demanded, left.One | right.Zero)) return instruction.Left;
                if (DemandedSubset(demanded, right.One | left.Zero)) return instruction.Right;
                if (ShrinkDemandedConstant(instruction, 1, demanded)) return instruction;
                if (!instruction.IsDisjoint)
                {
                    var leftCache = new LlvmValueWithCache(instruction.Left, left);
                    var rightCache = new LlvmValueWithCache(instruction.Right, right);
                    if (LlvmValueTracking.HaveNoCommonBitsSet(ref leftCache, ref rightCache, query))
                    { instruction.IsDisjoint = true; Count("demanded_bits.or.disjoint_inferred"); return instruction; }
                }
                return null;
            }
            if (DemandedSubset(demanded, right.Zero)) return instruction.Left;
            if (DemandedSubset(demanded, left.Zero)) return instruction.Right;
            if (DemandedSubset(demanded, right.Zero | left.Zero))
            {
                var inclusive = new LlvmBinaryOperator(LlvmOpcode.Or, instruction.Left, instruction.Right) { IsDisjoint = demanded.IsAllOnes };
                inclusive.TakeName(instruction);
                return InsertNewInstWith(inclusive, instruction);
            }
            if (DemandedSubset(demanded, right.Zero | right.One) && DemandedSubset(right.One, left.One))
                return InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.And, instruction.Left,
                    DemandedConstant(instruction.Type, ~right.One & demanded)), instruction);
            if (instruction.Right is LlvmConstantInt constant && !constant.Value.IsAllOnes)
            {
                if ((constant.Value | ~demanded).IsAllOnes)
                {
                    instruction.SetOperand(1, DemandedConstant(instruction.Type, LlvmAPInt.GetAllOnes(width)));
                    Count("demanded_bits.xor.not_canonicalized"); return instruction;
                }
                if (ShrinkDemandedConstant(instruction, 1, demanded)) return instruction;
            }
            if (instruction.Left is LlvmBinaryOperator { Opcode: LlvmOpcode.And, HasOneUse: true, Right: LlvmConstantInt andConstant } &&
                instruction.Right is LlvmConstantInt xorConstant && !(left.One & right.One & demanded).IsZero)
            {
                var mask = ~(left.One & right.One & demanded);
                var newAnd = InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.And, instruction.Left,
                    DemandedConstant(instruction.Type, mask & andConstant.Value)), instruction);
                return InsertNewInstWith(new LlvmBinaryOperator(LlvmOpcode.Xor, newAnd,
                    DemandedConstant(instruction.Type, mask & xorConstant.Value)), instruction);
            }
            return null;
        }

        private LlvmValue? SimplifyDemandedSelect(LlvmSelectInstruction instruction, LlvmAPInt demanded,
            ref LlvmKnownBits known, LlvmInstructionSimplify.BinaryQuery query, uint depth)
        {
            LlvmKnownBits left = new(demanded.BitWidth), right = new(demanded.BitWidth);
            if (SimplifyDemandedBits(instruction, 2, demanded, ref right, query, depth + 1) ||
                SimplifyDemandedBits(instruction, 1, demanded, ref left, query, depth + 1)) return instruction;
            if (CanonicalizeDemandedSelectConstant(instruction, 1, demanded) ||
                CanonicalizeDemandedSelectConstant(instruction, 2, demanded)) return instruction;
            left = LlvmKnownBitsAnalysis.AdjustKnownBitsForSelectArm(left, instruction.Condition, instruction.TrueValue, false,
                depth, query.Context, query.Tree, query.Metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.DC);
            right = LlvmKnownBitsAnalysis.AdjustKnownBitsForSelectArm(right, instruction.Condition, instruction.FalseValue, true,
                depth, query.Context, query.Tree, query.Metrics, query.UseInstructionInfo, query.Assumptions, query.AllowEphemerals, query.DC);
            known = left.IntersectWith(right);
            return null;
        }

        private bool CanonicalizeDemandedSelectConstant(LlvmSelectInstruction instruction, int operand, LlvmAPInt demanded)
        {
            if (instruction.GetOperand(operand) is not LlvmConstantInt selected) return false;
            if (instruction.Condition is not LlvmICmpInstruction { Right: LlvmConstantInt compared } cmp ||
                cmp.Left is LlvmConstant || compared.BitWidth != selected.BitWidth)
                return ShrinkDemandedConstant(instruction, operand, demanded);
            if (compared.Value == selected.Value) return false;
            if ((compared.Value & demanded) != (selected.Value & demanded)) return ShrinkDemandedConstant(instruction, operand, demanded);
            instruction.SetOperand(operand, DemandedConstant(instruction.Type, compared.Value));
            Count("demanded_bits.select.comparison_constant_reused");
            return true;
        }
    }
}
