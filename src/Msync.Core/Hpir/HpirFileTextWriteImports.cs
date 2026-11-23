namespace Msync.Core.Hpir;

public enum HpirFileTextSource : byte { Ready, Completion, Receipt }

public static class HpirFileTextWriteImports
{
    public static HpirImport Create(bool append, bool textPath, HpirFileTextSource source) =>
        new(HpirService.WriteFileText, (append ? 1 : 0) | (textPath ? 2 : 0) | ((int)source << 2),
            HpirWidth.Void, [HpirWidth.U64, HpirWidth.U64]);

    public static (bool Append, bool TextPath, HpirFileTextSource Source) Decode(int binding) =>
        ((binding & 1) != 0, (binding & 2) != 0, (HpirFileTextSource)(binding >> 2));

    public static string NativeName(bool append) => append ? "io.append_text" : "io.write_text";

    internal static bool Valid(HpirProgram program, HpirImport import) =>
        import.Binding is >= 0 and <= ((int)HpirFileTextSource.Receipt << 2 | 3)
        && import.Result == HpirWidth.Void && import.Arguments is [HpirWidth.U64, HpirWidth.U64]
        && program.PathAnchors
        && (Decode(import.Binding).Source != HpirFileTextSource.Completion || program.TextLayout >= 0);
}
