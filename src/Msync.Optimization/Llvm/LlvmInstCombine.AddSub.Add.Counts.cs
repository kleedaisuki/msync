// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAddSub.cpp
// foldToUnsignedSaturatedAdd/visitAdd saturating, population and ceil-log idioms.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static bool MatchAddSubIntrinsic(LlvmValue value, LlvmIntrinsicKind kind, out LlvmCallInstruction call)
        { call = value as LlvmCallInstruction ?? null!; return call is not null && call.CalledFunction?.IntrinsicDescriptor?.Kind == kind; }
        private LlvmInstruction? FoldToUnsignedSaturatedAdd(LlvmBinaryOperator instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue minimum = side == 0 ? instruction.Left : instruction.Right, value = side == 0 ? instruction.Right : instruction.Left;
                if (!MatchAddSubIntrinsic(minimum, LlvmIntrinsicKind.UMin, out var call)) continue;
                for (int argument = 0; argument != 2; ++argument)
                {
                    LlvmValue candidate = call.GetArgument(argument);
                    if (Negator.MatchNot(candidate, out var inverted) && ReferenceEquals(inverted, value))
                        return NewAddSubUAddSat(call.GetArgument(1 - argument), value, instruction);
                }
            }
            if (instruction.Right is LlvmConstantInt constant && MatchAddSubIntrinsic(instruction.Left, LlvmIntrinsicKind.UMin, out var min) &&
                min.GetArgument(1) is LlvmConstantInt invertedConstant && constant.Value == ~invertedConstant.Value)
                return NewAddSubUAddSat(min.GetArgument(0), constant, instruction);
            return null;
        }
        private LlvmCallInstruction NewAddSubUAddSat(LlvmValue left, LlvmValue right, LlvmInstruction context) =>
            LlvmIntrinsic.CreateBinary(LlvmIntrinsicKind.UAddSat, left, right, context.Parent!.Parent!.Parent!, Metrics);
        private LlvmInstruction? FoldAddSubSaturationAndPopcount(LlvmValue left, LlvmValue right, LlvmBinaryOperator instruction)
        {
            for (int side = 0; side != 2; ++side)
            {
                LlvmValue candidate = side == 0 ? left : right, value = side == 0 ? right : left;
                if (candidate.HasOneUse && MatchAddSubIntrinsic(candidate, LlvmIntrinsicKind.USubSat, out var call) && ReferenceEquals(call.GetArgument(1), value))
                    return ReplaceInstUsesWith(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.UMax, [call.GetArgument(0), value], instruction.Type));
            }
            if (!left.HasOneUse || !right.HasOneUse || !MatchAddSubIntrinsic(left, LlvmIntrinsicKind.CTPop, out var a) ||
                !MatchAddSubIntrinsic(right, LlvmIntrinsicKind.CTPop, out var b)) return null;
            var av = a.GetArgument(0); var bv = b.GetArgument(0);
            var ac = new LlvmValueWithCache(av); var bc = new LlvmValueWithCache(bv);
            if (!LlvmValueTracking.HaveNoCommonBitsSet(ref ac, ref bc, Query(instruction))) return null;
            var disjoint = Builder.Insert(new LlvmBinaryOperator(LlvmOpcode.Or, av, bv) { IsDisjoint = true });
            return ReplaceInstUsesWith(instruction, Builder.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [disjoint], instruction.Type));
        }
        private LlvmInstruction? FoldAddSubCeilLog2(LlvmValue comparison, LlvmValue xorValue, LlvmBinaryOperator instruction)
        {
            if (comparison is not LlvmCastInstruction { Opcode: LlvmOpcode.ZExt,
                    Operand: LlvmICmpInstruction { Right: LlvmConstantInt { IsOne: true } } compare } ||
                compare.Predicate is not (LlvmCmpPredicate.ICmpUgt or LlvmCmpPredicate.ICmpNe) ||
                !MatchAddSubIntrinsic(compare.Left, LlvmIntrinsicKind.CTPop, out var pop) || !xorValue.HasOneUse ||
                SkipAddSubCast(xorValue, LlvmOpcode.ZExt) is not LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, HasOneUse: true, Right: LlvmConstantInt xorConstant } xor ||
                !xor.Left.HasOneUse || SkipAddSubCast(xor.Left, LlvmOpcode.Trunc) is not LlvmCallInstruction { HasOneUse: true } leading ||
                leading.CalledFunction?.IntrinsicDescriptor?.Kind != LlvmIntrinsicKind.CTLZ || leading.GetArgument(1) is not LlvmConstantInt { IsOne: true }) return null;
            var value = pop.GetArgument(0);
            uint width = value.Type.GetScalarSizeInBits();
            if (!ReferenceEquals(leading.GetArgument(0), value) || (width & (width - 1)) != 0 || xorConstant.Value != new LlvmAPInt(xorConstant.Value.BitWidth, width - 1)) return null;
            var decrement = Builder.CreateAdd(value, LlvmConstantInt.Get(value.Context, LlvmAPInt.GetAllOnes(width)));
            var count = Builder.CreateIntrinsic(LlvmIntrinsicKind.CTLZ, [decrement, LlvmConstantInt.Get(LlvmIntegerType.Get(value.Context, 1), 0)], value.Type);
            var subtract = Builder.CreateSub(AddSubInt(value, width), count, nuw: true, nsw: true);
            return ReplaceInstUsesWith(instruction, Builder.CreateZExtOrTrunc(subtract, instruction.Type));
        }
    }
}
