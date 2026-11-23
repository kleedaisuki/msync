namespace Msync.Core.Hpir;

public static class HpirFileRenameImports
{
    public static HpirImport Create() =>
        new(HpirService.RenameFile, 0, HpirWidth.Void, [HpirWidth.U64, HpirWidth.U64]);

    internal static bool Valid(HpirProgram program, HpirImport import) =>
        import.Binding == 0 && import.Result == HpirWidth.Void
        && import.Arguments is [HpirWidth.U64, HpirWidth.U64] && program.PathAnchors;
}
