namespace Msync.Core.Hpir;

public static class HpirDirectoryCreationImports
{
    public const string Name = "fs.create_directories";

    public static HpirImport Create(bool text) =>
        new(HpirService.CreateDirectories, text ? 1 : 0, HpirWidth.U8, [HpirWidth.U64]);

    internal static bool Valid(HpirProgram program, HpirImport import) =>
        import.Binding is 0 or 1 && import.Result == HpirWidth.U8
        && import.Arguments is [HpirWidth.U64] && program.PathAnchors;
}
