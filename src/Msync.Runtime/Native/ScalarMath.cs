using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime;

internal static class ScalarMath
{
    internal static bool Predicate(HpirMathOp operation, double value) => operation switch
    {
        HpirMathOp.IsFinite => double.IsFinite(value),
        HpirMathOp.IsNaN => double.IsNaN(value),
        HpirMathOp.IsInfinite => double.IsInfinity(value),
        HpirMathOp.IsInteger => double.IsFinite(value) && value == Math.Truncate(value),
        HpirMathOp.IsSafeInteger => double.IsFinite(value) && value == Math.Truncate(value) && Math.Abs(value) <= 9007199254740991d,
        HpirMathOp.SignBit => double.IsNegative(value),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    internal static double Evaluate(HpirMathOp operation, double first, double second, double third) => operation switch
    {
        HpirMathOp.Abs => Math.Abs(first),
        HpirMathOp.Floor => Math.Floor(first),
        HpirMathOp.Ceil => Math.Ceiling(first),
        HpirMathOp.Trunc => Math.Truncate(first),
        HpirMathOp.Round => Math.Round(first, MidpointRounding.AwayFromZero),
        HpirMathOp.RoundEven => Math.Round(first, MidpointRounding.ToEven),
        HpirMathOp.Sqrt => Math.Sqrt(first),
        HpirMathOp.Cbrt => Math.Cbrt(first),
        HpirMathOp.Exp => Math.Exp(first),
        HpirMathOp.Exp2 => double.Exp2(first),
        HpirMathOp.ExpM1 => double.ExpM1(first),
        HpirMathOp.Log => Math.Log(first),
        HpirMathOp.Log2 => Math.Log2(first),
        HpirMathOp.Log10 => Math.Log10(first),
        HpirMathOp.LogP1 => double.LogP1(first),
        HpirMathOp.Sin => Math.Sin(first),
        HpirMathOp.Cos => Math.Cos(first),
        HpirMathOp.Tan => Math.Tan(first),
        HpirMathOp.Asin => Math.Asin(first),
        HpirMathOp.Acos => Math.Acos(first),
        HpirMathOp.Atan => Math.Atan(first),
        HpirMathOp.Sinh => Math.Sinh(first),
        HpirMathOp.Cosh => Math.Cosh(first),
        HpirMathOp.Tanh => Math.Tanh(first),
        HpirMathOp.Asinh => Math.Asinh(first),
        HpirMathOp.Acosh => Math.Acosh(first),
        HpirMathOp.Atanh => Math.Atanh(first),
        HpirMathOp.Divide => first / second,
        HpirMathOp.MinNumber => double.MinNumber(first, second),
        HpirMathOp.MaxNumber => double.MaxNumber(first, second),
        HpirMathOp.Pow => Math.Pow(first, second),
        HpirMathOp.Atan2 => Math.Atan2(first, second),
        HpirMathOp.Hypot => double.Hypot(first, second),
        HpirMathOp.FMod => first % second,
        HpirMathOp.Remainder => Math.IEEERemainder(first, second),
        HpirMathOp.CopySign => Math.CopySign(first, second),
        HpirMathOp.NextAfter => NextAfter(first, second),
        HpirMathOp.FusedMultiplyAdd => Math.FusedMultiplyAdd(first, second, third),
        HpirMathOp.Clamp => Clamp(first, second, third),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static double NextAfter(double value, double toward)
    {
        if (double.IsNaN(value)) return value;
        if (double.IsNaN(toward)) return toward;
        if (value == toward) return toward;
        return toward > value ? double.BitIncrement(value) : double.BitDecrement(value);
    }

    private static double Clamp(double value, double lower, double upper)
    {
        if (lower > upper) throw new MsyncException("math.clamp requires lower <= upper.");
        return Math.Clamp(value, lower, upper);
    }
}
