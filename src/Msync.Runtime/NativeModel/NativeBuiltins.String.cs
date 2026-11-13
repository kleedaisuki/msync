using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static NativeValue StringOperation(string name, IReadOnlyList<NativeValue> args)
    {
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
