using System.Text;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static ValueTask<Value> StringOperationAsync(string operation, IReadOnlyList<Value> args, CancellationToken token) =>
        operation == "join" ? JoinStringAsync(args, token) : ValueTask.FromResult(StringOperation(operation, args));

    private static Value ExtendedStringOperation(string operation, IReadOnlyList<Value> args)
    {
        string text = Text(args[0]);
        return operation switch
        {
            "length" => new NumberValue(ScalarLength(text)),
            "utf8_size" => new NumberValue(Encoding.UTF8.GetByteCount(text)),
            "is_ascii" => Bool(!text.AsSpan().ContainsAnyExceptInRange('\0', '\u007F')),
            "chars" => new RangeValue(Characters(text), TypeRef.String),
            "lines" => new RangeValue(Lines(text), TypeRef.String),
            "split_whitespace" => new RangeValue(Words(text), TypeRef.String),
            "char_at" => new StringValue(CharacterAt(text, TextNatural(args[1], "string.char_at index"))),
            "substring" => new StringValue(Substring(text, TextNatural(args[1], "string.substring start"), TextNatural(args[2], "string.substring count"))),
            "find" => FindString(text, Text(args[1]), backward: false),
            "rfind" => FindString(text, Text(args[1]), backward: true),
            "compare" => new NumberValue(Math.Sign(TextSemantics.CompareOrdinal(text, Text(args[1])))),
            "repeat" => new StringValue(RepeatString(text, TextNatural(args[1], "string.repeat count"))),
            _ => throw new MsyncException($"Unknown string native '{operation}'")
        };
    }

    private static double TextNatural(Value value, string role)
    {
        if (value is not NumberValue { Data: var number } || !double.IsFinite(number) || number < 0 || number != Math.Truncate(number))
            throw new MsyncException($"{role} must be a nonnegative finite integer Number.");
        return number;
    }

    private static int ScalarLength(ReadOnlySpan<char> text)
    {
        int count = 0;
        foreach (Rune unused in text.EnumerateRunes()) count++;
        return count;
    }

    private static int ScalarOffset(string text, double position, string role)
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

    private static string Substring(string text, double start, double count)
    {
        int first = ScalarOffset(text, start, "string.substring start");
        if (count == 0 || first == text.Length) return "";
        int offset = first;
        double remaining = count;
        foreach (Rune rune in text.AsSpan(first).EnumerateRunes())
        {
            if (remaining == 0) break;
            offset += rune.Utf16SequenceLength;
            remaining--;
        }
        return text[first..offset];
    }

    private static string CharacterAt(string text, double position)
    {
        int offset = ScalarOffset(text, position, "string.char_at index");
        if (offset == text.Length) throw new MsyncException($"string.char_at index {position} is outside String length {ScalarLength(text)}.");
        return Rune.GetRuneAt(text, offset).ToString();
    }

    private static Value FindString(string text, string part, bool backward)
    {
        int offset = backward ? text.LastIndexOf(part, StringComparison.Ordinal) : text.IndexOf(part, StringComparison.Ordinal);
        if (offset < 0) return Value.Null;
        return new NumberValue(ScalarLength(text.AsSpan(0, offset)));
    }

    private static string RepeatString(string text, double count)
    {
        if (count == 0 || text.Length == 0) return "";
        double length = text.Length * count;
        if (length > int.MaxValue) throw new MsyncException("string.repeat result exceeds the platform String length.");
        return string.Create((int)length, text, static (destination, value) =>
        {
            for (int offset = 0; offset < destination.Length; offset += value.Length)
                value.AsSpan().CopyTo(destination[offset..]);
        });
    }

    private static IEnumerable<Value> Characters(string text)
    {
        foreach (Rune rune in text.EnumerateRunes()) yield return new StringValue(rune.ToString());
    }

    private static IEnumerable<Value> Lines(string text)
    {
        using StringReader reader = new(text);
        while (reader.ReadLine() is { } line) yield return new StringValue(line);
    }

    private static IEnumerable<Value> Words(string text)
    {
        int start = -1, offset = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune)) { if (start < 0) start = offset; }
            else if (start >= 0) { yield return new StringValue(text[start..offset]); start = -1; }
            offset += rune.Utf16SequenceLength;
        }
        if (start >= 0) yield return new StringValue(text[start..]);
    }

    private static async ValueTask<Value> JoinStringAsync(IReadOnlyList<Value> args, CancellationToken token)
    {
        if (args[0] is not RangeValue values) throw new MsyncException("string.join expects Range<String>.");
        string separator = Text(args[1]);
        StringBuilder result = new();
        bool first = true;
        await foreach (Value value in values.Items.WithCancellation(token))
        {
            if (value is not StringValue text) throw new MsyncException($"string.join expects String elements, received {value.Type}.");
            if (!first) result.Append(separator);
            result.Append(text.Data);
            first = false;
        }
        return new StringValue(result.ToString());
    }
}
