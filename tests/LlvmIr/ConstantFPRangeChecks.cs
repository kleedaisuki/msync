// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Properties corresponding to LLVM ConstantFPRangeTest.cpp at
// a55e64432a516fd6507d3ecfba7407cfe425f940, specialized to the binary64 carrier.
using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class ConstantFPRangeChecks
{
    public static int Run()
    {
        ulong[] encodings = [
            0xfff0000000000000, 0xffefffffffffffff, 0xbff0000000000000,
            0x8010000000000000, 0x800fffffffffffff, 0x8000000000000001,
            0x8000000000000000, 0, 1, 0xfffffffffffff, 0x10000000000000,
            0x3ff0000000000000, 0x7fefffffffffffff, 0x7ff0000000000000,
            0x7ff8000000000001, 0xfff8000000000001, 0x7ff0000000000001, 0xfff0000000000001
        ];
        LlvmAPFloat[] values = encodings.Select(LlvmAPFloat.FromBits).ToArray();
        int checks = 0;
        foreach (LlvmAPFloat rhs in values)
        foreach (LlvmCmpPredicate predicate in Enumerable.Range(0, 16).Select(value => (LlvmCmpPredicate)value))
        {
            LlvmConstantFPRange? exact = LlvmConstantFPRange.MakeExactFCmpRegion(predicate, rhs);
            bool unrepresentable = predicate is LlvmCmpPredicate.FCmpOne or LlvmCmpPredicate.FCmpUne && !rhs.IsNaN;
            Require((exact is null) == unrepresentable, $"Exact representability {predicate} rhs={Bits(rhs):x16}");
            if (exact is null) continue;
            foreach (LlvmAPFloat lhs in values)
            {
                Require(exact.Contains(lhs) == Compare(predicate, lhs, rhs),
                    $"Exact membership {predicate} lhs={Bits(lhs):x16} rhs={Bits(rhs):x16}");
                ++checks;
            }
        }

        LlvmConstantFPRange[] ranges = [
            LlvmConstantFPRange.GetEmpty(), LlvmConstantFPRange.GetFull(), LlvmConstantFPRange.GetFinite(),
            LlvmConstantFPRange.GetNonNaN(), new(values[6]), new(values[7]),
            new(values[6], values[7], false, false), new(values[2], values[11], false, false),
            LlvmConstantFPRange.GetNaNOnly(LlvmAPFloatSemantics.IEEEdouble, true, false),
            LlvmConstantFPRange.GetNaNOnly(LlvmAPFloatSemantics.IEEEdouble, false, true),
            new(values[5], values[8], true, true)
        ];
        foreach (LlvmConstantFPRange left in ranges)
        foreach (LlvmConstantFPRange right in ranges)
        foreach (LlvmAPFloat value in values)
        {
            bool l = left.Contains(value), r = right.Contains(value);
            Require(left.IntersectWith(right).Contains(value) == (l && r), "Intersection membership");
            Require(!(l || r) || left.UnionWith(right).Contains(value), "Union containment");
            if (left.Contains(right)) Require(!r || l, "Range containment");
            ++checks;
        }

        foreach (LlvmConstantFPRange other in ranges)
        foreach (LlvmCmpPredicate predicate in Enumerable.Range(0, 16).Select(value => (LlvmCmpPredicate)value))
        foreach (LlvmAPFloat lhs in values)
        {
            LlvmConstantFPRange allowed = LlvmConstantFPRange.MakeAllowedFCmpRegion(predicate, other);
            LlvmConstantFPRange satisfying = LlvmConstantFPRange.MakeSatisfyingFCmpRegion(predicate, other);
            foreach (LlvmAPFloat rhs in values.Where(other.Contains))
            {
                bool truth = Compare(predicate, lhs, rhs);
                Require(!truth || allowed.Contains(lhs), $"Allowed {predicate}");
                Require(!satisfying.Contains(lhs) || truth, $"Satisfying {predicate}");
                ++checks;
            }
        }
        Require(new LlvmConstantFPRange(values[6]).Classify() == LlvmFPClassTest.NegZero, "Negative zero class");
        Require(new LlvmConstantFPRange(values[7]).Classify() == LlvmFPClassTest.PosZero, "Positive zero class");
        Require(new LlvmConstantFPRange(values[6], values[7], false, false).Classify() == LlvmFPClassTest.Zero, "Both zero classes");
        Require(LlvmConstantFPRange.GetFull().Classify() == LlvmFPClassTest.All, "Full class mask");
        return checks;
    }

    private static ulong Bits(LlvmAPFloat value) => value.BitcastToAPInt().GetZExtValue();
    private static bool Compare(LlvmCmpPredicate predicate, LlvmAPFloat lhs, LlvmAPFloat rhs)
    {
        int relation = lhs.Compare(rhs) switch
        {
            LlvmFloatCompare.Equal => 1,
            LlvmFloatCompare.GreaterThan => 2,
            LlvmFloatCompare.LessThan => 4,
            _ => 8
        };
        return ((int)predicate & relation) != 0;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"ConstantFPRange: {message}");
    }
}
