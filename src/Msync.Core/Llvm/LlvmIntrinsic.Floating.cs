// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Intrinsics.td FP
// definitions; Intrinsics.cpp overloaded names and getOrInsertDeclaration.
using System.Diagnostics;
using System.Globalization;

namespace Msync.Core.Llvm;

public readonly partial record struct LlvmIntrinsicKind
{
    public static LlvmIntrinsicKind FAbs { get; } = new("llvm.fabs");
    public static LlvmIntrinsicKind Frexp { get; } = new("llvm.frexp");
    public static LlvmIntrinsicKind SinCos { get; } = new("llvm.sincos");
    public static LlvmIntrinsicKind Sqrt { get; } = new("llvm.sqrt");
    public static LlvmIntrinsicKind Floor { get; } = new("llvm.floor");
    public static LlvmIntrinsicKind Ceil { get; } = new("llvm.ceil");
    public static LlvmIntrinsicKind Trunc { get; } = new("llvm.trunc");
    public static LlvmIntrinsicKind Rint { get; } = new("llvm.rint");
    public static LlvmIntrinsicKind NearbyInt { get; } = new("llvm.nearbyint");
    public static LlvmIntrinsicKind Round { get; } = new("llvm.round");
    public static LlvmIntrinsicKind RoundEven { get; } = new("llvm.roundeven");
    public static LlvmIntrinsicKind CopySign { get; } = new("llvm.copysign");
    public static LlvmIntrinsicKind Canonicalize { get; } = new("llvm.canonicalize");
    public static LlvmIntrinsicKind ArithmeticFence { get; } = new("llvm.arithmetic.fence");
    public static LlvmIntrinsicKind MinNum { get; } = new("llvm.minnum");
    public static LlvmIntrinsicKind MaxNum { get; } = new("llvm.maxnum");
    public static LlvmIntrinsicKind Minimum { get; } = new("llvm.minimum");
    public static LlvmIntrinsicKind Maximum { get; } = new("llvm.maximum");
    public static LlvmIntrinsicKind MinimumNum { get; } = new("llvm.minimumnum");
    public static LlvmIntrinsicKind MaximumNum { get; } = new("llvm.maximumnum");
    public static LlvmIntrinsicKind Ldexp { get; } = new("llvm.ldexp");
    public static LlvmIntrinsicKind Pow { get; } = new("llvm.pow");
    public static LlvmIntrinsicKind PowI { get; } = new("llvm.powi");
    public static LlvmIntrinsicKind Exp { get; } = new("llvm.exp");
    public static LlvmIntrinsicKind Exp2 { get; } = new("llvm.exp2");
    public static LlvmIntrinsicKind Exp10 { get; } = new("llvm.exp10");
    public static LlvmIntrinsicKind Log { get; } = new("llvm.log");
    public static LlvmIntrinsicKind Log2 { get; } = new("llvm.log2");
    public static LlvmIntrinsicKind Log10 { get; } = new("llvm.log10");
    public static LlvmIntrinsicKind Sin { get; } = new("llvm.sin");
    public static LlvmIntrinsicKind Cos { get; } = new("llvm.cos");
    public static LlvmIntrinsicKind Tan { get; } = new("llvm.tan");
    public static LlvmIntrinsicKind ASin { get; } = new("llvm.asin");
    public static LlvmIntrinsicKind ACos { get; } = new("llvm.acos");
    public static LlvmIntrinsicKind ATan { get; } = new("llvm.atan");
    public static LlvmIntrinsicKind ATan2 { get; } = new("llvm.atan2");
    public static LlvmIntrinsicKind Sinh { get; } = new("llvm.sinh");
    public static LlvmIntrinsicKind Cosh { get; } = new("llvm.cosh");
    public static LlvmIntrinsicKind Tanh { get; } = new("llvm.tanh");
    public static LlvmIntrinsicKind FMA { get; } = new("llvm.fma");
    public static LlvmIntrinsicKind FMulAdd { get; } = new("llvm.fmuladd");
    public static LlvmIntrinsicKind IsFPClass { get; } = new("llvm.is.fpclass");
}

public static partial class LlvmIntrinsic
{
    private static readonly LlvmIntrinsicDescriptor[] FloatingDescriptors =
    [
        new(LlvmIntrinsicKind.Frexp), new(LlvmIntrinsicKind.SinCos) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.FAbs), new(LlvmIntrinsicKind.Sqrt), new(LlvmIntrinsicKind.Floor),
        new(LlvmIntrinsicKind.Ceil), new(LlvmIntrinsicKind.Trunc), new(LlvmIntrinsicKind.Rint),
        new(LlvmIntrinsicKind.NearbyInt), new(LlvmIntrinsicKind.Round), new(LlvmIntrinsicKind.RoundEven),
        new(LlvmIntrinsicKind.CopySign), new(LlvmIntrinsicKind.Canonicalize), new(LlvmIntrinsicKind.ArithmeticFence),
        new(LlvmIntrinsicKind.MinNum) { Commutative = true }, new(LlvmIntrinsicKind.MaxNum) { Commutative = true },
        new(LlvmIntrinsicKind.Minimum) { Commutative = true }, new(LlvmIntrinsicKind.Maximum) { Commutative = true },
        new(LlvmIntrinsicKind.MinimumNum) { Commutative = true }, new(LlvmIntrinsicKind.MaximumNum) { Commutative = true },
        new(LlvmIntrinsicKind.Ldexp), new(LlvmIntrinsicKind.Pow), new(LlvmIntrinsicKind.PowI),
        new(LlvmIntrinsicKind.Exp), new(LlvmIntrinsicKind.Exp2), new(LlvmIntrinsicKind.Exp10),
        new(LlvmIntrinsicKind.Log), new(LlvmIntrinsicKind.Log2), new(LlvmIntrinsicKind.Log10),
        new(LlvmIntrinsicKind.Sin), new(LlvmIntrinsicKind.Cos), new(LlvmIntrinsicKind.FMA) { Commutative = true }, new(LlvmIntrinsicKind.FMulAdd) { Commutative = true },
        // These definitions remain outside the source's NoCreateUndefOrPoison group.
        new(LlvmIntrinsicKind.Tan) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.ASin) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.ACos) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.ATan) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.ATan2) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.Sinh) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.Cosh) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.Tanh) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.IsFPClass) { ImmediateArgumentIndex = 1 }
    ];

    public static bool IsFloatingIntrinsic(LlvmIntrinsicKind kind) => GetFloatingDescriptor(kind) is not null;
    public static bool IsFloatingMinMax(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.MinNum ||
        kind == LlvmIntrinsicKind.MaxNum || kind == LlvmIntrinsicKind.Minimum || kind == LlvmIntrinsicKind.Maximum ||
        kind == LlvmIntrinsicKind.MinimumNum || kind == LlvmIntrinsicKind.MaximumNum;
    private static LlvmIntrinsicDescriptor? GetFloatingDescriptor(LlvmIntrinsicKind kind)
    {
        foreach (var descriptor in FloatingDescriptors) if (descriptor.Kind == kind) return descriptor;
        return null;
    }
    private static LlvmIntrinsicDescriptor? LookupFloatingDescriptor(LlvmFunction function)
    {
        if (function.ArgumentCount == 0 || !function.FunctionType.GetParamType(0).IsFPOrFPVectorTy()) return null;
        var argument = function.FunctionType.GetParamType(0);
        foreach (var descriptor in FloatingDescriptors)
        {
            var kind = descriptor.Kind;
            LlvmType? exponent = kind == LlvmIntrinsicKind.Frexp && function.ReturnType is LlvmStructType aggregate
                ? aggregate.Elements[1] : IsFloatingExponent(kind) && function.ArgumentCount > 1
                    ? function.FunctionType.GetParamType(1) : null;
            if (kind == LlvmIntrinsicKind.Frexp && exponent is null) continue;
            if (IsFloatingExponent(kind) && (exponent is null ||
                !(kind == LlvmIntrinsicKind.PowI ? exponent.IsIntegerTy() : exponent.IsIntOrIntVectorTy()))) continue;
            if (function.Name == FloatingName(kind, argument, exponent)) return descriptor;
        }
        return null;
    }
    private static bool IsFloatingExponent(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.Ldexp || kind == LlvmIntrinsicKind.PowI;

    public static LlvmType GetFloatingIntrinsicReturnType(LlvmIntrinsicKind kind, LlvmType floatingType, LlvmType? exponentType = null)
    {
        if (kind == LlvmIntrinsicKind.IsFPClass) return floatingType.GetWithNewType(LlvmType.GetInt1Ty(floatingType.Context));
        if (kind == LlvmIntrinsicKind.Frexp) return LlvmStructType.Get(floatingType.Context,
            [floatingType, exponentType ?? floatingType.GetWithNewType(LlvmType.GetInt32Ty(floatingType.Context))]);
        return kind == LlvmIntrinsicKind.SinCos ? LlvmStructType.Get(floatingType.Context, [floatingType, floatingType]) : floatingType;
    }

    public static LlvmFunction GetDeclaration(LlvmModule module, LlvmIntrinsicKind kind, LlvmType floatingType,
        Dictionary<string, ulong>? metrics = null, LlvmType? exponentType = null)
    {
        Debug.Assert(floatingType.IsFPOrFPVectorTy() && ReferenceEquals(floatingType.Context, module.Context));
        Debug.Assert(GetFloatingDescriptor(kind) is not null);
        if (kind == LlvmIntrinsicKind.Frexp) exponentType ??= floatingType.GetWithNewType(LlvmType.GetInt32Ty(module.Context));
        LlvmType result = GetFloatingIntrinsicReturnType(kind, floatingType, exponentType);
        LlvmType[] parameters;
        if (IsFloatingExponent(kind))
        {
            exponentType ??= LlvmType.GetInt32Ty(module.Context);
            Debug.Assert(ReferenceEquals(exponentType.Context, module.Context));
            Debug.Assert(kind == LlvmIntrinsicKind.PowI ? exponentType.IsIntegerTy() : exponentType.IsIntOrIntVectorTy());
            parameters = [floatingType, exponentType];
        }
        else if (kind == LlvmIntrinsicKind.IsFPClass) parameters = [floatingType, LlvmType.GetInt32Ty(module.Context)];
        else if (kind == LlvmIntrinsicKind.FMA || kind == LlvmIntrinsicKind.FMulAdd) parameters = [floatingType, floatingType, floatingType];
        else if (kind == LlvmIntrinsicKind.CopySign || kind == LlvmIntrinsicKind.Pow || kind == LlvmIntrinsicKind.ATan2 || IsFloatingMinMax(kind))
            parameters = [floatingType, floatingType];
        else parameters = [floatingType];
        var declaration = GetOrInsertDeclaration(module, LlvmFunctionType.Get(result, parameters), FloatingName(kind, floatingType, exponentType), metrics);
        if (kind == LlvmIntrinsicKind.IsFPClass) declaration.AddParamAttr(1, LlvmAttributeKind.ImmArg);
        Count(metrics, floatingType.IsVectorTy() ? "floating.vector_declaration" : "floating.scalar_declaration");
        return declaration;
    }

    private static LlvmCallInstruction CreateFloatingIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments,
        LlvmModule module, Dictionary<string, ulong>? metrics)
    {
        LlvmType? exponent = IsFloatingExponent(kind) ? arguments[1].Type : null;
        var declaration = GetDeclaration(module, kind, arguments[0].Type, metrics, exponent);
        Count(metrics, "floating_call_created");
        return new(declaration.FunctionType, declaration, arguments);
    }
    public static LlvmCallInstruction CreateIntrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmValue> arguments,
        LlvmType resultType, LlvmModule module, Dictionary<string, ulong>? metrics = null)
    {
        if (kind == LlvmIntrinsicKind.Frexp && resultType is LlvmStructType pair)
        {
            var declaration = GetDeclaration(module, kind, arguments[0].Type, metrics, pair.Elements[1]);
            Count(metrics, "floating_call_created");
            return new(declaration.FunctionType, declaration, arguments);
        }
        if (IsSubvectorIntrinsic(kind)) return CreateSubvectorIntrinsic(kind, resultType, arguments, module, metrics);
        if (IsVectorFamilyIntrinsic(kind)) return CreateVectorIntrinsic(kind, arguments, resultType, module, metrics);
        if (arguments.Count != 0 && arguments[0].Type is LlvmVectorType && GetIntegerVectorDescriptor(kind) is not null)
            return CreateIntegerVectorIntrinsic(kind, arguments, resultType, module, metrics);
        var call = IsFloatingIntrinsic(kind) ? CreateFloatingIntrinsic(kind, arguments, module, metrics)
            : resultType is LlvmIntegerType integer ? CreateIntrinsic(kind, arguments, integer, module, metrics)
            : CreateIntrinsic(kind, arguments, module, metrics);
        Debug.Assert(ReferenceEquals(resultType, call.Type));
        return call;
    }
    private static string FloatingName(LlvmIntrinsicKind kind, LlvmType type, LlvmType? exponent) =>
        kind.Name + "." + FloatingTypeName(type) +
        (IsFloatingExponent(kind) || kind == LlvmIntrinsicKind.Frexp ? "." + FloatingTypeName(exponent!) : "");
    private static string FloatingTypeName(LlvmType type) => type switch
    {
        LlvmVectorType vector => (vector.ElementCount.IsScalable ? "nxv" : "v") +
            vector.ElementCount.KnownMinValue.ToString(CultureInfo.InvariantCulture) + FloatingTypeName(vector.ElementType),
        LlvmIntegerType integer => "i" + integer.BitWidth.ToString(CultureInfo.InvariantCulture),
        { Id: LlvmTypeId.HalfTyID } => "f16", { Id: LlvmTypeId.BFloatTyID } => "bf16", { Id: LlvmTypeId.FloatTyID } => "f32",
        { Id: LlvmTypeId.DoubleTyID } => "f64", { Id: LlvmTypeId.X86_FP80TyID } => "f80", { Id: LlvmTypeId.FP128TyID } => "f128",
        { Id: LlvmTypeId.PPC_FP128TyID } => "ppcf128",
        _ => throw new InvalidOperationException("Intrinsic floating overload requires a floating type.")
    };
}
