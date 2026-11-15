// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Intrinsics.td vector_insert/vector_extract; IRBuilder.h CreateInsert/ExtractVector.
using System.Diagnostics;
using System.Globalization;

namespace Msync.Core.Llvm;

public readonly partial record struct LlvmIntrinsicKind
{
    public static LlvmIntrinsicKind VectorInsert { get; } = new("llvm.vector.insert");
    public static LlvmIntrinsicKind VectorExtract { get; } = new("llvm.vector.extract");
}

public static partial class LlvmIntrinsic
{
    private static readonly LlvmIntrinsicDescriptor VectorInsertDescriptor = new(LlvmIntrinsicKind.VectorInsert)
    { CreatesUndefOrPoison = true, SourceMemoryEffects = new(0), SourceSpeculatable = true,
        SourcePropagatesPoison = false, ImmediateArgumentIndex = 2 };
    private static readonly LlvmIntrinsicDescriptor VectorExtractDescriptor = new(LlvmIntrinsicKind.VectorExtract)
    { CreatesUndefOrPoison = true, SourceMemoryEffects = new(0), SourceSpeculatable = true,
        SourcePropagatesPoison = false, ImmediateArgumentIndex = 1 };
    private static readonly LlvmIntrinsicDescriptor[] SubvectorDescriptors = [VectorInsertDescriptor, VectorExtractDescriptor];
    public static bool IsSubvectorIntrinsic(LlvmIntrinsicKind kind) =>
        kind == LlvmIntrinsicKind.VectorInsert || kind == LlvmIntrinsicKind.VectorExtract;
    private static LlvmIntrinsicDescriptor? GetSubvectorDescriptor(LlvmIntrinsicKind kind) =>
        kind == LlvmIntrinsicKind.VectorInsert ? VectorInsertDescriptor :
        kind == LlvmIntrinsicKind.VectorExtract ? VectorExtractDescriptor : null;

    private static LlvmIntrinsicDescriptor? LookupSubvectorDescriptor(LlvmFunction function)
    {
        if (function.ReturnType is not LlvmVectorType result) return null;
        var parameters = function.FunctionType.Parameters;
        foreach (var descriptor in SubvectorDescriptors)
        {
            bool insert = descriptor.Kind == LlvmIntrinsicKind.VectorInsert;
            if (parameters.Count != (insert ? 3 : 2) || !parameters[^1].IsIntegerTy(64)
                || parameters[0] is not LlvmVectorType || insert && parameters[0] != result
                || parameters[insert ? 1 : 0] is not LlvmVectorType other) continue;
            if (function.Name == SubvectorName(descriptor.Kind, result, other)) return descriptor;
        }
        return null;
    }

    public static LlvmCallInstruction CreateSubvectorIntrinsic(LlvmIntrinsicKind kind,
        LlvmType resultType, IReadOnlyList<LlvmValue> arguments, LlvmModule module,
        Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(IsSubvectorIntrinsic(kind));
        Debug.Assert(resultType is LlvmVectorType);
        bool insert = kind == LlvmIntrinsicKind.VectorInsert;
        Debug.Assert(arguments.Count == (insert ? 3 : 2) && arguments[^1].Type.IsIntegerTy(64));
        Debug.Assert(!insert || arguments[0].Type == resultType);
        var other = (LlvmVectorType)arguments[insert ? 1 : 0].Type;
        var signature = LlvmFunctionType.Get(resultType, arguments.Select(value => value.Type).ToArray());
        var function = GetOrInsertDeclaration(module, signature, SubvectorName(kind, resultType, other), metrics);
        function.AddParamAttr(insert ? 2 : 1, LlvmAttributeKind.ImmArg);
        Count(metrics, "subvector_call_created");
        return new(signature, function, arguments);
    }

    private static string SubvectorName(LlvmIntrinsicKind kind, LlvmType result, LlvmType other) =>
        kind.Name + "." + SubvectorTypeName(result) + "." + SubvectorTypeName(other);
    private static string SubvectorTypeName(LlvmType type) => type switch
    {
        LlvmVectorType vector => (vector.ElementCount.IsScalable ? "nxv" : "v") +
            vector.ElementCount.KnownMinValue.ToString(CultureInfo.InvariantCulture) + SubvectorTypeName(vector.ElementType),
        LlvmPointerType pointer => "p" + pointer.AddressSpace.ToString(CultureInfo.InvariantCulture),
        _ => FloatingTypeName(type)
    };
}
