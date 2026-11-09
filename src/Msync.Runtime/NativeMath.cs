using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static Value MathOperation(string operation, IReadOnlyList<Value> args)
    {
        double first = MathNumber(args[0]);
        if (operation is "is_finite" or "is_nan" or "is_infinite" or "signbit")
            return Bool(operation switch
            {
                "is_finite" => double.IsFinite(first),
                "is_nan" => double.IsNaN(first),
                "is_infinite" => double.IsInfinity(first),
                _ => double.IsNegative(first)
            });
        double second = args.Count > 1 ? MathNumber(args[1]) : 0;
        double third = args.Count > 2 ? MathNumber(args[2]) : 0;
        return new NumberValue(operation switch
        {
            "abs" => Math.Abs(first),
            "floor" => Math.Floor(first),
            "ceil" => Math.Ceiling(first),
            "trunc" => Math.Truncate(first),
            "round" => Math.Round(first, MidpointRounding.AwayFromZero),
            "round_even" => Math.Round(first, MidpointRounding.ToEven),
            "sqrt" => Math.Sqrt(first),
            "cbrt" => Math.Cbrt(first),
            "exp" => Math.Exp(first),
            "exp2" => double.Exp2(first),
            "expm1" => double.ExpM1(first),
            "log" => Math.Log(first),
            "log2" => Math.Log2(first),
            "log10" => Math.Log10(first),
            "log1p" => double.LogP1(first),
            "sin" => Math.Sin(first),
            "cos" => Math.Cos(first),
            "tan" => Math.Tan(first),
            "asin" => Math.Asin(first),
            "acos" => Math.Acos(first),
            "atan" => Math.Atan(first),
            "sinh" => Math.Sinh(first),
            "cosh" => Math.Cosh(first),
            "tanh" => Math.Tanh(first),
            "asinh" => Math.Asinh(first),
            "acosh" => Math.Acosh(first),
            "atanh" => Math.Atanh(first),
            "divide" => first / second,
            "fmin" => double.MinNumber(first, second),
            "fmax" => double.MaxNumber(first, second),
            "pow" => Math.Pow(first, second),
            "atan2" => Math.Atan2(first, second),
            "hypot" => double.Hypot(first, second),
            "fmod" => first % second,
            "remainder" => Math.IEEERemainder(first, second),
            "copysign" => Math.CopySign(first, second),
            "nextafter" => NextAfter(first, second),
            "fma" => Math.FusedMultiplyAdd(first, second, third),
            "clamp" => ClampNumber(first, second, third),
            _ => throw new MsyncException($"Unknown math native '{operation}'")
        });
    }

    private static double MathNumber(Value value) => value is NumberValue number ? number.Data
        : throw new MsyncException($"Expected Number, received {value.Type}");

    private static double NextAfter(double value, double toward)
    {
        if (double.IsNaN(value)) return value;
        if (double.IsNaN(toward)) return toward;
        if (value == toward) return toward;
        return toward > value ? double.BitIncrement(value) : double.BitDecrement(value);
    }

    private static double ClampNumber(double value, double lower, double upper)
    {
        if (lower > upper) throw new MsyncException("math.clamp requires lower <= upper.");
        return Math.Clamp(value, lower, upper);
    }
}
