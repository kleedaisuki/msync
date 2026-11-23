namespace Msync.Core.Hpir;

public enum HpirPathOp : byte
{
    Invalid, FromText, Join,
    RootName, RootDirectory, RootPath, RelativePath, ParentPath, Filename, Stem, Extension,
    Normal, Relative, Proximate, ReplaceFilename, ReplaceExtension, RemoveFilename, Append, Concat, Preferred,
    NativeString, GenericString, Display,
    HasRootName, HasRootDirectory, HasRootPath, HasRelativePath, HasParentPath, HasFilename, HasStem, HasExtension,
    IsAbsolute, IsRelative, IsEmpty, WorkingDirectory, HomeDirectory,
    Equal, NotEqual, SetWorkingDirectory, FromLiteral, Canonical, Qualify
}

public static class HpirPathImports
{
    public const int PathBytes = 48, PathAlignment = 8;
    public const int Endpoint = 0, IsQualified = 1, Raw = 8, Directory = 16, Home = 24, Spelling = 32, EndpointAddress = 40;
    private const int LeftText = 1 << 8, RightText = 1 << 9;

    public static HpirLayout BorrowedRecord() => new(PathBytes, PathAlignment, [])
    { BorrowOffsets = [Raw, Directory, Home, Spelling, EndpointAddress] };

    public static bool TryLexical(string name, out HpirPathOp operation)
    {
        operation = name switch
        {
            "root_name" => HpirPathOp.RootName,
            "root_directory" => HpirPathOp.RootDirectory,
            "root_path" => HpirPathOp.RootPath,
            "relative_path" => HpirPathOp.RelativePath,
            "parent_path" => HpirPathOp.ParentPath,
            "filename" => HpirPathOp.Filename,
            "stem" => HpirPathOp.Stem,
            "extension" => HpirPathOp.Extension,
            "lexically_normal" => HpirPathOp.Normal,
            "lexically_relative" => HpirPathOp.Relative,
            "lexically_proximate" => HpirPathOp.Proximate,
            "replace_filename" => HpirPathOp.ReplaceFilename,
            "replace_extension" => HpirPathOp.ReplaceExtension,
            "remove_filename" => HpirPathOp.RemoveFilename,
            "append" => HpirPathOp.Append,
            "concat" => HpirPathOp.Concat,
            "make_preferred" => HpirPathOp.Preferred,
            "string" or "native" => HpirPathOp.NativeString,
            "generic_string" => HpirPathOp.GenericString,
            "has_root_name" => HpirPathOp.HasRootName,
            "has_root_directory" => HpirPathOp.HasRootDirectory,
            "has_root_path" => HpirPathOp.HasRootPath,
            "has_relative_path" => HpirPathOp.HasRelativePath,
            "has_parent_path" => HpirPathOp.HasParentPath,
            "has_filename" => HpirPathOp.HasFilename,
            "has_stem" => HpirPathOp.HasStem,
            "has_extension" => HpirPathOp.HasExtension,
            "is_absolute" => HpirPathOp.IsAbsolute,
            "is_relative" => HpirPathOp.IsRelative,
            "path_empty" => HpirPathOp.IsEmpty,
            _ => HpirPathOp.Invalid
        };
        return operation != HpirPathOp.Invalid;
    }

    public static int OperandCount(HpirPathOp operation) => operation switch
    {
        HpirPathOp.WorkingDirectory or HpirPathOp.HomeDirectory => 0,
        HpirPathOp.Join or HpirPathOp.Relative or HpirPathOp.Proximate or HpirPathOp.ReplaceFilename
            or HpirPathOp.ReplaceExtension or HpirPathOp.Append or HpirPathOp.Concat
            or HpirPathOp.Equal or HpirPathOp.NotEqual or HpirPathOp.Qualify => 2,
        _ => 1
    };
    public static bool ReturnsPath(HpirPathOp operation) => operation is >= HpirPathOp.FromText and <= HpirPathOp.Preferred
        or HpirPathOp.WorkingDirectory or HpirPathOp.HomeDirectory or HpirPathOp.SetWorkingDirectory
        or HpirPathOp.FromLiteral or HpirPathOp.Canonical or HpirPathOp.Qualify;
    public static bool ReturnsText(HpirPathOp operation) => operation is HpirPathOp.NativeString or HpirPathOp.GenericString or HpirPathOp.Display;
    public static bool CanAllocate(HpirPathOp operation) => operation is > HpirPathOp.FromText and <= HpirPathOp.Preferred
        or HpirPathOp.GenericString or HpirPathOp.Display or HpirPathOp.Canonical or HpirPathOp.Qualify;

    public static HpirImport Create(HpirPathOp op, bool leftText, bool rightText)
    {
        bool path = ReturnsPath(op);
        int count = OperandCount(op) + (path ? 1 : 0);
        var arguments = new HpirWidth[count];
        Array.Fill(arguments, HpirWidth.U64);
        int binding = (int)op | (leftText ? LeftText : 0) | (rightText ? RightText : 0);
        return new(HpirService.Path, binding, path ? HpirWidth.Void : ReturnsText(op) ? HpirWidth.U64 : HpirWidth.U8, arguments);
    }

    public static (HpirPathOp Op, bool LeftText, bool RightText) Decode(int binding) =>
        ((HpirPathOp)(binding & 255), (binding & LeftText) != 0, (binding & RightText) != 0);

    internal static bool Valid(HpirProgram program, HpirImport import)
    {
        var (op, leftText, rightText) = Decode(import.Binding);
        if (op <= HpirPathOp.Invalid || op > HpirPathOp.Qualify || import.Binding >> 10 != 0
            || op is HpirPathOp.Equal or HpirPathOp.NotEqual && (leftText || rightText)
            || op == HpirPathOp.Qualify && leftText
            || rightText && OperandCount(op) != 2 || op is HpirPathOp.FromText or HpirPathOp.FromLiteral && !leftText
            || leftText && (op == HpirPathOp.Display || OperandCount(op) == 0) || !program.PathAnchors)
            return false;
        HpirImport expected = Create(op, leftText, rightText);
        return import.Result == expected.Result && import.Arguments.AsSpan().SequenceEqual(expected.Arguments)
            && (!CanAllocate(op) || (uint)program.TextLayout < (uint)program.Layouts.Count
                && HpirScalarImports.IsTextLayout(program.Layouts[program.TextLayout]));
    }
}
