namespace Msync.Core;

public static class TextSemantics
{
    public static StringComparer Ordinal => ScalarOrdinalComparer.Instance;

    public static int CompareOrdinal(string left, string right)
    {
        int common = left.AsSpan().CommonPrefixLength(right.AsSpan());
        if (common == Math.Min(left.Length, right.Length)) return left.Length.CompareTo(right.Length);
        char a = left[common], b = right[common];
        if (!char.IsHighSurrogate(a) && !char.IsHighSurrogate(b)) return a.CompareTo(b);
        return ScalarAt(left, common).CompareTo(ScalarAt(right, common));
    }

    private static int ScalarAt(string value, int offset)
    {
        char first = value[offset];
        return char.IsHighSurrogate(first) && offset + 1 < value.Length && char.IsLowSurrogate(value[offset + 1])
            ? char.ConvertToUtf32(first, value[offset + 1]) : first;
    }

    private sealed class ScalarOrdinalComparer : StringComparer
    {
        internal static readonly StringComparer Instance = new ScalarOrdinalComparer();

        public override int Compare(string? left, string? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            return CompareOrdinal(left, right);
        }

        public override bool Equals(string? left, string? right) => StringComparer.Ordinal.Equals(left, right);
        public override int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(value);
    }
}
