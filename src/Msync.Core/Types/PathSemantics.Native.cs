using Msync.Core.Native;

namespace Msync.Core;

public static partial class PathSemantics
{
    public static bool UsesWindowsPaths(NativePath path) =>
        OperatingSystem.IsWindows() && path.Connection?.Kind is null or ConnectionKind.Local;

    public static string Join(NativePath path, string item) =>
        JoinText(path.NativeText, item, UsesWindowsPaths(path));

    public static NativePath JoinValue(NativePath path, string item) =>
        JoinValue(path, item, OperatingSystem.IsWindows());

    public static NativePath JoinValue(NativePath path, string item, bool windowsHost) =>
        JoinPath(path, item, item, windowsHost);

    public static NativePath JoinValue(NativePath path, NativePath item) =>
        JoinValue(path, item, OperatingSystem.IsWindows());

    public static NativePath JoinValue(NativePath path, NativePath item, bool windowsHost) =>
        JoinPath(path, item.Raw, item.NativeText, windowsHost);

    private static NativePath JoinPath(NativePath path, string raw, string native, bool windowsHost)
    {
        bool windows = windowsHost && path.Connection?.Kind is null or ConnectionKind.Local;
        if (path.Connection is not null)
            return path.WithRaw(JoinText(path.NativeText, windows ? native : raw, windows));
        return path.WithRaw(JoinText(path.Raw, raw, false),
            windows ? JoinText(path.NativeText, native, true) : null);
    }

    public static NativePath Qualify(NativePath path, NativeConnection connection) => path with
    {
        Connection = connection,
        LocalSpelling = connection.Kind == ConnectionKind.Local ? path.LocalSpelling : null,
        WorkingDirectory = connection.Kind == ConnectionKind.Local ? path.WorkingDirectory : null,
        HomeDirectory = connection.Kind == ConnectionKind.Local ? path.HomeDirectory : null
    };

    private static string JoinText(string left, string right, bool windows) => windows
        ? left.TrimEnd('/', '\\') + '\\' + right.TrimStart('/', '\\')
        : left.TrimEnd('/') + '/' + right.TrimStart('/');

    public static string FileName(NativePath path) => FileNameSpan(path).ToString();

    public static ReadOnlySpan<char> FileNameSpan(NativePath path)
    {
        ReadOnlySpan<char> raw = path.Raw;
        if (UsesWindowsPaths(path))
        {
            raw = raw.TrimEnd("/\\");
            return raw[(raw.LastIndexOfAny('/', '\\') + 1)..];
        }
        raw = raw.TrimEnd('/');
        return raw[(raw.LastIndexOf('/') + 1)..];
    }
}
