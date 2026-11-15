// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Intrinsics.td int_vector_reduce_add; Intrinsics.cpp overloaded declaration naming.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial record struct LlvmIntrinsicKind
{
    public static LlvmIntrinsicKind VectorReduceAdd { get; } = new("llvm.vector.reduce.add");
}

public static partial class LlvmIntrinsic
{
    private static readonly LlvmIntrinsicDescriptor VectorReduceAddDescriptor = new(LlvmIntrinsicKind.VectorReduceAdd)
    { SourceMemoryEffects = new(0), SourceSpeculatable = true, SourcePropagatesPoison = false };

    private static LlvmIntrinsicDescriptor? GetReductionDescriptor(LlvmIntrinsicKind kind) =>
        kind == LlvmIntrinsicKind.VectorReduceAdd ? VectorReduceAddDescriptor : null;

    private static LlvmIntrinsicDescriptor? LookupReductionDescriptor(LlvmFunction function)
    {
        var parameters = function.FunctionType.Parameters;
        if (parameters.Count != 1 || parameters[0] is not LlvmVectorType { ElementType: LlvmIntegerType element } vector
            || function.ReturnType != element) return null;
        return function.Name == ReductionName(LlvmIntrinsicKind.VectorReduceAdd, vector) ? VectorReduceAddDescriptor : null;
    }

    private static LlvmCallInstruction CreateReductionIntrinsic(LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmValue> arguments, LlvmModule module, Dictionary<string, ulong>? metrics)
    {
        Debug.Assert(kind == LlvmIntrinsicKind.VectorReduceAdd && arguments.Count == 1);
        var vector = (LlvmVectorType)arguments[0].Type;
        Debug.Assert(vector.ElementType.IsIntegerTy() && vector.Context == module.Context);
        var signature = LlvmFunctionType.Get(vector.ElementType, [vector]);
        var declaration = GetOrInsertDeclaration(module, signature, ReductionName(kind, vector), metrics);
        Count(metrics, "reduction_call_created");
        return new(signature, declaration, arguments);
    }

    private static string ReductionName(LlvmIntrinsicKind kind, LlvmVectorType vector) =>
        kind.Name + "." + SubvectorTypeName(vector);
}
