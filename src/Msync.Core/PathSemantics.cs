namespace Msync.Core;

public static class PathSemantics
{
    public static bool UsesWindowsPaths(PathValue path) => OperatingSystem.IsWindows() && path.Connection?.Kind is null or ConnectionKind.Local;
    public static string Join(PathValue path, string item) => JoinText(path.NativeText, item, UsesWindowsPaths(path));
    public static PathValue JoinValue(PathValue path, Value item) => JoinValue(path, item, OperatingSystem.IsWindows());
    public static PathValue JoinValue(PathValue path, Value item, bool windowsHost)
    {
        string raw = item switch { PathValue right => right.Raw, StringValue text => text.Data, _ => throw new MsyncException($"Cannot join Path with {item.Type}") };
        bool windows = windowsHost && (path.Connection?.Kind is null or ConnectionKind.Local);
        string nativeItem = windows && item is PathValue rightPath ? rightPath.NativeText : raw;
        if (path.Connection is not null)
            return path.WithRaw(JoinText(path.NativeText, nativeItem, windows));
        string symbolic = JoinText(path.Raw, raw, false);
        string? local = windows ? JoinText(path.NativeText, nativeItem, true) : null;
        return path.WithRaw(symbolic, local);
    }
    public static PathValue Qualify(PathValue path, ConnectionValue connection) => path with
    {
        Connection = connection,
        LocalSpelling = connection.Kind == ConnectionKind.Local ? path.LocalSpelling : null,
        WorkingDirectory = connection.Kind == ConnectionKind.Local ? path.WorkingDirectory : null,
        HomeDirectory = connection.Kind == ConnectionKind.Local ? path.HomeDirectory : null
    };
    private static string JoinText(string left, string right, bool windows) => windows
        ? left.TrimEnd('/', '\\') + '\\' + right.TrimStart('/', '\\')
        : left.TrimEnd('/') + '/' + right.TrimStart('/');
    public static string FileName(PathValue path)
    {
        var raw = UsesWindowsPaths(path) ? path.Raw.Replace('\\', '/') : path.Raw;
        return raw.TrimEnd('/').Split('/')[^1];
    }
}
