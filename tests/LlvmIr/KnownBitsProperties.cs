using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class KnownBitsProperties
{
    public static long Run()
    {
        long checkedValues = 0;
        for (uint width = 1; width <= 4; ++width)
        {
            uint limit = 1U << (int)width, mask = limit - 1;
            var states = States(width).ToArray();
            foreach (LlvmKnownBits left in states)
            foreach (LlvmKnownBits right in states)
            {
                Check(left & right, (a, b) => a & b);
                Check(left | right, (a, b) => a | b);
                Check(left ^ right, (a, b) => a ^ b);
                Check(LlvmKnownBits.AddSub(true, false, false, left, right), (a, b) => (a + b) & mask);
                Check(LlvmKnownBits.AddSub(false, false, false, left, right), (a, b) => (a - b) & mask);
                Check(LlvmKnownBits.Mul(left, right), (a, b) => a * b & mask);
                Check(LlvmKnownBits.UDiv(left, right), (a, b) => b == 0 ? null : a / b);
                Check(LlvmKnownBits.URem(left, right), (a, b) => b == 0 ? null : a % b);
                Check(LlvmKnownBits.SDiv(left, right), (a, b) => b == 0 || (Signed(a) == -(int)(limit / 2) && Signed(b) == -1) ? null : unchecked((uint)(Signed(a) / Signed(b))) & mask);
                Check(LlvmKnownBits.SRem(left, right), (a, b) => b == 0 || (Signed(a) == -(int)(limit / 2) && Signed(b) == -1) ? null : unchecked((uint)(Signed(a) % Signed(b))) & mask);
                foreach (LlvmOpcode opcode in new[] { LlvmOpcode.Shl, LlvmOpcode.LShr, LlvmOpcode.AShr })
                    Check(LlvmKnownBits.Shift(opcode, left, right), (a, b) => b >= width ? null : opcode switch
                    {
                        LlvmOpcode.Shl => a << (int)b & mask,
                        LlvmOpcode.LShr => a >> (int)b,
                        _ => unchecked((uint)(Signed(a) >> (int)b)) & mask
                    });

                int Signed(uint value) => (value & (limit / 2)) == 0 ? (int)value : (int)value - (int)limit;
                void Check(LlvmKnownBits result, Func<uint, uint, uint?> operation)
                {
                    for (uint a = 0; a < limit; ++a)
                    {
                        if (!Matches(left, a)) continue;
                        for (uint b = 0; b < limit; ++b)
                        {
                            if (!Matches(right, b) || operation(a, b) is not uint value) continue;
                            if (!Matches(result, value))
                                throw new InvalidOperationException($"KnownBits i{width}: {a}, {b} -> {value}, Zero={result.Zero.GetZExtValue()}, One={result.One.GetZExtValue()}");
                            checkedValues++;
                        }
                    }
                }
            }
        }
        return checkedValues;
    }
    private static bool Matches(LlvmKnownBits bits, uint value) => (value & bits.Zero.GetZExtValue()) == 0 && (value & bits.One.GetZExtValue()) == bits.One.GetZExtValue();
    private static IEnumerable<LlvmKnownBits> States(uint width)
    {
        uint limit = 1U << (int)width;
        for (uint zero = 0; zero < limit; ++zero)
        for (uint one = 0; one < limit; ++one)
            if ((zero & one) == 0) yield return new(new LlvmAPInt(width, zero), new LlvmAPInt(width, one));
    }
}
