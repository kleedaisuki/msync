// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/InstructionCost.h; Analysis/TargetTransformInfo.h TargetCostConstants.
using System.Diagnostics;

namespace Msync.Optimization.Llvm;

public enum LlvmInstructionCostState : byte { Valid, Invalid }

public struct LlvmInstructionCost : IEquatable<LlvmInstructionCost>, IComparable<LlvmInstructionCost>
{
    private const long Granularity = 4;
    private long scaledValue;
    public LlvmInstructionCostState State { get; private set; }

    public LlvmInstructionCost(long value)
    {
        scaledValue = SaturatingMultiply(value, Granularity, out _);
        State = LlvmInstructionCostState.Valid;
    }

    private LlvmInstructionCost(long scaledValue, LlvmInstructionCostState state)
    { this.scaledValue = scaledValue; State = state; }

    public long Value { get { Debug.Assert(IsValid); return scaledValue / Granularity; } }
    public bool IsValid => State == LlvmInstructionCostState.Valid;
    public bool IsInvalid => !IsValid;
    public bool IsFree => this == Free;
    public static LlvmInstructionCost Free => new(0);
    public static LlvmInstructionCost Basic => new(1);
    public static LlvmInstructionCost Expensive => new(4);
    public static LlvmInstructionCost Max => new(long.MaxValue);
    public static LlvmInstructionCost Min => new(long.MinValue);
    public static LlvmInstructionCost Invalid => GetInvalid();
    public static LlvmInstructionCost GetMax() => Max;
    public static LlvmInstructionCost GetMin() => Min;
    public static LlvmInstructionCost GetInvalid(long value = 0) => new(value) { State = LlvmInstructionCostState.Invalid };
    public void SetValid() => State = LlvmInstructionCostState.Valid;
    public void SetInvalid() => State = LlvmInstructionCostState.Invalid;

    public static implicit operator LlvmInstructionCost(long value) => new(value);

    public static LlvmInstructionCost operator +(LlvmInstructionCost left, LlvmInstructionCost right)
    {
        long result = unchecked(left.scaledValue + right.scaledValue);
        if (((left.scaledValue ^ result) & (right.scaledValue ^ result)) < 0)
            result = right.scaledValue > 0 ? long.MaxValue : long.MinValue;
        return new(result, CombinedState(left, right));
    }

    public static LlvmInstructionCost operator -(LlvmInstructionCost left, LlvmInstructionCost right)
    {
        long result = unchecked(left.scaledValue - right.scaledValue);
        if (((left.scaledValue ^ right.scaledValue) & (left.scaledValue ^ result)) < 0)
            result = right.scaledValue > 0 ? long.MinValue : long.MaxValue;
        return new(result, CombinedState(left, right));
    }

    public static LlvmInstructionCost operator *(LlvmInstructionCost left, LlvmInstructionCost right)
    {
        long result = SaturatingMultiply(left.scaledValue, right.scaledValue, out bool overflow);
        if (!overflow) result /= Granularity;
        return new(result, CombinedState(left, right));
    }

    public static LlvmInstructionCost operator /(LlvmInstructionCost left, LlvmInstructionCost right)
    {
        long result = SaturatingMultiply(left.scaledValue, Granularity, out _) / right.scaledValue;
        return new(result, CombinedState(left, right));
    }

    // The source scalar divisor overload does not rescale or construct a cost.
    public static LlvmInstructionCost operator /(LlvmInstructionCost left, long right) =>
        new(left.scaledValue / right, left.State);
    public static LlvmInstructionCost operator ++(LlvmInstructionCost value) => value + Basic;
    public static LlvmInstructionCost operator --(LlvmInstructionCost value) => value - Basic;

    public int CompareTo(LlvmInstructionCost other)
    {
        int stateOrder = State.CompareTo(other.State);
        return stateOrder != 0 ? stateOrder : scaledValue.CompareTo(other.scaledValue);
    }
    public bool Equals(LlvmInstructionCost other) => State == other.State && scaledValue == other.scaledValue;
    public override bool Equals(object? obj) => obj is LlvmInstructionCost other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(scaledValue, State);
    public static bool operator ==(LlvmInstructionCost left, LlvmInstructionCost right) => left.Equals(right);
    public static bool operator !=(LlvmInstructionCost left, LlvmInstructionCost right) => !left.Equals(right);
    public static bool operator <(LlvmInstructionCost left, LlvmInstructionCost right) => left.CompareTo(right) < 0;
    public static bool operator >(LlvmInstructionCost left, LlvmInstructionCost right) => left.CompareTo(right) > 0;
    public static bool operator <=(LlvmInstructionCost left, LlvmInstructionCost right) => left.CompareTo(right) <= 0;
    public static bool operator >=(LlvmInstructionCost left, LlvmInstructionCost right) => left.CompareTo(right) >= 0;

    public LlvmInstructionCost Map(Func<long, LlvmInstructionCost> function) => IsValid ? function(scaledValue) : Invalid;

    private static LlvmInstructionCostState CombinedState(LlvmInstructionCost left, LlvmInstructionCost right) =>
        left.IsInvalid || right.IsInvalid ? LlvmInstructionCostState.Invalid : LlvmInstructionCostState.Valid;

    private static long SaturatingMultiply(long left, long right, out bool overflow)
    {
        long high = Math.BigMul(left, right, out long low);
        overflow = high != (low >> 63);
        if (!overflow) return low;
        return (left > 0 && right > 0) || (left < 0 && right < 0) ? long.MaxValue : long.MinValue;
    }
}
