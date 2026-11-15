using System.Numerics;
using Msync.Core.Llvm;

internal static class ApIntProperties
{
    internal static int Run()
    {
        Random random = new(0x4c4c564d);
        int checkedPairs = 0;
        foreach (uint width in new uint[] { 1, 8, 19, 32, 64, 65, 127, 128, 192, 257 })
        {
            BigInteger modulus = BigInteger.One << (int)width;
            BigInteger mask = modulus - 1;
            BigInteger[] edges = [0, 1, mask, modulus >> 1, (modulus >> 1) - 1];
            foreach (BigInteger a in edges)
                foreach (BigInteger b in edges) { Check(From(width, a), From(width, b)); checkedPairs++; }
            for (int index = 0; index < 24; index++)
            {
                byte[] first = new byte[(width + 7) / 8], second = new byte[first.Length];
                random.NextBytes(first); random.NextBytes(second);
                Check(From(width, new BigInteger(first, isUnsigned: true) & mask),
                    From(width, new BigInteger(second, isUnsigned: true) & mask));
                checkedPairs++;
            }
        }
        return checkedPairs;
    }

    private static void Check(LlvmAPInt left, LlvmAPInt right)
    {
        uint width = left.BitWidth;
        BigInteger a = Unsigned(left), b = Unsigned(right);
        BigInteger sa = Signed(left), sb = Signed(right);
        BigInteger limit = BigInteger.One << (int)width, mask = limit - 1;
        string context = $"APInt i{width} left={a} right={b}";
        Equal(left + right, a + b, "add");
        Equal(left - right, a - b, "subtract");
        Equal(left * right, a * b, "multiply");
        Equal(left & right, a & b, "and");
        Equal(left | right, a | b, "or");
        Equal(left ^ right, a ^ b, "xor");
        Equal(~left, ~a, "not");
        Equal(-left, -a, "negate");
        Program.Require(Math.Sign(left.Compare(right)) == Math.Sign(a.CompareTo(b)), context + ": unsigned comparison");
        Program.Require(Math.Sign(left.CompareSigned(right)) == Math.Sign(sa.CompareTo(sb)), context + ": signed comparison");
        left.UAddOverflow(right, out bool unsignedAdd);
        left.USubOverflow(right, out bool unsignedSub);
        left.UMulOverflow(right, out bool unsignedMul);
        left.SAddOverflow(right, out bool signedAdd);
        left.SSubOverflow(right, out bool signedSub);
        left.SMulOverflow(right, out bool signedMul);
        Program.Require(unsignedAdd == (a + b >= limit) && unsignedSub == (a < b)
            && unsignedMul == (a * b >= limit), context + ": unsigned overflow");
        BigInteger minimum = -(limit >> 1), maximum = (limit >> 1) - 1;
        Program.Require(signedAdd == Outside(sa + sb) && signedSub == Outside(sa - sb)
            && signedMul == Outside(sa * sb), context + ": signed overflow");
        foreach (uint shift in new[] { 0u, 1u, Math.Min(31u, width), Math.Min(64u, width), width })
        {
            Equal(left.Shl(shift), a << (int)shift, "shift-left " + shift);
            Equal(left.LShr(shift), a >> (int)shift, "logical-shift-right " + shift);
            Equal(left.AShr(shift), sa >> (int)shift, "arithmetic-shift-right " + shift);
        }
        if (width > 1) Equal(left.Trunc(width - 1).ZExt(width), a & ((limit >> 1) - 1), "truncate");
        Program.Require(Unsigned(left.ZExt(width + 67)) == a, context + ": zero extension");
        Program.Require(Signed(left.SExt(width + 67)) == sa, context + ": sign extension");
        if (b.IsZero) return;
        Equal(left.UDiv(right), a / b, "unsigned division");
        Equal(left.URem(right), a % b, "unsigned remainder");
        Equal(left.SDiv(right), sa / sb, "signed division");
        Equal(left.SRem(right), sa % sb, "signed remainder");
        LlvmAPInt.UDivRem(left, right, out LlvmAPInt quotient, out LlvmAPInt remainder);
        Equal(quotient, a / b, "combined unsigned quotient");
        Equal(remainder, a % b, "combined unsigned remainder");

        bool Outside(BigInteger value) => value < minimum || value > maximum;
        void Equal(LlvmAPInt actual, BigInteger expected, string operation) =>
            Program.Require(Unsigned(actual) == (expected & mask),
                $"{context}: {operation}; actual={Unsigned(actual)}, expected={expected & mask}");
    }

    private static LlvmAPInt From(uint width, BigInteger value)
    {
        ulong[] words = new ulong[(width + 63) / 64];
        for (int index = 0; index < words.Length; index++)
        {
            words[index] = (ulong)(value & ulong.MaxValue);
            value >>= 64;
        }
        return new(width, words);
    }
    private static BigInteger Unsigned(LlvmAPInt value)
    {
        BigInteger result = 0;
        for (uint index = value.NumWords; index != 0; index--) result = (result << 64) | value.GetWord(index - 1);
        return result;
    }
    private static BigInteger Signed(LlvmAPInt value) => value.IsNegative
        ? Unsigned(value) - (BigInteger.One << (int)value.BitWidth) : Unsigned(value);
}
