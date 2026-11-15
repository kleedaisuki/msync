using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class EquivalentIcmpProperties
{
    internal static long Run()
    {
        long checkedValues = 0;
        for (uint width = 1; width <= 5; ++width)
        {
            ulong size = 1UL << (int)width;
            List<LlvmConstantRange> ranges = [LlvmConstantRange.Empty(width), LlvmConstantRange.Full(width)];
            for (ulong lower = 0; lower < size; ++lower)
                for (ulong upper = 0; upper < size; ++upper)
                    if (lower != upper) ranges.Add(new(new LlvmAPInt(width, lower), new LlvmAPInt(width, upper)));
            foreach (var range in ranges)
            {
                range.GetEquivalentICmp(out var predicate, out var rhs, out var offset);
                bool noOffset = range.GetEquivalentICmp(out var directPredicate, out var directRhs);
                if (noOffset != offset.IsZero || directPredicate != predicate || directRhs != rhs)
                    throw new InvalidOperationException("EquivalentICmp overload disagreement.");
                for (ulong value = 0; value < size; ++value)
                {
                    var bits = new LlvmAPInt(width, value);
                    bool actual = Compare(predicate, bits + offset, rhs);
                    if (actual != range.Contains(bits))
                        throw new InvalidOperationException($"EquivalentICmp i{width} [{range.Lower},{range.Upper}) lost {value}.");
                    ++checkedValues;
                }
            }
        }
        return checkedValues;
    }

    private static bool Compare(LlvmCmpPredicate predicate, LlvmAPInt left, LlvmAPInt right) => predicate switch
    {
        LlvmCmpPredicate.ICmpEq => left == right,
        LlvmCmpPredicate.ICmpNe => left != right,
        LlvmCmpPredicate.ICmpUlt => left.Ult(right),
        LlvmCmpPredicate.ICmpUge => left.Uge(right),
        LlvmCmpPredicate.ICmpSlt => left.Slt(right),
        LlvmCmpPredicate.ICmpSge => left.Sge(right),
        _ => throw new InvalidOperationException("Unexpected range-comparison predicate.")
    };
}
