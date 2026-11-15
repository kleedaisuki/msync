// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/WithCache.h;
// ValueTracking.cpp haveNoCommonBitsSetSpecialCases/getNoCommonBitsSetResult.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public struct LlvmValueWithCache
{
    public LlvmValue Value { get; }
    public bool HasKnownBits { get; private set; }
    private LlvmKnownBits known;
    public LlvmValueWithCache(LlvmValue value) { Value = value; HasKnownBits = false; known = default; }
    public LlvmValueWithCache(LlvmValue value, LlvmKnownBits known) { Value = value; this.known = known; HasKnownBits = true; }
    internal LlvmKnownBits GetKnownBits(LlvmInstructionSimplify.BinaryQuery query)
    {
        if (!HasKnownBits)
        {
            known = LlvmKnownBitsAnalysis.Compute(Value, context: query.Context, tree: query.Tree, metrics: query.Metrics,
                useInstructionInfo: query.UseInstructionInfo, assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC);
            HasKnownBits = true;
            if (query.Metrics is not null) query.Metrics["value_tracking.with_cache.miss"] = query.Metrics.GetValueOrDefault("value_tracking.with_cache.miss") + 1;
        }
        else if (query.Metrics is not null) query.Metrics["value_tracking.with_cache.hit"] = query.Metrics.GetValueOrDefault("value_tracking.with_cache.hit") + 1;
        return known;
    }
}

public enum LlvmNoCommonBitsSetResult : byte { Unknown, OnlyIfUndefIgnored, Known }

public static partial class LlvmValueTracking
{
    public static LlvmNoCommonBitsSetResult GetNoCommonBitsSetResult(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null)
    {
        var lhs = new LlvmValueWithCache(left); var rhs = new LlvmValueWithCache(right);
        return GetNoCommonBitsSetResult(ref lhs, ref rhs, new(layout, context, metrics, tree, Assumptions: assumptions, AllowEphemerals: allowEphemerals, DC: conditions));
    }
    public static bool HaveNoCommonBitsSet(LlvmValue left, LlvmValue right, LlvmDataLayout layout,
        LlvmInstruction? context = null, LlvmDominatorTree? tree = null, Dictionary<string, ulong>? metrics = null,
        LlvmAssumptionCache? assumptions = null, bool allowEphemerals = false, LlvmDomConditionCache? conditions = null) =>
        GetNoCommonBitsSetResult(left, right, layout, context, tree, metrics, assumptions, allowEphemerals, conditions) == LlvmNoCommonBitsSetResult.Known;

    internal static bool HaveNoCommonBitsSet(ref LlvmValueWithCache left, ref LlvmValueWithCache right,
        LlvmInstructionSimplify.BinaryQuery query) => GetNoCommonBitsSetResult(ref left, ref right, query) == LlvmNoCommonBitsSetResult.Known;

    internal static LlvmNoCommonBitsSetResult GetNoCommonBitsSetResult(ref LlvmValueWithCache left,
        ref LlvmValueWithCache right, LlvmInstructionSimplify.BinaryQuery query)
    {
        Count(query.Metrics, "value_tracking.no_common_bits.queries");
        var result = NoCommonBitsSpecialCases(left.Value, right.Value, query);
        if (result == LlvmNoCommonBitsSetResult.Known) return result;
        var commuted = NoCommonBitsSpecialCases(right.Value, left.Value, query);
        if (commuted == LlvmNoCommonBitsSetResult.Known) return commuted;
        if (LlvmKnownBits.HaveNoCommonBitsSet(left.GetKnownBits(query), right.GetKnownBits(query))) return LlvmNoCommonBitsSetResult.Known;
        return result == LlvmNoCommonBitsSetResult.OnlyIfUndefIgnored || commuted == LlvmNoCommonBitsSetResult.OnlyIfUndefIgnored
            ? LlvmNoCommonBitsSetResult.OnlyIfUndefIgnored : LlvmNoCommonBitsSetResult.Unknown;
    }

    private static LlvmNoCommonBitsSetResult NoCommonBitsSpecialCases(LlvmValue lhs, LlvmValue rhs,
        LlvmInstructionSimplify.BinaryQuery query)
    {
        bool Defined(LlvmValue value) => IsGuaranteedNotToBeUndef(value, query.Tree, query.Context, query.Metrics,
            assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC);
        static LlvmNoCommonBitsSetResult IfDefined(bool defined) => defined ? LlvmNoCommonBitsSetResult.Known : LlvmNoCommonBitsSetResult.OnlyIfUndefIgnored;
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.And } and)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
                if (NoCommonNot(orientation == 0 ? and.Left : and.Right, out var mask) && NoCommonOther(rhs, LlvmOpcode.And, mask, out _)) return IfDefined(Defined(mask));
        }
        if (rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.And } rightAnd)
        {
            if ((NoCommonNot(rightAnd.Left, out var notLeft) && ReferenceEquals(notLeft, lhs))
                || (NoCommonNot(rightAnd.Right, out var notRight) && ReferenceEquals(notRight, lhs))) return IfDefined(Defined(lhs));
        }
        if (rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor)
        {
            for (int orientation = 0; orientation < 2; ++orientation)
            {
                var masked = orientation == 0 ? xor.Left : xor.Right;
                var y = orientation == 0 ? xor.Right : xor.Left;
                if (NoCommonOther(masked, LlvmOpcode.And, lhs, out var other) && ReferenceEquals(other, y)) return IfDefined(Defined(lhs) && Defined(y));
            }
        }
        if (lhs is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } a
            && rhs is LlvmCastInstruction { Opcode: LlvmOpcode.ZExt or LlvmOpcode.SExt } b && NoCommonNot(b.Operand, out var not) && ReferenceEquals(not, a.Operand))
            return IfDefined(Defined(a.Operand));
        if (lhs is LlvmBinaryOperator { Opcode: LlvmOpcode.And } pair && NoCommonNot(rhs, out var inverted)
            && NoCommonOther(inverted, LlvmOpcode.Or, pair.Left, out var remaining) && ReferenceEquals(remaining, pair.Right))
            return IfDefined(Defined(pair.Left) && Defined(pair.Right));
        if (rhs is LlvmBinaryOperator { Opcode: LlvmOpcode.Shl or LlvmOpcode.LShr,
            Right: LlvmBinaryOperator { Opcode: LlvmOpcode.Sub, Left: LlvmConstantInt bound } subtract } rightShift
            && lhs is LlvmBinaryOperator leftShift
            && leftShift.Opcode == (rightShift.Opcode == LlvmOpcode.Shl ? LlvmOpcode.LShr : LlvmOpcode.Shl)
            && ReferenceEquals(leftShift.Right, subtract.Right) && bound.Value.Uge(new(bound.BitWidth, lhs.Type.GetScalarSizeInBits())))
            return LlvmNoCommonBitsSetResult.Known;
        return LlvmNoCommonBitsSetResult.Unknown;
    }
    private static bool NoCommonOther(LlvmValue value, LlvmOpcode opcode, LlvmValue specific, out LlvmValue other)
    {
        if (value is LlvmBinaryOperator binary && binary.Opcode == opcode)
        {
            if (ReferenceEquals(binary.Left, specific)) { other = binary.Right; return true; }
            if (ReferenceEquals(binary.Right, specific)) { other = binary.Left; return true; }
        }
        other = null!; return false;
    }
    private static bool NoCommonNot(LlvmValue value, out LlvmValue operand)
    {
        if (value is LlvmBinaryOperator { Opcode: LlvmOpcode.Xor } xor)
        {
            if (xor.Left is LlvmConstantInt { IsMinusOne: true }) { operand = xor.Right; return true; }
            if (xor.Right is LlvmConstantInt { IsMinusOne: true }) { operand = xor.Left; return true; }
        }
        operand = null!; return false;
    }
}
