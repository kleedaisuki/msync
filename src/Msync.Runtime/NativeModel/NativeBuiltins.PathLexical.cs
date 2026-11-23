using Msync.Core;
using Msync.Core.Hpir;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    internal static NativePathValue PathLexicalValue(HpirPathOp op, NativePathValue path, NativePathValue right)
    {
        if (op == HpirPathOp.Join) return PathSemantics.JoinValue(path, right);
        if (op is HpirPathOp.Filename or HpirPathOp.Stem or HpirPathOp.Extension)
        {
            NativePathComponent component = op switch
            {
                HpirPathOp.Filename => NativePathComponent.Filename,
                HpirPathOp.Stem => NativePathComponent.Stem,
                _ => NativePathComponent.Extension
            };
            return MaterializeLexicalComponent(path, component, OperatingSystem.IsWindows());
        }
        bool windows = LocalWindows(path);
        bool projected = path.Connection is null && windows;
        NativePath raw = new(path.Raw, windows, projected ? '/' : null);
        if (op is HpirPathOp.Relative or HpirPathOp.Proximate)
        {
            bool sameEndpoint = path.Connection == right.Connection;
            string relative = sameEndpoint ? raw.RelativeTo(new NativePath(right.Raw, windows)) : "";
            if (op == HpirPathOp.Proximate && relative.Length == 0) return path;
            string? local = projected && sameEndpoint
                ? new NativePath(path.NativeText, true).RelativeTo(new NativePath(right.NativeText, true)) : null;
            return path.WithRaw(relative, local);
        }
        return path.WithRaw(Project(op, raw, right.Raw),
            projected ? Project(op, new NativePath(path.NativeText, true), right.NativeText) : null);
    }

    internal static string PathLexicalString(HpirPathOp op, NativePathValue path) => op switch
    {
        HpirPathOp.Display => path.ToString(),
        HpirPathOp.NativeString => path.NativeText,
        HpirPathOp.GenericString => LocalWindows(path) ? path.Raw.Replace('\\', '/') : path.Raw,
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    internal static bool PathLexicalPredicate(HpirPathOp op, NativePathValue path)
    {
        bool windows = LocalWindows(path);
        NativePath value = new(path.Raw, windows, path.Connection is null && windows ? '/' : null);
        return op switch
        {
            HpirPathOp.HasRootName => !value.RootNameSpan.IsEmpty,
            HpirPathOp.HasRootDirectory => value.RootDirectory,
            HpirPathOp.HasRootPath => value.RootDirectory || !value.RootNameSpan.IsEmpty,
            HpirPathOp.HasRelativePath => !value.RelativeSpan.IsEmpty,
            HpirPathOp.HasParentPath => value.Parent.Length > 0,
            HpirPathOp.HasFilename => !value.FilenameSpan.IsEmpty,
            HpirPathOp.HasStem => !value.StemSpan.IsEmpty,
            HpirPathOp.HasExtension => !value.ExtensionSpan.IsEmpty,
            HpirPathOp.IsAbsolute => value.IsAbsolute,
            HpirPathOp.IsRelative => !value.IsAbsolute,
            HpirPathOp.IsEmpty => path.Raw.Length == 0,
            _ => throw new ArgumentOutOfRangeException(nameof(op))
        };
    }

    private static string Project(HpirPathOp op, NativePath value, string? argument) => op switch
    {
        HpirPathOp.RootName => value.RootName,
        HpirPathOp.RootDirectory => value.RootDirectory ? value.Separator.ToString() : "",
        HpirPathOp.RootPath => value.Root,
        HpirPathOp.RelativePath => value.Relative,
        HpirPathOp.ParentPath => value.Parent,
        HpirPathOp.Normal => value.Normal(),
        HpirPathOp.RemoveFilename => value.RemoveFilename,
        HpirPathOp.Preferred => value.Preferred,
        HpirPathOp.Concat => value.Source + argument,
        HpirPathOp.Append => value.Append(argument!),
        HpirPathOp.ReplaceFilename => new NativePath(value.RemoveFilename, value.Windows, value.Separator).Append(argument!),
        HpirPathOp.ReplaceExtension => ReplaceExtension(value, argument!),
        _ => throw new ArgumentOutOfRangeException(nameof(op))
    };

    private static string ReplaceExtension(NativePath path, string extension)
    {
        var prefix = path.Source.AsSpan(0, path.Source.Length - path.ExtensionSpan.Length);
        return extension.Length == 0 || extension[0] == '.' ? string.Concat(prefix, extension)
            : string.Concat(prefix, ".", extension);
    }
}
