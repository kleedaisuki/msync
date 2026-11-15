using Msync.Core.Llvm;

namespace Msync.Core.Native;

// Stable target operation identities, not LLVM's generated intrinsic IDs.
public enum NativeMachineIntrinsic : byte
{
    SMin, SMax, UMin, UMax, SCmp, UCmp,
    CTPop, CTLZ, CTTZ, BSwap, BitReverse, Abs, FShl, FShr,
    UAddSat, SAddSat, USubSat, SSubSat, UShlSat, SShlSat, UMulH, SMulH,
    UAddOverflowValue, UAddOverflowFlag, SAddOverflowValue, SAddOverflowFlag,
    USubOverflowValue, USubOverflowFlag, SSubOverflowValue, SSubOverflowFlag,
    UMulOverflowValue, UMulOverflowFlag, SMulOverflowValue, SMulOverflowFlag
}

public static partial class NativeMachineOperations
{
    public static bool IsIntegerBinary(LlvmOpcode opcode) => opcode is LlvmOpcode.Add or LlvmOpcode.Sub or LlvmOpcode.Mul
        or LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem
        or LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr;
    public static bool IsFloatingBinary(LlvmOpcode opcode) => opcode is LlvmOpcode.FAdd or LlvmOpcode.FSub
        or LlvmOpcode.FMul or LlvmOpcode.FDiv or LlvmOpcode.FRem;
    public static bool IsCast(LlvmOpcode opcode) => opcode is LlvmOpcode.Trunc or LlvmOpcode.ZExt or LlvmOpcode.SExt
        or LlvmOpcode.FPToUI or LlvmOpcode.FPToSI or LlvmOpcode.UIToFP or LlvmOpcode.SIToFP
        or LlvmOpcode.FPTrunc or LlvmOpcode.FPExt or LlvmOpcode.BitCast;
    public static int ArgumentCount(NativeMachineIntrinsic operation) => operation switch
    {
        NativeMachineIntrinsic.CTPop or NativeMachineIntrinsic.BSwap or NativeMachineIntrinsic.BitReverse => 1,
        NativeMachineIntrinsic.FShl or NativeMachineIntrinsic.FShr => 3,
        _ => 2
    };
    public static bool IsOverflowFlag(NativeMachineIntrinsic operation) => operation is
        NativeMachineIntrinsic.UAddOverflowFlag or NativeMachineIntrinsic.SAddOverflowFlag or
        NativeMachineIntrinsic.USubOverflowFlag or NativeMachineIntrinsic.SSubOverflowFlag or
        NativeMachineIntrinsic.UMulOverflowFlag or NativeMachineIntrinsic.SMulOverflowFlag;

    public static NativeValue Binary(LlvmOpcode opcode, NativeValue left, NativeValue right, NativeType resultType)
    {
        if (IsIntegerBinary(opcode))
        {
            LlvmAPInt a = left.AsMachineInteger(), b = right.AsMachineInteger();
            LlvmAPInt result = opcode switch
            {
                LlvmOpcode.Add => a + b, LlvmOpcode.Sub => a - b, LlvmOpcode.Mul => a * b,
                LlvmOpcode.UDiv => a.UDiv(b), LlvmOpcode.SDiv => a.SDiv(b),
                LlvmOpcode.URem => a.URem(b), LlvmOpcode.SRem => a.SRem(b),
                LlvmOpcode.And => a & b, LlvmOpcode.Or => a | b, LlvmOpcode.Xor => a ^ b,
                LlvmOpcode.Shl => a.Shl(b), LlvmOpcode.LShr => a.LShr(b), LlvmOpcode.AShr => a.AShr(b),
                _ => throw new InvalidOperationException("Unknown integer machine operation.")
            };
            return IntegerResult(result, resultType);
        }
        LlvmAPFloat x = left.AsMachineFloat(), y = right.AsMachineFloat();
        LlvmAPFloat value = opcode switch
        {
            LlvmOpcode.FAdd => x.Add(y).Value, LlvmOpcode.FSub => x.Subtract(y).Value,
            LlvmOpcode.FMul => x.Multiply(y).Value, LlvmOpcode.FDiv => x.Divide(y).Value,
            LlvmOpcode.FRem => x.Mod(y).Value,
            _ => throw new InvalidOperationException("Unknown floating machine operation.")
        };
        return FloatingResult(value, resultType);
    }

    public static NativeValue Compare(LlvmCmpPredicate predicate, NativeValue left, NativeValue right)
    {
        if (predicate >= LlvmCmpPredicate.ICmpEq)
        {
            LlvmAPInt a = left.AsMachineInteger(), b = right.AsMachineInteger();
            return NativeValue.FromBool(predicate switch
            {
                LlvmCmpPredicate.ICmpEq => a == b, LlvmCmpPredicate.ICmpNe => a != b,
                LlvmCmpPredicate.ICmpUlt => a.Ult(b), LlvmCmpPredicate.ICmpUle => a.Ule(b),
                LlvmCmpPredicate.ICmpUgt => a.Ugt(b), LlvmCmpPredicate.ICmpUge => a.Uge(b),
                LlvmCmpPredicate.ICmpSlt => a.Slt(b), LlvmCmpPredicate.ICmpSle => a.Sle(b),
                LlvmCmpPredicate.ICmpSgt => a.Sgt(b), LlvmCmpPredicate.ICmpSge => a.Sge(b),
                _ => throw new InvalidOperationException("Unknown integer machine predicate.")
            });
        }
        LlvmFloatCompare comparison = left.AsMachineFloat().Compare(right.AsMachineFloat());
        bool unordered = comparison == LlvmFloatCompare.Unordered;
        bool equal = comparison == LlvmFloatCompare.Equal;
        bool less = comparison == LlvmFloatCompare.LessThan;
        bool greater = comparison == LlvmFloatCompare.GreaterThan;
        return NativeValue.FromBool(predicate switch
        {
            LlvmCmpPredicate.FCmpFalse => false, LlvmCmpPredicate.FCmpTrue => true,
            LlvmCmpPredicate.FCmpOeq => equal, LlvmCmpPredicate.FCmpOgt => greater,
            LlvmCmpPredicate.FCmpOge => equal || greater, LlvmCmpPredicate.FCmpOlt => less,
            LlvmCmpPredicate.FCmpOle => equal || less, LlvmCmpPredicate.FCmpOne => !unordered && !equal,
            LlvmCmpPredicate.FCmpOrd => !unordered, LlvmCmpPredicate.FCmpUno => unordered,
            LlvmCmpPredicate.FCmpUeq => unordered || equal, LlvmCmpPredicate.FCmpUgt => unordered || greater,
            LlvmCmpPredicate.FCmpUge => unordered || equal || greater, LlvmCmpPredicate.FCmpUlt => unordered || less,
            LlvmCmpPredicate.FCmpUle => unordered || equal || less, LlvmCmpPredicate.FCmpUne => !equal,
            _ => throw new InvalidOperationException("Unknown floating machine predicate.")
        });
    }

    public static NativeValue Unary(LlvmOpcode opcode, NativeValue value, NativeType resultType) => opcode switch
    {
        LlvmOpcode.FNeg => FloatingResult(value.AsMachineFloat().ChangeSign(), resultType),
        _ => throw new InvalidOperationException("Unknown scalar machine unary operation.")
    };

    public static NativeValue Cast(LlvmOpcode opcode, NativeValue value, NativeType resultType)
    {
        uint width = resultType.Machine?.BitWidth ?? 1;
        LlvmAPFloatSemantics semantics = resultType.Machine?.FloatFormat ?? LlvmAPFloatSemantics.IEEEdouble;
        return opcode switch
        {
            LlvmOpcode.Trunc => IntegerResult(value.AsMachineInteger().Trunc(width), resultType),
            LlvmOpcode.ZExt => IntegerResult(value.AsMachineInteger().ZExt(width), resultType),
            LlvmOpcode.SExt => IntegerResult(value.AsMachineInteger().SExt(width), resultType),
            LlvmOpcode.FPToUI => IntegerResult(value.AsMachineFloat().ConvertToInteger(width, false).Value, resultType),
            LlvmOpcode.FPToSI => IntegerResult(value.AsMachineFloat().ConvertToInteger(width, true).Value, resultType),
            LlvmOpcode.UIToFP => FloatingResult(LlvmAPFloat.FromAPInt(semantics, value.AsMachineInteger(), false).Value, resultType),
            LlvmOpcode.SIToFP => FloatingResult(LlvmAPFloat.FromAPInt(semantics, value.AsMachineInteger(), true).Value, resultType),
            LlvmOpcode.FPTrunc or LlvmOpcode.FPExt => FloatingResult(value.AsMachineFloat().Convert(semantics).Value, resultType),
            LlvmOpcode.BitCast => BitCast(value, resultType),
            _ => throw new InvalidOperationException("Unknown scalar machine cast.")
        };
    }

    private static NativeValue BitCast(NativeValue value, NativeType resultType)
    {
        LlvmAPInt bits = value.Kind is NativeKind.Bool or NativeKind.MachineInteger
            ? value.AsMachineInteger() : value.AsMachineFloat().BitcastToAPInt();
        return resultType.Machine?.IsInteger == true || !resultType.IsMachine && resultType.Kind == NativeKind.Bool
            ? IntegerResult(bits, resultType)
            : FloatingResult(new(resultType.Machine?.FloatFormat ?? LlvmAPFloatSemantics.IEEEdouble, bits), resultType);
    }

    internal static NativeValue IntegerResult(LlvmAPInt bits, NativeType type) => !type.IsMachine && type.Kind == NativeKind.Bool
        ? NativeValue.FromBool(!bits.IsZero) : NativeValue.FromMachineInteger(bits, type.Machine);
    internal static NativeValue FloatingResult(LlvmAPFloat value, NativeType type) => type.IsMachine
        ? NativeValue.FromMachineFloat(value, type.Machine)
        : NativeValue.FromNumber(BitConverter.UInt64BitsToDouble(value.BitcastToAPInt().GetZExtValue()));
}
