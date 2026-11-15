// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: APFloat.cpp
// IEEEFloat addOrSubtractSpecials, multiplySpecials, divideSpecials,
// modSpecials/remainderSpecials and arithmetic entry points (IEEE754 branch).
using System.Diagnostics;
using C = Msync.Core.Llvm.LlvmFloatCategory;
using S = Msync.Core.Llvm.LlvmFloatStatus;

namespace Msync.Core.Llvm;

public readonly partial struct LlvmAPFloat
{
    private partial struct IeeeFloat
    {
        // C++ switch fallthrough into the common NaN body is an explicit call
        // because C# tuple-pattern labels cannot be goto-case targets.
        private S NaNStatus(IeeeFloat rhs)
        {
            if (Signaling) { MakeQuiet(); return S.InvalidOp; }
            return rhs.Signaling ? S.InvalidOp : S.OK;
        }
        private S AddOrSubtractSpecials(IeeeFloat rhs, bool subtract)
        {
            switch ((Category, rhs.Category))
            {
                case (C.Zero, C.NaN): case (C.Normal, C.NaN): case (C.Infinity, C.NaN):
                    this = rhs;
                    return NaNStatus(rhs);
                case (C.NaN, C.Zero): case (C.NaN, C.Normal): case (C.NaN, C.Infinity): case (C.NaN, C.NaN):
                    return NaNStatus(rhs);
                case (C.Normal, C.Zero): case (C.Infinity, C.Normal): case (C.Infinity, C.Zero):
                    return S.OK;
                case (C.Normal, C.Infinity): case (C.Zero, C.Infinity):
                    Category = C.Infinity; Sign = rhs.Sign ^ subtract;
                    return S.OK;
                case (C.Zero, C.Normal):
                    this = rhs; Sign = rhs.Sign ^ subtract;
                    return S.OK;
                case (C.Zero, C.Zero): return S.OK;
                case (C.Infinity, C.Infinity):
                    if ((Sign ^ rhs.Sign) != subtract) { MakeNaN(); return S.InvalidOp; }
                    return S.OK;
                case (C.Normal, C.Normal): return S.DivByZero;
                default: throw new UnreachableException();
            }
        }
        private S MultiplySpecials(IeeeFloat rhs)
        {
            switch ((Category, rhs.Category))
            {
                case (C.Zero, C.NaN): case (C.Normal, C.NaN): case (C.Infinity, C.NaN):
                    this = rhs; Sign = false;
                    Sign ^= rhs.Sign;
                    return NaNStatus(rhs);
                case (C.NaN, C.Zero): case (C.NaN, C.Normal): case (C.NaN, C.Infinity): case (C.NaN, C.NaN):
                    Sign ^= rhs.Sign;
                    return NaNStatus(rhs);
                case (C.Normal, C.Infinity): case (C.Infinity, C.Normal): case (C.Infinity, C.Infinity):
                    Category = C.Infinity; return S.OK;
                case (C.Zero, C.Normal): case (C.Normal, C.Zero): case (C.Zero, C.Zero):
                    Category = C.Zero; return S.OK;
                case (C.Zero, C.Infinity): case (C.Infinity, C.Zero):
                    MakeNaN(); return S.InvalidOp;
                case (C.Normal, C.Normal): return S.OK;
                default: throw new UnreachableException();
            }
        }
        private S DivideSpecials(IeeeFloat rhs)
        {
            switch ((Category, rhs.Category))
            {
                case (C.Zero, C.NaN): case (C.Normal, C.NaN): case (C.Infinity, C.NaN):
                    this = rhs; Sign = false;
                    Sign ^= rhs.Sign;
                    return NaNStatus(rhs);
                case (C.NaN, C.Zero): case (C.NaN, C.Normal): case (C.NaN, C.Infinity): case (C.NaN, C.NaN):
                    Sign ^= rhs.Sign;
                    return NaNStatus(rhs);
                case (C.Infinity, C.Zero): case (C.Infinity, C.Normal): case (C.Zero, C.Infinity): case (C.Zero, C.Normal):
                    return S.OK;
                case (C.Normal, C.Infinity): Category = C.Zero; return S.OK;
                case (C.Normal, C.Zero): Category = C.Infinity; return S.DivByZero;
                case (C.Infinity, C.Infinity): case (C.Zero, C.Zero): MakeNaN(); return S.InvalidOp;
                case (C.Normal, C.Normal): return S.OK;
                default: throw new UnreachableException();
            }
        }
        private S ModSpecials(IeeeFloat rhs, bool remainder)
        {
            // The two upstream switches differ only in the normal/normal
            // sentinel; preserve both upstream status meanings.
            switch ((Category, rhs.Category))
            {
                case (C.Zero, C.NaN): case (C.Normal, C.NaN): case (C.Infinity, C.NaN):
                    this = rhs;
                    return NaNStatus(rhs);
                case (C.NaN, C.Zero): case (C.NaN, C.Normal): case (C.NaN, C.Infinity): case (C.NaN, C.NaN):
                    return NaNStatus(rhs);
                case (C.Zero, C.Infinity): case (C.Zero, C.Normal): case (C.Normal, C.Infinity): return S.OK;
                case (C.Normal, C.Zero): case (C.Infinity, C.Zero): case (C.Infinity, C.Normal):
                case (C.Infinity, C.Infinity): case (C.Zero, C.Zero): MakeNaN(); return S.InvalidOp;
                case (C.Normal, C.Normal): return remainder ? S.DivByZero : S.OK;
                default: throw new UnreachableException();
            }
        }
        internal S AddOrSubtract(IeeeFloat rhs, LlvmRoundingMode mode, bool subtract)
        {
            S status = AddOrSubtractSpecials(rhs, subtract);
            if (status == S.DivByZero)
            {
                LostFraction fraction = AddOrSubtractSignificand(rhs, subtract);
                status = Normalize(mode, fraction);
                Debug.Assert(Category != C.Zero || fraction == LostFraction.ExactlyZero);
            }
            if (Category == C.Zero)
                if (rhs.Category != C.Zero || (Sign == rhs.Sign) == subtract) Sign = mode == LlvmRoundingMode.TowardNegative;
            return status;
        }
        internal S Multiply(IeeeFloat rhs, LlvmRoundingMode mode)
        {
            Sign ^= rhs.Sign;
            S status = MultiplySpecials(rhs);
            if (Normal)
            {
                LostFraction fraction = MultiplySignificand(rhs);
                status = Normalize(mode, fraction);
                if (fraction != LostFraction.ExactlyZero) status |= S.Inexact;
            }
            return status;
        }
        internal S Divide(IeeeFloat rhs, LlvmRoundingMode mode)
        {
            Sign ^= rhs.Sign;
            S status = DivideSpecials(rhs);
            if (Normal)
            {
                LostFraction fraction = DivideSignificand(rhs);
                status = Normalize(mode, fraction);
                if (fraction != LostFraction.ExactlyZero) status |= S.Inexact;
            }
            return status;
        }
    }
}
