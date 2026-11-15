// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// IR/ConstantRange.cpp isIntrinsicSupported/intrinsic and smin/smax/umin/umax,
// restricted to the explicit current source intrinsic registry.
namespace Msync.Core.Llvm;

public sealed partial record LlvmConstantRange
{
    public static bool IsIntrinsicSupported(LlvmIntrinsicKind kind) => kind == LlvmIntrinsicKind.SMin ||
        kind == LlvmIntrinsicKind.SMax || kind == LlvmIntrinsicKind.UMin || kind == LlvmIntrinsicKind.UMax ||
        kind == LlvmIntrinsicKind.CTPop || kind == LlvmIntrinsicKind.CTTZ;
    public static LlvmConstantRange Intrinsic(LlvmIntrinsicKind kind, IReadOnlyList<LlvmConstantRange> operands,
        Dictionary<string, ulong>? metrics = null)
    {
        if (metrics is not null) metrics["range.intrinsic.queries"] = metrics.GetValueOrDefault("range.intrinsic.queries") + 1;
        if (kind == LlvmIntrinsicKind.SMin) return operands[0].SMin(operands[1]);
        if (kind == LlvmIntrinsicKind.SMax) return operands[0].SMax(operands[1]);
        if (kind == LlvmIntrinsicKind.UMin) return operands[0].UMin(operands[1]);
        if (kind == LlvmIntrinsicKind.UMax) return operands[0].UMax(operands[1]);
        if (kind == LlvmIntrinsicKind.CTPop) return operands[0].CTPop();
        if (kind == LlvmIntrinsicKind.CTTZ)
        {
            var zeroIsPoison = operands[1].GetSingleElement();
            System.Diagnostics.Debug.Assert(zeroIsPoison is { BitWidth: 1 });
            return operands[0].CTTZ(zeroIsPoison!.Value.IsOne);
        }
        throw new InvalidOperationException("Unsupported intrinsic");
    }
    public LlvmConstantRange SMax(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var lower = SMax(GetSignedMin(), other.GetSignedMin());
        var upper = Inc(SMax(GetSignedMax(), other.GetSignedMax()));
        var result = GetNonEmpty(lower, upper);
        return IsSignWrappedSet || other.IsSignWrappedSet
            ? result.IntersectWith(UnionWith(other, LlvmPreferredRangeType.Signed), LlvmPreferredRangeType.Signed) : result;
    }
    public LlvmConstantRange UMax(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var lower = UMax(GetUnsignedMin(), other.GetUnsignedMin());
        var upper = Inc(UMax(GetUnsignedMax(), other.GetUnsignedMax()));
        var result = GetNonEmpty(lower, upper);
        return IsWrappedSet || other.IsWrappedSet
            ? result.IntersectWith(UnionWith(other, LlvmPreferredRangeType.Unsigned), LlvmPreferredRangeType.Unsigned) : result;
    }
    public LlvmConstantRange SMin(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var lower = SMin(GetSignedMin(), other.GetSignedMin());
        var upper = Inc(SMin(GetSignedMax(), other.GetSignedMax()));
        var result = GetNonEmpty(lower, upper);
        return IsSignWrappedSet || other.IsSignWrappedSet
            ? result.IntersectWith(UnionWith(other, LlvmPreferredRangeType.Signed), LlvmPreferredRangeType.Signed) : result;
    }
    public LlvmConstantRange UMin(LlvmConstantRange other)
    {
        if (IsEmptySet || other.IsEmptySet) return GetEmpty();
        var lower = UMin(GetUnsignedMin(), other.GetUnsignedMin());
        var upper = Inc(UMin(GetUnsignedMax(), other.GetUnsignedMax()));
        var result = GetNonEmpty(lower, upper);
        return IsWrappedSet || other.IsWrappedSet
            ? result.IntersectWith(UnionWith(other, LlvmPreferredRangeType.Unsigned), LlvmPreferredRangeType.Unsigned) : result;
    }
}
