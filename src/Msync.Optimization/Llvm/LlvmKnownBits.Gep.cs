// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp GEP transfer.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmKnownBitsAnalysis
{
    private static LlvmKnownBits ComputeGep(LlvmGetElementPtrInstruction gep, uint depth, LlvmInstruction? context,
        LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics, bool useInstructionInfo, LlvmAssumptionCache? assumptions, bool allowEphemerals, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null, LlvmDataLayout? layout = null)
    {
        layout ??= GetQueryLayout(gep, context);
        uint width = layout.GetPointerTypeSizeInBits(gep.Type);
        uint indexWidth = layout.GetIndexTypeSizeInBits(gep.Type);
        LlvmKnownBits known = Compute(gep.PointerOperand, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout);
        LlvmAPInt constant = LlvmAPInt.GetZero(indexWidth);
        LlvmType indexed = gep.SourceElementType;
        for (int index = 0; index < gep.IndexCount && !known.IsUnknown; ++index)
        {
            Count(metrics, "known_bits.gep.indices");
            LlvmValue value = gep.GetIndex(index);
            if (index != 0 && indexed is LlvmStructType structure)
            {
                uint member = (uint)((LlvmConstantInt)value).Value.GetZExtValue();
                constant += new LlvmAPInt(indexWidth, layout.GetStructLayout(structure).GetElementOffset(member).GetFixedValue());
                indexed = structure.Elements[(int)member];
                continue;
            }
            bool vectorElement = index != 0 && indexed is LlvmVectorType;
            if (index != 0) indexed = indexed switch
            {
                LlvmArrayType array => array.ElementType,
                LlvmVectorType vector => vector.ElementType,
                _ => throw new InvalidOperationException("Invalid GEP sequential index.")
            };
            if (!indexed.IsSized()) { known = new(width); break; }
            LlvmTypeSize stride = vectorElement
                ? layout.GetTypeStoreSize(indexed) : layout.GetTypeAllocSize(indexed);
            if (value is LlvmConstantInt integer && !stride.IsScalable)
            {
                constant += integer.Value.SExtOrTrunc(indexWidth) * new LlvmAPInt(indexWidth, stride.GetKnownMinValue());
                continue;
            }
            LlvmKnownBits amount = Compute(value, depth + 1, context, tree, metrics, useInstructionInfo, assumptions, allowEphemerals, conditions, condContext, layout).SExtOrTrunc(indexWidth);
            LlvmKnownBits scaling = stride.IsScalable
                ? new(LlvmAPInt.GetLowBitsSet(indexWidth, (uint)System.Numerics.BitOperations.TrailingZeroCount(stride.GetKnownMinValue())), LlvmAPInt.GetZero(indexWidth))
                : LlvmKnownBits.MakeConstant(new(indexWidth, stride.GetKnownMinValue()));
            AddIndexToKnown(LlvmKnownBits.Mul(amount, scaling));
        }
        if (!known.IsUnknown && !constant.IsZero) AddIndexToKnown(LlvmKnownBits.MakeConstant(constant));
        return known;

        void AddIndexToKnown(LlvmKnownBits offset)
        {
            if (indexWidth == width)
            { known = LlvmKnownBits.AddSub(true, false, false, known, offset); return; }
            var low = LlvmKnownBits.AddSub(true, false, false, known.Trunc(indexWidth), offset);
            var zero = known.Zero;
            var one = known.One;
            zero = zero.InsertBits(low.Zero, 0);
            one = one.InsertBits(low.One, 0);
            known = new(zero, one);
        }
    }
    // Operator.cpp GEPOperator::getMaxPreservedAlignment, expressed as log2.
    private static uint GetGepPreservedZeros(LlvmGetElementPtrInstruction gep, LlvmDataLayout? layout = null)
    {
        layout ??= GetQueryLayout(gep, null);
        uint result = 32; // Value::MaxAlignmentExponent.
        LlvmType indexed = gep.SourceElementType;
        for (int index = 0; index < gep.IndexCount; ++index)
        {
            LlvmValue value = gep.GetIndex(index);
            ulong offset;
            if (index != 0 && indexed is LlvmStructType structure)
            {
                uint member = (uint)((LlvmConstantInt)value).Value.GetZExtValue();
                offset = layout.GetStructLayout(structure).GetElementOffset(member).GetFixedValue();
                indexed = structure.Elements[(int)member];
            }
            else
            {
                if (index != 0) indexed = indexed switch
                {
                    LlvmArrayType array => array.ElementType,
                    LlvmVectorType vector => vector.ElementType,
                    _ => throw new InvalidOperationException("Invalid GEP sequential index.")
                };
                ulong count = value is LlvmConstantInt integer ? integer.Value.GetLimitedValue() : 1;
                offset = unchecked(layout.GetTypeAllocSize(indexed).GetKnownMinValue() * count);
            }
            result = Math.Min(result, (uint)System.Numerics.BitOperations.TrailingZeroCount(offset));
        }
        return result;
    }

}
