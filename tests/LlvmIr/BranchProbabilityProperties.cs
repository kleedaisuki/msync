using Msync.Optimization.Llvm;

internal static class BranchProbabilityProperties
{
    internal static int Run()
    {
        int checks = 0;
        uint denominator = LlvmBranchProbability.Denominator;
        Require(default(LlvmBranchProbability).IsUnknown && new LlvmBranchProbability().IsUnknown,
            "Source default branch probability is unknown, including CLR zero-initialized storage.");
        Require(new LlvmBranchProbability(99, 100).Numerator == 2126008812U,
            "Predictable branch threshold uses rounded 31-bit probability, not a floating ratio.");
        uint[] numerators = [0, 1, 3, denominator / 3, denominator / 2, denominator - 1, denominator];
        ulong[] values = [0, 1, 3, uint.MaxValue, 1UL << 32, 1UL << 63, ulong.MaxValue - 1, ulong.MaxValue];
        foreach (uint numerator in numerators)
        {
            LlvmBranchProbability probability = LlvmBranchProbability.GetRaw(numerator);
            Require(probability.GetCompl().Numerator == denominator - numerator && probability.GetCompl().GetCompl() == probability,
                "Complement preserves exact fixed-denominator representation.");
            foreach (ulong value in values)
            {
                Require(probability.Scale(value) == (ulong)((UInt128)value * numerator / denominator),
                    "Scale computes full-precision floor across 64-bit boundaries.");
                if (numerator != 0)
                {
                    UInt128 inverse = (UInt128)value * denominator / numerator;
                    Require(probability.ScaleByInverse(value) == (inverse > ulong.MaxValue ? ulong.MaxValue : (ulong)inverse),
                        "Inverse scale computes full-precision floor and saturates overflow.");
                }
            }
            foreach (uint other in numerators)
            {
                LlvmBranchProbability rhs = LlvmBranchProbability.GetRaw(other);
                Require((probability + rhs).Numerator == Math.Min((ulong)numerator + other, denominator), "Probability addition saturates at one.");
                Require((probability - rhs).Numerator == (numerator < other ? 0 : numerator - other), "Probability subtraction saturates at zero.");
                Require((probability * rhs).Numerator == ((ulong)numerator * other + denominator / 2) / denominator,
                    "Probability multiplication rounds to the nearest fixed-point unit.");
                if (other != 0)
                    Require((probability / rhs).Numerator == unchecked((uint)(((ulong)numerator * denominator + other / 2) / other)),
                        "Probability division retains source rounded result and uint narrowing.");
            }
            LlvmBranchProbability powered = LlvmBranchProbability.One;
            for (uint exponent = 0; exponent < 6; ++exponent)
            {
                Require(probability.Pow(exponent) == powered, "Source power rounds at every multiplication.");
                powered *= probability;
            }
        }
        LlvmBranchProbability[] unknown = [new(1, 4), LlvmBranchProbability.Unknown, LlvmBranchProbability.Unknown];
        LlvmBranchProbability.NormalizeProbabilities(unknown);
        Require(unknown[0] == new LlvmBranchProbability(1, 4) && unknown[1] == new LlvmBranchProbability(3, 8) && unknown[2] == unknown[1],
            "Normalization distributes missing probability among unknown edges.");
        LlvmBranchProbability[] zeros = [LlvmBranchProbability.Zero, LlvmBranchProbability.Zero, LlvmBranchProbability.Zero];
        LlvmBranchProbability.NormalizeProbabilities(zeros);
        Require(Array.TrueForAll(zeros, p => p == new LlvmBranchProbability(1, 3)), "All-zero edges normalize to equal probabilities.");
        LlvmBranchProbability[] excessive = [LlvmBranchProbability.One, LlvmBranchProbability.One, LlvmBranchProbability.Unknown];
        LlvmBranchProbability.NormalizeProbabilities(excessive);
        Require(excessive[0] == new LlvmBranchProbability(1, 2) && excessive[1] == excessive[0] && excessive[2].IsZero,
            "Overfull known edges normalize while unknown edges receive zero.");
        Require(LlvmBranchProbability.GetBranchProbability(ulong.MaxValue / 2, ulong.MaxValue) ==
            new LlvmBranchProbability(uint.MaxValue / 2, uint.MaxValue), "64-bit construction preserves source common right-shift reduction.");
        return checks;
        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }
}
