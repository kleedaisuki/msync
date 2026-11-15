// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ValueTracking.cpp6858-6986.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool CanIgnoreSignBitOfZero(LlvmUse use, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.ignore_zero_sign.queries");
        var user = (LlvmInstruction)use.User;
        bool ignored = user.IsFloatingMathOperator && user is ILlvmFastMathInstruction floating &&
            (floating.FastMathFlags & LlvmFastMathFlags.NoSignedZeros) != 0;
        if (!ignored) ignored = user.Opcode switch
        {
            LlvmOpcode.FPToSI or LlvmOpcode.FPToUI or LlvmOpcode.FCmp => true,
            LlvmOpcode.FRem => use.OperandNo == 1,
            LlvmOpcode.Call when user is LlvmCallInstruction call => CallIgnoresZeroSign(call, use.OperandNo),
            _ => false
        };
        Count(metrics, ignored ? "value_tracking.ignore_zero_sign.yes" : "value_tracking.ignore_zero_sign.no");
        return ignored;
    }

    private static bool CallIgnoresZeroSign(LlvmCallInstruction call, int operand)
    {
        return call.CalledFunction?.IntrinsicDescriptor?.Kind.Name switch
        {
            "llvm.fabs" or "llvm.exp" or "llvm.exp2" or "llvm.exp10" or "llvm.log" or
                "llvm.log2" or "llvm.log10" or "llvm.cos" or "llvm.acos" or "llvm.cosh" or
                "llvm.fptosi.sat" or "llvm.fptoui.sat" or "llvm.lround" or "llvm.llround" or
                "llvm.lrint" or "llvm.llrint" => true,
            "llvm.copysign" => operand == 0,
            "llvm.pow" => operand == 1,
            "llvm.is.fpclass" => ((LlvmFPClassTest)((LlvmConstantInt)call.GetArgument(1)).GetZExtValue() &
                LlvmFPClassTest.Zero) is LlvmFPClassTest.Zero or LlvmFPClassTest.None,
            _ => false
        };
    }

    public static bool CanIgnoreSignBitOfNaN(LlvmUse use, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.ignore_nan_sign.queries");
        var user = (LlvmInstruction)use.User;
        bool ignored = user.IsFloatingMathOperator && user is ILlvmFastMathInstruction floating &&
            (floating.FastMathFlags & LlvmFastMathFlags.NoNaNs) != 0;
        if (!ignored) ignored = user.Opcode switch
        {
            LlvmOpcode.FPToSI or LlvmOpcode.FPToUI or LlvmOpcode.FAdd or LlvmOpcode.FSub or
                LlvmOpcode.FMul or LlvmOpcode.FDiv or LlvmOpcode.FRem or LlvmOpcode.FPTrunc or
                LlvmOpcode.FPExt or LlvmOpcode.FCmp => true,
            LlvmOpcode.Ret => (user.Parent!.Parent!.ReturnAttributes.NoFPClass & LlvmFPClassTest.NaN) != 0,
            LlvmOpcode.Call or LlvmOpcode.Invoke when user is LlvmCallBase call => CallIgnoresNaNSign(call, use.OperandNo),
            _ => false
        };
        Count(metrics, ignored ? "value_tracking.ignore_nan_sign.yes" : "value_tracking.ignore_nan_sign.no");
        return ignored;
    }

    private static bool CallIgnoresNaNSign(LlvmCallBase call, int operand)
    {
        if (call.CalledFunction?.IntrinsicDescriptor is { } intrinsic)
            return intrinsic.Kind.Name switch
            {
                "llvm.fabs" or "llvm.maxnum" or "llvm.minnum" or "llvm.maximum" or "llvm.minimum" or
                    "llvm.maximumnum" or "llvm.minimumnum" or "llvm.canonicalize" or "llvm.fma" or
                    "llvm.fmuladd" or "llvm.sqrt" or "llvm.pow" or "llvm.powi" or "llvm.fptoui.sat" or
                    "llvm.fptosi.sat" or "llvm.is.fpclass" => true,
                "llvm.copysign" => operand == 0,
                _ => false
            };
        return (call.GetParamNoFPClass(operand) & LlvmFPClassTest.NaN) != 0;
    }
}
