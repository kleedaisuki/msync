// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Intrinsics.td vector_reverse, experimental_vp_reverse, vector_splice_left/right,
// masked_load/gather; Intrinsics.cpp overloaded types and declaration naming.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public readonly partial record struct LlvmIntrinsicKind
{
    public static LlvmIntrinsicKind VectorReverse { get; } = new("llvm.vector.reverse");
    public static LlvmIntrinsicKind ExperimentalVpReverse { get; } = new("llvm.experimental.vp.reverse");
    public static LlvmIntrinsicKind VectorSpliceLeft { get; } = new("llvm.vector.splice.left");
    public static LlvmIntrinsicKind VectorSpliceRight { get; } = new("llvm.vector.splice.right");
    public static LlvmIntrinsicKind MaskedLoad { get; } = new("llvm.masked.load");
    public static LlvmIntrinsicKind MaskedGather { get; } = new("llvm.masked.gather");
}

public static partial class LlvmIntrinsic
{
    private static readonly LlvmIntrinsicDescriptor[] VectorFamilyDescriptors =
    [
        PureVectorDescriptor(LlvmIntrinsicKind.VectorReverse),
        PureVectorDescriptor(LlvmIntrinsicKind.ExperimentalVpReverse),
        PureVectorDescriptor(LlvmIntrinsicKind.VectorSpliceLeft),
        PureVectorDescriptor(LlvmIntrinsicKind.VectorSpliceRight),
        new(LlvmIntrinsicKind.MaskedLoad)
        { SourceMemoryEffects = new(1), SourceSpeculatable = false, SourcePropagatesPoison = false, CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.MaskedGather)
        { SourceMemoryEffects = new(LlvmMemoryEffectsAttribute.ReadRaw), SourceSpeculatable = false,
            SourcePropagatesPoison = false, CreatesUndefOrPoison = true }
    ];

    private static LlvmIntrinsicDescriptor PureVectorDescriptor(LlvmIntrinsicKind kind) => new(kind)
    { SourceMemoryEffects = new(0), SourceSpeculatable = true, SourcePropagatesPoison = false, CreatesUndefOrPoison = true };

    public static bool IsVectorFamilyIntrinsic(LlvmIntrinsicKind kind) => GetVectorFamilyDescriptor(kind) is not null;

    private static LlvmIntrinsicDescriptor? GetVectorFamilyDescriptor(LlvmIntrinsicKind kind) =>
        VectorFamilyDescriptors.FirstOrDefault(descriptor => descriptor.Kind == kind);

    private static LlvmIntrinsicDescriptor? LookupVectorFamilyDescriptor(LlvmFunction function)
    {
        if (function.ReturnType is not LlvmVectorType result) return null;
        foreach (var descriptor in VectorFamilyDescriptors)
            if (TryVectorFamilySignature(descriptor.Kind, result, function.FunctionType.Parameters,
                out var signature, out var name) && function.Name == name && function.FunctionType == signature)
                return descriptor;
        return null;
    }

    public static LlvmCallInstruction CreateVectorIntrinsic(LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmValue> arguments, LlvmType resultType, LlvmModule module,
        Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(IsVectorFamilyIntrinsic(kind));
        var declaration = GetVectorDeclaration(module, kind, resultType, arguments.Select(value => value.Type).ToArray(), metrics);
        Count(metrics, "vector_call_created");
        return new(declaration.FunctionType, declaration, arguments);
    }

    public static LlvmFunction GetVectorDeclaration(LlvmModule module, LlvmIntrinsicKind kind,
        LlvmType resultType, IReadOnlyList<LlvmType> argumentTypes, Dictionary<string, ulong>? metrics = null)
    {
        if (resultType is not LlvmVectorType vector || !TryVectorFamilySignature(kind, vector, argumentTypes,
            out var signature, out var name))
            throw new ArgumentException("Vector intrinsic types do not match the source signature.", nameof(argumentTypes));
        Debug.Assert(resultType.Context == module.Context);
        var declaration = GetOrInsertDeclaration(module, signature, name, metrics);
        if (kind == LlvmIntrinsicKind.MaskedLoad)
        {
            var attributes = declaration.GetParamAttributes(0);
            declaration.SetParamAttributes(0, attributes with { CaptureInfo = LlvmCaptureInfo.None() });
        }
        return declaration;
    }

    private static bool TryVectorFamilySignature(LlvmIntrinsicKind kind, LlvmVectorType result,
        IReadOnlyList<LlvmType> arguments, out LlvmFunctionType signature, out string name)
    {
        signature = null!;
        name = "";
        LlvmType mask = result.GetWithNewType(LlvmType.GetInt1Ty(result.Context));
        bool matching;
        if (kind == LlvmIntrinsicKind.VectorReverse)
            matching = arguments.Count == 1 && arguments[0] == result;
        else if (kind == LlvmIntrinsicKind.ExperimentalVpReverse)
            matching = arguments.Count == 3 && arguments[0] == result && arguments[1] == mask && arguments[2].IsIntegerTy(32);
        else if (kind == LlvmIntrinsicKind.VectorSpliceLeft || kind == LlvmIntrinsicKind.VectorSpliceRight)
            matching = arguments.Count == 3 && arguments[0] == result && arguments[1] == result && arguments[2].IsIntegerTy(32);
        else if (kind == LlvmIntrinsicKind.MaskedLoad)
            matching = arguments.Count == 3 && arguments[0] is LlvmPointerType && arguments[1] == mask && arguments[2] == result;
        else if (kind == LlvmIntrinsicKind.MaskedGather)
            matching = arguments.Count == 3 && arguments[0] is LlvmVectorType { ElementType: LlvmPointerType } pointers
                && pointers.ElementCount == result.ElementCount && arguments[1] == mask && arguments[2] == result;
        else return false;
        if (!matching) return false;
        signature = LlvmFunctionType.Get(result, arguments);
        name = kind.Name + "." + SubvectorTypeName(result);
        if (kind == LlvmIntrinsicKind.MaskedLoad || kind == LlvmIntrinsicKind.MaskedGather)
            name += "." + SubvectorTypeName(arguments[0]);
        return true;
    }
}
