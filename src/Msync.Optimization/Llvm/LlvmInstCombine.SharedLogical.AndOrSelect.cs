// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineAndOrXor.cpp
// areInverseVectorBitmasks, getSelectCondition, matchSelectFromAndOr;
// InstCombiner.h peekThroughBitcast.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private static LlvmValue SharedLogicalPeekBitcast(LlvmValue value, bool oneUse = false)
            => value is LlvmCastInstruction { Opcode: LlvmOpcode.BitCast } cast && (!oneUse || cast.HasOneUse) ? cast.Operand : value;
        private static LlvmType SharedLogicalCompareType(LlvmType type)
        {
            LlvmType boolean = LlvmIntegerType.Get(type.Context, 1);
            return type is LlvmVectorType vector ? LlvmVectorType.Get(boolean, vector.ElementCount) : boolean;
        }
        private static bool AreInverseVectorBitmasks(LlvmConstant first, LlvmConstant second)
        {
            var type = (LlvmFixedVectorType)first.Type;
            for (uint i = 0; i < type.NumElements; ++i)
            {
                LlvmConstant? left = first.GetAggregateElement(i), right = second.GetAggregateElement(i);
                if (left is null || right is null) return false;
                if (!(SharedLogicalZero(left) && SharedLogicalAllOnes(right) || SharedLogicalZero(right) && SharedLogicalAllOnes(left))) return false;
            }
            return true;
        }
        private LlvmValue? GetSelectCondition(LlvmValue first, LlvmValue second, bool same)
        {
            LlvmType type = first.Type;
            if (!type.IsIntOrIntVectorTy() || !second.Type.IsIntOrIntVectorTy()) return null;
            bool complementary = same ? first == second : SharedLogicalNot(second, out var notSecond) && notSecond == first;
            if (complementary)
            {
                if (type.IsIntOrIntVectorTy(1)) return first;
                first = SharedLogicalPeekBitcast(first);
                if (first.Type.IsIntOrIntVectorTy())
                {
                    uint signBits = ComputeNumSignBits(first);
                    if (signBits == first.Type.GetScalarSizeInBits() && signBits <= type.GetScalarSizeInBits())
                        return Builder.CreateTrunc(first, SharedLogicalCompareType(first.Type));
                }
                return null;
            }
            if (same) return null;
            if (first is LlvmConstant firstConstant && second is LlvmConstant secondConstant &&
                firstConstant == LlvmConstantExpr.GetNot(secondConstant) && ComputeNumSignBits(first) == type.GetScalarSizeInBits())
                return Builder.CreateZExtOrTrunc(first, SharedLogicalCompareType(type));
            if (first is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } extension && extension.Operand.Type.IsIntOrIntVectorTy(1))
            {
                LlvmValue condition = extension.Operand;
                if (second is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } other &&
                    SharedLogicalNot(other.Operand, out var inner) && inner == condition) return condition;
                if (second.HasOneUse && SharedLogicalNot(second, out var complement) &&
                    SharedLogicalPeekBitcast(complement, true) is LlvmCastInstruction { Opcode: LlvmOpcode.SExt } inverse && inverse.Operand == condition)
                    return condition;
            }
            if (!type.IsVectorTy()) return null;
            if (first is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Left: LlvmCastInstruction { Opcode: LlvmOpcode.SExt } firstExtension, Right: LlvmConstant firstMask } &&
                second is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor, Left: LlvmCastInstruction { Opcode: LlvmOpcode.SExt } secondExtension, Right: LlvmConstant secondMask } &&
                firstExtension.Operand == secondExtension.Operand && firstExtension.Operand.Type.IsIntOrIntVectorTy(1) &&
                AreInverseVectorBitmasks(firstMask, secondMask))
            {
                LlvmConstant conditionMask = LlvmConstantExpr.GetTrunc(firstMask, SharedLogicalCompareType(type));
                return Builder.CreateXor(firstExtension.Operand, conditionMask);
            }
            return null;
        }
        private LlvmValue? MatchSelectFromAndOr(LlvmValue firstMask, LlvmValue whenTrue, LlvmValue secondMask,
            LlvmValue whenFalse, bool invertFalseValue = false)
        {
            LlvmType originalType = firstMask.Type;
            firstMask = SharedLogicalPeekBitcast(firstMask, true);
            secondMask = SharedLogicalPeekBitcast(secondMask, true);
            if (GetSelectCondition(firstMask, secondMask, invertFalseValue) is not { } condition) return null;
            LlvmType selectType = firstMask.Type;
            if (condition.Type is LlvmVectorType vector)
            {
                uint elements = vector.ElementCount.KnownMinValue;
                ulong bits = selectType.GetPrimitiveSizeInBits().GetKnownMinValue();
                selectType = LlvmVectorType.Get(LlvmIntegerType.Get(firstMask.Context, (uint)(bits / elements)), vector.ElementCount);
            }
            LlvmValue trueValue = Builder.CreateBitCast(whenTrue, selectType);
            if (invertFalseValue) whenFalse = Builder.CreateNot(whenFalse);
            LlvmValue falseValue = Builder.CreateBitCast(whenFalse, selectType);
            LlvmValue result = Builder.CreateSelect(condition, trueValue, falseValue);
            Count("logical.and_or_to_select");
            return Builder.CreateBitCast(result, originalType);
        }
    }
}
