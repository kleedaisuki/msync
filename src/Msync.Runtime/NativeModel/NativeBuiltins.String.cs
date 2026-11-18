using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static NativeValue StringOperation(string name, IReadOnlyList<NativeValue> args)
    {
        if (TryTextSpanOperation(name, args, out NativeValue result)) return result;
        string first = Text(args[0]);
        return name switch
        {
            "contains" => Bool(first.Contains(Text(args[1]), StringComparison.Ordinal)),
            "starts_with" => Bool(first.StartsWith(Text(args[1]), StringComparison.Ordinal)),
            "ends_with" => Bool(first.EndsWith(Text(args[1]), StringComparison.Ordinal)),
            "concat" => NativeValue.FromString(first + Text(args[1])),
            "trim" => NativeValue.FromString(first.Trim()),
            "trim_start" => NativeValue.FromString(first.TrimStart()),
            "trim_end" => NativeValue.FromString(first.TrimEnd()),
            "to_lowercase" => NativeValue.FromString(UnicodeCasing.ToLower(first)),
            "to_uppercase" => NativeValue.FromString(UnicodeCasing.ToUpper(first)),
            "replace" => NativeValue.FromString(first.Replace(Text(args[1]), Text(args[2]), StringComparison.Ordinal)),
            "split" => StringSlices(Split(first, Text(args[1]))),
            "empty" => Bool(first.Length == 0),
            "equal" => Bool(first == Text(args[1])),
            _ => ExtendedStringOperation(name, args)
        };
    }

    private static bool TryTextSpanOperation(string name, IReadOnlyList<NativeValue> args, out NativeValue result)
    {
        result = default;
        NativeValue input = args[0];
        if (input.Kind != NativeKind.String) return false;
        ReadOnlySpan<char> first = input.AsStringSpan();
        switch (name)
        {
            case "empty": result = Bool(first.IsEmpty); return true;
            case "to_lowercase" or "to_uppercase" when input.NativeStringEdge != 0:
                bool upper = name == "to_uppercase";
                result = input.OpenTextBuffer(UnicodeCasing.ChangeSpan(first, default, upper), out Span<char> mapped);
                UnicodeCasing.ChangeSpan(first, mapped, upper);
                return true;
            case "repeat" when input.NativeStringEdge != 0:
                double count = TextNatural(args[1], "string.repeat count");
                double length = first.Length * count;
                if (length > int.MaxValue) throw new MsyncException("string.repeat result exceeds the platform String length.");
                result = input.OpenTextBuffer((int)length, out Span<char> repeated);
                for (int offset = 0; offset < repeated.Length; offset += first.Length)
                    first.CopyTo(repeated[offset..]);
                return true;
            case "replace" when input.NativeStringEdge != 0 && args[1].Kind == NativeKind.String && args[2].Kind == NativeKind.String:
                ReadOnlySpan<char> search = args[1].AsStringSpan();
                if (search.IsEmpty) return false;
                result = ReplaceNative(input, first, search, args[2].AsStringSpan());
                return true;

            case "trim": result = input.BuildString(first.Trim()); return true;
            case "trim_start": result = input.BuildString(first.TrimStart()); return true;
            case "trim_end": result = input.BuildString(first.TrimEnd()); return true;
            case "substring":
                result = input.BuildString(Substring(first, TextNatural(args[1], "string.substring start"),
                    TextNatural(args[2], "string.substring count")));
                return true;

            case "length": result = NativeValue.FromNumber(ScalarLength(first)); return true;
            case "utf8_size": result = NativeValue.FromNumber(System.Text.Encoding.UTF8.GetByteCount(first)); return true;
            case "is_ascii": result = Bool(!first.ContainsAnyExceptInRange('\0', '\u007f')); return true;
        }
        if (args.Count != 2 || args[1].Kind != NativeKind.String) return false;
        ReadOnlySpan<char> second = args[1].AsStringSpan();
        switch (name)
        {
            case "concat": result = NativeValue.ConcatStrings(input, args[1]); return true;
            case "find": result = FindString(first, second, backward: false); return true;
            case "rfind": result = FindString(first, second, backward: true); return true;
            case "contains": result = Bool(first.Contains(second, StringComparison.Ordinal)); return true;
            case "starts_with": result = Bool(first.StartsWith(second, StringComparison.Ordinal)); return true;
            case "ends_with": result = Bool(first.EndsWith(second, StringComparison.Ordinal)); return true;
            case "equal": result = Bool(first.SequenceEqual(second)); return true;
            case "compare": result = NativeValue.FromNumber(Math.Sign(TextSemantics.CompareOrdinal(first, second))); return true;
            default: return false;
        }
    }

    private static NativeValue ReplaceNative(NativeValue input, ReadOnlySpan<char> text, ReadOnlySpan<char> search,
        ReadOnlySpan<char> replacement)
    {
        int matches = 0;
        for (ReadOnlySpan<char> rest = text; rest.IndexOf(search, StringComparison.Ordinal) is >= 0 and var at;)
        {
            matches++;
            rest = rest[(at + search.Length)..];
        }
        if (matches == 0) return input;
        int length = checked((int)(text.Length + (long)matches * (replacement.Length - search.Length)));
        NativeValue output = input.OpenTextBuffer(length, out Span<char> destination);
        while (text.IndexOf(search, StringComparison.Ordinal) is >= 0 and var offset)
        {
            text[..offset].CopyTo(destination);
            replacement.CopyTo(destination[offset..]);
            text = text[(offset + search.Length)..];
            destination = destination[(offset + replacement.Length)..];
        }
        text.CopyTo(destination);
        return output;
    }

    private static IEnumerable<TextSlice> Split(string text, string separator)
    {
        if (separator.Length == 0)
        {
            yield return new TextSlice("");
            foreach (var slice in Characters(text)) yield return slice;
            yield return new TextSlice("");
            yield break;
        }

        int start = 0;
        while (text.IndexOf(separator, start, StringComparison.Ordinal) is >= 0 and var match)
        {
            yield return new TextSlice(text, start, match - start);
            start = match + separator.Length;
        }
        yield return new TextSlice(text, start, text.Length - start);
    }
}
