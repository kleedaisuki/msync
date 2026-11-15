// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Intrinsics.td,
// IntrinsicInst.h memory classifications and Intrinsics.cpp declarations.
using System.Diagnostics;
using System.Globalization;

namespace Msync.Core.Llvm;

public readonly partial record struct LlvmIntrinsicKind
{
    public static LlvmIntrinsicKind MemCpy { get; } = new("llvm.memcpy");
    public static LlvmIntrinsicKind MemCpyInline { get; } = new("llvm.memcpy.inline");
    public static LlvmIntrinsicKind MemMove { get; } = new("llvm.memmove");
    public static LlvmIntrinsicKind MemSet { get; } = new("llvm.memset");
    public static LlvmIntrinsicKind MemSetInline { get; } = new("llvm.memset.inline");
    public static LlvmIntrinsicKind LifetimeStart { get; } = new("llvm.lifetime.start");
    public static LlvmIntrinsicKind LifetimeEnd { get; } = new("llvm.lifetime.end");
    public static LlvmIntrinsicKind InvariantStart { get; } = new("llvm.invariant.start");
    public static LlvmIntrinsicKind InvariantEnd { get; } = new("llvm.invariant.end");
    public static LlvmIntrinsicKind LaunderInvariantGroup { get; } = new("llvm.launder.invariant.group");
    public static LlvmIntrinsicKind FakeUse { get; } = new("llvm.fake.use");
}

public sealed partial record LlvmIntrinsicDescriptor
{
    public bool IsMemoryIntrinsic => LlvmIntrinsic.IsMemoryIntrinsic(Kind);
    public bool IsLifetimeStartOrEnd => Kind == LlvmIntrinsicKind.LifetimeStart || Kind == LlvmIntrinsicKind.LifetimeEnd;
    public bool IsLifetimeIntrinsic => IsLifetimeStartOrEnd;
    public bool IsInvariantStartOrEnd => Kind == LlvmIntrinsicKind.InvariantStart || Kind == LlvmIntrinsicKind.InvariantEnd;
    public bool IsLaunderInvariantGroup => Kind == LlvmIntrinsicKind.LaunderInvariantGroup;
    public bool IsFakeUse => Kind == LlvmIntrinsicKind.FakeUse;
    public bool IsAssumeLikeIntrinsic => IsAssume || IsLifetimeStartOrEnd || IsInvariantStartOrEnd;
}

public static partial class LlvmIntrinsic
{
    private static readonly LlvmIntrinsicDescriptor[] memoryDescriptors =
    [
        MemoryDescriptor(LlvmIntrinsicKind.MemCpy, 3, 3),
        MemoryDescriptor(LlvmIntrinsicKind.MemCpyInline, 3, 3),
        MemoryDescriptor(LlvmIntrinsicKind.MemMove, 3, 3),
        MemoryDescriptor(LlvmIntrinsicKind.MemSet, 2, 3),
        MemoryDescriptor(LlvmIntrinsicKind.MemSetInline, 2, 3),
        MemoryDescriptor(LlvmIntrinsicKind.LifetimeStart, 3),
        MemoryDescriptor(LlvmIntrinsicKind.LifetimeEnd, 3),
        MemoryDescriptor(LlvmIntrinsicKind.InvariantStart, 3, 0),
        MemoryDescriptor(LlvmIntrinsicKind.InvariantEnd, 3, 1),
        MemoryDescriptor(LlvmIntrinsicKind.LaunderInvariantGroup, 12, speculatable: true),
        MemoryDescriptor(LlvmIntrinsicKind.FakeUse, 12)
    ];

    private static LlvmIntrinsicDescriptor MemoryDescriptor(LlvmIntrinsicKind kind, uint effects,
        int immediateArgument = -1, bool speculatable = false) => new(kind)
    {
        SourceMemoryEffects = new(effects),
        SourceSpeculatable = speculatable,
        SourcePropagatesPoison = false,
        CreatesUndefOrPoison = true,
        ImmediateArgumentIndex = immediateArgument
    };

    public static bool IsMemoryIntrinsic(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.MemCpy ||
        kind == LlvmIntrinsicKind.MemCpyInline || kind == LlvmIntrinsicKind.MemMove ||
        kind == LlvmIntrinsicKind.MemSet || kind == LlvmIntrinsicKind.MemSetInline;

    public static bool IsMemoryFamilyIntrinsic(LlvmIntrinsicKind kind) => GetMemoryDescriptor(kind) is not null;
    private static LlvmIntrinsicDescriptor? GetMemoryDescriptor(LlvmIntrinsicKind kind) =>
        memoryDescriptors.FirstOrDefault(descriptor => descriptor.Kind == kind);

    private static LlvmIntrinsicDescriptor? LookupMemoryDescriptor(LlvmFunction function)
    {
        foreach (var descriptor in memoryDescriptors)
        {
            if (!TryMemorySignature(descriptor.Kind, function.FunctionType.Parameters, function.Context,
                out var signature, out var name)) continue;
            if (function.Name == name && function.FunctionType == signature) return descriptor;
        }
        return null;
    }

    public static LlvmCallInstruction CreateMemoryIntrinsic(LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmValue> arguments, LlvmModule module, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(IsMemoryFamilyIntrinsic(kind));
        var argumentTypes = arguments.Select(value => value.Type).ToArray();
        if (!TryMemorySignature(kind, argumentTypes, module.Context, out var signature, out var name))
            throw new ArgumentException("Memory intrinsic argument types do not match the source signature.", nameof(arguments));
        var declaration = GetOrInsertDeclaration(module, signature, name, metrics);
        InstallMemoryArgumentAttributes(declaration, kind);
        Count(metrics, "memory_call_created");
        return new(declaration.FunctionType, declaration, arguments);
    }

    public static LlvmFunction GetMemoryDeclaration(LlvmModule module, LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmType> argumentTypes, Dictionary<string, ulong>? metrics = null)
    {
        if (!TryMemorySignature(kind, argumentTypes, module.Context, out var signature, out var name))
            throw new ArgumentException("Memory intrinsic argument types do not match the source signature.", nameof(argumentTypes));
        var declaration = GetOrInsertDeclaration(module, signature, name, metrics);
        InstallMemoryArgumentAttributes(declaration, kind);
        return declaration;
    }

    private static bool TryMemorySignature(LlvmIntrinsicKind kind, IReadOnlyList<LlvmType> arguments,
        LlvmContext context, out LlvmFunctionType signature, out string name)
    {
        signature = null!; name = "";
        var voidType = LlvmType.GetVoidTy(context);
        if (kind == LlvmIntrinsicKind.FakeUse)
        {
            signature = LlvmFunctionType.Get(voidType, [], true); name = kind.Name; return true;
        }
        if (IsMemoryIntrinsic(kind))
        {
            if (arguments.Count != 4 || arguments[0] is not LlvmPointerType destination ||
                arguments[2] is not LlvmIntegerType length || !arguments[3].IsIntegerTy(1)) return false;
            bool transfer = kind == LlvmIntrinsicKind.MemCpy || kind == LlvmIntrinsicKind.MemCpyInline || kind == LlvmIntrinsicKind.MemMove;
            if (transfer ? arguments[1] is not LlvmPointerType : !arguments[1].IsIntegerTy(8)) return false;
            signature = LlvmFunctionType.Get(voidType, arguments);
            name = kind.Name + "." + PointerSuffix(destination);
            if (transfer) name += "." + PointerSuffix((LlvmPointerType)arguments[1]);
            name += ".i" + length.BitWidth.ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (kind == LlvmIntrinsicKind.LifetimeStart || kind == LlvmIntrinsicKind.LifetimeEnd || kind == LlvmIntrinsicKind.LaunderInvariantGroup)
        {
            if (arguments.Count != 1 || arguments[0] is not LlvmPointerType pointer) return false;
            signature = LlvmFunctionType.Get(kind == LlvmIntrinsicKind.LaunderInvariantGroup ? pointer : voidType, arguments);
            name = kind.Name + "." + PointerSuffix(pointer); return true;
        }
        if (kind == LlvmIntrinsicKind.InvariantStart)
        {
            if (arguments.Count != 2 || !arguments[0].IsIntegerTy(64) || arguments[1] is not LlvmPointerType pointer) return false;
            signature = LlvmFunctionType.Get(LlvmPointerType.Get(context), arguments);
            name = kind.Name + "." + PointerSuffix(pointer); return true;
        }
        if (kind == LlvmIntrinsicKind.InvariantEnd)
        {
            if (arguments.Count != 3 || arguments[0] != LlvmPointerType.Get(context) ||
                !arguments[1].IsIntegerTy(64) || arguments[2] is not LlvmPointerType pointer) return false;
            signature = LlvmFunctionType.Get(voidType, arguments);
            name = kind.Name + "." + PointerSuffix(pointer); return true;
        }
        return false;
    }

    private static string PointerSuffix(LlvmPointerType type) => "p" + type.AddressSpace.ToString(CultureInfo.InvariantCulture);

    private static void InstallMemoryArgumentAttributes(LlvmFunction declaration, LlvmIntrinsicKind kind)
    {
        int immediate = GetMemoryDescriptor(kind)!.ImmediateArgumentIndex;
        if (immediate >= 0) declaration.AddParamAttr(immediate, LlvmAttributeKind.ImmArg);
        if (IsMemoryIntrinsic(kind))
        {
            SetPointerArgument(0, LlvmAttributeKind.WriteOnly);
            if (kind == LlvmIntrinsicKind.MemCpy || kind == LlvmIntrinsicKind.MemCpyInline || kind == LlvmIntrinsicKind.MemMove)
                SetPointerArgument(1, LlvmAttributeKind.ReadOnly);
            if (kind == LlvmIntrinsicKind.MemCpy || kind == LlvmIntrinsicKind.MemCpyInline)
            { declaration.AddParamAttr(0, LlvmAttributeKind.NoAlias); declaration.AddParamAttr(1, LlvmAttributeKind.NoAlias); }
        }
        else if (kind == LlvmIntrinsicKind.LifetimeStart || kind == LlvmIntrinsicKind.LifetimeEnd)
            SetPointerArgument(0, LlvmAttributeKind.None);
        else if (kind == LlvmIntrinsicKind.InvariantStart) SetPointerArgument(1, LlvmAttributeKind.None);
        else if (kind == LlvmIntrinsicKind.InvariantEnd) SetPointerArgument(2, LlvmAttributeKind.None);

        void SetPointerArgument(int index, LlvmAttributeKind access)
        {
            var attributes = declaration.GetParamAttributes(index);
            declaration.SetParamAttributes(index, attributes with
            { Flags = attributes.Flags | access, CaptureInfo = LlvmCaptureInfo.None() });
        }
    }
}
