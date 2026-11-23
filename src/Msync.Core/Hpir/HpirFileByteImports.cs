namespace Msync.Core.Hpir;

public static class HpirFileByteImports
{
    public const int ChunkBytes = 4096;
    public const string InvalidByte = "Byte ranges require Number values in the integral interval 0..255.";

    public static HpirImport Open(bool append, bool textPath) => new(HpirService.FileBytesOpen,
        (append ? 1 : 0) | (textPath ? 2 : 0), HpirWidth.U64, [HpirWidth.U64, HpirWidth.U64]);
    public static HpirImport Chunk(bool final) => new(HpirService.FileBytesChunk, final ? 1 : 0,
        final ? HpirWidth.Void : HpirWidth.U64, [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64]);

    internal static bool Valid(HpirImport import) => import.Service switch
    {
        HpirService.FileBytesOpen => (uint)import.Binding <= 3 && import.Result == HpirWidth.U64
            && import.Arguments is [HpirWidth.U64, HpirWidth.U64],
        HpirService.FileBytesChunk => (uint)import.Binding <= 1
            && import.Result == (import.Binding == 0 ? HpirWidth.U64 : HpirWidth.Void)
            && import.Arguments is [HpirWidth.U64, HpirWidth.U64, HpirWidth.U64],
        _ => false
    };

    public static string NativeName(bool append) => append ? "io.append_bytes" : "io.write_bytes";
}
