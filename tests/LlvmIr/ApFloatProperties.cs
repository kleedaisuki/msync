using Msync.Core.Llvm;

internal static class ApFloatProperties
{
    internal static int Run()
    {
        ulong[] edges = [0, 0x8000000000000000, 1, 0x8000000000000001,
            0x000fffffffffffff, 0x0010000000000000, 0x3fefffffffffffff,
            0x3ff0000000000000, 0x3ff0000000000001, 0x4000000000000000,
            0xbff0000000000000, 0x7fefffffffffffff, 0xffefffffffffffff,
            0x7ff0000000000000, 0xfff0000000000000, 0x7ff8000000000123, 0x7ff0000000000042];
        int pairs = 0;
        foreach (ulong left in edges)
            foreach (ulong right in edges) { Pair(left, right); pairs++; }
        Random random = new(0x46504c4c);
        for (int index = 0; index < 96; index++)
        {
            byte[] bytes = new byte[16]; random.NextBytes(bytes);
            Pair(BitConverter.ToUInt64(bytes, 0), BitConverter.ToUInt64(bytes, 8)); pairs++;
        }
        Status(1, 0, LlvmFloatStatus.DivByZero);
        Status(0, 0, LlvmFloatStatus.InvalidOp);
        LlvmAPFloat signaling = LlvmAPFloat.FromBits(0x7ff0000000000042);
        LlvmAPFloatResult quieted = signaling.Add(new(1));
        Program.Require(quieted.Status == LlvmFloatStatus.InvalidOp
            && quieted.Value.BitcastToAPInt().GetZExtValue() == 0x7ff8000000000042,
            "APFloat must quiet a signaling NaN without losing its payload.");
        foreach (double value in new[] { -5.5, -2.5, -.5, -0d, 0d, .5, 2.5, 5.5, 1e30 })
        {
            LlvmAPFloat number = new(value);
            Bits(number.RoundToIntegral(LlvmRoundingMode.NearestTiesToEven).Value, Math.Round(value, MidpointRounding.ToEven), "round-even");
            Bits(number.RoundToIntegral(LlvmRoundingMode.NearestTiesToAway).Value, Math.Round(value, MidpointRounding.AwayFromZero), "round-away");
            Bits(number.RoundToIntegral(LlvmRoundingMode.TowardZero).Value, Math.Truncate(value), "round-zero");
            Bits(number.RoundToIntegral(LlvmRoundingMode.TowardPositive).Value, Math.Ceiling(value), "round-positive");
            Bits(number.RoundToIntegral(LlvmRoundingMode.TowardNegative).Value, Math.Floor(value), "round-negative");
        }
        return pairs;
    }

    private static void Pair(ulong left, ulong right)
    {
        LlvmAPFloat a = LlvmAPFloat.FromBits(left), b = LlvmAPFloat.FromBits(right);
        double x = BitConverter.UInt64BitsToDouble(left), y = BitConverter.UInt64BitsToDouble(right);
        string inputs = $"({left:X16}, {right:X16})";
        Bits(a.Add(b).Value, x + y, "add " + inputs);
        Bits(a.Subtract(b).Value, x - y, "subtract " + inputs);
        Bits(a.Multiply(b).Value, x * y, "multiply " + inputs);
        Bits(a.Divide(b).Value, x / y, "divide " + inputs);
        Bits(a.Mod(b).Value, x % y, "mod " + inputs);
        Bits(a.Remainder(b).Value, Math.IEEERemainder(x, y), "remainder " + inputs);
        LlvmFloatCompare expected = double.IsNaN(x) || double.IsNaN(y) ? LlvmFloatCompare.Unordered
            : x < y ? LlvmFloatCompare.LessThan : x > y ? LlvmFloatCompare.GreaterThan : LlvmFloatCompare.Equal;
        Program.Require(a.Compare(b) == expected, "APFloat compare " + inputs);
    }

    private static void Status(double left, double right, LlvmFloatStatus expected) =>
        Program.Require(new LlvmAPFloat(left).Divide(new(right)).Status == expected,
            $"APFloat divide({left}, {right}) status must be {expected}.");

    private static void Bits(LlvmAPFloat actual, double expected, string operation)
    {
        if (double.IsNaN(expected))
        {
            Program.Require(actual.IsNaN, "APFloat " + operation + ": expected NaN.");
            return;
        }
        ulong bits = actual.BitcastToAPInt().GetZExtValue(), wanted = BitConverter.DoubleToUInt64Bits(expected);
        Program.Require(bits == wanted, $"APFloat {operation}: actual {bits:X16}, expected {wanted:X16}.");
    }
}
