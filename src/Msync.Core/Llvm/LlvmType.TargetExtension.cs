// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Translated from DerivedTypes.h and Type.cpp, LLVM a55e64432a516fd6507d3ecfba7407cfe425f940.
using System.Diagnostics;
namespace Msync.Core.Llvm;

[Flags]
public enum LlvmTargetExtensionProperty : ulong
{
    HasZeroInit = 1, CanBeGlobal = 2, CanBeLocal = 4, CanBeVectorElement = 8, IsTokenLike = 16
}

public sealed class LlvmTargetExtensionType : LlvmType
{
    public string Name { get; }
    public IReadOnlyList<LlvmType> TypeParameters => containedTypes;
    public IReadOnlyList<uint> IntParameters { get; }
    public int NumTypeParameters => TypeParameters.Count;
    public int NumIntParameters => IntParameters.Count;
    private LlvmTargetExtensionType(LlvmContext context, string name, IReadOnlyList<LlvmType> types, IReadOnlyList<uint> ints)
        : base(context, LlvmTypeId.TargetExtTyID)
    { Name = name; containedTypes = Array.AsReadOnly(types.ToArray()); IntParameters = Array.AsReadOnly(ints.ToArray()); }
    public LlvmType GetTypeParameter(int index) => TypeParameters[index];
    public uint GetIntParameter(int index) => IntParameters[index];
    public static LlvmTargetExtensionType Get(LlvmContext context, string name, IReadOnlyList<LlvmType> types, IReadOnlyList<uint> ints)
    {
        if (!TryGet(context, name, types, ints, out var type, out string? error)) throw new InvalidOperationException(error);
        return type!;
    }
    public static bool TryGet(LlvmContext context, string name, IReadOnlyList<LlvmType> types, IReadOnlyList<uint> ints,
        out LlvmTargetExtensionType? type, out string? error)
    {
        var key = new LlvmTypeKey(types, name: name, ints: ints);
        if (context.TargetExtTypes.TryGetValue(key, out type)) { error = null; return true; }
        type = new(context, name, types, ints);
        context.TargetExtTypes.Add(key.Copy(), type);
        error = name switch
        {
            "aarch64.svcount" when types.Count != 0 || ints.Count != 0 => "target extension type aarch64.svcount should have no parameters",
            "riscv.vector.tuple" when types.Count != 1 || ints.Count != 1 => "target extension type riscv.vector.tuple should have one type parameter and one integer parameter",
            "amdgcn.named.barrier" when types.Count != 0 || ints.Count != 1 => "target extension type amdgcn.named.barrier should have no type parameters and one integer parameter",
            "amdgpu.stridemark" when types.Count != 0 || ints.Count > 1 => "target extension type amdgpu.stridemark should have no type parameters and at most one integer parameter",
            _ => null
        };
        if (error is null) return true;
        type = null;
        return false;
    }
    public LlvmType GetLayoutType() => GetTargetTypeInfo().Layout;
    public bool HasProperty(LlvmTargetExtensionProperty property) => (GetTargetTypeInfo().Properties & property) == property;
    private (LlvmType Layout, LlvmTargetExtensionProperty Properties) GetTargetTypeInfo()
    {
        const LlvmTargetExtensionProperty global = LlvmTargetExtensionProperty.CanBeGlobal;
        const LlvmTargetExtensionProperty local = LlvmTargetExtensionProperty.CanBeLocal;
        const LlvmTargetExtensionProperty zero = LlvmTargetExtensionProperty.HasZeroInit;
        const LlvmTargetExtensionProperty vector = LlvmTargetExtensionProperty.CanBeVectorElement;
        if (Name is "spirv.Image" or "spirv.SignedImage") return (LlvmPointerType.Get(Context), global | local);
        if (Name == "spirv.Type")
        {
            Debug.Assert(NumIntParameters == 3, "Wrong number of parameters for spirv.Type");
            uint size = IntParameters[1], alignment = IntParameters[2];
            return (size > 0 && alignment > 0
                ? LlvmArrayType.Get(GetIntNTy(Context, alignment), unchecked(size * 8) / alignment)
                : GetInt32Ty(Context), global | local);
        }
        if (Name is "spirv.IntegralConstant" or "spirv.Literal") return (GetVoidTy(Context), 0);
        if (Name is "spirv.Padding" or "dx.Padding") return (LlvmArrayType.Get(GetInt8Ty(Context), IntParameters[0]), global);
        if (Name.StartsWith("spirv.", StringComparison.Ordinal))
            return (LlvmPointerType.Get(Context), zero | global | local | (Name.EndsWith("TypedPointerType", StringComparison.Ordinal) ? vector : 0));
        if (Name == "aarch64.svcount") return (LlvmScalableVectorType.Get(GetInt1Ty(Context), 16), zero | local);
        if (Name == "riscv.vector.tuple")
        {
            // RISCVTargetParser.h: RVVBitsPerBlock=64, RVVBytesPerBlock=64/8.
            uint count = unchecked(Math.Max(((LlvmScalableVectorType)TypeParameters[0]).MinNumElements, 8u) * IntParameters[0]);
            return (LlvmScalableVectorType.Get(GetInt8Ty(Context), count), local | zero);
        }
        if (Name.StartsWith("dx.", StringComparison.Ordinal)) return (LlvmPointerType.Get(Context), global | local);
        if (Name == "amdgcn.named.barrier") return (GetInt8Ty(Context), global);
        if (Name == "amdgpu.stridemark") return (GetVoidTy(Context), LlvmTargetExtensionProperty.IsTokenLike);
        if (Name == "llvm.test.vectorelement") return (GetInt32Ty(Context), local | vector);
        if (Name is "wasm.funcref" or "wasm.externref") return (LlvmPointerType.Get(Context), zero | global | local);
        return (GetVoidTy(Context), 0);
    }
}
