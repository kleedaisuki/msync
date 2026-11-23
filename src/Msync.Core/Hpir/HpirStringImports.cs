namespace Msync.Core.Hpir;

public enum HpirStringOp : byte
{
    Contains, StartsWith, EndsWith, Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual, IsAscii, Empty,
    Length, Utf8Size, Compare,
    Concat, Trim, TrimStart, TrimEnd, ToLower, ToUpper, Replace, CharAt, Substring, Repeat, JoinSnapshot,
    UniqueSnapshot, SortSnapshot, LineFeedCount, WordCount
}

public static class HpirStringImports
{
    public static bool TryNative(string name, out HpirStringOp operation)
    {
        if (!name.StartsWith("string.", StringComparison.Ordinal))
        {
            operation = default;
            return false;
        }
        HpirStringOp? match = name.AsSpan("string.".Length) switch
        {
            "contains" => HpirStringOp.Contains, "starts_with" => HpirStringOp.StartsWith,
            "ends_with" => HpirStringOp.EndsWith, "equal" => HpirStringOp.Equal,
            "is_ascii" => HpirStringOp.IsAscii, "empty" => HpirStringOp.Empty,
            "length" => HpirStringOp.Length, "utf8_size" => HpirStringOp.Utf8Size,
            "line_feed_count" => HpirStringOp.LineFeedCount, "word_count" => HpirStringOp.WordCount,
            "compare" => HpirStringOp.Compare, "concat" => HpirStringOp.Concat,
            "trim" => HpirStringOp.Trim, "trim_start" => HpirStringOp.TrimStart, "trim_end" => HpirStringOp.TrimEnd,
            "to_lowercase" => HpirStringOp.ToLower, "to_uppercase" => HpirStringOp.ToUpper,
            "replace" => HpirStringOp.Replace, "char_at" => HpirStringOp.CharAt,
            "substring" => HpirStringOp.Substring, "repeat" => HpirStringOp.Repeat,
            _ => null
        };
        operation = match.GetValueOrDefault();
        return match.HasValue;
    }

    public static string Name(HpirStringOp operation) => operation switch
    {
        HpirStringOp.Contains => "string.contains", HpirStringOp.StartsWith => "string.starts_with",
        HpirStringOp.EndsWith => "string.ends_with", HpirStringOp.Equal => "string.equal",
        HpirStringOp.NotEqual => "string.not_equal", HpirStringOp.Less => "string.less",
        HpirStringOp.LessEqual => "string.less_equal", HpirStringOp.Greater => "string.greater",
        HpirStringOp.GreaterEqual => "string.greater_equal", HpirStringOp.IsAscii => "string.is_ascii",
        HpirStringOp.Empty => "string.empty", HpirStringOp.Length => "string.length", HpirStringOp.Utf8Size => "string.utf8_size",
        HpirStringOp.Compare => "string.compare", HpirStringOp.Concat => "string.concat", HpirStringOp.Trim => "string.trim",
        HpirStringOp.TrimStart => "string.trim_start", HpirStringOp.TrimEnd => "string.trim_end",
        HpirStringOp.ToLower => "string.to_lowercase", HpirStringOp.ToUpper => "string.to_uppercase",
        HpirStringOp.Replace => "string.replace", HpirStringOp.CharAt => "string.char_at",
        HpirStringOp.Substring => "string.substring", HpirStringOp.Repeat => "string.repeat",
        HpirStringOp.JoinSnapshot => "string.join",
        HpirStringOp.UniqueSnapshot => "shell.unique_lines.ready_snapshot",
        HpirStringOp.SortSnapshot => "ranges.sort.text_ready_snapshot",
        HpirStringOp.LineFeedCount => "string.line_feed_count", HpirStringOp.WordCount => "string.word_count",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static bool ProducesText(HpirStringOp operation) => operation is >= HpirStringOp.Concat and <= HpirStringOp.JoinSnapshot;

    public static bool NumericArgument(HpirStringOp operation, int index) =>
        index != 0 && operation is HpirStringOp.CharAt or HpirStringOp.Substring or HpirStringOp.Repeat;

    public static HpirImport Create(HpirStringOp operation)
    {
        var arguments = new HpirWidth[Arity(operation)];
        Array.Fill(arguments, HpirWidth.U64);
        return new(HpirService.String, (int)operation, Result(operation), arguments);
    }

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        if ((uint)import.Binding > (uint)HpirStringOp.WordCount) return false;
        var operation = (HpirStringOp)import.Binding;
        return (!ProducesText(operation) || program.TextLayout >= 0)
            && import.Result == Result(operation) && import.Arguments.Length == Arity(operation)
            && import.Arguments.All(static argument => argument == HpirWidth.U64);
    }

    private static int Arity(HpirStringOp operation) => operation switch
    {
        HpirStringOp.IsAscii or HpirStringOp.Empty or HpirStringOp.Length or HpirStringOp.Utf8Size
            or HpirStringOp.Trim or HpirStringOp.TrimStart or HpirStringOp.TrimEnd or HpirStringOp.ToLower or HpirStringOp.ToUpper
            or HpirStringOp.LineFeedCount or HpirStringOp.WordCount => 1,
        HpirStringOp.Replace or HpirStringOp.Substring or HpirStringOp.JoinSnapshot
            or HpirStringOp.UniqueSnapshot or HpirStringOp.SortSnapshot => 3,
        _ => 2
    };

    private static HpirWidth Result(HpirStringOp operation) =>
        operation <= HpirStringOp.Empty ? HpirWidth.U8 : HpirWidth.U64;
}
