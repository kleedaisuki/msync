namespace Msync.Runtime;

internal static class TransferOverlap
{
    private sealed record Node(string Source, string Relative, FileAttributes Attributes, DateTime Modified, UnixFileMode Mode, string? Link);

    internal static bool SameNode(string source, string target)
    {
        if (!Path.Exists(target)) return false;
        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0) return false;
        if (OperatingSystem.IsWindows())
            return string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
        return NativeFileIdentity.Equivalent(source, target);
    }

    internal static bool NeedsSnapshot(string source, string target, bool replace)
    {
        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0) return false;
        return Within(source, target) || replace && Within(target, source);
    }

    private static bool Within(string root, string path)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string normalizedPath = Path.GetFullPath(path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison)) return true;
        if (OperatingSystem.IsWindows() || !Directory.Exists(root)) return false;
        string? ancestor = Path.GetDirectoryName(path);
        while (ancestor is not null)
        {
            if (Directory.Exists(ancestor) && NativeFileIdentity.Equivalent(root, ancestor)) return true;
            ancestor = Path.GetDirectoryName(ancestor);
        }
        return false;
    }

    internal static async Task CopyAsync(string source, string target, bool replace, CancellationToken cancellationToken, int bufferSize = 65536)
    {
        // Only an actually overlapping tree needs retained bytes before publication.
        List<Node> nodes = [];
        Capture(source, "", nodes);
        string parent = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".msync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        string payload = Path.Combine(stage, "payload");
        try
        {
            foreach (Node node in nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string snapshot = Path.Combine(payload, node.Relative);
                if (node.Link is not null)
                {
                    if ((node.Attributes & FileAttributes.Directory) != 0)
                        Directory.CreateSymbolicLink(snapshot, node.Link);
                    else File.CreateSymbolicLink(snapshot, node.Link);
                }
                else if ((node.Attributes & FileAttributes.Directory) != 0) Directory.CreateDirectory(snapshot);
                else File.Copy(node.Source, snapshot);
            }
            for (int index = nodes.Count - 1; index >= 0; --index)
            {
                Node node = nodes[index];
                if (node.Link is not null) continue;
                string snapshot = Path.Combine(payload, node.Relative);
                File.SetLastWriteTimeUtc(snapshot, node.Modified);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(snapshot, node.Mode);
            }
            if (replace) TransferFiles.RemoveNode(target);
            await TransferFiles.CopyNodeAsync(payload, target, cancellationToken, bufferSize);
        }
        finally { Directory.Delete(stage, recursive: true); }
    }

    private static void Capture(string source, string relative, ICollection<Node> nodes)
    {
        FileAttributes attributes = File.GetAttributes(source);
        bool directory = (attributes & FileAttributes.Directory) != 0;
        bool link = (attributes & FileAttributes.ReparsePoint) != 0;
        FileSystemInfo info = directory ? new DirectoryInfo(source) : new FileInfo(source);
        UnixFileMode mode = OperatingSystem.IsWindows() || link ? default : File.GetUnixFileMode(source);
        nodes.Add(new(source, relative, attributes, info.LastWriteTimeUtc, mode, link ? info.LinkTarget : null));
        if (!directory || link) return;
        foreach (string child in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
            Capture(child, Path.Combine(relative, Path.GetFileName(child)), nodes);
    }
}
