// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantFolding.cpp
// ConstantFoldFP/ConstantFoldBinaryFP and llvm_fenv_{clear,test}except.
using System.Runtime.InteropServices;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    private static LlvmConstant? FoldFloatingHostIntrinsic(LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmConstant> operands, LlvmType type, Dictionary<string, ulong>? metrics)
    {
        if (operands[0] is not LlvmConstantFP first) return null;
        if (operands.Count == 2 && operands[1] is not LlvmConstantFP) return null;
        if (operands.Count == 2 && !type.IsHalfTy() && !type.IsFloatTy() && !type.IsDoubleTy()) return null;
        var value = first.Value;
        if (operands.Count == 1 && (value.IsNaN || value.IsInfinity)) return null;
        if (kind == LlvmIntrinsicKind.Log || kind == LlvmIntrinsicKind.Log2 || kind == LlvmIntrinsicKind.Log10)
        {
            if (value.IsZero) return LlvmConstantFP.Get(type, LlvmAPFloat.GetInf(value.Semantics, true));
            if (value.IsNegative) return LlvmConstantFP.Get(type, LlvmAPFloat.FromBits(0x7ff8000000000000UL).Convert(value.Semantics).Value);
            if (value.BitwiseIsEqual(LlvmAPFloat.GetOne(value.Semantics)))
                return LlvmConstantFP.Get(type, LlvmAPFloat.GetZero(value.Semantics));
        }
        if (kind == LlvmIntrinsicKind.ATan && value.IsZero) return first;
        if (!LlvmHostFloatingPoint.Supports(kind, operands.Count)) return null;
        if (!LlvmHostFloatingPoint.Available)
        {
            Count(metrics, "constant_fold.floating.host_unavailable");
            return null;
        }
        Count(metrics, "constant_fold.floating.host_queries");
        double a = BitConverter.UInt64BitsToDouble(value.Convert(LlvmAPFloatSemantics.IEEEdouble).Value.BitcastToAPInt().GetZExtValue());
        double b = operands.Count == 2 && operands[1] is LlvmConstantFP second
            ? BitConverter.UInt64BitsToDouble(second.Value.Convert(LlvmAPFloatSemantics.IEEEdouble).Value.BitcastToAPInt().GetZExtValue()) : 0;
        if (!LlvmHostFloatingPoint.TryEvaluate(kind, a, b, out double result))
        {
            Count(metrics, "constant_fold.floating.host_exception");
            return null;
        }
        Count(metrics, "constant_fold.floating.host_folded");
        return LlvmConstantFP.Get(type, new LlvmAPFloat(result).Convert(type.GetFltSemantics()).Value);
    }
}

// The source deliberately uses the host's libm, including its fenv/errno outcome.
// Keep this host ABI out of target execution: runtime LLVM intrinsics have no
// language-level domain checks and do not demand pending inputs eagerly.
internal static class LlvmHostFloatingPoint
{
    internal static bool Available { get; } = FindHost();

    private static bool FindHost()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            return false;
        if (!NativeLibrary.TryLoad("libc.so.6", out nint libc)) return false;
        if (!NativeLibrary.TryLoad("libm.so.6", out nint libm))
        { NativeLibrary.Free(libc); return false; }
        bool available = NativeLibrary.TryGetExport(libc, "__errno_location", out _) &&
            NativeLibrary.TryGetExport(libm, "feclearexcept", out _) && NativeLibrary.TryGetExport(libm, "fetestexcept", out _);
        NativeLibrary.Free(libm);
        NativeLibrary.Free(libc);
        return available;
    }
    internal static bool Supports(LlvmIntrinsicKind kind, int arity) => arity == 2 ? kind == LlvmIntrinsicKind.Pow :
        arity == 1 && (kind == LlvmIntrinsicKind.Log || kind == LlvmIntrinsicKind.Log2 || kind == LlvmIntrinsicKind.Log10 ||
        kind == LlvmIntrinsicKind.Exp || kind == LlvmIntrinsicKind.Exp2 || kind == LlvmIntrinsicKind.Exp10 ||
        kind == LlvmIntrinsicKind.Sin || kind == LlvmIntrinsicKind.Cos || kind == LlvmIntrinsicKind.Sinh ||
        kind == LlvmIntrinsicKind.Cosh || kind == LlvmIntrinsicKind.ATan || kind == LlvmIntrinsicKind.Sqrt);

    internal static bool TryEvaluate(LlvmIntrinsicKind kind, double a, double b, out double result)
    {
        int all = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 0x3d : 0x1f;
        int inexact = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 0x20 : 0x10;
        ClearExceptions(all);
        nint errno = ErrnoLocation();
        Marshal.WriteInt32(errno, 0);
        if (kind == LlvmIntrinsicKind.Pow) result = Pow(a, b);
        else if (kind == LlvmIntrinsicKind.Log) result = Log(a);
        else if (kind == LlvmIntrinsicKind.Log2) result = Log2(a);
        else if (kind == LlvmIntrinsicKind.Log10) result = Log10(a);
        else if (kind == LlvmIntrinsicKind.Exp) result = Exp(a);
        else if (kind == LlvmIntrinsicKind.Exp2) result = Pow(2, a);
        else if (kind == LlvmIntrinsicKind.Exp10) result = Pow(10, a);
        else if (kind == LlvmIntrinsicKind.Sin) result = Sin(a);
        else if (kind == LlvmIntrinsicKind.Cos) result = Cos(a);
        else if (kind == LlvmIntrinsicKind.Sinh) result = Sinh(a);
        else if (kind == LlvmIntrinsicKind.Cosh) result = Cosh(a);
        else if (kind == LlvmIntrinsicKind.ATan) result = ATan(a);
        else result = Sqrt(a);
        int error = Marshal.ReadInt32(errno);
        if (error is not (33 or 34) && TestExceptions(all & ~inexact) == 0) return true;
        ClearExceptions(all);
        Marshal.WriteInt32(errno, 0);
        return false;
    }

    [DllImport("libc.so.6", EntryPoint = "__errno_location")] private static extern nint ErrnoLocation();
    [DllImport("libm.so.6", EntryPoint = "feclearexcept")] private static extern int ClearExceptions(int exceptions);
    [DllImport("libm.so.6", EntryPoint = "fetestexcept")] private static extern int TestExceptions(int exceptions);
    [DllImport("libm.so.6", EntryPoint = "pow")] private static extern double Pow(double a, double b);
    [DllImport("libm.so.6", EntryPoint = "log")] private static extern double Log(double value);
    [DllImport("libm.so.6", EntryPoint = "log2")] private static extern double Log2(double value);
    [DllImport("libm.so.6", EntryPoint = "log10")] private static extern double Log10(double value);
    [DllImport("libm.so.6", EntryPoint = "exp")] private static extern double Exp(double value);
    [DllImport("libm.so.6", EntryPoint = "sin")] private static extern double Sin(double value);
    [DllImport("libm.so.6", EntryPoint = "cos")] private static extern double Cos(double value);
    [DllImport("libm.so.6", EntryPoint = "sinh")] private static extern double Sinh(double value);
    [DllImport("libm.so.6", EntryPoint = "cosh")] private static extern double Cosh(double value);
    [DllImport("libm.so.6", EntryPoint = "atan")] private static extern double ATan(double value);
    [DllImport("libm.so.6", EntryPoint = "sqrt")] private static extern double Sqrt(double value);
}
