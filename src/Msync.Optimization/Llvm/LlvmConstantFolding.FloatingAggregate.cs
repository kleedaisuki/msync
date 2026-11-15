// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFolding.cpp
// ConstantFoldStructCall and ConstantFoldScalarFrexpCall.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;
public static partial class LlvmConstantFolding
{
    private static LlvmConstant? FoldFloatingAggregate(LlvmIntrinsicKind kind, IReadOnlyList<LlvmConstant> operands,
        LlvmStructType type, Dictionary<string, ulong>? metrics, LlvmCallBase? call)
    {
        if (operands[0].Type is LlvmFixedVectorType vector)
        {
            var first = new LlvmConstant[vector.NumElements];
            var second = new LlvmConstant[vector.NumElements];
            for (uint index = 0; index < vector.NumElements; ++index)
            {
                var lane = operands[0].GetAggregateElement(index);
                if (lane is null) return null;
                var result = FoldFloatingAggregateLane(kind, lane, type.Elements[1].GetScalarType(), metrics, call);
                if (result.First is null || result.Second is null) return null;
                first[index] = result.First; second[index] = result.Second;
                Count(metrics, "constant_fold.floating.aggregate_lanes");
            }
            return LlvmConstantStruct.Get(type, [LlvmConstantVector.Get(first), LlvmConstantVector.Get(second)]);
        }
        var scalar = FoldFloatingAggregateLane(kind, operands[0], type.Elements[1], metrics, call);
        if (scalar.First is null || scalar.Second is null) return null;
        Count(metrics, "constant_fold.floating.aggregate_folded");
        return LlvmConstantStruct.Get(type, [scalar.First, scalar.Second]);
    }
    private static (LlvmConstant? First, LlvmConstant? Second) FoldFloatingAggregateLane(LlvmIntrinsicKind kind,
        LlvmConstant operand, LlvmType secondType, Dictionary<string, ulong>? metrics, LlvmCallBase? call)
    {
        if (kind == LlvmIntrinsicKind.Frexp)
        {
            if (operand is not LlvmConstantFP value) return default;
            var mantissa = value.Value.Frexp(out int exponent);
            return (LlvmConstantFP.Get(operand.Type, mantissa), LlvmConstantInt.Get(secondType,
                mantissa.IsInfinity || mantissa.IsNaN ? 0UL : unchecked((ulong)(long)exponent), isSigned: true));
        }
        var sine = FoldScalarIntrinsic(LlvmIntrinsicKind.Sin, [operand], operand.Type, metrics, call);
        var cosine = FoldScalarIntrinsic(LlvmIntrinsicKind.Cos, [operand], operand.Type, metrics, call);
        return (sine, cosine);
    }
}
