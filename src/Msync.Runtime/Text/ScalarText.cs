using System.Buffers;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

internal static class ScalarText
{
    internal static double Natural(double number, string role)
    {
        if (!double.IsFinite(number) || number < 0 || number != Math.Truncate(number))
            throw new MsyncException($"{role} must be a nonnegative finite integer Number.");
        return number;
    }

    internal static int ScalarLength(ReadOnlySpan<char> text)
    {
        if (!text.ContainsAnyInRange('\ud800', '\udfff')) return text.Length;
        int count = 0;
        foreach (Rune unused in text.EnumerateRunes()) count++;
        return count;
    }

    internal static int WordCount(ReadOnlySpan<char> text)
    {
        int count = 0;
        bool inside = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            bool word = !Rune.IsWhiteSpace(rune);
            if (word && !inside) ++count;
            inside = word;
        }
        return count;
    }

    private static int ScalarOffset(ReadOnlySpan<char> text, double position, string role)
    {
        int offset = 0, index = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (index == position) return offset;
            offset += rune.Utf16SequenceLength;
            index++;
        }
        if (index == position) return text.Length;
        throw new MsyncException($"{role} {position} exceeds String length {index} (Unicode scalar values).");
    }

    internal static ReadOnlySpan<char> Substring(ReadOnlySpan<char> text, double start, double count)
    {
        int first = ScalarOffset(text, start, "string.substring start");
        if (count == 0 || first == text.Length) return "";
        int offset = first;
        foreach (Rune rune in text[first..].EnumerateRunes())
        {
            if (count == 0) break;
            offset += rune.Utf16SequenceLength;
            count--;
        }
        return text[first..offset];
    }

    internal static ReadOnlySpan<char> CharacterAt(ReadOnlySpan<char> text, double position)
    {
        int offset = ScalarOffset(text, position, "string.char_at index");
        if (offset == text.Length) throw new MsyncException($"string.char_at index {position} is outside String length {ScalarLength(text)}.");
        if (Rune.DecodeFromUtf16(text[offset..], out _, out int width) != OperationStatus.Done)
            _ = Rune.GetRuneAt(text.ToString(), offset);
        return text.Slice(offset, width);
    }

    internal static int RepeatLength(int chars, double count)
    {
        double length = chars * count;
        if (length > int.MaxValue) throw new MsyncException("string.repeat result exceeds the platform String length.");
        return (int)length;
    }

    internal static int ReplacementLength(ReadOnlySpan<char> text, ReadOnlySpan<char> search,
        int replacementLength, out bool matches)
    {
        if (search.IsEmpty) throw new ArgumentException("The value cannot be an empty string.", "oldValue");
        int count = 0;
        for (var rest = text; rest.IndexOf(search, StringComparison.Ordinal) is >= 0 and var offset;)
        {
            count++;
            rest = rest[(offset + search.Length)..];
        }
        matches = count != 0;
        return checked((int)(text.Length + (long)count * (replacementLength - search.Length)));
    }

    internal static void Replace(ReadOnlySpan<char> text, ReadOnlySpan<char> search,
        ReadOnlySpan<char> replacement, Span<char> destination)
    {
        while (text.IndexOf(search, StringComparison.Ordinal) is >= 0 and var offset)
        {
            text[..offset].CopyTo(destination);
            replacement.CopyTo(destination[offset..]);
            text = text[(offset + search.Length)..];
            destination = destination[(offset + replacement.Length)..];
        }
        text.CopyTo(destination);
    }
}
