using Msync.Core.Llvm;

internal static class ExactNoWrapProperties
{
    internal static int Run()
    {
        int cases = 0;
        foreach (uint width in new uint[] { 1, 2, 3, 4, 5 })
        {
            long size = 1L << (int)width, maximum = size - 1, signedMinimum = -(size >> 1), signedMaximum = (size >> 1) - 1;
            foreach (LlvmOpcode opcode in new[] { LlvmOpcode.Add, LlvmOpcode.Sub, LlvmOpcode.Mul, LlvmOpcode.Shl })
                for (long rhs = 0; rhs < size; ++rhs)
                    foreach (uint flags in new uint[] { 1, 2 })
                    {
                        LlvmConstantRange region = LlvmConstantRange.MakeExactNoWrapRegion(opcode, new(width, (ulong)rhs), flags);
                        for (long lhs = 0; lhs < size; ++lhs)
                        {
                            long left = flags == 1 || lhs <= signedMaximum ? lhs : lhs - size;
                            long right = flags == 1 || rhs <= signedMaximum ? rhs : rhs - size;
                            long result = opcode switch
                            {
                                LlvmOpcode.Add => left + right,
                                LlvmOpcode.Sub => left - right,
                                LlvmOpcode.Mul => left * right,
                                _ => left << (int)rhs
                            };
                            bool expected = opcode == LlvmOpcode.Shl && rhs >= width ||
                                (flags == 1 ? result >= 0 && result <= maximum : result >= signedMinimum && result <= signedMaximum);
                            Program.Require(region.Contains(new LlvmAPInt(width, (ulong)lhs)) == expected,
                                $"Exact no-wrap {opcode}/i{width}/flags{flags}: {lhs}, {rhs} must match integer bounds.");
                            ++cases;
                        }
                    }
            for (long lhs = signedMinimum; lhs <= signedMaximum; ++lhs)
                for (long rhs = signedMinimum; rhs <= signedMaximum; ++rhs)
                {
                    if (rhs == 0 || lhs == signedMinimum && rhs == -1) continue;
                    long quotient = lhs / rhs, remainder = lhs % rhs;
                    long down = quotient - (remainder != 0 && (remainder < 0) != (rhs < 0) ? 1 : 0);
                    long up = quotient + (remainder != 0 && (remainder < 0) == (rhs < 0) ? 1 : 0);
                    var left = new LlvmAPInt(width, unchecked((ulong)lhs), isSigned: true);
                    var right = new LlvmAPInt(width, unchecked((ulong)rhs), isSigned: true);
                    Program.Require(LlvmAPInt.RoundingSDiv(left, right, LlvmAPIntRounding.Down).GetSExtValue() == down &&
                        LlvmAPInt.RoundingSDiv(left, right, LlvmAPIntRounding.Up).GetSExtValue() == up &&
                        LlvmAPInt.RoundingSDiv(left, right, LlvmAPIntRounding.TowardZero).GetSExtValue() == quotient,
                        "Signed APInt rounding must round the mathematical remainder rather than assume a positive divisor.");
                    ++cases;
                }
        }
        return cases;
    }
}
