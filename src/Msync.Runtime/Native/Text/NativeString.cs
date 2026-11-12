using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static Value StringOperation(string name, IReadOnlyList<Value> args)
    {
        string first = Text(args[0]);
        return name switch
        {
            "contains" => Bool(first.Contains(Text(args[1]), StringComparison.Ordinal)),
            "starts_with" => Bool(first.StartsWith(Text(args[1]), StringComparison.Ordinal)),
            "ends_with" => Bool(first.EndsWith(Text(args[1]), StringComparison.Ordinal)),
            "concat" => new StringValue(first + Text(args[1])),
            "trim" => new StringValue(first.Trim()),
            "trim_start" => new StringValue(first.TrimStart()),
            "trim_end" => new StringValue(first.TrimEnd()),
            "to_lowercase" => new StringValue(UnicodeCasing.ToLower(first)),
            "to_uppercase" => new StringValue(UnicodeCasing.ToUpper(first)),
            "replace" => new StringValue(first.Replace(Text(args[1]), Text(args[2]), StringComparison.Ordinal)),
            "split" => RangeValue.TextSlices(Split(first, Text(args[1]))),
            "empty" => Bool(first.Length == 0),
            "equal" => Bool(first == Text(args[1])),
            _ => ExtendedStringOperation(name, args)
        };
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
