namespace Msync.Core;

public static class ValueRepresentation
{
    public static IEqualityComparer<Value> Comparer { get; } = new RepresentationComparer();

    public static bool Same(Value? left, Value? right) => left is NumberValue a && right is NumberValue b
        ? BitConverter.DoubleToInt64Bits(a.Data) == BitConverter.DoubleToInt64Bits(b.Data)
        : EqualityComparer<Value>.Default.Equals(left, right);

    public static int IndexOf(IReadOnlyList<Value> values, Value value)
    {
        for (var index = 0; index < values.Count; index++) if (Same(values[index], value)) return index;
        return -1;
    }

    private sealed class RepresentationComparer : IEqualityComparer<Value>
    {
        public bool Equals(Value? left, Value? right) => Same(left, right);
        public int GetHashCode(Value value) => value is NumberValue number
            ? HashCode.Combine(typeof(NumberValue), BitConverter.DoubleToInt64Bits(number.Data)) : value.GetHashCode();
    }
}
