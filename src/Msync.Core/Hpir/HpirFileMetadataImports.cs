namespace Msync.Core.Hpir;

public enum HpirFileMetadata : byte
{
    Invalid, Size, Exists, IsRegularFile, IsDirectory, IsSymlink, IsBlockFile,
    IsCharacterFile, IsFifo, IsSocket, IsOther, IsEmpty, IsReadonly, Canonical
}

public static class HpirFileMetadataImports
{
    public static HpirFileMetadata Operation(string name) => name == "io.file_size" ? HpirFileMetadata.Size
        : name.StartsWith("fs.", StringComparison.Ordinal) ? Predicate(name.AsSpan(3)) : HpirFileMetadata.Invalid;

    public static HpirFileMetadata Predicate(ReadOnlySpan<char> name) => name switch
    {
        "exists" => HpirFileMetadata.Exists,
        "is_regular_file" => HpirFileMetadata.IsRegularFile,
        "is_directory" => HpirFileMetadata.IsDirectory,
        "is_symlink" => HpirFileMetadata.IsSymlink,
        "is_block_file" => HpirFileMetadata.IsBlockFile,
        "is_character_file" => HpirFileMetadata.IsCharacterFile,
        "is_fifo" => HpirFileMetadata.IsFifo,
        "is_socket" => HpirFileMetadata.IsSocket,
        "is_other" => HpirFileMetadata.IsOther,
        "is_empty" => HpirFileMetadata.IsEmpty,
        "is_readonly" => HpirFileMetadata.IsReadonly,
        _ => HpirFileMetadata.Invalid
    };

    public static HpirWidth Payload(HpirFileMetadata op) => op == HpirFileMetadata.Size ? HpirWidth.U64 : HpirWidth.U8;

    public static string Name(HpirFileMetadata operation) => operation switch
    {
        HpirFileMetadata.Size => "file_size",
        HpirFileMetadata.Exists => "exists",
        HpirFileMetadata.IsRegularFile => "is_regular_file",
        HpirFileMetadata.IsDirectory => "is_directory",
        HpirFileMetadata.IsSymlink => "is_symlink",
        HpirFileMetadata.IsBlockFile => "is_block_file",
        HpirFileMetadata.IsCharacterFile => "is_character_file",
        HpirFileMetadata.IsFifo => "is_fifo",
        HpirFileMetadata.IsSocket => "is_socket",
        HpirFileMetadata.IsOther => "is_other",
        HpirFileMetadata.IsEmpty => "is_empty",
        HpirFileMetadata.IsReadonly => "is_readonly",
        HpirFileMetadata.Canonical => "canonical",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static string NativeName(HpirFileMetadata operation) => (operation == HpirFileMetadata.Size ? "io." : "fs.") + Name(operation);

    public static HpirImport Create(HpirFileMetadata op, bool text, int layout)
    {
        if ((uint)layout > (uint)int.MaxValue >> 5) throw new ArgumentOutOfRangeException(nameof(layout));
        return new(HpirService.FileMetadata, (layout << 5) | (text ? 16 : 0) | (int)op, HpirWidth.U64,
            [HpirWidth.U64, HpirWidth.U64]);
    }

    public static (HpirFileMetadata Operation, bool Text, int Layout) Decode(int binding) =>
        ((HpirFileMetadata)(binding & 15), (binding & 16) != 0, binding >> 5);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        var (op, _, layout) = Decode(import.Binding);
        return op is >= HpirFileMetadata.Size and <= HpirFileMetadata.IsReadonly
            && import.Result == HpirWidth.U64 && import.Arguments is [HpirWidth.U64, HpirWidth.U64]
            && (uint)layout < (uint)program.Layouts.Count
            && program.Layouts[layout] is { Size: 32, Alignment: 8, PointerOffsets.Count: 0, BorrowOffsets.Count: 0 };
    }
}
