// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantFold.cpp
// ConstantFoldExtractValueInstruction / ConstantFoldInsertValueInstruction.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldExtractValueInstruction(LlvmConstant aggregate, IReadOnlyList<uint> indices,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.aggregate.extract.queries");
        return ExtractValue(aggregate, indices, 0, metrics);
    }
    private static LlvmConstant? ExtractValue(LlvmConstant aggregate, IReadOnlyList<uint> indices, int start,
        Dictionary<string, ulong>? metrics)
    {
        if (start == indices.Count) return aggregate;
        Count(metrics, "constant_fold.aggregate.extract.elements");
        LlvmConstant? element = aggregate.GetAggregateElement(indices[start]);
        return element is null ? null : ExtractValue(element, indices, start + 1, metrics);
    }
    public static LlvmConstant? ConstantFoldInsertValueInstruction(LlvmConstant aggregate, LlvmConstant value,
        IReadOnlyList<uint> indices, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.aggregate.insert.queries");
        return InsertValue(aggregate, value, indices, 0, metrics);
    }
    private static LlvmConstant? InsertValue(LlvmConstant aggregate, LlvmConstant value,
        IReadOnlyList<uint> indices, int start, Dictionary<string, ulong>? metrics)
    {
        if (start == indices.Count) return value;
        int count = aggregate.Type is LlvmStructType structure ? structure.Elements.Count : (int)((LlvmArrayType)aggregate.Type).NumElements;
        LlvmConstant[] result = new LlvmConstant[count];
        for (int index = 0; index < count; ++index)
        {
            Count(metrics, "constant_fold.aggregate.insert.elements");
            LlvmConstant? element = aggregate.GetAggregateElement((uint)index);
            if (element is null) return null;
            if (indices[start] == index) element = InsertValue(element, value, indices, start + 1, metrics);
            result[index] = element!;
        }
        return aggregate.Type is LlvmStructType type ? LlvmConstantStruct.Get(type, result) :
            LlvmConstantArray.Get((LlvmArrayType)aggregate.Type, result);
    }
}
