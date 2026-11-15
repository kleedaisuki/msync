using Msync.Core.Llvm;
using Msync.Optimization.Llvm;

internal static class ConstantRangeProbe
{
    private static readonly LlvmOpcode[] Operations = [LlvmOpcode.Add, LlvmOpcode.Sub, LlvmOpcode.Mul,
        LlvmOpcode.UDiv, LlvmOpcode.SDiv, LlvmOpcode.URem, LlvmOpcode.SRem, LlvmOpcode.And,
        LlvmOpcode.Or, LlvmOpcode.Xor, LlvmOpcode.Shl, LlvmOpcode.LShr, LlvmOpcode.AShr];
    internal static long Run()
    {
        long checkedValues = 0;
        for (uint width = 1; width <= 3; ++width)
        {
            int size = 1 << (int)width;
            var ranges = new List<LlvmConstantRange> { LlvmConstantRange.Empty(width), LlvmConstantRange.Full(width) };
            for (int lower = 0; lower < size; ++lower)
                for (int upper = 0; upper < size; ++upper)
                    if (lower != upper) ranges.Add(new(new LlvmAPInt(width, (ulong)lower), new LlvmAPInt(width, (ulong)upper)));
            foreach (var left in ranges)
                foreach (var right in ranges)
                {
                    var union = left.UnionWith(right);
                    var intersection = left.IntersectWith(right);
                    for (int value = 0; value < size; ++value)
                    {
                        var bits = new LlvmAPInt(width, (ulong)value);
                        bool a = left.Contains(bits), b = right.Contains(bits);
                        Require(!(a || b) || union.Contains(bits), width, left, right, "union", value);
                        Require(!(a && b) || intersection.Contains(bits), width, left, right, "intersection", value);
                        checkedValues += 2;
                    }
                    foreach (var opcode in Operations)
                    {
                        var result = left.BinaryOp(opcode, right);
                        for (int a = 0; a < size; ++a)
                        {
                            if (!left.Contains(new LlvmAPInt(width, (ulong)a))) continue;
                            for (int b = 0; b < size; ++b)
                            {
                                if (!right.Contains(new LlvmAPInt(width, (ulong)b))) continue;
                                if (!Evaluate(opcode, a, b, size, out int expected)) continue;
                                Require(result.Contains(new LlvmAPInt(width, (ulong)expected)), width, left, right, opcode.ToString(), expected);
                                ++checkedValues;
                            }
                        }
                    }
                }
        }
        return checkedValues;
    }
    private static bool Evaluate(LlvmOpcode operation, int a, int b, int size, out int result)
    {
        int signedA = a >= size / 2 ? a - size : a;
        int signedB = b >= size / 2 ? b - size : b;
        result = 0;
        if (operation is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem && b == 0) return false;
        if (operation == LlvmOpcode.SDiv && signedA == -size / 2 && signedB == -1) return false;
        if (operation is LlvmOpcode.Shl or LlvmOpcode.LShr or LlvmOpcode.AShr && b >= int.Log2(size)) return false;
        result = operation switch
        {
            LlvmOpcode.Add => a + b, LlvmOpcode.Sub => a - b, LlvmOpcode.Mul => a * b,
            LlvmOpcode.UDiv => a / b, LlvmOpcode.SDiv => signedA / signedB,
            LlvmOpcode.URem => a % b, LlvmOpcode.SRem => signedA % signedB,
            LlvmOpcode.And => a & b, LlvmOpcode.Or => a | b, LlvmOpcode.Xor => a ^ b,
            LlvmOpcode.Shl => a << b, LlvmOpcode.LShr => a >> b, LlvmOpcode.AShr => signedA >> b,
            _ => throw new InvalidOperationException()
        };
        result &= size - 1;
        return true;
    }
    private static void Require(bool condition, uint width, LlvmConstantRange left,
        LlvmConstantRange right, string operation, int value)
    {
        if (!condition) throw new InvalidOperationException($"ConstantRange i{width} {operation} lost {value}; " +
            $"lhs=[{left.Lower.GetZExtValue()},{left.Upper.GetZExtValue()}), " +
            $"rhs=[{right.Lower.GetZExtValue()},{right.Upper.GetZExtValue()}).");
    }
}
