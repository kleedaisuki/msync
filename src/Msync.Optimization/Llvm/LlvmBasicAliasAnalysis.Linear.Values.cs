// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp,
// CastedValue and LinearExpression.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

internal sealed partial class LlvmBasicAliasAnalysis
{
    internal readonly record struct CastedValue(LlvmValue V, uint ZExtBits, uint SExtBits,
        uint TruncBits, bool IsNonNegative)
    {
        internal CastedValue(LlvmValue value) : this(value, 0, 0, 0, false) { }
        internal uint BitWidth => PrimitiveWidth(V) - TruncBits + ZExtBits + SExtBits;
        private static uint PrimitiveWidth(LlvmValue value) => (uint)value.Type.GetPrimitiveSizeInBits().GetFixedValue();
        internal CastedValue WithValue(LlvmValue value, bool preserveNonNegative) =>
            new(value, ZExtBits, SExtBits, TruncBits, IsNonNegative && preserveNonNegative);
        internal CastedValue WithZExtOfValue(LlvmValue value, bool zextNonNegative)
        {
            uint extendBy = PrimitiveWidth(V) - PrimitiveWidth(value);
            if (extendBy <= TruncBits) return new(value, ZExtBits, SExtBits, TruncBits - extendBy, IsNonNegative);
            extendBy -= TruncBits;
            return new(value, ZExtBits + SExtBits + extendBy, 0, 0, zextNonNegative);
        }
        internal CastedValue WithSExtOfValue(LlvmValue value)
        {
            uint extendBy = PrimitiveWidth(V) - PrimitiveWidth(value);
            if (extendBy <= TruncBits) return new(value, ZExtBits, SExtBits, TruncBits - extendBy, IsNonNegative);
            extendBy -= TruncBits;
            return new(value, ZExtBits, SExtBits + extendBy, 0, IsNonNegative);
        }
        internal LlvmAPInt EvaluateWith(LlvmAPInt value)
        {
            Debug.Assert(value.BitWidth == PrimitiveWidth(V));
            if (TruncBits != 0) value = value.Trunc(value.BitWidth - TruncBits);
            if (SExtBits != 0) value = value.SExt(value.BitWidth + SExtBits);
            if (ZExtBits != 0) value = value.ZExt(value.BitWidth + ZExtBits);
            return value;
        }
        internal LlvmConstantRange EvaluateWith(LlvmConstantRange value)
        {
            Debug.Assert(value.BitWidth == PrimitiveWidth(V));
            if (TruncBits != 0) value = value.Truncate(value.BitWidth - TruncBits);
            if (IsNonNegative && !value.IsAllNonNegative)
                value = value.IntersectWith(new(LlvmAPInt.GetZero(value.BitWidth), LlvmAPInt.GetSignedMinValue(value.BitWidth)));
            if (SExtBits != 0) value = value.SignExtend(value.BitWidth + SExtBits);
            if (ZExtBits != 0) value = value.ZeroExtend(value.BitWidth + ZExtBits);
            return value;
        }
        internal LlvmKnownBits EvaluateWith(LlvmKnownBits value)
        {
            Debug.Assert(value.BitWidth == PrimitiveWidth(V));
            if (TruncBits != 0) value = value.Trunc(value.BitWidth - TruncBits);
            if (SExtBits != 0) value = value.SExt(value.BitWidth + SExtBits);
            if (ZExtBits != 0) value = value.ZExt(value.BitWidth + ZExtBits);
            return value;
        }
        internal bool CanDistributeOver(bool nuw, bool nsw) => (ZExtBits == 0 || nuw) && (SExtBits == 0 || nsw);
        internal bool HasSameCastsAs(CastedValue other)
        {
            if (V.Type != other.V.Type) return false;
            if (ZExtBits == other.ZExtBits && SExtBits == other.SExtBits && TruncBits == other.TruncBits) return true;
            return (IsNonNegative || other.IsNonNegative) && ZExtBits + SExtBits == other.ZExtBits + other.SExtBits &&
                TruncBits == other.TruncBits;
        }
    }

    internal struct LinearExpression
    {
        internal CastedValue Val;
        internal LlvmAPInt Scale, Offset;
        internal bool IsNUW, IsNSW;
        internal LinearExpression(CastedValue value, LlvmAPInt scale, LlvmAPInt offset, bool nuw, bool nsw)
        { Val = value; Scale = scale; Offset = offset; IsNUW = nuw; IsNSW = nsw; }
        internal LinearExpression(CastedValue value) : this(value, new(value.BitWidth, 1), LlvmAPInt.GetZero(value.BitWidth), true, true) { }
        internal LinearExpression Mul(LlvmAPInt other, bool mulIsNUW, bool mulIsNSW)
        {
            bool nsw = IsNSW && (other.IsOne || mulIsNSW && Offset.IsZero);
            bool nuw = IsNUW && (other.IsOne || mulIsNUW);
            var scale = Scale.SMulOverflow(other, out bool scaleOverflow);
            nsw &= !scaleOverflow;
            return new(Val, scale, Offset * other, nuw, nsw);
        }
    }
}
