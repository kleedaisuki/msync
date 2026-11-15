// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: ConstantRange.cpp casts.
using System.Diagnostics;
namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    public LlvmConstantRange CastOp(LlvmOpcode opcode, uint width) => opcode switch
    {
        LlvmOpcode.Trunc => Truncate(width), LlvmOpcode.SExt => SignExtend(width),
        LlvmOpcode.ZExt => ZeroExtend(width), LlvmOpcode.BitCast => this,
        LlvmOpcode.FPToUI or LlvmOpcode.FPToSI => BitWidth == width ? this : Full(width),
        LlvmOpcode.UIToFP => GetNonEmpty(LlvmAPInt.GetMinValue(BitWidth).ZExtOrTrunc(Math.Max(BitWidth, width)),
            Inc(LlvmAPInt.GetMaxValue(BitWidth).ZExtOrTrunc(Math.Max(BitWidth, width)))),
        LlvmOpcode.SIToFP => GetNonEmpty(LlvmAPInt.GetSignedMinValue(BitWidth).SExtOrTrunc(Math.Max(BitWidth, width)),
            Inc(LlvmAPInt.GetSignedMaxValue(BitWidth).SExtOrTrunc(Math.Max(BitWidth, width)))),
        LlvmOpcode.FPTrunc or LlvmOpcode.FPExt or LlvmOpcode.IntToPtr or LlvmOpcode.PtrToAddr or
            LlvmOpcode.PtrToInt or LlvmOpcode.AddrSpaceCast => Full(width),
        _ => throw new InvalidOperationException("unsupported cast type")
    };
    public LlvmConstantRange ZeroExtend(uint width)
    {
        if (IsEmptySet) return Empty(width);
        if (width == BitWidth) return this;
        Debug.Assert(BitWidth < width);
        if (IsFullSet || IsUpperWrapped)
        {
            var lower = Upper.IsZero ? Lower.ZExt(width) : LlvmAPInt.GetZero(width);
            return new(lower, LlvmAPInt.GetOneBitSet(width, BitWidth));
        }
        return new(Lower.ZExt(width), Upper.ZExt(width));
    }
    public LlvmConstantRange SignExtend(uint width)
    {
        if (IsEmptySet) return Empty(width);
        if (width == BitWidth) return this;
        Debug.Assert(BitWidth < width);
        if (Upper.IsMinSignedValue) return new(Lower.SExt(width), Upper.ZExt(width));
        if (IsFullSet || IsSignWrappedSet)
            return new(LlvmAPInt.GetHighBitsSet(width, width - BitWidth + 1), Inc(LlvmAPInt.GetLowBitsSet(width, BitWidth - 1)));
        return new(Lower.SExt(width), Upper.SExt(width));
    }
    public LlvmConstantRange Truncate(uint width, uint noWrapKind = 0)
    {
        if (width == BitWidth) return this;
        Debug.Assert(BitWidth > width);
        if (IsEmptySet) return Empty(width);
        if (IsFullSet) return Full(width);
        var lower = Lower;
        var upper = Upper;
        var union = Empty(width);
        if (IsUpperWrapped)
        {
            if (Upper.GetActiveBits() > width) return Full(width);
            if ((noWrapKind & NoUnsignedWrap) != 0)
            {
                union = new(LlvmAPInt.GetZero(width), Upper.Trunc(width));
                upper = LlvmAPInt.GetOneBitSet(BitWidth, width);
            }
            else
            {
                if (Upper.CountTrailingOnes() == width) return Full(width);
                union = new(LlvmAPInt.GetMaxValue(width), Upper.Trunc(width));
                upper = LlvmAPInt.GetMaxValue(BitWidth);
                if (lower == upper) return union;
            }
        }
        if (lower.GetActiveBits() > width)
        {
            if ((noWrapKind & NoUnsignedWrap) != 0) return union;
            var adjust = lower & LlvmAPInt.GetBitsSetFrom(BitWidth, width);
            lower -= adjust;
            upper -= adjust;
        }
        uint upperWidth = upper.GetActiveBits();
        if (upperWidth <= width) return new LlvmConstantRange(lower.Trunc(width), upper.Trunc(width)).UnionWith(union);
        if (!lower.IsZero && (noWrapKind & NoUnsignedWrap) != 0)
            return new LlvmConstantRange(lower.Trunc(width), LlvmAPInt.GetZero(width)).UnionWith(union);
        if (upperWidth == width + 1)
        {
            upper = upper.ClearBits(width, width + 1);
            if (upper.Ult(lower)) return new LlvmConstantRange(lower.Trunc(width), upper.Trunc(width)).UnionWith(union);
        }
        return Full(width);
    }
    public LlvmConstantRange ZExtOrTrunc(uint width) => BitWidth > width ? Truncate(width) : BitWidth < width ? ZeroExtend(width) : this;
    public LlvmConstantRange SExtOrTrunc(uint width) => BitWidth > width ? Truncate(width) : BitWidth < width ? SignExtend(width) : this;
}
