namespace Msync.Core.Hpir;

// Take retains the original ordinal binding group on the development wire.
public enum HpirRangeBound : byte { Take, Repeat, EnumerateStart, Drop, None }

public static class HpirTransformImports
{
    public const int Environment = HpirRangeImports.PlanBytes;
    public const int VectorData = sizeof(ulong);
    public const int VectorHeaderBytes = 2 * sizeof(ulong);

    public static HpirLayout TransformLayout() =>
        new(Environment + sizeof(ulong), 8, []) { BorrowOffsets = [Environment] };
    public static HpirLayout VectorLayout() => new(VectorHeaderBytes, 8, [VectorData]);
    public static HpirLayout ElementLayout(bool reference) =>
        new(sizeof(ulong), 8, []) { BorrowOffsets = reference ? [0] : [] };

    public static HpirImport Capture(int layout) =>
        new(HpirService.RangeTransform, layout, HpirWidth.U64, [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]);

    public static HpirImport Take(int layout, HpirFutureInput count) =>
        new(HpirService.RangeTransformTake, checked(layout * 4 + (int)count), HpirWidth.U64,
            count == HpirFutureInput.Either ? [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64] : [HpirWidth.U64, HpirWidth.U64]);

    public static HpirImport Bound(HpirRangeBound operation, HpirFutureInput input) =>
        new(HpirService.RangeBound, (int)operation * 4 + (int)input, HpirWidth.U64,
            input == HpirFutureInput.Either ? [HpirWidth.U64, HpirWidth.U64] : [HpirWidth.U64]);

    public static (HpirRangeBound Operation, HpirFutureInput Input) DecodeBound(int binding) =>
        ((HpirRangeBound)(binding >> 2), (HpirFutureInput)(binding & 3));

    public static string BoundName(HpirRangeBound op) => op switch
    {
        HpirRangeBound.Take => "take",
        HpirRangeBound.Drop => "drop",
        HpirRangeBound.Repeat => "repeat",
        HpirRangeBound.EnumerateStart => "enumerate_from",
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    public static HpirImport Open() =>
        new(HpirService.RangeTransformOpen, 0, HpirWidth.Void, [HpirWidth.U64, HpirWidth.U64]);

    public static HpirImport Grow(int layout) =>
        new(HpirService.RangeBufferGrow, layout, HpirWidth.U64, [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]);

    public static HpirImport PackWords(int layout, int segments) =>
        new(HpirService.RangePackWords, layout, HpirWidth.U64,
            Enumerable.Repeat(HpirWidth.U64, checked(segments * 2)).ToArray());

    internal static bool Valid(HpirProgram program, HpirImport import) => import.Service switch
    {
        HpirService.RangeTransform => Matches(program, import.Binding, TransformLayout())
            && Signature(import, Capture(import.Binding)),
        HpirService.RangeTransformTake => ValidTake(program, import),
        HpirService.RangeBound => ValidBound(import),
        HpirService.RangeTransformOpen => import.Binding == 0 && Signature(import, Open()),
        HpirService.RangeBufferGrow => (Matches(program, import.Binding, ElementLayout(false))
            || Matches(program, import.Binding, ElementLayout(true))) && Signature(import, Grow(import.Binding)),
        HpirService.RangePackWords => (uint)import.Binding < (uint)program.Layouts.Count
            && program.Layouts[import.Binding].Size > 0
            && import.Result == HpirWidth.U64 && import.Arguments.Length >= 4 && (import.Arguments.Length & 1) == 0
            && import.Arguments.All(static width => width == HpirWidth.U64),
        _ => false
    };

    private static bool ValidBound(HpirImport import)
    {
        var (operation, input) = DecodeBound(import.Binding);
        return (uint)import.Binding < (int)HpirRangeBound.None * 4 && (uint)input <= (uint)HpirFutureInput.Either
            && Signature(import, Bound(operation, input));
    }

    private static bool ValidTake(HpirProgram program, HpirImport import)
    {
        var (layout, mode) = HpirRangeImports.DecodeWindow(import.Binding);
        return (uint)mode <= (uint)HpirFutureInput.Either && Matches(program, layout, TransformLayout())
            && Signature(import, Take(layout, mode));
    }

    private static bool Matches(HpirProgram program, int index, HpirLayout expected)
    {
        if ((uint)index >= (uint)program.Layouts.Count) return false;
        HpirLayout layout = program.Layouts[index];
        return layout.Size == expected.Size && layout.Alignment == expected.Alignment
            && layout.PointerOffsets.SequenceEqual(expected.PointerOffsets)
            && layout.BorrowOffsets.SequenceEqual(expected.BorrowOffsets);
    }

    private static bool Signature(HpirImport actual, HpirImport expected) =>
        actual.Result == expected.Result && actual.Arguments.AsSpan().SequenceEqual(expected.Arguments);
}
