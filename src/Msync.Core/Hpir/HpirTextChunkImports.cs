namespace Msync.Core.Hpir;

public static class HpirTextChunkImports
{
    public const int Owner = HpirTransformImports.VectorHeaderBytes;
    public const int ViewBytes = Owner + sizeof(ulong);

    public static HpirLayout ViewLayout() => new(ViewBytes, 8, [Owner]);
    public static HpirLayout BufferLayout() => new(16, 8, [8]);
    public static HpirLayout ItemLayout() => HpirTransformImports.ElementLayout(reference: true);

    public static HpirImport Append(int layout) =>
        new(HpirService.AppendTextChunk, layout, HpirWidth.U64, [HpirWidth.U64, HpirWidth.U64]);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        int first = import.Binding;
        return first >= 0 && first <= program.Layouts.Count - 3
            && import is { Result: HpirWidth.U64, Arguments: [HpirWidth.U64, HpirWidth.U64] }
            && Matches(program.Layouts[first], ViewLayout())
            && Matches(program.Layouts[first + 1], BufferLayout())
            && Matches(program.Layouts[first + 2], ItemLayout());
    }

    private static bool Matches(HpirLayout actual, HpirLayout expected) =>
        actual.Size == expected.Size && actual.Alignment == expected.Alignment
        && actual.PointerOffsets.SequenceEqual(expected.PointerOffsets)
        && actual.BorrowOffsets.SequenceEqual(expected.BorrowOffsets);
}
