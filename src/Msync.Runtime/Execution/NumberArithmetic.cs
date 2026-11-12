using Msync.Core;

namespace Msync.Runtime;

internal static class NumberArithmetic
{
    internal static double Apply(double left, double right, OpCode operation) => operation switch
    {
        OpCode.Add => left + right,
        OpCode.Subtract => left - right,
        OpCode.Multiply => left * right,
        OpCode.Remainder => left % right,
        _ => left / right
    };

    internal static bool Test(double left, double right, OpCode operation) => operation switch
    {
        OpCode.Equal => left == right,
        OpCode.NotEqual => left != right,
        OpCode.Less => left < right,
        OpCode.LessEqual => left <= right,
        OpCode.Greater => left > right,
        _ => left >= right
    };
}
