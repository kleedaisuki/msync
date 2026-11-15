// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Support/KnownFPClass.h/.cpp,
// ADT/FloatingPointMode.h. Signed NaN classes retain the upstream 12-bit mask.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public partial struct LlvmKnownFPClass
{
    private const ushort AllMask = 4095, PositiveMask = 0xfc0, NegativeMask = 0x03f;
    private ushort mask;
    public LlvmKnownFPClass() => mask = AllMask;
    public LlvmKnownFPClass(C classes) => mask = ToMask(classes);
    private LlvmKnownFPClass(ushort signedMask) => mask = signedMask;
    public LlvmKnownFPClass(LlvmAPFloat value)
    {
        C classes = value.IsNaN ? value.IsSignaling ? C.SNaN : C.QNaN :
            value.IsInfinity ? C.Infinity : value.IsZero ? C.Zero : value.IsDenormal ? C.Subnormal : C.Normal;
        mask = ToMask(classes);
        SetSignBit(value.IsNegative);
    }
    public static LlvmKnownFPClass Empty => new((ushort)0);
    public C GetKnownFPClasses() => (C)((mask & 0x3fc) | ((mask & 0x801) != 0 ? 2 : 0) | ((mask & 0x402) != 0 ? 1 : 0));
    public bool? SignBit => (mask & NegativeMask) == 0 ? false : (mask & PositiveMask) == 0 ? true : null;
    public bool IsUnknown => mask == AllMask;
    public bool IsKnownNever(C classes) => (mask & ToMask(classes)) == 0;
    public bool IsKnownAlways(C classes) => IsKnownNever(C.All & ~classes);
    public bool IsKnownNeverNaN() => IsKnownNever(C.NaN);
    public bool IsKnownNeverInfinity() => IsKnownNever(C.Infinity);
    public bool IsKnownNeverInfOrNaN() => IsKnownNever(C.NaN | C.Infinity);
    public bool IsKnownNeverLogicalZero(LlvmDenormalMode mode) => ApplyInputDenormalMode(this, mode).IsKnownNever(C.Zero);
    public bool IsKnownNeverLogicalNegZero(LlvmDenormalMode mode) => ApplyInputDenormalMode(this, mode).IsKnownNever(C.NegZero);
    public bool IsKnownNeverLogicalPosZero(LlvmDenormalMode mode) => ApplyInputDenormalMode(this, mode).IsKnownNever(C.PosZero);
    public const C OrderedLessThanZeroMask = C.NegSubnormal | C.NegNormal | C.NegInfinity;
    public const C OrderedGreaterThanZeroMask = C.PosSubnormal | C.PosNormal | C.PosInfinity;
    public bool CannotBeOrderedLessThanZero() => IsKnownNever(OrderedLessThanZeroMask);
    public bool CannotBeOrderedGreaterThanZero() => IsKnownNever(OrderedGreaterThanZeroMask);
    public void KnownNot(C classes) => mask &= (ushort)~ToMask(classes);
    public void SetKnownFPClasses(C classes) => mask = ToMask(classes);
    public LlvmKnownFPClass IntersectWith(LlvmKnownFPClass other) => new((ushort)(mask | other.mask));
    public LlvmKnownFPClass UnionWith(LlvmKnownFPClass other) => new((ushort)(mask & other.mask));
    public static LlvmKnownFPClass operator |(LlvmKnownFPClass left, LlvmKnownFPClass right) => left.IntersectWith(right);
    private static ushort ToMask(C classes) => (ushort)(((ushort)classes & 0x3fc) |
        ((classes & C.QNaN) != 0 ? 0x801 : 0) | ((classes & C.SNaN) != 0 ? 0x402 : 0));
    public void SetSignBit(bool? sign)
    {
        if (sign.HasValue) { mask &= sign.Value ? NegativeMask : PositiveMask; return; }
        ushort opposite = ReverseMask(mask);
        mask |= opposite;
    }
    private static ushort ReverseMask(ushort input)
    {
        ushort result = 0;
        for (int bit = 0; bit < 12; ++bit)
            if ((input & (1 << bit)) != 0) result |= (ushort)(1 << (11 - bit));
        return result;
    }
    public void Negate() => mask = ReverseMask(mask);
    public static LlvmKnownFPClass Negate(LlvmKnownFPClass source) { source.Negate(); return source; }
    public void PropagateNonSNaN(LlvmKnownFPClass source) { if (source.IsKnownNever(C.SNaN)) KnownNot(C.SNaN); }
    public void PropagateNonSNaN(LlvmKnownFPClass left, LlvmKnownFPClass right)
    { if (left.IsKnownNever(C.SNaN) && right.IsKnownNever(C.SNaN)) KnownNot(C.SNaN); }
    public void PropagateNonNaN(LlvmKnownFPClass source)
    { PropagateNonSNaN(source); if (source.IsKnownNeverNaN()) KnownNot(C.NaN); }
    public void PropagateNonNaN(LlvmKnownFPClass left, LlvmKnownFPClass right)
    { PropagateNonSNaN(left, right); if (left.IsKnownNeverNaN() && right.IsKnownNeverNaN()) KnownNot(C.NaN); }
    public static LlvmKnownFPClass Square(LlvmKnownFPClass source, LlvmDenormalMode mode)
    { var known = Multiply(source, source, mode); known.KnownNot(C.Negative); known.PropagateNonNaN(source); return known; }
    public static LlvmKnownFPClass ApplyInputDenormalMode(LlvmKnownFPClass source, LlvmDenormalMode mode) => ApplyDenormalMode(source, mode);
    public static LlvmKnownFPClass ApplyOutputDenormalMode(LlvmKnownFPClass source, LlvmDenormalMode mode) => ApplyDenormalMode(source, mode);
    private static LlvmKnownFPClass ApplyDenormalMode(LlvmKnownFPClass source, LlvmDenormalMode mode)
    {
        var known = source;
        if (mode == LlvmDenormalMode.IEEE) return known;
        if (!source.IsKnownNever(mode == LlvmDenormalMode.PreserveSign ? C.PosSubnormal : C.Subnormal)) known |= new LlvmKnownFPClass(C.PosZero);
        if (mode != LlvmDenormalMode.PositiveZero && !source.IsKnownNever(C.NegSubnormal)) known |= new LlvmKnownFPClass(C.NegZero);
        return known;
    }
}
