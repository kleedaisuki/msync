using System.Buffers.Binary;
using System.Numerics;
using Msync.Core;

namespace Msync.Runtime;

internal static class Word32
{
    internal static uint Evaluate(NumericIntrinsic operation, double first, double second) =>
        Apply(operation, Operand(first), RightOperand(operation, second));

    internal static uint Apply(NumericIntrinsic operation, uint word, uint right)
    {
        return operation switch
        {
            NumericIntrinsic.And => word & right,
            NumericIntrinsic.Or => word | right,
            NumericIntrinsic.Xor => word ^ right,
            NumericIntrinsic.Not => ~word,
            NumericIntrinsic.ShiftLeft => word << (int)right,
            NumericIntrinsic.ShiftRight => word >> (int)right,
            NumericIntrinsic.ShiftRightSigned => unchecked((uint)((int)word >> (int)right)),
            NumericIntrinsic.RotateLeft => BitOperations.RotateLeft(word, (int)right),
            NumericIntrinsic.RotateRight => BitOperations.RotateRight(word, (int)right),
            NumericIntrinsic.PopCount => (uint)BitOperations.PopCount(word),
            NumericIntrinsic.LeadingZeroCount => (uint)BitOperations.LeadingZeroCount(word),
            NumericIntrinsic.TrailingZeroCount => (uint)BitOperations.TrailingZeroCount(word),
            NumericIntrinsic.ReverseBytes => BinaryPrimitives.ReverseEndianness(word),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    internal static bool TryOperand(double value, out uint word)
    {
        if (value >= 0 && value <= uint.MaxValue && value == Math.Truncate(value))
        {
            word = (uint)value;
            return true;
        }
        word = 0;
        return false;
    }

    internal static uint Operand(double value) => TryOperand(value, out uint word)
        ? word : throw new MsyncException("Word32 operand must be an exact integer from 0 through 4294967295.");

    internal static uint RightOperand(NumericIntrinsic operation, double value) => operation switch
    {
        NumericIntrinsic.And or NumericIntrinsic.Or or NumericIntrinsic.Xor => Operand(value),
        _ => CountOperation(operation) ? Count(value) : 0
    };

    internal static uint RightOperand(NumericIntrinsic operation, uint value) =>
        !CountOperation(operation) || value <= 31 ? value : throw InvalidCount();

    private static bool CountOperation(NumericIntrinsic operation) => operation is NumericIntrinsic.ShiftLeft
        or NumericIntrinsic.ShiftRight or NumericIntrinsic.ShiftRightSigned or NumericIntrinsic.RotateLeft
        or NumericIntrinsic.RotateRight;

    // The host masks shift counts. The guest rejects out-of-range counts instead.
    private static uint Count(double value) => value >= 0 && value <= 31 && value == Math.Truncate(value)
        ? (uint)value : throw InvalidCount();

    private static MsyncException InvalidCount() => new("Word32 shift count must be an exact integer from 0 through 31.");
}
