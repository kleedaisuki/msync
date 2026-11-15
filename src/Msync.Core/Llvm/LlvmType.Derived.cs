// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from DerivedTypes.h, Type.cpp and TypedPointerType.h/.cpp,
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public sealed class LlvmFunctionType : LlvmType
{
    public LlvmType ReturnType => containedTypes[0];
    public IReadOnlyList<LlvmType> Parameters { get; }
    public int NumParams => Parameters.Count;
    public bool IsVarArg { get; }
    private LlvmFunctionType(LlvmType result, IReadOnlyList<LlvmType> parameters, bool vararg)
        : base(result.Context, LlvmTypeId.FunctionTyID)
    {
        Debug.Assert(IsValidReturnType(result), "invalid return type for function");
        foreach (var parameter in parameters) Debug.Assert(IsValidArgumentType(parameter), "invalid argument type for function");
        IsVarArg = vararg;
        Parameters = Array.AsReadOnly(parameters.ToArray());
        containedTypes = Array.AsReadOnly(new[] { result }.Concat(parameters).ToArray());
    }
    public LlvmType GetParamType(int index) => Parameters[index];
    public static LlvmFunctionType Get(LlvmType result, IReadOnlyList<LlvmType> parameters, bool isVarArg = false)
    {
        var key = new LlvmTypeKey(parameters, isVarArg, result);
        if (!result.Context.FunctionTypes.TryGetValue(key, out var type))
            result.Context.FunctionTypes.Add(key.Copy(), type = new(result, parameters, isVarArg));
        return type;
    }
    public static LlvmFunctionType Get(LlvmType result, bool isVarArg = false) => Get(result, [], isVarArg);
    public static bool IsValidReturnType(LlvmType type) => !type.IsFunctionTy() && !type.IsLabelTy() && !type.IsMetadataTy();
    public static bool IsValidArgumentType(LlvmType type) => type.IsFirstClassType() && !type.IsLabelTy();
}

public sealed class LlvmArrayType : LlvmType
{
    public LlvmType ElementType => containedTypes[0];
    public ulong NumElements { get; }
    private LlvmArrayType(LlvmType element, ulong count) : base(element.Context, LlvmTypeId.ArrayTyID)
    { containedTypes = Array.AsReadOnly(new[] { element }); NumElements = count; }
    public static LlvmArrayType Get(LlvmType element, ulong count)
    {
        Debug.Assert(IsValidElementType(element), "Invalid type for array element!");
        if (!element.Context.ArrayTypes.TryGetValue((element, count), out var type))
            element.Context.ArrayTypes.Add((element, count), type = new(element, count));
        return type;
    }
    public static bool IsValidElementType(LlvmType type) => !type.IsVoidTy() && !type.IsLabelTy() &&
        !type.IsMetadataTy() && !type.IsFunctionTy() && !type.IsTokenTy() && !type.IsX86_AMXTy();
}

public sealed class LlvmPointerType : LlvmType
{
    public uint AddressSpace { get; }
    private LlvmPointerType(LlvmContext context, uint addressSpace) : base(context, LlvmTypeId.PointerTyID)
    { Debug.Assert(addressSpace < (1 << 24), "Subclass data too large for field"); AddressSpace = addressSpace; }
    public static LlvmPointerType Get(LlvmContext context, uint addressSpace = 0)
    {
        if (addressSpace == 0) return context.As0PointerType ??= new(context, 0);
        if (!context.PointerTypes.TryGetValue(addressSpace, out var type))
            context.PointerTypes.Add(addressSpace, type = new(context, addressSpace));
        return type;
    }
    public static LlvmPointerType GetUnqual(LlvmContext context) => Get(context);
    public static bool IsValidElementType(LlvmType type) => !type.IsVoidTy() && !type.IsLabelTy() &&
        !type.IsMetadataTy() && !type.IsTokenTy() && !type.IsX86_AMXTy();
    public static bool IsLoadableOrStorableType(LlvmType type) => IsValidElementType(type) && !type.IsFunctionTy();
}

// Upstream explicitly forbids TypedPointerType (and types containing it) in LLVM IR.
// It remains represented for DXIL/SPIR-V tracking; this is not an opaque-pointer replacement.
public sealed class LlvmTypedPointerType : LlvmType
{
    public LlvmType ElementType => containedTypes[0];
    public uint AddressSpace { get; }
    private LlvmTypedPointerType(LlvmType element, uint addressSpace) : base(element.Context, LlvmTypeId.TypedPointerTyID)
    {
        Debug.Assert(addressSpace < (1 << 24), "Subclass data too large for field");
        containedTypes = Array.AsReadOnly(new[] { element }); AddressSpace = addressSpace;
    }
    public static LlvmTypedPointerType Get(LlvmType element, uint addressSpace)
    {
        Debug.Assert(IsValidElementType(element), "Invalid type for pointer element!");
        if (!element.Context.TypedPointerTypes.TryGetValue((element, addressSpace), out var type))
            element.Context.TypedPointerTypes.Add((element, addressSpace), type = new(element, addressSpace));
        return type;
    }
    public static bool IsValidElementType(LlvmType type) => LlvmPointerType.IsValidElementType(type);
}
