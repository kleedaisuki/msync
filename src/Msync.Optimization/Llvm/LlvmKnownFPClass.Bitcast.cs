// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: KnownFPClass::bitcast,
// represented IEEEdouble arm; no x87 or vector bitcast adaptation.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public partial struct LlvmKnownFPClass
{
    public static LlvmKnownFPClass Bitcast(LlvmKnownBits bits)
    {
        var known = new LlvmKnownFPClass();
        if (!(bits.Zero & bits.One).IsZero) return known;
        if (bits.BitWidth != 64) throw new NotSupportedException("KnownFPClass bitcast supports represented IEEEdouble only.");
        if (bits.IsNonNegative) known.SetSignBit(false);
        else if (bits.IsNegative) known.SetSignBit(true);
        var exponent = new LlvmAPInt(64, 0x7ff0000000000000UL);
        var mantissa = new LlvmAPInt(64, 0xfffffffffffffUL);
        bool exponentAllZero = (bits.Zero & exponent) == exponent;
        bool exponentAllOne = (bits.One & exponent) == exponent;
        bool exponentNotZero = !(bits.One & exponent).IsZero;
        bool exponentNotOne = !(bits.Zero & exponent).IsZero;
        bool mantissaAllZero = (bits.Zero & mantissa) == mantissa;
        bool mantissaNotZero = !(bits.One & mantissa).IsZero;
        if (exponentNotZero) known.KnownNot(C.Zero | C.Subnormal);
        if (exponentNotOne) known.KnownNot(C.Infinity | C.NaN);
        if (exponentAllZero || exponentAllOne) known.KnownNot(C.Normal);
        if (mantissaNotZero) known.KnownNot(C.Zero | C.Infinity);
        if (mantissaAllZero) known.KnownNot(C.Subnormal | C.NaN);
        if ((bits.One.GetZExtValue() & (1UL << 51)) != 0) known.KnownNot(C.SNaN);
        else if ((bits.Zero.GetZExtValue() & (1UL << 51)) != 0) known.KnownNot(C.QNaN);
        return known;
    }

    public LlvmKnownBits ToKnownBits()
    {
        C classes = GetKnownFPClasses();
        var zero = LlvmAPInt.GetZero(64);
        var one = LlvmAPInt.GetZero(64);
        if (classes == C.None) return new(zero, one);
        if ((classes & C.Normal) == 0)
        {
            zero = one = LlvmAPInt.GetAllOnes(64);
            var mantissa = new LlvmAPInt(64, 0xfffffffffffffUL);
            var exponent = new LlvmAPInt(64, 0x7ff0000000000000UL);
            var quiet = LlvmAPInt.GetOneBitSet(64, 51);
            var payload = mantissa & ~quiet;
            if ((classes & C.NaN) != 0)
            {
                zero &= ~exponent;
                zero &= ~payload;
                one &= ~payload;
                if ((classes & C.QNaN) != 0) zero &= ~quiet;
                if ((classes & C.SNaN) != 0) one &= ~quiet;
            }
            if ((classes & C.Infinity) != 0) { zero &= ~exponent; one &= ~mantissa; }
            if ((classes & C.Subnormal) != 0) { one &= ~exponent; one &= ~mantissa; zero &= ~mantissa; }
            if ((classes & C.Zero) != 0) { one &= ~exponent; one &= ~mantissa; }
        }
        var sign = LlvmAPInt.GetSignedMinValue(64);
        zero &= ~sign;
        one &= ~sign;
        if (SignBit is bool negative)
        {
            if (negative) one |= sign;
            else zero |= sign;
        }
        return new(zero, one);
    }

}
