// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Intrinsics.td,
// Intrinsics.cpp getOrInsertDeclaration; IRBuilder.cpp CreateBinaryIntrinsic.
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Msync.Core.Llvm;

/// <summary>Source intrinsic identity; not the generated TableGen numeric ID.</summary>
public readonly partial record struct LlvmIntrinsicKind
{
    private LlvmIntrinsicKind(string name) => Name = name;
    public string Name { get; }
    public static LlvmIntrinsicKind Assume { get; } = new("llvm.assume");
    public static LlvmIntrinsicKind SMin { get; } = new("llvm.smin");
    public static LlvmIntrinsicKind SMax { get; } = new("llvm.smax");
    public static LlvmIntrinsicKind UMin { get; } = new("llvm.umin");
    public static LlvmIntrinsicKind UMax { get; } = new("llvm.umax");
    public static LlvmIntrinsicKind FShl { get; } = new("llvm.fshl");
    public static LlvmIntrinsicKind CTPop { get; } = new("llvm.ctpop");
    public static LlvmIntrinsicKind CTTZ { get; } = new("llvm.cttz");
    public static LlvmIntrinsicKind SCmp { get; } = new("llvm.scmp");
    public static LlvmIntrinsicKind UCmp { get; } = new("llvm.ucmp");
    public override string ToString() => Name;
}

/// <summary>Attributes from the represented scalar DefaultAttrsIntrinsic definitions.</summary>
public sealed partial record LlvmIntrinsicDescriptor(LlvmIntrinsicKind Kind)
{
    public bool CreatesUndefOrPoison { get; init; }
    public bool Commutative { get; init; }
    public int ImmediateArgumentIndex { get; init; } = -1;
    public LlvmMemoryEffectsAttribute? SourceMemoryEffects { get; init; }
    public bool? SourceSpeculatable { get; init; }
    public bool? SourcePropagatesPoison { get; init; }
    public bool IsAssume => Kind == LlvmIntrinsicKind.Assume;
    public bool DoesNotAccessMemory => SourceMemoryEffects is { } memory ? memory.Raw == 0 : !IsAssume;
    public bool OnlyAccessesInaccessibleMemory => SourceMemoryEffects is { } memory ? (memory.Raw & ~12U) == 0 : IsAssume;
    public bool OnlyWritesMemory => SourceMemoryEffects is { } memory ? (memory.Raw & LlvmMemoryEffectsAttribute.ReadRaw) == 0 : IsAssume;
    public bool IsNoUndefArgument(int index) => IsAssume && index == 0;
    public bool NoUnwind => true;
    public bool WillReturn => true;
    public bool NoCallback => true;
    public bool NoSync => true;
    public bool NoFree => true;
    public bool Speculatable => SourceSpeculatable ?? !IsAssume;
    public bool NoCreateUndefOrPoison => !IsAssume && Kind != LlvmIntrinsicKind.CTTZ && !CreatesUndefOrPoison;
    public bool PropagatesPoison => SourcePropagatesPoison ?? !IsAssume;
    public bool HasComparisonRange => Kind == LlvmIntrinsicKind.SCmp || Kind == LlvmIntrinsicKind.UCmp;
    public bool IsCommutative => Commutative || LlvmIntrinsic.IsMinMax(Kind);
    public bool IsImmediateArgument(int index) => Kind == LlvmIntrinsicKind.CTTZ && index == 1 || index == ImmediateArgumentIndex;
}

public static partial class LlvmIntrinsic
{
    private static readonly LlvmIntrinsicDescriptor[] descriptors = [new(LlvmIntrinsicKind.Assume), new(LlvmIntrinsicKind.SMin), new(LlvmIntrinsicKind.SMax),
        new(LlvmIntrinsicKind.UMin), new(LlvmIntrinsicKind.UMax), new(LlvmIntrinsicKind.FShl),
        new(LlvmIntrinsicKind.CTPop), new(LlvmIntrinsicKind.CTTZ), new(LlvmIntrinsicKind.SCmp), new(LlvmIntrinsicKind.UCmp)];

    public static LlvmIntrinsicDescriptor? GetDescriptor(LlvmFunction function) => function.IntrinsicDescriptor;

    internal static LlvmIntrinsicDescriptor? LookupDescriptor(LlvmFunction function)
    {
        if (!function.HasLLVMReservedName) return null;
        string name = function.Name;
        if (name == LlvmIntrinsicKind.Assume.Name) return GetDescriptor(LlvmIntrinsicKind.Assume);
        if (LookupSubvectorDescriptor(function) is { } subvectorDescriptor) return subvectorDescriptor;
        if (LookupReductionDescriptor(function) is { } reductionDescriptor) return reductionDescriptor;
        if (LookupVectorFamilyDescriptor(function) is { } vectorDescriptor) return vectorDescriptor;
        if (LookupIntegerVectorDescriptor(function) is { } integerVectorDescriptor) return integerVectorDescriptor;
        if (LookupFloatingDescriptor(function) is { } floatingDescriptor) return floatingDescriptor;
        if (LookupMemoryDescriptor(function) is { } memoryDescriptor) return memoryDescriptor;
        if (LookupIntegerDescriptor(function) is { } integerDescriptor) return integerDescriptor;
        if (function.ReturnType is not LlvmIntegerType integer) return null;
        foreach (LlvmIntrinsicDescriptor descriptor in descriptors)
        {
            if (descriptor.IsAssume) continue;
            LlvmIntegerType? argument = descriptor.HasComparisonRange && function.FunctionType.NumParams != 0
                ? function.FunctionType.Parameters[0] as LlvmIntegerType : integer;
            if (argument is not null && name == OverloadedName(descriptor.Kind, integer, argument)) return descriptor;
        }
        return null;
    }

    public static LlvmIntrinsicDescriptor GetDescriptor(LlvmIntrinsicKind kind) =>
        descriptors.FirstOrDefault(descriptor => descriptor.Kind == kind) ?? GetIntegerDescriptor(kind) ??
        GetFloatingDescriptor(kind) ?? GetMemoryDescriptor(kind) ?? GetSubvectorDescriptor(kind) ?? GetReductionDescriptor(kind) ?? GetVectorFamilyDescriptor(kind) ??
        throw new ArgumentOutOfRangeException(nameof(kind));

    // Intrinsics.td int_assume has no overloaded type parameters and returns void.
    public static LlvmFunction GetDeclaration(LlvmModule module, LlvmIntrinsicKind kind, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(kind == LlvmIntrinsicKind.Assume);
        Count(metrics, "declaration_queries");
        var signature = LlvmFunctionType.Get(LlvmType.GetVoidTy(module.Context), [LlvmType.GetInt1Ty(module.Context)]);
        byte[] name = Encoding.UTF8.GetBytes(kind.Name);
        if (module.GetFunction(name) is { } existing)
        {
            if (existing.FunctionType == signature) { Count(metrics, "declaration_reused"); return existing; }
            existing.SetName(existing.Name + ".invalid"); Count(metrics, "invalid_declaration_renamed");
        }
        Count(metrics, "declaration_created");
        return LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage, kind.Name, module);
    }
    public static LlvmCallInstruction CreateAssumption(LlvmValue condition, LlvmModule module,
        IReadOnlyList<LlvmOperandBundleDef>? bundles = null, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(condition.Type.IsIntegerTy(1));
        var declaration = GetDeclaration(module, LlvmIntrinsicKind.Assume, metrics);
        Count(metrics, "assumption_created");
        return new(declaration.FunctionType, declaration, [condition], bundles);
    }
    public static LlvmFunction GetDeclaration(LlvmModule module, LlvmIntrinsicKind kind, LlvmIntegerType type, Dictionary<string, ulong>? metrics = null)
        => GetDeclaration(module, kind, type, type, metrics);

    public static LlvmFunction GetDeclaration(LlvmModule module, LlvmIntrinsicKind kind, LlvmIntegerType resultType,
        LlvmIntegerType argumentType, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "declaration_queries");
        Debug.Assert(resultType.Context == module.Context && argumentType.Context == module.Context);
        _ = GetDescriptor(kind);
        LlvmType[] parameters = kind == LlvmIntrinsicKind.CTPop ? [argumentType]
            : kind == LlvmIntrinsicKind.CTTZ ? [argumentType, LlvmIntegerType.Get(module.Context, 1)]
            : kind == LlvmIntrinsicKind.FShl ? [argumentType, argumentType, argumentType]
            : [argumentType, argumentType];
        LlvmFunctionType signature = IntegerSignature(kind, argumentType) ?? LlvmFunctionType.Get(resultType, parameters);
        byte[] name = Encoding.UTF8.GetBytes(OverloadedName(kind, resultType, argumentType));
        if (module.GetFunction(name) is { } existing)
        {
            if (existing.FunctionType == signature) { Count(metrics, "declaration_reused"); return existing; }
            existing.SetName(existing.Name + ".invalid");
            Count(metrics, "invalid_declaration_renamed");
        }
        Count(metrics, "declaration_created");
        return LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage, Encoding.UTF8.GetString(name), module);
    }

    // ConstantFolder::FoldIntrinsic returns null: the default IRBuilder creates
    // a real call here. InstructionSimplify/ConstantFolding handle later folds.
    public static LlvmCallInstruction CreateBinary(LlvmIntrinsicKind kind, LlvmValue left, LlvmValue right, LlvmModule module, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "binary_call_created");
        return CreateIntrinsic(kind, [left, right], module, metrics);
    }

    public static LlvmCallInstruction CreateIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments,
        LlvmModule module, Dictionary<string, ulong>? metrics = null)
    {
        if (kind == LlvmIntrinsicKind.VectorReduceAdd) return CreateReductionIntrinsic(kind, arguments, module, metrics);
        if (IsVectorFamilyIntrinsic(kind)) return CreateVectorIntrinsic(kind, arguments,
            kind == LlvmIntrinsicKind.MaskedLoad || kind == LlvmIntrinsicKind.MaskedGather ? arguments[2].Type : arguments[0].Type, module, metrics);
        if (IsFloatingIntrinsic(kind)) return CreateFloatingIntrinsic(kind, arguments, module, metrics);
        if (IsMemoryFamilyIntrinsic(kind)) return CreateMemoryIntrinsic(kind, arguments, module, metrics);
        if (arguments.Count != 0 && arguments[0].Type is LlvmVectorType && GetIntegerVectorDescriptor(kind) is not null)
            return CreateIntegerVectorIntrinsic(kind, arguments, null, module, metrics);
        if (kind == LlvmIntrinsicKind.PtrMask)
        {
            var declaration = GetDeclaration(module, kind, (LlvmPointerType)arguments[0].Type, (LlvmIntegerType)arguments[1].Type, metrics);
            Count(metrics, "call_created");
            return new(declaration.FunctionType, declaration, arguments);
        }
        return CreateIntrinsic(kind, arguments, (LlvmIntegerType)arguments[0].Type, module, metrics);
    }

    public static LlvmCallInstruction CreateIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments,
        LlvmIntegerType resultType, LlvmModule module, Dictionary<string, ulong>? metrics = null)
    {
        if (kind == LlvmIntrinsicKind.VectorReduceAdd)
        {
            var reduction = CreateReductionIntrinsic(kind, arguments, module, metrics);
            Debug.Assert(resultType == reduction.Type);
            return reduction;
        }
        if (IsFloatingIntrinsic(kind)) return CreateFloatingIntrinsic(kind, arguments, module, metrics);
        if (arguments.Count != 0 && arguments[0].Type is LlvmVectorType && GetIntegerVectorDescriptor(kind) is not null)
            return CreateIntegerVectorIntrinsic(kind, arguments, resultType, module, metrics);
        var declaration = GetDeclaration(module, kind, resultType, arguments.Count == 0 ? resultType : (LlvmIntegerType)arguments[0].Type, metrics);
        Count(metrics, "call_created");
        return new(declaration.FunctionType, declaration, arguments);
    }

    public static bool IsMinMax(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.SMin || kind == LlvmIntrinsicKind.SMax ||
        kind == LlvmIntrinsicKind.UMin || kind == LlvmIntrinsicKind.UMax;

    // Instructions.cpp CallBase bundle effects: assume bundles do not add ordinary memory effects.
    public static bool HasReadingOperandBundles(LlvmCallBase call) => call.CalledFunction?.IntrinsicDescriptor?.IsAssume != true && call.OperandBundles.Any(bundle =>
        bundle.Tag is not ("ptrauth" or "kcfi" or "convergencectrl" or "deactivation-symbol"));
    public static bool HasClobberingOperandBundles(LlvmCallBase call) => call.CalledFunction?.IntrinsicDescriptor?.IsAssume != true && call.OperandBundles.Any(bundle =>
        bundle.Tag is not ("deopt" or "funclet" or "ptrauth" or "kcfi" or "convergencectrl" or "deactivation-symbol"));

    private static void Count(Dictionary<string, ulong>? metrics, string outcome)
    {
        if (metrics is null) return;
        string key = "intrinsic." + outcome;
        metrics[key] = metrics.GetValueOrDefault(key) + 1;
    }

    private static LlvmFunction GetOrInsertDeclaration(LlvmModule module, LlvmFunctionType signature, string name,
        Dictionary<string, ulong>? metrics)
    {
        Count(metrics, "declaration_queries");
        if (module.GetFunction(Encoding.UTF8.GetBytes(name)) is { } existing)
        {
            if (existing.FunctionType == signature) { Count(metrics, "declaration_reused"); return existing; }
            existing.SetName(existing.Name + ".invalid");
            Count(metrics, "invalid_declaration_renamed");
        }
        Count(metrics, "declaration_created");
        return LlvmFunction.Create(signature, LlvmLinkage.ExternalLinkage, name, module);
    }

    private static string OverloadedName(LlvmIntrinsicKind kind, LlvmIntegerType resultType, LlvmIntegerType argumentType) =>
        kind.Name + ".i" + resultType.BitWidth.ToString(CultureInfo.InvariantCulture) +
        (kind == LlvmIntrinsicKind.SCmp || kind == LlvmIntrinsicKind.UCmp
            ? ".i" + argumentType.BitWidth.ToString(CultureInfo.InvariantCulture) : "");

    public static LlvmIntrinsicKind GetInverseMinMax(LlvmIntrinsicKind kind) =>
        kind == LlvmIntrinsicKind.SMin ? LlvmIntrinsicKind.SMax : kind == LlvmIntrinsicKind.SMax ? LlvmIntrinsicKind.SMin :
        kind == LlvmIntrinsicKind.UMin ? LlvmIntrinsicKind.UMax : kind == LlvmIntrinsicKind.UMax ? LlvmIntrinsicKind.UMin :
        throw new ArgumentOutOfRangeException(nameof(kind));

    public static LlvmAPInt GetSaturationPoint(LlvmIntrinsicKind kind, uint width) =>
        kind == LlvmIntrinsicKind.SMin ? LlvmAPInt.GetSignedMinValue(width) : kind == LlvmIntrinsicKind.SMax ? LlvmAPInt.GetSignedMaxValue(width) :
        kind == LlvmIntrinsicKind.UMin ? LlvmAPInt.GetMinValue(width) : kind == LlvmIntrinsicKind.UMax ? LlvmAPInt.GetMaxValue(width) :
        throw new ArgumentOutOfRangeException(nameof(kind));

    public static LlvmCmpPredicate GetMinMaxPredicate(LlvmIntrinsicKind kind) =>
        kind == LlvmIntrinsicKind.SMin ? LlvmCmpPredicate.ICmpSlt : kind == LlvmIntrinsicKind.SMax ? LlvmCmpPredicate.ICmpSgt :
        kind == LlvmIntrinsicKind.UMin ? LlvmCmpPredicate.ICmpUlt : kind == LlvmIntrinsicKind.UMax ? LlvmCmpPredicate.ICmpUgt :
        throw new ArgumentOutOfRangeException(nameof(kind));
}
