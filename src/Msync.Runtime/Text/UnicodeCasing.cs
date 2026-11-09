using System.Text;

namespace Msync.Runtime;

public static class UnicodeCasing
{
    public static string ToUpper(string value) => Change(value, upper: true);
    public static string ToLower(string value) => Change(value, upper: false);

    private static string Change(string value, bool upper)
    {
        if (Ascii.IsValid(value)) return upper ? value.ToUpperInvariant() : value.ToLowerInvariant();
        StringBuilder result = new(value.Length);
        bool precededByCased = false;
        int offset = 0;
        while (offset < value.Length)
        {
            int start = offset;
            int scalar = Scalar(value, ref offset);
            if (!upper && scalar == 0x03A3 && precededByCased && !FollowedByCased(value, offset))
                result.Append('\u03C2');
            else
                Append(result, value.AsSpan(start, offset - start), scalar, upper);
            // Final_Sigma context skips ignorable characters even when they are also Cased.
            if (!upper && !Contains(UnicodeCasingData.CaseIgnorable, scalar))
                precededByCased = Contains(UnicodeCasingData.Cased, scalar);
        }
        return result.ToString();
    }

    private static void Append(StringBuilder result, ReadOnlySpan<char> original, int scalar, bool upper)
    {
        if (scalar < 128)
        {
            if (upper && scalar is >= 'a' and <= 'z') scalar -= 32;
            if (!upper && scalar is >= 'A' and <= 'Z') scalar += 32;
            result.Append((char)scalar);
            return;
        }
        ReadOnlySpan<int> mappings = upper ? UnicodeCasingData.UpperMappings : UnicodeCasingData.LowerMappings;
        int index = FindMapping(mappings, scalar);
        if (index < 0) { result.Append(original); return; }
        string text = upper ? UnicodeCasingData.UpperText : UnicodeCasingData.LowerText;
        result.Append(text.AsSpan(mappings[index + 1], mappings[index + 2]));
    }

    private static int FindMapping(ReadOnlySpan<int> mappings, int scalar)
    {
        int low = 0, high = mappings.Length / 3 - 1;
        while (low <= high)
        {
            int middle = (low + high) / 2;
            int index = middle * 3;
            int current = mappings[index];
            if (current == scalar) return index;
            if (current < scalar) low = middle + 1;
            else high = middle - 1;
        }
        return -1;
    }

    private static bool FollowedByCased(string value, int offset)
    {
        while (offset < value.Length)
        {
            int scalar = Scalar(value, ref offset);
            if (!Contains(UnicodeCasingData.CaseIgnorable, scalar))
                return Contains(UnicodeCasingData.Cased, scalar);
        }
        return false;
    }

    private static bool Contains(ReadOnlySpan<int> ranges, int scalar)
    {
        int low = 0, high = ranges.Length / 2 - 1;
        while (low <= high)
        {
            int index = (low + high) / 2;
            if (scalar < ranges[index * 2]) high = index - 1;
            else if (scalar > ranges[index * 2 + 1]) low = index + 1;
            else return true;
        }
        return false;
    }

    private static int Scalar(string value, ref int offset)
    {
        char first = value[offset++];
        if (char.IsHighSurrogate(first) && offset < value.Length && char.IsLowSurrogate(value[offset]))
            return char.ConvertToUtf32(first, value[offset++]);
        return first;
    }
}
