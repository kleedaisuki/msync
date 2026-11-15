// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ADT/APFloat.h/.cpp,
// IEEEFloat one-word IEEE arithmetic and comparison. Immutable public carrier adapter.
using System.Diagnostics;
using System.Numerics;

namespace Msync.Core.Llvm;

public enum LlvmRoundingMode : sbyte
{
    Invalid = -1, TowardZero = 0, NearestTiesToEven = 1, TowardPositive = 2,
    TowardNegative = 3, NearestTiesToAway = 4, Dynamic = 7
}
[Flags]
public enum LlvmFloatStatus : byte
{
    OK = 0, InvalidOp = 1, DivByZero = 2, Overflow = 4, Underflow = 8, Inexact = 16
}
public enum LlvmFloatCompare : byte { LessThan, Equal, GreaterThan, Unordered }
public readonly record struct LlvmAPFloatResult(LlvmAPFloat Value, LlvmFloatStatus Status);

public readonly partial struct LlvmAPFloat
{
    public const int IEK_Zero = int.MinValue + 1, IEK_NaN = int.MinValue, IEK_Inf = int.MaxValue;
    public int ILogB() => IsNaN ? IEK_NaN : IsZero ? IEK_Zero : IsInfinity ? IEK_Inf : new IeeeFloat(this).ILogB();
    public LlvmFloatCompare CompareAbsoluteValue(LlvmAPFloat rhs) => new IeeeFloat(this).CompareAbsolute(new(rhs));
    public bool IsSignaling => IsNaN && (bits & (1UL << (Format.Precision - 2))) == 0;
    public LlvmAPFloat ChangeSign() => FromBits(Semantics, bits ^ Format.SignMask);
    public LlvmAPFloatResult Add(LlvmAPFloat rhs, LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.AddOrSubtract(new(rhs), mode, false);
        return new(value.Pack(), status);
    }
    public LlvmAPFloatResult Subtract(LlvmAPFloat rhs, LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.AddOrSubtract(new(rhs), mode, true);
        return new(value.Pack(), status);
    }
    public LlvmAPFloatResult Multiply(LlvmAPFloat rhs, LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.Multiply(new(rhs), mode);
        return new(value.Pack(), status);
    }
    public LlvmAPFloatResult Divide(LlvmAPFloat rhs, LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.Divide(new(rhs), mode);
        return new(value.Pack(), status);
    }
    public LlvmAPFloatResult Mod(LlvmAPFloat rhs)
    {
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.Mod(new(rhs));
        return new(value.Pack(), status);
    }
    public LlvmAPFloatResult Remainder(LlvmAPFloat rhs)
    {
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.Remainder(new(rhs));
        return new(value.Pack(), status);
    }
    public LlvmAPFloatResult RoundToIntegral(LlvmRoundingMode mode)
    {
        IeeeFloat value = new(this);
        LlvmFloatStatus status = value.RoundToIntegral(mode);
        return new(value.Pack(), status);
    }
    public static LlvmAPFloatResult FromAPInt(LlvmAPInt integer, bool isSigned,
        LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven) =>
        FromAPInt(LlvmAPFloatSemantics.IEEEdouble, integer, isSigned, mode);
    public static LlvmAPFloatResult FromAPInt(LlvmAPFloatSemantics semantics, LlvmAPInt integer, bool isSigned,
        LlvmRoundingMode mode = LlvmRoundingMode.NearestTiesToEven)
    {
        IeeeFloat value = new(GetZero(semantics));
        LlvmFloatStatus status = value.ConvertFromAPInt(integer, isSigned, mode);
        return new(value.Pack(), status);
    }
    public LlvmFloatCompare Compare(LlvmAPFloat rhs) => new IeeeFloat(this).Compare(new(rhs));

    private enum LostFraction : byte { ExactlyZero, LessThanHalf, ExactlyHalf, MoreThanHalf }

    // The source remainder algorithm also uses a precision+2,
    // expanded-exponent temporary without exposing that format publicly.
    private partial struct IeeeFloat
    {
        internal ulong Significand;
        internal int Exponent;
        internal bool Sign;
        internal LlvmFloatCategory Category;
        internal int Precision, MinExponent, MaxExponent;
        internal LlvmAPFloatSemantics Semantics;
        internal readonly bool Normal => Category == LlvmFloatCategory.Normal;
        internal readonly bool Signaling => Category == LlvmFloatCategory.NaN && (Significand & (1UL << (Precision - 2))) == 0;
        internal IeeeFloat(LlvmAPFloat value)
        {
            var format = value.Format;
            Semantics = value.Semantics;
            Precision = format.Precision; MinExponent = format.MinExponent; MaxExponent = format.MaxExponent;
            Sign = value.IsNegative;
            Category = value.Category;
            ulong raw = value.bits;
            Significand = raw & format.FractionMask;
            int storedExponent = (int)((raw >> format.FractionBits) & format.ExponentMask);
            Exponent = storedExponent - format.Bias;
            if (Normal)
            {
                if (storedExponent == 0) Exponent = MinExponent;
                else Significand |= 1UL << format.FractionBits;
            }
        }
        internal readonly LlvmAPFloat Pack()
        {
            var format = GetFormat(Semantics);
            Debug.Assert(Precision == format.Precision && MinExponent == format.MinExponent && MaxExponent == format.MaxExponent);
            ulong exponent, significand;
            if (Normal)
            {
                exponent = (ulong)(Exponent + format.Bias);
                significand = Significand;
                if (exponent == 1 && (significand & (1UL << format.FractionBits)) == 0) exponent = 0;
            }
            else if (Category == LlvmFloatCategory.Zero) { exponent = 0; significand = 0; }
            else if (Category == LlvmFloatCategory.Infinity) { exponent = format.ExponentMask; significand = 0; }
            else { exponent = format.ExponentMask; significand = Significand; }
            return FromBits(Semantics, (significand & format.FractionMask) |
                ((exponent & format.ExponentMask) << format.FractionBits) | (Sign ? format.SignMask : 0));
        }
        private void MakeNaN()
        {
            Category = LlvmFloatCategory.NaN; Sign = false; Exponent = MaxExponent + 1;
            Significand = 1UL << (Precision - 2);
        }
        internal void MakeQuiet() => Significand |= 1UL << (Precision - 2);
        internal readonly uint MSB() => Significand == 0 ? uint.MaxValue : (uint)BitOperations.Log2(Significand);
        private LostFraction ShiftSignificandRight(uint bits)
        {
            Exponent += (int)bits;
            LostFraction fraction = TruncatedFraction(Significand, bits);
            Significand = bits >= 64 ? 0 : Significand >> (int)bits;
            return fraction;
        }
        private void ShiftSignificandLeft(uint bits)
        {
            Debug.Assert(bits < Precision);
            if (bits == 0) return;
            Significand <<= (int)bits;
            Exponent -= (int)bits;
            Debug.Assert(Significand != 0);
        }
        internal readonly LlvmFloatCompare CompareAbsolute(IeeeFloat rhs)
        {
            Debug.Assert(Normal && rhs.Normal);
            int comparison = Exponent - rhs.Exponent;
            if (comparison == 0) comparison = Significand < rhs.Significand ? -1 : Significand > rhs.Significand ? 1 : 0;
            return comparison > 0 ? LlvmFloatCompare.GreaterThan : comparison < 0 ? LlvmFloatCompare.LessThan : LlvmFloatCompare.Equal;
        }
    }

    private static LostFraction TruncatedFraction(ulong word, uint bits)
    {
        uint lsb = word == 0 ? uint.MaxValue : (uint)BitOperations.TrailingZeroCount(word);
        if (bits <= lsb) return LostFraction.ExactlyZero;
        if (bits == lsb + 1) return LostFraction.ExactlyHalf;
        if (bits <= 64 && (word & (1UL << (int)(bits - 1))) != 0) return LostFraction.MoreThanHalf;
        return LostFraction.LessThanHalf;
    }
    private static LostFraction TruncatedFraction(ReadOnlySpan<ulong> words, uint bits)
    {
        uint lsb = LlvmAPInt.WordsLSB(words);
        if (bits <= lsb) return LostFraction.ExactlyZero;
        if (bits == lsb + 1) return LostFraction.ExactlyHalf;
        if (bits <= (uint)words.Length * 64 && (words[(int)((bits - 1) / 64)] & (1UL << (int)((bits - 1) % 64))) != 0)
            return LostFraction.MoreThanHalf;
        return LostFraction.LessThanHalf;
    }
    private static LostFraction Combine(LostFraction more, LostFraction less)
    {
        if (less != LostFraction.ExactlyZero)
        {
            if (more == LostFraction.ExactlyZero) more = LostFraction.LessThanHalf;
            else if (more == LostFraction.ExactlyHalf) more = LostFraction.MoreThanHalf;
        }
        return more;
    }
}
