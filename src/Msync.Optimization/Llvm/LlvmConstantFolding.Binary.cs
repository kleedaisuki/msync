// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/ConstantFold.cpp
// ConstantFoldUnaryInstruction/BinaryInstruction, scalar represented slice;
// IR/Constants.cpp ConstantExpr::getBinOpIdentity/getBinOpAbsorber.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldUnaryOpOperand(LlvmOpcode opcode, LlvmConstant operand,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.unary.queries");
        Debug.Assert(opcode == LlvmOpcode.FNeg);
        if (operand.Type.IsVectorTy()) { Count(metrics, "constant_fold.unported.vector"); return null; }
        if (operand is LlvmUndefValue) return operand;
        return operand is LlvmConstantFP floating ? LlvmConstantFP.Get(operand.Type, floating.Value.ChangeSign()) : null;
    }

    public static LlvmConstant? ConstantFoldBinaryOpOperands(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "constant_fold.binary.queries");
        LlvmConstant? result = FoldSymbolicBinary(opcode, left, right, layout, metrics);
        result ??= LlvmConstantExpr.IsDesirableBinOp(opcode)
            ? LlvmConstantExpr.Get(opcode, left, right) : FoldBinary(opcode, left, right, layout, metrics);
        RecordResult(metrics, "constant_fold.binary", result);
        return result;
    }
    private static LlvmConstant? FoldBinary(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right,
        LlvmDataLayout layout, Dictionary<string, ulong>? metrics)
    {
        Debug.Assert(ReferenceEquals(left.Type, right.Type));
        if (left.Type.IsVectorTy()) return FoldVectorBinary(opcode, left, right, layout, metrics);
        LlvmType type = left.Type;
        LlvmConstant? identity = BinOpIdentity(opcode, type, false);
        if (identity is not null)
        {
            if (ReferenceEquals(left, identity)) { Count(metrics, "constant_fold.binary.left_identity"); return right; }
            if (ReferenceEquals(right, identity)) { Count(metrics, "constant_fold.binary.right_identity"); return left; }
        }
        else if (ReferenceEquals(right, BinOpIdentity(opcode, type, true))) { Count(metrics, "constant_fold.binary.rhs_only_identity"); return left; }
        if (left is LlvmPoisonValue || right is LlvmPoisonValue) return LlvmPoisonValue.Get(type);
        if (left is LlvmUndefValue || right is LlvmUndefValue) return FoldUndefBinary(opcode, left, right);
        if (right is LlvmConstantInt rhs)
        {
            if (ReferenceEquals(right, BinOpAbsorber(opcode, type, false))) { Count(metrics, "constant_fold.binary.rhs_absorber"); return right; }
            if (opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem)
            {
                if (rhs.IsZero) return LlvmPoisonValue.Get(type);
                if (rhs.IsOne && opcode is LlvmOpcode.URem or LlvmOpcode.SRem) return Null(type);
            }
        }
        else if (left is LlvmConstantInt && IsCommutative(opcode))
            return ConstantFoldBinaryOpOperands(opcode, right, left, layout, metrics);
        if (left is LlvmConstantInt lhs)
        {
            if (right is LlvmConstantInt rhsInteger) return FoldIntegerBinary(opcode, lhs, rhsInteger);
            if (ReferenceEquals(left, BinOpAbsorber(opcode, type, true))) { Count(metrics, "constant_fold.binary.lhs_absorber"); return left; }
        }
        else if (left is LlvmConstantFP fpLeft && right is LlvmConstantFP fpRight)
        {
            LlvmAPFloat a = fpLeft.Value, b = fpRight.Value;
            LlvmAPFloat? result = opcode switch
            {
                LlvmOpcode.FAdd => a.Add(b).Value, LlvmOpcode.FSub => a.Subtract(b).Value,
                LlvmOpcode.FMul => a.Multiply(b).Value, LlvmOpcode.FDiv => a.Divide(b).Value,
                LlvmOpcode.FRem => a.Mod(b).Value, _ => null
            };
            return result is { } value ? LlvmConstantFP.Get(type, value) : null;
        }
        return null;
    }

    private static LlvmConstant FoldUndefBinary(LlvmOpcode opcode, LlvmConstant left, LlvmConstant right)
    {
        LlvmType type = left.Type;
        bool both = left is LlvmUndefValue && right is LlvmUndefValue;
        switch (opcode)
        {
            case LlvmOpcode.Xor:
                return both ? Null(type) : LlvmUndefValue.Get(type);
            case LlvmOpcode.Add: case LlvmOpcode.Sub: return LlvmUndefValue.Get(type);
            case LlvmOpcode.And: return both ? left : Null(type);
            case LlvmOpcode.Mul:
                if (both) return left;
                if ((left is LlvmConstantInt a && (a.Value.GetWord(0) & 1) != 0) ||
                    (right is LlvmConstantInt b && (b.Value.GetWord(0) & 1) != 0)) return LlvmUndefValue.Get(type);
                return Null(type);
            case LlvmOpcode.SDiv: case LlvmOpcode.UDiv: case LlvmOpcode.SRem: case LlvmOpcode.URem:
                return right is LlvmUndefValue || right.IsNullValue ? LlvmPoisonValue.Get(type) : Null(type);
            case LlvmOpcode.Or: return both ? left : AllOnes(type);
            case LlvmOpcode.LShr: case LlvmOpcode.AShr: case LlvmOpcode.Shl:
                return right is LlvmUndefValue ? LlvmPoisonValue.Get(type) : Null(type);
            case LlvmOpcode.FSub:
                if (left is LlvmConstantFP { Value.IsZero: true, Value.IsNegative: true } && right is LlvmUndefValue) return right;
                goto case LlvmOpcode.FAdd;
            case LlvmOpcode.FAdd: case LlvmOpcode.FMul: case LlvmOpcode.FDiv: case LlvmOpcode.FRem:
                return both ? left : LlvmConstantFP.Get(type, LlvmAPFloat.FromBits(0x7ff8000000000000UL));
            default: throw new UnreachableException();
        }
    }
    private static LlvmConstant? FoldIntegerBinary(LlvmOpcode opcode, LlvmConstantInt left, LlvmConstantInt right)
    {
        LlvmAPInt a = left.Value, b = right.Value;
        LlvmType type = left.Type;
        if (opcode is LlvmOpcode.SDiv or LlvmOpcode.SRem && b.IsAllOnes && a.IsMinSignedValue)
            return LlvmPoisonValue.Get(type);
        if (opcode is LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr && b.GetLimitedValue() >= a.BitWidth)
            return LlvmPoisonValue.Get(type);
        LlvmAPInt? result = opcode switch
        {
            LlvmOpcode.Add => a + b, LlvmOpcode.Sub => a - b, LlvmOpcode.Mul => a * b,
            LlvmOpcode.UDiv => a.UDiv(b), LlvmOpcode.SDiv => a.SDiv(b),
            LlvmOpcode.URem => a.URem(b), LlvmOpcode.SRem => a.SRem(b),
            LlvmOpcode.And => a & b, LlvmOpcode.Or => a | b, LlvmOpcode.Xor => a ^ b,
            LlvmOpcode.Shl => a.Shl(b), LlvmOpcode.LShr => a.LShr(b), LlvmOpcode.AShr => a.AShr(b), _ => null
        };
        return result is { } value ? LlvmConstantInt.Get(type.Context, value) : null;
    }
    private static bool IsCommutative(LlvmOpcode opcode) => opcode is LlvmOpcode.Add or LlvmOpcode.Mul or
        LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor or LlvmOpcode.FAdd or LlvmOpcode.FMul;
    internal static LlvmConstant? BinOpIdentity(LlvmOpcode opcode, LlvmType type, bool allowRhs, bool noSignedZeros = false) =>
        LlvmConstantExpr.GetBinOpIdentity(opcode, type, allowRhs, noSignedZeros);
    internal static LlvmConstant? BinOpAbsorber(LlvmOpcode opcode, LlvmType type, bool allowLhs) =>
        LlvmConstantExpr.GetBinOpAbsorber(opcode, type, allowLhs);
    private static LlvmConstant Null(LlvmType type) => LlvmConstant.GetNullValue(type);
    private static LlvmConstant AllOnes(LlvmType type) => LlvmConstantExpr.GetBinOpIdentity(LlvmOpcode.And, type)!;
    private static void RecordResult(Dictionary<string, ulong>? metrics, string phase, LlvmConstant? result)
    {
        if (metrics is null) return;
        Count(metrics, phase + (result is null ? ".declined" : ".folded"));
        if (result is not null) Count(metrics, phase + (result switch
        {
            LlvmPoisonValue => ".poison", LlvmUndefValue => ".undef", LlvmConstantInt => ".integer",
            LlvmConstantFP => ".fp", LlvmConstantByte => ".byte", LlvmConstantExpr => ".expression",
            LlvmConstantVector or LlvmConstantDataVector => ".vector", _ => ".other"
        }));
    }
    private static void Count(Dictionary<string, ulong>? metrics, string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
