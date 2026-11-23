namespace Msync.Core.Hpir;

public static class HpirRangeImports
{
    public const int PlanBytes = 40, DeferredPlanBytes = 56, CursorBytes = 56, Alignment = 8;
    public const int Current = 0, Limit = 8, Step = 16, Skip = 24, Fence = 32, Position = 40,
        Stalled = 48, Descending = 49;
    private const int ModeBits = 2, SequenceModeBits = 3 * ModeBits;
    private const int ModeMask = (1 << ModeBits) - 1;

    public static HpirLayout ReadyLayout() => new(PlanBytes, Alignment, []);
    public static HpirLayout DeferredLayout() => new(DeferredPlanBytes, Alignment, [0, 8, 16]);

    public static HpirImport Create(HpirService service, int layout, HpirFutureInput count = HpirFutureInput.Ready) => service switch
    {
        HpirService.RangeSequence => new(service, layout, HpirWidth.U64, [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]),
        HpirService.RangeDrop or HpirService.RangeTake => new(service, Bind(layout, (int)count, ModeBits), HpirWidth.U64,
            Words(1 + ScalarWords(count))),
        HpirService.RangeOpen => new(service, layout, HpirWidth.Void, [HpirWidth.U64, HpirWidth.U64]),
        _ => throw new ArgumentOutOfRangeException(nameof(service))
    };

    public static HpirImport DeferredSequence(int layout, HpirFutureInput first, HpirFutureInput limit, HpirFutureInput step)
    {
        int modes = (int)first | ((int)limit << ModeBits) | ((int)step << (2 * ModeBits));
        return new(HpirService.RangeDeferredSequence, Bind(layout, modes, SequenceModeBits), HpirWidth.U64,
            Words(ScalarWords(first) + ScalarWords(limit) + ScalarWords(step)));
    }

    public static (int Layout, HpirFutureInput First, HpirFutureInput Limit, HpirFutureInput Step) DecodeSequence(int binding) =>
        (binding >> SequenceModeBits, Mode(binding), Mode(binding >> ModeBits), Mode(binding >> (2 * ModeBits)));

    public static (int Layout, HpirFutureInput Count) DecodeWindow(int binding) => (binding >> ModeBits, Mode(binding));

    internal static bool Valid(HpirImport import, IReadOnlyList<HpirLayout> layouts) => import.Service switch
    {
        HpirService.RangeOpen => import.Result == HpirWidth.Void && Words(import, 2)
            && IsLayout(layouts, import.Binding, ReadyLayout()),
        HpirService.RangeSequence => import.Result == HpirWidth.U64 && Words(import, 3)
            && IsLayout(layouts, import.Binding, ReadyLayout()),
        HpirService.RangeDrop or HpirService.RangeTake => ValidWindow(import, layouts),
        HpirService.RangeDeferredSequence => ValidSequence(import, layouts),
        _ => false
    };

    private static bool ValidWindow(HpirImport import, IReadOnlyList<HpirLayout> layouts)
    {
        var (layout, count) = DecodeWindow(import.Binding);
        return Valid(count) && import.Result == HpirWidth.U64 && Words(import, 1 + ScalarWords(count))
            && IsLayout(layouts, layout, ReadyLayout());
    }

    private static bool ValidSequence(HpirImport import, IReadOnlyList<HpirLayout> layouts)
    {
        var (layout, first, limit, step) = DecodeSequence(import.Binding);
        return Valid(first) && Valid(limit) && Valid(step) && (first | limit | step) != HpirFutureInput.Ready
            && import.Result == HpirWidth.U64 && Words(import, ScalarWords(first) + ScalarWords(limit) + ScalarWords(step))
            && IsLayout(layouts, layout, DeferredLayout());
    }

    private static bool IsLayout(IReadOnlyList<HpirLayout> layouts, int index, HpirLayout expected)
    {
        if ((uint)index >= (uint)layouts.Count) return false;
        HpirLayout actual = layouts[index];
        return actual.Size == expected.Size && actual.Alignment == expected.Alignment && actual.BorrowOffsets.Count == 0
            && actual.PointerOffsets.SequenceEqual(expected.PointerOffsets);
    }

    private static int Bind(int layout, int modes, int bits) => (uint)layout <= int.MaxValue >> bits
        ? layout << bits | modes : throw new ArgumentOutOfRangeException(nameof(layout));

    private static HpirFutureInput Mode(int binding) => (HpirFutureInput)(binding & ModeMask);
    private static bool Valid(HpirFutureInput mode) => (uint)mode <= (uint)HpirFutureInput.Either;
    private static int ScalarWords(HpirFutureInput mode) => mode == HpirFutureInput.Either ? 2 : 1;
    private static HpirWidth[] Words(int count)
    {
        var values = new HpirWidth[count];
        Array.Fill(values, HpirWidth.U64);
        return values;
    }

    private static bool Words(HpirImport import, int count) =>
        import.Arguments.Length == count && import.Arguments.All(static width => width == HpirWidth.U64);
}
