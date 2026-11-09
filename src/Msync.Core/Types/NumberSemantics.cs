namespace Msync.Core;

/// <summary>Numerical ordering is unordered for NaN; representation identity is separate.</summary>
public static class NumberSemantics
{
    public static int? Compare(double left, double right) =>
        double.IsNaN(left) || double.IsNaN(right) ? null : left.CompareTo(right);
}
