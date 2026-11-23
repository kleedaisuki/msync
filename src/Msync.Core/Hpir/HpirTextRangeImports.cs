namespace Msync.Core.Hpir;

public enum HpirTextRangeSource : byte { Stdin, FilePath, FileText }

public static class HpirTextRangeImports
{
    public static HpirLayout Plan(HpirTextRangeSource source) => source == HpirTextRangeSource.Stdin
        ? new(1, 1, [])
        : HpirPathImports.BorrowedRecord();

    public static HpirImport Source(HpirTextRangeSource source, int layout)
    {
        if ((uint)layout > int.MaxValue >> 2) throw new ArgumentOutOfRangeException(nameof(layout));
        return new(HpirService.TextRangeSource, (layout << 2) | (int)source, HpirWidth.U64,
            source == HpirTextRangeSource.Stdin ? [] : [HpirWidth.U64]);
    }

    public static (HpirTextRangeSource Source, int Layout) DecodeSource(int binding) =>
        ((HpirTextRangeSource)(binding & 3), binding >> 2);

    public static HpirImport Open(bool file) => new(HpirService.TextRangeOpen, file ? 1 : 0, HpirWidth.U64, [HpirWidth.U64]);
    public static HpirImport Next() => new(HpirService.TextRangeNext, 0, HpirWidth.U8, [HpirWidth.U64, HpirWidth.U64]);
    public static HpirImport Close() => new(HpirService.TextRangeClose, 0, HpirWidth.Void, [HpirWidth.U64]);
    public static HpirImport Join(bool file) => new(HpirService.TextRangeJoin, file ? 1 : 0, HpirWidth.U64,
        [HpirWidth.U64, HpirWidth.U64]);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        if (program.TextLayout < 0) return false;
        HpirImport expected;
        switch (import.Service)
        {
            case HpirService.TextRangeSource:
                var (source, layout) = DecodeSource(import.Binding);
                if (source > HpirTextRangeSource.FileText || (uint)layout >= (uint)program.Layouts.Count
                    || source != HpirTextRangeSource.Stdin && !program.PathAnchors) return false;
                HpirLayout expectedLayout = Plan(source), actual = program.Layouts[layout];
                if (expectedLayout.Size != actual.Size || expectedLayout.Alignment != actual.Alignment
                    || actual.PointerOffsets.Count != 0 || !actual.BorrowOffsets.SequenceEqual(expectedLayout.BorrowOffsets))
                    return false;
                expected = Source(source, layout);
                break;
            case HpirService.TextRangeOpen:
                if ((uint)import.Binding > 1) return false;
                expected = Open(import.Binding != 0);
                break;
            case HpirService.TextRangeNext: expected = Next(); break;
            case HpirService.TextRangeClose: expected = Close(); break;
            case HpirService.TextRangeJoin:
                if ((uint)import.Binding > 1) return false;
                expected = Join(import.Binding != 0);
                break;
            default: return false;
        }
        return expected.Binding == import.Binding && expected.Result == import.Result
            && expected.Arguments.AsSpan().SequenceEqual(import.Arguments);
    }
}
