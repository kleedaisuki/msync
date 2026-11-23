namespace Msync.Core.Hpir;

public enum HpirRegexOp : byte { MatchString, MatchPath, IsMatch, Escape, Replace }

public static class HpirRegexImports
{
    public static bool TryNative(string name, out HpirRegexOp operation)
    {
        HpirRegexOp? found = name switch
        {
            "regex.is_match" => HpirRegexOp.IsMatch,
            "regex.escape" => HpirRegexOp.Escape,
            "regex.replace" => HpirRegexOp.Replace,
            _ => null
        };
        operation = found.GetValueOrDefault();
        return found.HasValue;
    }

    public static string Name(HpirRegexOp operation) => operation switch
    {
        HpirRegexOp.MatchString => "regex.operator.string",
        HpirRegexOp.MatchPath => "regex.operator.path",
        HpirRegexOp.IsMatch => "regex.is_match",
        HpirRegexOp.Escape => "regex.escape",
        HpirRegexOp.Replace => "regex.replace",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static bool ProducesText(HpirRegexOp operation) => operation is HpirRegexOp.Escape or HpirRegexOp.Replace;
    public static int Arity(HpirRegexOp operation) => operation switch { HpirRegexOp.Escape => 1, HpirRegexOp.Replace => 3, _ => 2 };

    public static HpirImport Create(HpirRegexOp operation, int literal)
    {
        if (literal < -1 || literal >= int.MaxValue >> 3) throw new ArgumentOutOfRangeException(nameof(literal));
        var arguments = new HpirWidth[Arity(operation) - (literal < 0 ? 0 : 1)];
        Array.Fill(arguments, HpirWidth.U64);
        return new(HpirService.Regex, ((literal + 1) << 3) | (int)operation,
            ProducesText(operation) ? HpirWidth.U64 : HpirWidth.U8, arguments);
    }

    public static (HpirRegexOp Operation, int Literal) Decode(int binding) => ((HpirRegexOp)(binding & 7), (binding >> 3) - 1);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        var (operation, literal) = Decode(import.Binding);
        if (operation > HpirRegexOp.Replace || import.Binding < 0 || literal >= program.TextLiterals.Count
            || literal >= 0 && operation == HpirRegexOp.Escape) return false;
        bool text = ProducesText(operation);
        return (!text || program.TextLayout >= 0) && import.Result == (text ? HpirWidth.U64 : HpirWidth.U8)
            && import.Arguments.Length == Arity(operation) - (literal < 0 ? 0 : 1)
            && import.Arguments.All(static argument => argument == HpirWidth.U64);
    }
}
