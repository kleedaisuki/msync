// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/BranchProbability.h and Support/BranchProbability.cpp.
using System.Diagnostics;

namespace Msync.Optimization.Llvm;

public readonly struct LlvmBranchProbability : IEquatable<LlvmBranchProbability>, IComparable<LlvmBranchProbability>
{
    public const uint Denominator = 1U << 31;
    // Zero-initialized CLR storage represents the source default UnknownN.
    private readonly uint encodedNumerator;
    public uint Numerator => encodedNumerator ^ uint.MaxValue;
    public bool IsZero => Numerator == 0;
    public bool IsOne => Numerator == Denominator;
    public bool IsUnknown => Numerator == uint.MaxValue;
    public static LlvmBranchProbability Zero => GetRaw(0);
    public static LlvmBranchProbability One => GetRaw(Denominator);
    public static LlvmBranchProbability Unknown => default;

    private LlvmBranchProbability(uint raw) => encodedNumerator = raw ^ uint.MaxValue;
    public LlvmBranchProbability(uint numerator, uint denominator)
    {
        Debug.Assert(denominator > 0 && numerator <= denominator);
        uint raw = denominator == Denominator ? numerator : (uint)(((ulong)numerator * Denominator + denominator / 2) / denominator);
        encodedNumerator = raw ^ uint.MaxValue;
    }
    public static LlvmBranchProbability GetRaw(uint numerator) => new(numerator);
    public static LlvmBranchProbability GetZero() => Zero;
    public static LlvmBranchProbability GetOne() => One;
    public static LlvmBranchProbability GetUnknown() => Unknown;
    public static LlvmBranchProbability GetBranchProbability(ulong numerator, ulong denominator)
    {
        Debug.Assert(numerator <= denominator);
        int scale = 0;
        while (denominator > uint.MaxValue) { denominator >>= 1; ++scale; }
        return new((uint)(numerator >> scale), (uint)denominator);
    }
    public static LlvmBranchProbability GetBranchProbability(double probability)
    {
        Debug.Assert(probability >= 0 && probability <= 1);
        return new((uint)Math.Round(probability * Denominator, MidpointRounding.AwayFromZero), Denominator);
    }
    public double ToDouble() => (double)Numerator / Denominator;
    public LlvmBranchProbability GetCompl() => GetRaw(unchecked(Denominator - Numerator));

    public static LlvmBranchProbability operator +(LlvmBranchProbability left, LlvmBranchProbability right)
    {
        Debug.Assert(!left.IsUnknown && !right.IsUnknown);
        ulong sum = (ulong)left.Numerator + right.Numerator;
        return GetRaw(sum > Denominator ? Denominator : (uint)sum);
    }
    public static LlvmBranchProbability operator -(LlvmBranchProbability left, LlvmBranchProbability right)
    {
        Debug.Assert(!left.IsUnknown && !right.IsUnknown);
        return GetRaw(left.Numerator < right.Numerator ? 0 : left.Numerator - right.Numerator);
    }
    public static LlvmBranchProbability operator *(LlvmBranchProbability left, LlvmBranchProbability right)
    {
        Debug.Assert(!left.IsUnknown && !right.IsUnknown);
        return GetRaw((uint)(((ulong)left.Numerator * right.Numerator + Denominator / 2) / Denominator));
    }
    public static LlvmBranchProbability operator *(LlvmBranchProbability left, uint right)
    {
        Debug.Assert(!left.IsUnknown);
        ulong product = (ulong)left.Numerator * right;
        return GetRaw(product > Denominator ? Denominator : (uint)product);
    }
    public static LlvmBranchProbability operator /(LlvmBranchProbability left, LlvmBranchProbability right)
    {
        Debug.Assert(!left.IsUnknown && !right.IsUnknown);
        return GetRaw(unchecked((uint)(((ulong)left.Numerator * Denominator + right.Numerator / 2) / right.Numerator)));
    }
    public static LlvmBranchProbability operator /(LlvmBranchProbability left, uint right)
    {
        Debug.Assert(!left.IsUnknown && right > 0);
        return GetRaw(left.Numerator / right);
    }
    public LlvmBranchProbability Pow(uint exponent)
    {
        LlvmBranchProbability result = One;
        for (uint index = 0; index < exponent; ++index) result *= this;
        return result;
    }

    public ulong Scale(ulong value) => Scale(value, Numerator, Denominator);
    public ulong ScaleByInverse(ulong value) => Scale(value, Denominator, Numerator);
    private static ulong Scale(ulong value, uint numerator, uint denominator)
    {
        Debug.Assert(denominator != 0);
        if (value == 0 || denominator == numerator) return value;
        ulong highProduct = (value >> 32) * numerator;
        ulong lowProduct = (value & uint.MaxValue) * numerator;
        uint upper = (uint)(highProduct >> 32), lower = unchecked((uint)lowProduct);
        uint partial = unchecked((uint)highProduct);
        uint middle = unchecked(partial + (uint)(lowProduct >> 32));
        upper = unchecked(upper + (middle < partial ? 1U : 0U));
        ulong remainder = ((ulong)upper << 32) | middle;
        ulong upperQuotient = remainder / denominator;
        if (upperQuotient > uint.MaxValue) return ulong.MaxValue;
        remainder = ((remainder % denominator) << 32) | lower;
        ulong lowerQuotient = remainder / denominator;
        ulong quotient = unchecked((upperQuotient << 32) + lowerQuotient);
        return quotient < lowerQuotient ? ulong.MaxValue : quotient;
    }

    public static void NormalizeProbabilities(Span<LlvmBranchProbability> probabilities)
    {
        if (probabilities.IsEmpty) return;
        uint unknownCount = 0;
        ulong sum = 0;
        foreach (LlvmBranchProbability probability in probabilities)
            if (probability.IsUnknown) ++unknownCount; else sum += probability.Numerator;
        if (unknownCount > 0)
        {
            LlvmBranchProbability unknown = sum < Denominator ? GetRaw((uint)((Denominator - sum) / unknownCount)) : Zero;
            for (int index = 0; index < probabilities.Length; ++index)
                if (probabilities[index].IsUnknown) probabilities[index] = unknown;
            if (sum <= Denominator) return;
        }
        if (sum == 0)
        { probabilities.Fill(new(1, (uint)probabilities.Length)); return; }
        for (int index = 0; index < probabilities.Length; ++index)
            probabilities[index] = GetRaw((uint)(((ulong)probabilities[index].Numerator * Denominator + sum / 2) / sum));
    }

    public int CompareTo(LlvmBranchProbability other)
    { Debug.Assert(!IsUnknown && !other.IsUnknown); return Numerator.CompareTo(other.Numerator); }
    public bool Equals(LlvmBranchProbability other) => Numerator == other.Numerator;
    public override bool Equals(object? obj) => obj is LlvmBranchProbability other && Equals(other);
    public override int GetHashCode() => Numerator.GetHashCode();
    public static bool operator ==(LlvmBranchProbability left, LlvmBranchProbability right) => left.Equals(right);
    public static bool operator !=(LlvmBranchProbability left, LlvmBranchProbability right) => !left.Equals(right);
    public static bool operator <(LlvmBranchProbability left, LlvmBranchProbability right) => left.CompareTo(right) < 0;
    public static bool operator >(LlvmBranchProbability left, LlvmBranchProbability right) => left.CompareTo(right) > 0;
    public static bool operator <=(LlvmBranchProbability left, LlvmBranchProbability right) => left.CompareTo(right) <= 0;
    public static bool operator >=(LlvmBranchProbability left, LlvmBranchProbability right) => left.CompareTo(right) >= 0;
}
