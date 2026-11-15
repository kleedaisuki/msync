// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp
// IEEEFloat roundToIntegral, compare, convertFromAPInt/convertFromUnsignedParts.
using System.Diagnostics;
using C = Msync.Core.Llvm.LlvmFloatCategory;
using S = Msync.Core.Llvm.LlvmFloatStatus;
using R = Msync.Core.Llvm.LlvmRoundingMode;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    private partial struct IeeeFloat
    {
        internal S RoundToIntegral(R mode)
        {
            if (Category == C.Infinity) return S.OK;
            if (Category == C.NaN)
            {
                if (Signaling) { MakeQuiet(); return S.InvalidOp; }
                return S.OK;
            }
            if (Category == C.Zero || Exponent + 1 >= Precision) return S.OK;
            // The one-word IEEE formats use the source power-of-two magic value.
            LlvmAPInt integer = new LlvmAPInt(64, 1).Shl((uint)(Precision - 1));
            IeeeFloat magic = new(GetZero(Semantics));
            S status = magic.ConvertFromAPInt(integer, false, R.NearestTiesToEven);
            Debug.Assert(status == S.OK);
            magic.Sign = Sign;
            bool inputSign = Sign;
            status = AddOrSubtract(magic, mode, false);
            AddOrSubtract(magic, mode, true);
            if (inputSign != Sign) Sign = !Sign;
            return status;
        }
        internal S ConvertFromAPInt(LlvmAPInt integer, bool isSigned, R mode)
        {
            Sign = false;
            if (isSigned && integer.IsNegative) { Sign = true; integer = -integer; }
            Span<ulong> singleWord = stackalloc ulong[1];
            singleWord[0] = integer.IsSingleWord ? integer.GetZExtValue() : 0;
            ReadOnlySpan<ulong> source = integer.IsSingleWord ? singleWord : integer.MultiWordData;
            Category = C.Normal;
            uint omsb = unchecked(LlvmAPInt.WordsMSB(source) + 1);
            Span<ulong> destination = stackalloc ulong[1];
            LostFraction fraction;
            if (Precision <= omsb)
            {
                Exponent = (int)omsb - 1;
                fraction = TruncatedFraction(source, omsb - (uint)Precision);
                LlvmAPInt.ExtractWords(destination, source, (uint)Precision, omsb - (uint)Precision);
            }
            else
            {
                Exponent = Precision - 1;
                fraction = LostFraction.ExactlyZero;
                LlvmAPInt.ExtractWords(destination, source, omsb, 0);
            }
            Significand = destination[0];
            return Normalize(mode, fraction);
        }
        internal readonly LlvmFloatCompare Compare(IeeeFloat rhs)
        {
            Debug.Assert(Precision == rhs.Precision && MinExponent == rhs.MinExponent && MaxExponent == rhs.MaxExponent);
            switch ((Category, rhs.Category))
            {
                case (C.NaN, _): case (_, C.NaN): return LlvmFloatCompare.Unordered;
                case (C.Infinity, C.Normal): case (C.Infinity, C.Zero): case (C.Normal, C.Zero):
                    return Sign ? LlvmFloatCompare.LessThan : LlvmFloatCompare.GreaterThan;
                case (C.Normal, C.Infinity): case (C.Zero, C.Infinity): case (C.Zero, C.Normal):
                    return rhs.Sign ? LlvmFloatCompare.GreaterThan : LlvmFloatCompare.LessThan;
                case (C.Infinity, C.Infinity):
                    return Sign == rhs.Sign ? LlvmFloatCompare.Equal : Sign ? LlvmFloatCompare.LessThan : LlvmFloatCompare.GreaterThan;
                case (C.Zero, C.Zero): return LlvmFloatCompare.Equal;
                case (C.Normal, C.Normal): break;
                default: throw new UnreachableException();
            }
            if (Sign != rhs.Sign) return Sign ? LlvmFloatCompare.LessThan : LlvmFloatCompare.GreaterThan;
            LlvmFloatCompare comparison = CompareAbsolute(rhs);
            if (!Sign) return comparison;
            return comparison switch
            {
                LlvmFloatCompare.LessThan => LlvmFloatCompare.GreaterThan,
                LlvmFloatCompare.GreaterThan => LlvmFloatCompare.LessThan,
                _ => comparison
            };
        }
    }
}
