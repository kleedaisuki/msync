using Msync.Core.Native;

namespace Msync.Core;

/// <summary>Numerical ordering is unordered for NaN; representation identity is separate.</summary>
public static class NumberSemantics
{
    public static int? Compare(double left, double right) =>
        double.IsNaN(left) || double.IsNaN(right) ? null : left.CompareTo(right);

    internal static double Arithmetic(NativeBinary operation, double left, double right) => operation switch
    {
        NativeBinary.Add => left + right,
        NativeBinary.Subtract => left - right,
        NativeBinary.Multiply => left * right,
        NativeBinary.Divide => left / right,
        NativeBinary.Remainder => left % right,
        NativeBinary.Power => Math.Pow(left, right),
        _ => throw new InvalidOperationException($"Unknown binary instruction {operation}.")
    };

    internal static bool Compare(NativeBinary operation, int? comparison) => operation switch
    {
        NativeBinary.Equal => comparison == 0,
        NativeBinary.NotEqual => comparison != 0,
        NativeBinary.Less => comparison < 0,
        NativeBinary.LessEqual => comparison <= 0,
        NativeBinary.Greater => comparison > 0,
        NativeBinary.GreaterEqual => comparison >= 0,
        _ => throw new InvalidOperationException($"{operation} is not a comparison.")
    };
}
