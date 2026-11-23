namespace Msync.Core.Hpir;

public static class HpirPathSelectionImports
{
    public const int PatternOffset = HpirPathImports.PathBytes;
    public const int RecipeOffset = PatternOffset + sizeof(int);
    public const int PlanBytes = PatternOffset + HpirPathImports.PathAlignment;
    public static HpirLayout Layout() => HpirPathImports.BorrowedRecord() with { Size = PlanBytes };
    private static readonly IReadOnlyList<int> BorrowedFields = Layout().BorrowOffsets;

    public static HpirImport Capture(int layout) => Capture(layout, prepared: false);

    public static HpirImport Capture(int layout, bool prepared) => new(HpirService.PathSelection, layout, HpirWidth.U64,
        prepared ? [HpirWidth.U64, HpirWidth.U32, HpirWidth.U32] : [HpirWidth.U64, HpirWidth.U32]);

    internal static bool Valid(HpirProgram program, HpirImport import) => (uint)import.Binding < (uint)program.Layouts.Count
        && program.TextLiterals.Count != 0 && import.Result == HpirWidth.U64
        && (import.Arguments is [HpirWidth.U64, HpirWidth.U32]
            || program.IteratorSelections.Count != 0 && import.Arguments is [HpirWidth.U64, HpirWidth.U32, HpirWidth.U32])
        && program.Layouts[import.Binding] is { Size: PlanBytes, Alignment: HpirPathImports.PathAlignment, PointerOffsets.Count: 0 } layout
        && layout.BorrowOffsets.SequenceEqual(BorrowedFields);
}
