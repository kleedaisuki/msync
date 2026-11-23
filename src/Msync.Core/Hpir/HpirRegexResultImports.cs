namespace Msync.Core.Hpir;

public static class HpirRegexResultImports
{
    public const int MatchValue = 0, MatchIndex = 8, MatchLength = 16, MatchGroups = 24, MatchBytes = 32;
    public const int GroupName = 0, GroupValue = 8, GroupIndex = 16, GroupLength = 24, GroupSuccess = 32, GroupBytes = 40;

    public static HpirImport Find(int matchLayout, bool literal) => new(HpirService.RegexFind,
        checked(matchLayout * 2 + (literal ? 1 : 0)), HpirWidth.U64,
        [HpirWidth.U64, literal ? HpirWidth.U32 : HpirWidth.U64, HpirWidth.U32, HpirWidth.U32, HpirWidth.U32]);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        int layout = import.Binding >> 1;
        bool literal = (import.Binding & 1) != 0;
        return program.TextLayout >= 0 && (!literal || program.TextLiterals.Count != 0)
            && (uint)layout < (uint)program.Layouts.Count && MatchLayout(program.Layouts[layout])
            && import.Result == HpirWidth.U64 && import.Arguments is
                [HpirWidth.U64, var pattern, HpirWidth.U32, HpirWidth.U32, HpirWidth.U32]
            && pattern == (literal ? HpirWidth.U32 : HpirWidth.U64);
    }

    public static bool MatchLayout(HpirLayout layout) => layout is { Size: MatchBytes, Alignment: 8, PointerOffsets.Count: 0 }
        && layout.BorrowOffsets.SequenceEqual([MatchValue, MatchGroups]);

    public static bool GroupLayout(HpirLayout layout) => layout is { Size: GroupBytes, Alignment: 8, PointerOffsets.Count: 0 }
        && layout.BorrowOffsets.SequenceEqual([GroupName, GroupValue]);
}
