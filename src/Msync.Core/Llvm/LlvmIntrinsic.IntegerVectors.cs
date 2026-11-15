// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Intrinsics.td llvm_anyint_ty signatures and Intrinsics.cpp overload naming.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public static partial class LlvmIntrinsic
{
    private static LlvmIntrinsicDescriptor? GetIntegerVectorDescriptor(LlvmIntrinsicKind kind)
    {
        if (kind == LlvmIntrinsicKind.Assume || kind == LlvmIntrinsicKind.PtrMask || kind == LlvmIntrinsicKind.VScale) return null;
        foreach (var descriptor in descriptors)
            if (descriptor.Kind == kind) return descriptor;
        foreach (var descriptor in integerDescriptors)
            if (descriptor.Kind == kind) return descriptor;
        return null;
    }

    private static LlvmIntrinsicDescriptor? LookupIntegerVectorDescriptor(LlvmFunction function)
    {
        if (function.FunctionType.Parameters.Count == 0 ||
            function.FunctionType.Parameters[0] is not LlvmVectorType { ElementType: LlvmIntegerType } argument) return null;
        foreach (var descriptor in descriptors)
            if (MatchesIntegerVectorDeclaration(function, argument, descriptor)) return descriptor;
        foreach (var descriptor in integerDescriptors)
            if (MatchesIntegerVectorDeclaration(function, argument, descriptor)) return descriptor;
        return null;
    }

    private static bool MatchesIntegerVectorDeclaration(LlvmFunction function, LlvmVectorType argument,
        LlvmIntrinsicDescriptor descriptor) =>
        TryIntegerVectorSignature(descriptor.Kind, function.ReturnType, argument, out var signature, out var name)
        && function.FunctionType == signature && function.Name == name;

    private static LlvmCallInstruction CreateIntegerVectorIntrinsic(LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmValue> arguments, LlvmType? resultType, LlvmModule module,
        Dictionary<string, ulong>? metrics)
    {
        var argument = (LlvmVectorType)arguments[0].Type;
        resultType ??= IsOverflowIntrinsic(kind)
            ? LlvmStructType.Get(module.Context, [argument, argument.GetWithNewType(LlvmType.GetInt1Ty(module.Context))]) : argument;
        var declaration = GetDeclaration(module, kind, resultType, argument, metrics);
        Count(metrics, "integer_vector_call_created");
        return new(declaration.FunctionType, declaration, arguments);
    }

    public static LlvmFunction GetDeclaration(LlvmModule module, LlvmIntrinsicKind kind,
        LlvmType resultType, LlvmType argumentType, Dictionary<string, ulong>? metrics)
    {
        if (resultType is LlvmIntegerType scalarResult && argumentType is LlvmIntegerType scalarArgument)
            return GetDeclaration(module, kind, scalarResult, scalarArgument, metrics);
        var vector = (LlvmVectorType)argumentType;
        bool valid = TryIntegerVectorSignature(kind, resultType, vector, out var signature, out var name);
        Debug.Assert(valid && vector.Context == module.Context && GetIntegerVectorDescriptor(kind) is not null);
        var declaration = GetOrInsertDeclaration(module, signature, name, metrics);
        var descriptor = GetIntegerVectorDescriptor(kind)!;
        if (descriptor.ImmediateArgumentIndex >= 0) declaration.AddParamAttr(descriptor.ImmediateArgumentIndex, LlvmAttributeKind.ImmArg);
        if (kind == LlvmIntrinsicKind.CTTZ) declaration.AddParamAttr(1, LlvmAttributeKind.ImmArg);
        return declaration;
    }

    private static bool TryIntegerVectorSignature(LlvmIntrinsicKind kind, LlvmType result,
        LlvmVectorType argument, out LlvmFunctionType signature, out string name)
    {
        signature = null!;
        name = "";
        if (argument.ElementType is not LlvmIntegerType || kind == LlvmIntrinsicKind.Assume ||
            kind == LlvmIntrinsicKind.PtrMask || kind == LlvmIntrinsicKind.VScale) return false;
        bool compare = kind == LlvmIntrinsicKind.SCmp || kind == LlvmIntrinsicKind.UCmp;
        LlvmType expected = IsOverflowIntrinsic(kind)
            ? LlvmStructType.Get(argument.Context, [argument, argument.GetWithNewType(LlvmType.GetInt1Ty(argument.Context))]) : argument;
        if (compare ? !result.IsIntOrIntVectorTy() : result != expected) return false;
        LlvmType boolean = LlvmType.GetInt1Ty(argument.Context);
        LlvmType[] parameters = kind == LlvmIntrinsicKind.CTPop || kind == LlvmIntrinsicKind.BSwap || kind == LlvmIntrinsicKind.BitReverse
            ? [argument]
            : kind == LlvmIntrinsicKind.Abs || kind == LlvmIntrinsicKind.CTLZ || kind == LlvmIntrinsicKind.CTTZ ? [argument, boolean]
            : kind == LlvmIntrinsicKind.FShl || kind == LlvmIntrinsicKind.FShr ? [argument, argument, argument]
            : [argument, argument];
        signature = LlvmFunctionType.Get(result, parameters);
        name = kind.Name + "." + SubvectorTypeName(compare ? result : argument);
        if (compare) name += "." + SubvectorTypeName(argument);
        return true;
    }
}
