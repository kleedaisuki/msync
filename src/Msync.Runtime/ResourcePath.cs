using System.Runtime.InteropServices;

namespace Msync.Runtime;

internal static class ResourcePath
{
    internal static string Resolve(string path)
    {
        if (OperatingSystem.IsWindows()) return ResolveWindows(path);
        var pending = new Stack<string>();
        var current = path.TrimEnd(Path.DirectorySeparatorChar);
        while (current.Length != 0)
        {
            var pointer = RealPath(current, IntPtr.Zero);
            if (pointer != IntPtr.Zero)
            {
                string resolved;
                try { resolved = Marshal.PtrToStringUTF8(pointer)!; }
                finally { Free(pointer); }
                return Path.GetFullPath(pending.Aggregate(resolved, Path.Combine));
            }
            var parent = Path.GetDirectoryName(current);
            if (parent == current) break;
            pending.Push(Path.GetFileName(current)); current = parent ?? "";
        }
        return Path.GetFullPath(path);
    }
    private static string ResolveWindows(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        foreach (var item in full[root.Length..].Split(Path.DirectorySeparatorChar))
        {
            root = Path.Combine(root, item);
            var info = new DirectoryInfo(root);
            if (info.Exists && info.LinkTarget is not null) root = info.ResolveLinkTarget(true)!.FullName;
        }
        return root;
    }
    [DllImport("libc", EntryPoint = "realpath", SetLastError = true)]
    private static extern IntPtr RealPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);
    [DllImport("libc", EntryPoint = "free")]
    private static extern void Free(IntPtr pointer);
}
