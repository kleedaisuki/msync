// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Constants.cpp getExtractElement,
// getInsertElement/getShuffleVector; ConstantFold.cpp corresponding IR folders.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed partial class LlvmConstantExpr
{
    public static LlvmConstant GetExtractElement(LlvmConstant vector, LlvmConstant index) =>
        GetExtractElement(vector, index, null)!;
    public static LlvmConstant? GetExtractElement(LlvmConstant vector, LlvmConstant index, LlvmType? onlyIfReducedType)
    {
        Debug.Assert(vector.Type.IsVectorTy() && index.Type.IsIntegerTy());
        LlvmType type = ((LlvmVectorType)vector.Type).ElementType;
        if (FoldExtractElement(vector, index) is { } folded) return folded;
        return onlyIfReducedType == type ? null : Intern(type, LlvmOpcode.ExtractElement, [vector, index]);
    }
    public static LlvmConstant GetInsertElement(LlvmConstant vector, LlvmConstant element, LlvmConstant index) =>
        GetInsertElement(vector, element, index, null)!;
    public static LlvmConstant? GetInsertElement(LlvmConstant vector, LlvmConstant element, LlvmConstant index,
        LlvmType? onlyIfReducedType)
    {
        Debug.Assert(vector.Type is LlvmVectorType type && type.ElementType == element.Type && index.Type.IsIntegerTy());
        if (FoldInsertElement(vector, element, index) is { } folded) return folded;
        return onlyIfReducedType == vector.Type ? null : Intern(vector.Type, LlvmOpcode.InsertElement, [vector, element, index]);
    }
    public static LlvmConstant GetShuffleVector(LlvmConstant left, LlvmConstant right, IReadOnlyList<int> mask) =>
        GetShuffleVector(left, right, mask, null)!;
    public static LlvmConstant? GetShuffleVector(LlvmConstant left, LlvmConstant right, IReadOnlyList<int> mask,
        LlvmType? onlyIfReducedType)
    {
        Debug.Assert(LlvmShuffleVectorInstruction.IsValidOperands(left, right, mask));
        if (FoldShuffleVector(left, right, mask) is { } folded) return folded;
        var vector = (LlvmVectorType)left.Type;
        LlvmType type = LlvmVectorType.Get(vector.ElementType, (uint)mask.Count, vector.ElementCount.IsScalable);
        return onlyIfReducedType == type ? null : Intern(type, LlvmOpcode.ShuffleVector, [left, right], shuffleMask: mask);
    }

    private static LlvmConstant? FoldExtractElement(LlvmConstant vector, LlvmConstant index)
    {
        var type = (LlvmVectorType)vector.Type;
        if (vector is LlvmPoisonValue || index is LlvmUndefValue) return LlvmPoisonValue.Get(type.ElementType);
        if (vector is LlvmUndefValue) return LlvmUndefValue.Get(type.ElementType);
        if (index is not LlvmConstantInt position) return null;
        if (type is LlvmFixedVectorType fixedType && position.Value.GetLimitedValue() >= fixedType.NumElements)
            return LlvmPoisonValue.Get(type.ElementType);
        if (vector is LlvmConstantExpr expression)
        {
            if (expression.Opcode == LlvmOpcode.GetElementPtr)
            {
                var operands = expression.Operands.Select(value => (LlvmConstant)value!).Select(operand =>
                    operand.Type.IsVectorTy() ? GetExtractElement(operand, index) : operand).ToArray();
                return expression.GetWithOperands(operands, type.ElementType, sourceType: expression.SourceElementType);
            }
            if (expression.Opcode == LlvmOpcode.InsertElement && expression.GetConstantOperand(2) is LlvmConstantInt insertedIndex)
                return insertedIndex.Value.ZExtOrTrunc(Math.Max(insertedIndex.BitWidth, position.BitWidth)) ==
                    position.Value.ZExtOrTrunc(Math.Max(insertedIndex.BitWidth, position.BitWidth))
                    ? expression.GetConstantOperand(1) : GetExtractElement(expression.GetConstantOperand(0), index);
        }
        if (vector.GetAggregateElement(index) is { } element) return element;
        return position.Value.GetLimitedValue() < type.ElementCount.KnownMinValue ? vector.GetSplatValue() : null;
    }

    private static LlvmConstant? FoldInsertElement(LlvmConstant vector, LlvmConstant element, LlvmConstant index)
    {
        if (index is LlvmUndefValue) return LlvmPoisonValue.Get(vector.Type);
        if (vector.IsNullValue && element.IsNullValue) return vector;
        if (index is not LlvmConstantInt position || vector.Type is not LlvmFixedVectorType type) return null;
        if (position.Value.GetLimitedValue() >= type.NumElements) return LlvmPoisonValue.Get(type);
        var elements = new LlvmConstant[type.NumElements];
        for (uint lane = 0; lane < type.NumElements; ++lane)
        {
            if (lane == position.GetZExtValue()) elements[lane] = element;
            else if (vector.GetAggregateElement(lane) is { } old) elements[lane] = old;
            else return null;
        }
        return LlvmConstantVector.Get(elements);
    }

    private static LlvmConstant? FoldShuffleVector(LlvmConstant left, LlvmConstant right, IReadOnlyList<int> mask)
    {
        var source = (LlvmVectorType)left.Type;
        LlvmElementCount count = new((uint)mask.Count, source.ElementCount.IsScalable);
        var type = LlvmVectorType.Get(source.ElementType, count);
        if (mask.All(lane => lane == -1)) return LlvmPoisonValue.Get(type);
        if (mask.All(lane => lane == 0))
        {
            var element = GetExtractElement(left, LlvmConstantInt.Get(LlvmType.GetInt32Ty(left.Context), 0));
            if (!count.IsScalable || element.IsNullValue || element is LlvmUndefValue)
                return LlvmConstantVector.GetSplat(count, element);
        }
        if (count.IsScalable) return null;
        uint sourceCount = source.ElementCount.KnownMinValue;
        var elements = new LlvmConstant[mask.Count];
        for (int lane = 0; lane < mask.Count; ++lane)
        {
            int selected = mask[lane];
            if (selected == -1) { elements[lane] = LlvmPoisonValue.Get(source.ElementType); continue; }
            bool fromRight = (uint)selected >= sourceCount;
            elements[lane] = GetExtractElement(fromRight ? right : left,
                LlvmConstantInt.Get(LlvmType.GetInt32Ty(left.Context), fromRight ? (uint)selected - sourceCount : (uint)selected));
        }
        return LlvmConstantVector.Get(elements);
    }
}
