namespace Msync.Core;

public static class PathSemantics
{
    public static bool UsesWindowsPaths(PathValue path) => OperatingSystem.IsWindows() && path.Connection?.Kind is null or ConnectionKind.Local;
    public static string Join(PathValue path, string item)
    {
        return UsesWindowsPaths(path)
            ? path.Raw.TrimEnd('/', '\\') + '\\' + item.TrimStart('/', '\\')
            : path.Raw.TrimEnd('/') + '/' + item.TrimStart('/');
    }
    public static string FileName(PathValue path)
    {
        var raw = UsesWindowsPaths(path) ? path.Raw.Replace('\\', '/') : path.Raw;
        return raw.TrimEnd('/').Split('/')[^1];
    }
}
