namespace Msync.Core.Hpir;

public static class HpirDirectoryImports
{
    public const int RecursiveOffset = HpirPathImports.PathBytes;

    public static HpirLayout Plan() => HpirPathImports.BorrowedRecord() with { Size = RecursiveOffset + sizeof(ulong) };

    public static HpirImport Source(int layout, bool text, bool recursive) => new(HpirService.DirectorySource,
        checked(layout * 4) | (text ? 1 : 0) | (recursive ? 2 : 0), HpirWidth.U64, [HpirWidth.U64]);

    public static (int Layout, bool Text, bool Recursive) DecodeSource(int binding) =>
        (binding >> 2, (binding & 1) != 0, (binding & 2) != 0);

    public static HpirImport Open() => Open(selection: false);

    public static HpirImport Open(bool selection) =>
        new(HpirService.DirectoryOpen, selection ? 1 : 0, HpirWidth.U64, [HpirWidth.U64]);

    public static HpirImport Next() => new(HpirService.DirectoryNext, 0, HpirWidth.U8,
        [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]);

    public static HpirImport Close() => new(HpirService.DirectoryClose, 0, HpirWidth.Void, [HpirWidth.U64]);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        if (program.TextLayout < 0) return false;
        HpirImport expected;
        switch (import.Service)
        {
            case HpirService.DirectorySource:
                var (layout, text, recursive) = DecodeSource(import.Binding);
                if ((uint)layout >= (uint)program.Layouts.Count || text && !program.PathAnchors) return false;
                var found = program.Layouts[layout];
                var expectedLayout = Plan();
                if (found.Size != expectedLayout.Size || found.Alignment != expectedLayout.Alignment
                    || found.PointerOffsets.Count != 0 || !found.BorrowOffsets.SequenceEqual(expectedLayout.BorrowOffsets))
                    return false;
                expected = Source(layout, text, recursive);
                break;
            case HpirService.DirectoryOpen: expected = Open(import.Binding != 0); break;
            case HpirService.DirectoryNext: expected = Next(); break;
            case HpirService.DirectoryClose: expected = Close(); break;
            default: return false;
        }
        return import.Binding == expected.Binding && import.Result == expected.Result
            && import.Arguments.AsSpan().SequenceEqual(expected.Arguments);
    }
}
