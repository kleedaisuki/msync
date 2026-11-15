// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Intrinsics.td,
// Intrinsics.cpp type matching and IntrinsicInst.h commutativity switch.
using System.Globalization;
namespace Msync.Core.Llvm;

public readonly partial record struct LlvmIntrinsicKind
{
    public static LlvmIntrinsicKind Abs { get; } = new("llvm.abs");
    public static LlvmIntrinsicKind CTLZ { get; } = new("llvm.ctlz");
    public static LlvmIntrinsicKind FShr { get; } = new("llvm.fshr");
    public static LlvmIntrinsicKind BSwap { get; } = new("llvm.bswap");
    public static LlvmIntrinsicKind BitReverse { get; } = new("llvm.bitreverse");
    public static LlvmIntrinsicKind UAddSat { get; } = new("llvm.uadd.sat");
    public static LlvmIntrinsicKind SAddSat { get; } = new("llvm.sadd.sat");
    public static LlvmIntrinsicKind USubSat { get; } = new("llvm.usub.sat");
    public static LlvmIntrinsicKind SSubSat { get; } = new("llvm.ssub.sat");
    public static LlvmIntrinsicKind UShlSat { get; } = new("llvm.ushl.sat");
    public static LlvmIntrinsicKind SShlSat { get; } = new("llvm.sshl.sat");
    public static LlvmIntrinsicKind UAddWithOverflow { get; } = new("llvm.uadd.with.overflow");
    public static LlvmIntrinsicKind SAddWithOverflow { get; } = new("llvm.sadd.with.overflow");
    public static LlvmIntrinsicKind USubWithOverflow { get; } = new("llvm.usub.with.overflow");
    public static LlvmIntrinsicKind SSubWithOverflow { get; } = new("llvm.ssub.with.overflow");
    public static LlvmIntrinsicKind UMulWithOverflow { get; } = new("llvm.umul.with.overflow");
    public static LlvmIntrinsicKind SMulWithOverflow { get; } = new("llvm.smul.with.overflow");
    public static LlvmIntrinsicKind PtrMask { get; } = new("llvm.ptrmask");
    public static LlvmIntrinsicKind SMulH { get; } = new("llvm.smulh");
    public static LlvmIntrinsicKind UMulH { get; } = new("llvm.umulh");
    public static LlvmIntrinsicKind VScale { get; } = new("llvm.vscale");
}

public static partial class LlvmIntrinsic
{
    private static readonly LlvmIntrinsicDescriptor[] integerDescriptors =
    [
        new(LlvmIntrinsicKind.Abs) { CreatesUndefOrPoison = true, ImmediateArgumentIndex = 1 },
        new(LlvmIntrinsicKind.CTLZ) { CreatesUndefOrPoison = true, ImmediateArgumentIndex = 1 },
        new(LlvmIntrinsicKind.FShr), new(LlvmIntrinsicKind.BSwap), new(LlvmIntrinsicKind.BitReverse),
        new(LlvmIntrinsicKind.UAddSat) { Commutative = true }, new(LlvmIntrinsicKind.SAddSat) { Commutative = true },
        new(LlvmIntrinsicKind.USubSat), new(LlvmIntrinsicKind.SSubSat),
        new(LlvmIntrinsicKind.UShlSat) { CreatesUndefOrPoison = true }, new(LlvmIntrinsicKind.SShlSat) { CreatesUndefOrPoison = true },
        new(LlvmIntrinsicKind.UAddWithOverflow) { Commutative = true }, new(LlvmIntrinsicKind.SAddWithOverflow) { Commutative = true },
        new(LlvmIntrinsicKind.USubWithOverflow), new(LlvmIntrinsicKind.SSubWithOverflow),
        new(LlvmIntrinsicKind.UMulWithOverflow) { Commutative = true }, new(LlvmIntrinsicKind.SMulWithOverflow) { Commutative = true },
        new(LlvmIntrinsicKind.PtrMask), new(LlvmIntrinsicKind.SMulH) { Commutative = true },
        new(LlvmIntrinsicKind.UMulH) { Commutative = true },
        new(LlvmIntrinsicKind.VScale) { CreatesUndefOrPoison = true, SourcePropagatesPoison = false }
    ];

    private static LlvmIntrinsicDescriptor? GetIntegerDescriptor(LlvmIntrinsicKind kind) =>
        integerDescriptors.FirstOrDefault(descriptor => descriptor.Kind == kind);

    private static LlvmIntrinsicDescriptor? LookupIntegerDescriptor(LlvmFunction function)
    {
        if (function.ArgumentCount == 0)
            return function.ReturnType is LlvmIntegerType scalar &&
                function.Name == LlvmIntrinsicKind.VScale.Name + ".i" + scalar.BitWidth.ToString(CultureInfo.InvariantCulture)
                ? GetIntegerDescriptor(LlvmIntrinsicKind.VScale) : null;
        if (function.ReturnType is LlvmPointerType pointer && function.FunctionType.Parameters.Count == 2 &&
            function.FunctionType.Parameters[1] is LlvmIntegerType mask && function.Name == PointerMaskName(pointer, mask))
            return GetIntegerDescriptor(LlvmIntrinsicKind.PtrMask);
        if (function.FunctionType.Parameters[0] is not LlvmIntegerType integer) return null;
        string suffix = ".i" + integer.BitWidth.ToString(CultureInfo.InvariantCulture);
        return integerDescriptors.FirstOrDefault(descriptor => function.Name == descriptor.Kind.Name + suffix);
    }

    private static LlvmFunctionType? IntegerSignature(LlvmIntrinsicKind kind, LlvmIntegerType type)
    {
        if (GetIntegerDescriptor(kind) is null || kind == LlvmIntrinsicKind.PtrMask) return null;
        LlvmType boolean = LlvmIntegerType.Get(type.Context, 1);
        LlvmType result = IsOverflowIntrinsic(kind) ? LlvmStructType.Get(type.Context, [type, boolean]) : type;
        LlvmType[] arguments = kind == LlvmIntrinsicKind.VScale ? []
            : kind == LlvmIntrinsicKind.BSwap || kind == LlvmIntrinsicKind.BitReverse ? [type]
            : kind == LlvmIntrinsicKind.Abs || kind == LlvmIntrinsicKind.CTLZ ? [type, boolean]
            : kind == LlvmIntrinsicKind.FShr ? [type, type, type] : [type, type];
        return LlvmFunctionType.Get(result, arguments);
    }

    public static bool IsOverflowIntrinsic(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.UAddWithOverflow ||
        kind == LlvmIntrinsicKind.SAddWithOverflow || kind == LlvmIntrinsicKind.USubWithOverflow ||
        kind == LlvmIntrinsicKind.SSubWithOverflow || kind == LlvmIntrinsicKind.UMulWithOverflow || kind == LlvmIntrinsicKind.SMulWithOverflow;

    public static bool IsSaturatingIntrinsic(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.UAddSat ||
        kind == LlvmIntrinsicKind.SAddSat || kind == LlvmIntrinsicKind.USubSat || kind == LlvmIntrinsicKind.SSubSat ||
        kind == LlvmIntrinsicKind.UShlSat || kind == LlvmIntrinsicKind.SShlSat;

    public static LlvmOpcode GetOverflowOpcode(LlvmIntrinsicKind kind)
    {
        if (kind == LlvmIntrinsicKind.UAddWithOverflow || kind == LlvmIntrinsicKind.SAddWithOverflow) return LlvmOpcode.Add;
        if (kind == LlvmIntrinsicKind.USubWithOverflow || kind == LlvmIntrinsicKind.SSubWithOverflow) return LlvmOpcode.Sub;
        System.Diagnostics.Debug.Assert(kind == LlvmIntrinsicKind.UMulWithOverflow || kind == LlvmIntrinsicKind.SMulWithOverflow);
        return LlvmOpcode.Mul;
    }

    public static uint GetOverflowNoWrapKind(LlvmIntrinsicKind kind)
    {
        System.Diagnostics.Debug.Assert(IsOverflowIntrinsic(kind));
        return kind == LlvmIntrinsicKind.SAddWithOverflow || kind == LlvmIntrinsicKind.SSubWithOverflow ||
            kind == LlvmIntrinsicKind.SMulWithOverflow ? 2U : 1U;
    }

    public static LlvmFunction GetDeclaration(LlvmModule module, LlvmIntrinsicKind kind, LlvmPointerType pointer,
        LlvmIntegerType mask, Dictionary<string, ulong>? metrics = null)
    {
        System.Diagnostics.Debug.Assert(kind == LlvmIntrinsicKind.PtrMask);
        return GetOrInsertDeclaration(module, LlvmFunctionType.Get(pointer, [pointer, mask]), PointerMaskName(pointer, mask), metrics);
    }

    private static string PointerMaskName(LlvmPointerType pointer, LlvmIntegerType mask) =>
        LlvmIntrinsicKind.PtrMask.Name + ".p" + pointer.AddressSpace.ToString(CultureInfo.InvariantCulture) +
        ".i" + mask.BitWidth.ToString(CultureInfo.InvariantCulture);
}
