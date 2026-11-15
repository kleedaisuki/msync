using Msync.Optimization.Llvm;

internal static class InstructionCostProperties
{
    internal static int Run()
    {
        int checks = 0;
        long[] rawValues = [long.MinValue, long.MinValue + 1, -400, -17, -4, -3, -1, 0, 1, 3, 4, 17, 400, long.MaxValue - 1, long.MaxValue];
        foreach (long raw in rawValues)
        {
            LlvmInstructionCost cost = FromRaw(raw);
            Require(cost.IsValid && cost.Value == raw / 4, "Cost Value truncates fixed-point quarters toward zero.");
            Require((cost == LlvmInstructionCost.Free) == (raw == 0), "A fractional nonzero cost is not free.");
            Require(new LlvmInstructionCost(raw) == FromRaw(Clamp((Int128)raw * 4)), "Constructor saturates signed fixed-point scaling.");
            foreach (long other in rawValues)
            {
                LlvmInstructionCost rhs = FromRaw(other);
                Require(cost + rhs == FromRaw(Clamp((Int128)raw + other)), "Cost addition saturates its raw signed value.");
                Require(cost - rhs == FromRaw(Clamp((Int128)raw - other)), "Cost subtraction saturates its raw signed value.");
                Int128 product = (Int128)raw * other;
                long multiplied = product > long.MaxValue || product < long.MinValue ? Clamp(product) : (long)product / 4;
                Require(cost * rhs == FromRaw(multiplied), "Cost multiply saturates before removing fixed-point scale.");
                Require((cost < rhs) == (raw < other) && (cost == rhs) == (raw == other), "Cost comparison preserves fractional distinctions.");
                long numerator = Clamp((Int128)raw * 4);
                if (other != 0 && !(numerator == long.MinValue && other == -1))
                    Require(cost / rhs == FromRaw(numerator / other), "Cost division scales with saturation before dividing.");
                foreach (bool leftInvalid in new[] { false, true })
                {
                    LlvmInstructionCost left = cost, right = rhs;
                    if (leftInvalid) left.SetInvalid(); else right.SetInvalid();
                    Require((left + right).IsInvalid && (left - right).IsInvalid && (left * right).IsInvalid,
                        "Either invalid operand propagates through arithmetic.");
                }
            }
        }
        LlvmInstructionCost quarter = LlvmInstructionCost.Basic / 4;
        Require(quarter.Value == 0 && !quarter.IsFree && quarter * 4 == LlvmInstructionCost.Basic,
            "Scalar division retains sub-unit cost precision.");
        Require(LlvmInstructionCost.Expensive == new LlvmInstructionCost(4), "TTI expensive instruction constant is four basic costs.");
        Require(LlvmInstructionCost.Max < LlvmInstructionCost.GetInvalid(long.MinValue), "Every valid cost sorts below every invalid cost.");
        LlvmInstructionCost invalid = LlvmInstructionCost.GetInvalid(-2);
        Require(invalid < LlvmInstructionCost.Invalid && invalid != LlvmInstructionCost.Invalid, "Invalid costs retain raw-value ordering.");
        invalid.SetValid();
        Require(invalid == new LlvmInstructionCost(-2), "Explicit state change preserves numeric payload.");
        bool mapped = false;
        Require(LlvmInstructionCost.Invalid.Map(value => { mapped = true; return value; }).IsInvalid && !mapped,
            "Mapping an invalid cost does not invoke the callback.");
        Require(quarter.Map(value => value) == LlvmInstructionCost.Basic, "Source map callback receives raw fixed-point value.");
        LlvmInstructionCost counter = quarter;
        Require(counter++ == quarter && counter == quarter + 1 && --counter == quarter,
            "Increment and decrement preserve fractional payload and return source values.");
        return checks;

        void Require(bool condition, string message) { Program.Require(condition, message); ++checks; }
    }

    private static LlvmInstructionCost FromRaw(long raw) => new LlvmInstructionCost(raw / 4) + new LlvmInstructionCost(raw % 4) / 4;
    private static long Clamp(Int128 value) => value > long.MaxValue ? long.MaxValue : value < long.MinValue ? long.MinValue : (long)value;
}
