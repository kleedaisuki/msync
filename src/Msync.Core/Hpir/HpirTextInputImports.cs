namespace Msync.Core.Hpir;

public static class HpirTextInputImports
{
    public static HpirLayout Completion() => new(24, 8, [0, 8]);
    public static HpirLayout ConcatLayout() => new(32, 8, []) { BorrowOffsets = [0, 8, 24] };
    public static HpirLayout NullProjection() => new(32, 8, [0]);
    public static HpirLayout NumberProjection() => new(40, 8, [0]);

    public static HpirImport ReadLine(int layout) => new(HpirService.ReadLineText, layout, HpirWidth.U64, []);
    public static HpirImport ReadStdin(int layout) => new(HpirService.ReadStdinText, layout, HpirWidth.U64, []);
    public static HpirImport Concat(int layout) => new(HpirService.ConcatTextPending, layout, HpirWidth.U64,
        [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]);
    public static HpirImport ReadFile(int layout, bool text) => new(HpirService.ReadFileText,
        checked(layout * 2 + (text ? 1 : 0)), HpirWidth.U64, [HpirWidth.U64]);
    public static HpirImport ProcessOutput(int layout, HpirService service) => new(service, layout, HpirWidth.U64, [HpirWidth.U64]);
    public static HpirImport Demand() => new(HpirService.DemandText, 0, HpirWidth.U64, [HpirWidth.U64]);
    public static HpirImport IsNull(int layout) => new(HpirService.TextIsNull, layout, HpirWidth.U64, [HpirWidth.U64]);
    public static HpirImport ParseNumber(int layout) => new(HpirService.TextParseNumber, layout, HpirWidth.U64, [HpirWidth.U64]);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        if (import.Result != HpirWidth.U64 || program.TextLayout < 0) return false;
        if (import.Service is HpirService.ReadLineText or HpirService.ReadStdinText)
            return import.Arguments.Length == 0 && LayoutMatches(program, import.Binding, Completion());
        if (import.Service == HpirService.ConcatTextPending)
            return import.Arguments is [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]
                && LayoutMatches(program, import.Binding, ConcatLayout());
        if (import.Arguments is not [HpirWidth.U64]) return false;
        return import.Service switch
        {
            HpirService.DemandText => import.Binding == 0,
            HpirService.ReadFileText => LayoutMatches(program, import.Binding >> 1, Completion()),
            HpirService.ProcessStdout or HpirService.ProcessStderr => LayoutMatches(program, import.Binding, Completion()),
            HpirService.TextIsNull => LayoutMatches(program, import.Binding, NullProjection()),
            HpirService.TextParseNumber => LayoutMatches(program, import.Binding, NumberProjection()),
            _ => false
        };
    }

    private static bool LayoutMatches(HpirProgram program, int index, HpirLayout expected)
    {
        if ((uint)index >= (uint)program.Layouts.Count) return false;
        HpirLayout actual = program.Layouts[index];
        return actual.Size == expected.Size && actual.Alignment == expected.Alignment
            && actual.PointerOffsets.SequenceEqual(expected.PointerOffsets)
            && actual.BorrowOffsets.SequenceEqual(expected.BorrowOffsets);
    }
}
