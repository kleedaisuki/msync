using Msync.Core.Llvm;

namespace Msync.Core.Native;

public static partial class NativeMachineOperations
{
    public static NativeValue Intrinsic(NativeMachineIntrinsic operation, NativeValue first, NativeValue second,
        NativeValue third, NativeType resultType)
    {
        LlvmAPInt a = first.AsMachineInteger();
        if (operation is NativeMachineIntrinsic.CTPop or NativeMachineIntrinsic.CTLZ or NativeMachineIntrinsic.CTTZ)
            return IntegerResult(new(a.BitWidth, operation switch
            {
                NativeMachineIntrinsic.CTPop => a.PopCount(), NativeMachineIntrinsic.CTLZ => a.CountLeadingZeros(),
                _ => a.CountTrailingZeros()
            }), resultType);
        if (operation is NativeMachineIntrinsic.Abs or NativeMachineIntrinsic.BSwap or NativeMachineIntrinsic.BitReverse)
            return IntegerResult(operation switch
            {
                NativeMachineIntrinsic.Abs => a.IsNegative ? -a : a,
                NativeMachineIntrinsic.BSwap => a.ByteSwap(), _ => a.ReverseBits()
            }, resultType);
        LlvmAPInt b = second.AsMachineInteger();
        if (operation is NativeMachineIntrinsic.FShl or NativeMachineIntrinsic.FShr)
            return IntegerResult(FunnelShift(a, b, third.AsMachineInteger(), operation == NativeMachineIntrinsic.FShr), resultType);
        if (operation is NativeMachineIntrinsic.SCmp or NativeMachineIntrinsic.UCmp)
        {
            int comparison = operation == NativeMachineIntrinsic.SCmp ? (a.Slt(b) ? -1 : a.Sgt(b) ? 1 : 0)
                : a.Ult(b) ? -1 : a.Ugt(b) ? 1 : 0;
            return IntegerResult(new(resultType.Machine?.BitWidth ?? 1, unchecked((ulong)(long)comparison), isSigned: true), resultType);
        }
        if (operation is >= NativeMachineIntrinsic.UAddOverflowValue and <= NativeMachineIntrinsic.SMulOverflowFlag)
            return WithOverflow(operation, a, b, resultType);
        LlvmAPInt result = operation switch
        {
            NativeMachineIntrinsic.SMin => a.Slt(b) ? a : b, NativeMachineIntrinsic.SMax => a.Sgt(b) ? a : b,
            NativeMachineIntrinsic.UMin => a.Ult(b) ? a : b, NativeMachineIntrinsic.UMax => a.Ugt(b) ? a : b,
            NativeMachineIntrinsic.UAddSat => a.UAddSat(b), NativeMachineIntrinsic.SAddSat => a.SAddSat(b),
            NativeMachineIntrinsic.USubSat => a.USubSat(b), NativeMachineIntrinsic.SSubSat => a.SSubSat(b),
            NativeMachineIntrinsic.UShlSat => a.UShlSat(b), NativeMachineIntrinsic.SShlSat => a.SShlSat(b),
            NativeMachineIntrinsic.UMulH => (a.ZExt(a.BitWidth * 2) * b.ZExt(b.BitWidth * 2)).ExtractBits(a.BitWidth, a.BitWidth),
            NativeMachineIntrinsic.SMulH => (a.SExt(a.BitWidth * 2) * b.SExt(b.BitWidth * 2)).ExtractBits(a.BitWidth, a.BitWidth),
            _ => throw new InvalidOperationException("Unknown integer machine intrinsic.")
        };
        return IntegerResult(result, resultType);
    }

    private static LlvmAPInt FunnelShift(LlvmAPInt first, LlvmAPInt second, LlvmAPInt shift, bool right)
    {
        uint width = first.BitWidth;
        uint amount = (uint)shift.URem(new(width, width)).GetZExtValue();
        if (amount == 0) return right ? second : first;
        return right ? first.Shl(width - amount) | second.LShr(amount)
            : first.Shl(amount) | second.LShr(width - amount);
    }

    private static NativeValue WithOverflow(NativeMachineIntrinsic operation, LlvmAPInt first, LlvmAPInt second,
        NativeType resultType)
    {
        bool overflow;
        LlvmAPInt result = operation switch
        {
            NativeMachineIntrinsic.UAddOverflowValue or NativeMachineIntrinsic.UAddOverflowFlag => first.UAddOverflow(second, out overflow),
            NativeMachineIntrinsic.SAddOverflowValue or NativeMachineIntrinsic.SAddOverflowFlag => first.SAddOverflow(second, out overflow),
            NativeMachineIntrinsic.USubOverflowValue or NativeMachineIntrinsic.USubOverflowFlag => first.USubOverflow(second, out overflow),
            NativeMachineIntrinsic.SSubOverflowValue or NativeMachineIntrinsic.SSubOverflowFlag => first.SSubOverflow(second, out overflow),
            NativeMachineIntrinsic.UMulOverflowValue or NativeMachineIntrinsic.UMulOverflowFlag => first.UMulOverflow(second, out overflow),
            _ => first.SMulOverflow(second, out overflow)
        };
        return IsOverflowFlag(operation) ? NativeValue.FromBool(overflow) : IntegerResult(result, resultType);
    }
}
