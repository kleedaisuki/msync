// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: KnownFPClass::bitcast,
// IEEE half/bfloat/single/double masks; no x87/quad format substitution.
using Msync.Core.Llvm;
using C = Msync.Core.Llvm.LlvmFPClassTest;

namespace Msync.Optimization.Llvm;

public partial struct LlvmKnownFPClass
{
    public static LlvmKnownFPClass Bitcast(LlvmKnownBits bits, LlvmAPFloatSemantics semantics = LlvmAPFloatSemantics.IEEEdouble)
    {
        var known = new LlvmKnownFPClass();
        if (!(bits.Zero & bits.One).IsZero) return known;
        uint width = LlvmAPFloat.SemanticsSizeInBits(semantics);
        uint precision = LlvmAPFloat.SemanticsPrecision(semantics);
        System.Diagnostics.Debug.Assert(bits.BitWidth == width);
        if (bits.IsNonNegative) known.SetSignBit(false);
        else if (bits.IsNegative) known.SetSignBit(true);
        var exponent = LlvmAPFloat.GetInf(semantics).BitcastToAPInt();
        var mantissa = LlvmAPInt.GetLowBitsSet(width, precision - 1);
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
        if ((bits.One.GetZExtValue() & (1UL << (int)(precision - 2))) != 0) known.KnownNot(C.SNaN);
        else if ((bits.Zero.GetZExtValue() & (1UL << (int)(precision - 2))) != 0) known.KnownNot(C.QNaN);
        return known;
    }

    public LlvmKnownBits ToKnownBits(LlvmAPFloatSemantics semantics = LlvmAPFloatSemantics.IEEEdouble)
    {
        uint width = LlvmAPFloat.SemanticsSizeInBits(semantics);
        uint precision = LlvmAPFloat.SemanticsPrecision(semantics);
        C classes = GetKnownFPClasses();
        var zero = LlvmAPInt.GetZero(width);
        var one = LlvmAPInt.GetZero(width);
        if (classes == C.None) return new(zero, one);
        if ((classes & C.Normal) == 0)
        {
            zero = one = LlvmAPInt.GetAllOnes(width);
            var mantissa = LlvmAPInt.GetLowBitsSet(width, precision - 1);
            var exponent = LlvmAPFloat.GetInf(semantics).BitcastToAPInt();
            var quiet = LlvmAPInt.GetOneBitSet(width, precision - 2);
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
        var sign = LlvmAPInt.GetSignedMinValue(width);
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
