// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/InstrTypes.h,
// IR/Instructions.h and corresponding lib/IR sources.
using System.Diagnostics;

namespace Msync.Core.Llvm;

public sealed class LlvmUnaryOperator : LlvmInstruction, ILlvmFastMathInstruction
{
    public LlvmUnaryOperator(LlvmValue operand) : base(operand.Type, LlvmOpcode.FNeg, [operand])
        => Debug.Assert(operand.Type.GetScalarType().IsFloatingPointTy());
    public LlvmValue Operand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmFastMathFlags FastMathFlags { get; set; }
    protected override LlvmInstruction CloneImpl() => new LlvmUnaryOperator(Operand) { FastMathFlags = FastMathFlags };
}

public sealed class LlvmBinaryOperator : LlvmInstruction, ILlvmFastMathInstruction
{
    public LlvmBinaryOperator(LlvmOpcode opcode, LlvmValue left, LlvmValue right)
        : base(left.Type, opcode, [left, right])
    {
        Debug.Assert(opcode is >= LlvmOpcode.Add and <= LlvmOpcode.Xor);
        Debug.Assert(ReferenceEquals(left.Type, right.Type));
    }
    public LlvmValue Left { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue Right { get => GetOperand(1)!; set => SetOperand(1, value); }
    public bool HasNoUnsignedWrap { get; set; }
    public bool HasNoSignedWrap { get; set; }
    public bool IsExact { get; set; }
    public bool IsDisjoint { get; set; }
    public LlvmFastMathFlags FastMathFlags { get; set; }
    public bool IsCommutative => Opcode is LlvmOpcode.Add or LlvmOpcode.FAdd or LlvmOpcode.Mul
        or LlvmOpcode.FMul or LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor;
    public bool IsAssociative => Opcode switch
    {
        LlvmOpcode.Add or LlvmOpcode.Mul or LlvmOpcode.And or LlvmOpcode.Or or LlvmOpcode.Xor => true,
        LlvmOpcode.FMul => FastMathFlags.HasFlag(LlvmFastMathFlags.AllowReassoc),
        LlvmOpcode.FAdd => (FastMathFlags & (LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoSignedZeros))
            == (LlvmFastMathFlags.AllowReassoc | LlvmFastMathFlags.NoSignedZeros),
        _ => false
    };
    public void SwapOperands()
    {
        LlvmValue previous = Left; Left = Right; Right = previous;
    }
    protected override LlvmInstruction CloneImpl() => new LlvmBinaryOperator(Opcode, Left, Right)
    { HasNoUnsignedWrap = HasNoUnsignedWrap, HasNoSignedWrap = HasNoSignedWrap, IsExact = IsExact, IsDisjoint = IsDisjoint, FastMathFlags = FastMathFlags };
}

public sealed partial class LlvmCastInstruction : LlvmInstruction, ILlvmFastMathInstruction
{
    public LlvmCastInstruction(LlvmOpcode opcode, LlvmValue operand, LlvmType destination)
        : base(destination, opcode, [operand]) => Debug.Assert(opcode is >= LlvmOpcode.Trunc and <= LlvmOpcode.AddrSpaceCast);
    public LlvmValue Operand { get => GetOperand(0)!; set => SetOperand(0, value); }
    public bool HasNoUnsignedWrap { get; set; }
    public bool HasNoSignedWrap { get; set; }
    public bool IsNonNegative { get; set; }
    public bool HasNonNull { get; set; }
    public LlvmFastMathFlags FastMathFlags { get; set; }
    protected override LlvmInstruction CloneImpl() => new LlvmCastInstruction(Opcode, Operand, Type)
    { HasNoUnsignedWrap = HasNoUnsignedWrap, HasNoSignedWrap = HasNoSignedWrap, IsNonNegative = IsNonNegative, HasNonNull = HasNonNull, FastMathFlags = FastMathFlags };
}

public enum LlvmCmpPredicate : byte
{
    FCmpFalse = 0, FCmpOeq = 1, FCmpOgt = 2, FCmpOge = 3, FCmpOlt = 4, FCmpOle = 5,
    FCmpOne = 6, FCmpOrd = 7, FCmpUno = 8, FCmpUeq = 9, FCmpUgt = 10, FCmpUge = 11,
    FCmpUlt = 12, FCmpUle = 13, FCmpUne = 14, FCmpTrue = 15, BadFCmpPredicate = 16,
    ICmpEq = 32, ICmpNe = 33, ICmpUgt = 34, ICmpUge = 35, ICmpUlt = 36, ICmpUle = 37,
    ICmpSgt = 38, ICmpSge = 39, ICmpSlt = 40, ICmpSle = 41
}

public abstract class LlvmCmpInstruction : LlvmInstruction
{
    protected LlvmCmpInstruction(LlvmOpcode opcode, LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right)
        : base(ComparisonType(left.Type), opcode, [left, right])
    {
        Debug.Assert(ReferenceEquals(left.Type, right.Type));
        Predicate = predicate;
    }
    public LlvmCmpPredicate Predicate { get; set; }
    public bool IsCommutative => Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe or
        LlvmCmpPredicate.FCmpFalse or LlvmCmpPredicate.FCmpTrue or LlvmCmpPredicate.FCmpOeq or
        LlvmCmpPredicate.FCmpOne or LlvmCmpPredicate.FCmpUeq or LlvmCmpPredicate.FCmpUne or
        LlvmCmpPredicate.FCmpOrd or LlvmCmpPredicate.FCmpUno;
    public LlvmValue Left { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue Right { get => GetOperand(1)!; set => SetOperand(1, value); }
    public void SwapOperands()
    {
        LlvmValue previous = Left; Left = Right; Right = previous;
        Predicate = GetSwappedPredicate(Predicate);
    }
    public static LlvmCmpPredicate GetSwappedPredicate(LlvmCmpPredicate predicate) => predicate switch
    {
        LlvmCmpPredicate.FCmpOgt => LlvmCmpPredicate.FCmpOlt,
        LlvmCmpPredicate.FCmpOge => LlvmCmpPredicate.FCmpOle,
        LlvmCmpPredicate.FCmpOlt => LlvmCmpPredicate.FCmpOgt,
        LlvmCmpPredicate.FCmpOle => LlvmCmpPredicate.FCmpOge,
        LlvmCmpPredicate.FCmpUgt => LlvmCmpPredicate.FCmpUlt,
        LlvmCmpPredicate.FCmpUge => LlvmCmpPredicate.FCmpUle,
        LlvmCmpPredicate.FCmpUlt => LlvmCmpPredicate.FCmpUgt,
        LlvmCmpPredicate.FCmpUle => LlvmCmpPredicate.FCmpUge,
        LlvmCmpPredicate.ICmpUgt => LlvmCmpPredicate.ICmpUlt,
        LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUle,
        LlvmCmpPredicate.ICmpUlt => LlvmCmpPredicate.ICmpUgt,
        LlvmCmpPredicate.ICmpUle => LlvmCmpPredicate.ICmpUge,
        LlvmCmpPredicate.ICmpSgt => LlvmCmpPredicate.ICmpSlt,
        LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSle,
        LlvmCmpPredicate.ICmpSlt => LlvmCmpPredicate.ICmpSgt,
        LlvmCmpPredicate.ICmpSle => LlvmCmpPredicate.ICmpSge,
        _ => predicate
    };
    public static LlvmCmpPredicate GetInversePredicate(LlvmCmpPredicate predicate) =>
        (byte)predicate <= 15 ? (LlvmCmpPredicate)((byte)predicate ^ 15) : predicate switch
        {
            LlvmCmpPredicate.ICmpEq => LlvmCmpPredicate.ICmpNe,
            LlvmCmpPredicate.ICmpNe => LlvmCmpPredicate.ICmpEq,
            LlvmCmpPredicate.ICmpUgt => LlvmCmpPredicate.ICmpUle,
            LlvmCmpPredicate.ICmpUge => LlvmCmpPredicate.ICmpUlt,
            LlvmCmpPredicate.ICmpUlt => LlvmCmpPredicate.ICmpUge,
            LlvmCmpPredicate.ICmpUle => LlvmCmpPredicate.ICmpUgt,
            LlvmCmpPredicate.ICmpSgt => LlvmCmpPredicate.ICmpSle,
            LlvmCmpPredicate.ICmpSge => LlvmCmpPredicate.ICmpSlt,
            LlvmCmpPredicate.ICmpSlt => LlvmCmpPredicate.ICmpSge,
            LlvmCmpPredicate.ICmpSle => LlvmCmpPredicate.ICmpSgt,
            _ => throw new ArgumentOutOfRangeException(nameof(predicate))
        };
}

public sealed class LlvmICmpInstruction : LlvmCmpInstruction
{
    public LlvmICmpInstruction(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right)
        : base(LlvmOpcode.ICmp, predicate, left, right) => Debug.Assert((byte)predicate is >= 32 and <= 41);
    public LlvmICmpInstruction(LlvmCmpPredicateInfo predicate, LlvmValue left, LlvmValue right)
        : this(predicate.Predicate, left, right) => SameSign = predicate.HasSameSign;
    public LlvmCmpPredicateInfo GetCmpPredicate() => new(Predicate, SameSign);
    public bool SameSign { get; set; }
    protected override LlvmInstruction CloneImpl() => new LlvmICmpInstruction(Predicate, Left, Right) { SameSign = SameSign };
}

public sealed class LlvmFCmpInstruction : LlvmCmpInstruction, ILlvmFastMathInstruction
{
    public LlvmFCmpInstruction(LlvmCmpPredicate predicate, LlvmValue left, LlvmValue right)
        : base(LlvmOpcode.FCmp, predicate, left, right) => Debug.Assert((byte)predicate <= 15);
    public LlvmFastMathFlags FastMathFlags { get; set; }
    protected override LlvmInstruction CloneImpl() => new LlvmFCmpInstruction(Predicate, Left, Right) { FastMathFlags = FastMathFlags };
}

public sealed class LlvmSelectInstruction : LlvmInstruction, ILlvmFastMathInstruction
{
    public LlvmSelectInstruction(LlvmValue condition, LlvmValue whenTrue, LlvmValue whenFalse)
        : base(whenTrue.Type, LlvmOpcode.Select, [condition, whenTrue, whenFalse])
    {
        Debug.Assert(ReferenceEquals(whenTrue.Type, whenFalse.Type));
        Debug.Assert(condition.Type.GetScalarType().IsIntegerTy(1));
    }
    public LlvmValue Condition { get => GetOperand(0)!; set => SetOperand(0, value); }
    public LlvmValue TrueValue { get => GetOperand(1)!; set => SetOperand(1, value); }
    public LlvmValue FalseValue { get => GetOperand(2)!; set => SetOperand(2, value); }
    public LlvmFastMathFlags FastMathFlags { get; set; }
    protected override LlvmInstruction CloneImpl() => new LlvmSelectInstruction(Condition, TrueValue, FalseValue) { FastMathFlags = FastMathFlags };
}

public sealed class LlvmFreezeInstruction : LlvmInstruction
{
    public LlvmFreezeInstruction(LlvmValue operand) : base(operand.Type, LlvmOpcode.Freeze, [operand]) { }
    public LlvmValue Operand { get => GetOperand(0)!; set => SetOperand(0, value); }
    protected override LlvmInstruction CloneImpl() => new LlvmFreezeInstruction(Operand);
}
