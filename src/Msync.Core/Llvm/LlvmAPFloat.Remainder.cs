// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp
// IEEEFloat mod/remainder, ilogb/scalbn, and the finite extension branch of
// convert used by remainder's precision+2 / expanded-exponent temporary.
using System.Diagnostics;
using C = Msync.Core.Llvm.LlvmFloatCategory;
using S = Msync.Core.Llvm.LlvmFloatStatus;
using R = Msync.Core.Llvm.LlvmRoundingMode;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    private partial struct IeeeFloat
    {
        internal readonly int ILogB()
        {
            Debug.Assert(Normal);
            if (Exponent != MinExponent || (Significand & (1UL << (Precision - 1))) != 0) return Exponent;
            IeeeFloat normalized = this;
            int significandBits = Precision - 1;
            normalized.Exponent += significandBits;
            normalized.Normalize(R.NearestTiesToEven, LostFraction.ExactlyZero);
            return normalized.Exponent - significandBits;
        }
        private readonly IeeeFloat ScaleB(int exponent, R mode)
        {
            IeeeFloat value = this;
            int maxIncrement = MaxExponent - (MinExponent - (Precision - 1)) + 1;
            value.Exponent += Math.Clamp(exponent, -maxIncrement - 1, maxIncrement);
            value.Normalize(mode, LostFraction.ExactlyZero);
            if (value.Category == C.NaN) value.MakeQuiet();
            return value;
        }
        internal S Mod(IeeeFloat rhs)
        {
            S status = ModSpecials(rhs, false);
            bool originalSign = Sign;
            while (Normal && rhs.Normal && CompareAbsolute(rhs) != LlvmFloatCompare.LessThan)
            {
                int exponent = ILogB() - rhs.ILogB();
                IeeeFloat value = rhs.ScaleB(exponent, R.NearestTiesToEven);
                if (value.Category == C.NaN || CompareAbsolute(value) == LlvmFloatCompare.LessThan)
                    value = rhs.ScaleB(exponent - 1, R.NearestTiesToEven);
                value.Sign = Sign;
                status = AddOrSubtract(value, R.NearestTiesToEven, true);
                Debug.Assert(status == S.OK);
            }
            if (Category == C.Zero) Sign = originalSign;
            return status;
        }
        internal S Remainder(IeeeFloat rhs)
        {
            bool originalSign = Sign;
            S status = ModSpecials(rhs, true);
            if (status != S.DivByZero) return status;
            status = S.OK;
            IeeeFloat twiceDenominator = rhs;
            if (twiceDenominator.AddOrSubtract(rhs, R.NearestTiesToEven, false) == S.OK)
            {
                status = Mod(twiceDenominator);
                Debug.Assert(status == S.OK);
            }
            IeeeFloat denominator = rhs;
            denominator.Sign = false;
            Sign = false;
            IeeeFloat extendedValue = ExtendForRemainder();
            IeeeFloat extendedDenominator = denominator.ExtendForRemainder();
            // Passing structs by value preserves the source self-add snapshot.
            status = extendedValue.AddOrSubtract(extendedValue, R.NearestTiesToEven, false);
            Debug.Assert(status == S.OK);
            if (extendedValue.Compare(extendedDenominator) == LlvmFloatCompare.GreaterThan)
            {
                status = AddOrSubtract(denominator, R.NearestTiesToEven, true);
                Debug.Assert(status == S.OK);
                status = extendedValue.AddOrSubtract(extendedDenominator, R.NearestTiesToEven, true);
                Debug.Assert(status == S.OK);
                status = extendedValue.AddOrSubtract(extendedDenominator, R.NearestTiesToEven, true);
                Debug.Assert(status == S.OK);
                LlvmFloatCompare comparison = extendedValue.Compare(extendedDenominator);
                if (comparison is LlvmFloatCompare.GreaterThan or LlvmFloatCompare.Equal)
                {
                    status = AddOrSubtract(denominator, R.NearestTiesToEven, true);
                    Debug.Assert(status == S.OK);
                }
            }
            if (Category == C.Zero) Sign = originalSign;
            else Sign ^= originalSign;
            return status;
        }
        private readonly IeeeFloat ExtendForRemainder()
        {
            Debug.Assert(Normal || Category == C.Zero);
            IeeeFloat value = this;
            value.MaxExponent++;
            value.MinExponent--;
            value.Precision += 2;
            if (Normal)
            {
                value.Significand <<= 2;
                S status = value.Normalize(R.NearestTiesToEven, LostFraction.ExactlyZero);
                Debug.Assert(status == S.OK);
            }
            return value;
        }
    }
}
