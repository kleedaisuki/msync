namespace Msync.Core.Hpir;

public static class HpirFormatImports
{
    public const char Positional = 'v', Named = 'f';
    public const char Text = 's', CompletedText = 't', Number = 'n', Bool = 'b', Null = '0', RangeDisplay = 'r', ProcessDisplay = 'p';

    public static HpirImport Create(int schema, int fields)
    {
        var args = new HpirWidth[fields + 1];
        Array.Fill(args, HpirWidth.U64);
        return new(HpirService.TextFormat, schema, HpirWidth.U64, args);
    }

    public static TypeRef? Type(char kind) => kind switch
    {
        Text or CompletedText => TypeRef.String, Number => TypeRef.Number, Bool => TypeRef.Bool, Null => TypeRef.Null,
        RangeDisplay => new(TypeKind.Range), ProcessDisplay => TypeRef.Process, _ => null
    };

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        if ((uint)import.Binding >= (uint)program.TextLiterals.Count || program.TextLayout < 0
            || import.Result != HpirWidth.U64 || import.Arguments.Length == 0
            || !import.Arguments.All(static width => width == HpirWidth.U64)) return false;
        ReadOnlySpan<char> schema = program.TextLiterals[import.Binding].AsSpan();
        if (schema.IsEmpty || schema[0] is not (Positional or Named)) return false;
        bool named = schema[0] == Named;
        schema = schema[1..];
        for (int index = 1; index < import.Arguments.Length; ++index)
        {
            if (schema.IsEmpty || Type(schema[0]) is null) return false;
            schema = schema[1..];
            if (!named) continue;
            int end = schema.IndexOf('\0');
            if (end <= 0) return false;
            schema = schema[(end + 1)..];
        }
        return schema.IsEmpty;
    }
}
