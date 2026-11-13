using System.Buffers;
using System.Text;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static ValueTask<NativeValue> StringOperationAsync(string operation, IReadOnlyList<NativeValue> args, CancellationToken token) =>
        operation == "join" ? JoinStringAsync(args, token) : ValueTask.FromResult(StringOperation(operation, args));

    private static NativeValue ExtendedStringOperation(string operation, IReadOnlyList<NativeValue> args)
    {
        string text = Text(args[0]);
        return operation switch
        {
            "length" => NativeValue.FromNumber(ScalarLength(text)),
            "utf8_size" => NativeValue.FromNumber(Encoding.UTF8.GetByteCount(text)),
            "is_ascii" => Bool(!text.AsSpan().ContainsAnyExceptInRange('\0', '\u007F')),
            "chars" => StringSlices(Characters(text)),
            "lines" => StringSlices(Lines(text)),
            "split_whitespace" => StringSlices(Words(text)),
            "char_at" => NativeValue.FromString(CharacterAt(text, TextNatural(args[1], "string.char_at index"))),
            "substring" => NativeValue.FromString(Substring(text, TextNatural(args[1], "string.substring start"), TextNatural(args[2], "string.substring count"))),
            "find" => FindString(text, Text(args[1]), backward: false),
            "rfind" => FindString(text, Text(args[1]), backward: true),
            "compare" => NativeValue.FromNumber(Math.Sign(TextSemantics.CompareOrdinal(text, Text(args[1])))),
            "repeat" => NativeValue.FromString(RepeatString(text, TextNatural(args[1], "string.repeat count"))),
            _ => throw new MsyncException($"Unknown string native '{operation}'")
        };
    }

    private static double TextNatural(NativeValue value, string role)
    {
        double number = Number(value);
        if (!double.IsFinite(number) || number < 0 || number != Math.Truncate(number))
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

    private static NativeValue FindString(string text, string part, bool backward)
    {
        int offset = backward ? text.LastIndexOf(part, StringComparison.Ordinal) : text.IndexOf(part, StringComparison.Ordinal);
        if (offset < 0) return NativeValue.Null;
        return NativeValue.FromNumber(ScalarLength(text.AsSpan(0, offset)));
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

    private static IEnumerable<TextSlice> Characters(string text)
    {
        for (int offset = 0; offset < text.Length;)
        {
            var status = Rune.DecodeFromUtf16(text.AsSpan(offset), out _, out int width);
            yield return status == OperationStatus.Done ? new TextSlice(text, offset, width) : new TextSlice("\uFFFD");
            offset += width;
        }
    }

    private static IEnumerable<TextSlice> Lines(string text)
    {
        for (int start = 0; start < text.Length;)
        {
            int width = text.AsSpan(start).IndexOfAny('\r', '\n');
            if (width < 0)
            {
                yield return new TextSlice(text, start, text.Length - start);
                yield break;
            }
            yield return new TextSlice(text, start, width);
            int terminator = start + width;
            start = terminator + 1;
            if (text[terminator] == '\r' && start < text.Length && text[start] == '\n') start++;
        }
    }

    private static IEnumerable<TextSlice> Words(string text)
    {
        int start = -1, offset = 0;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune)) { if (start < 0) start = offset; }
            else if (start >= 0) { yield return new TextSlice(text, start, offset - start); start = -1; }
            offset += rune.Utf16SequenceLength;
        }
        if (start >= 0) yield return new TextSlice(text, start, text.Length - start);
    }

    private static async ValueTask<NativeValue> JoinStringAsync(IReadOnlyList<NativeValue> args, CancellationToken token)
    {
        NativeRange values = Range(args[0], "join");
        string separator = Text(args[1]);
        StringBuilder output = new();
        bool first = true;
        await foreach (NativeValue value in values.Items.WithCancellation(token))
        {
            if (value.Kind != NativeKind.String)
                throw new MsyncException($"string.join expects String elements, received {value.Type}.");
            if (!first) output.Append(separator);
            output.Append(value.AsString());
            first = false;
        }
        return NativeValue.FromString(output.ToString());
    }

    private static NativeValue StringSlices(IEnumerable<TextSlice> slices) =>
        NativeValue.FromRange(new NativeRange(ReadSlices(slices), TypeRef.String));

    private static async IAsyncEnumerable<NativeValue> ReadSlices(IEnumerable<TextSlice> slices)
    {
        foreach (TextSlice slice in slices) yield return NativeValue.FromString(slice.ToOwnedString());
        await Task.CompletedTask;
    }
}
