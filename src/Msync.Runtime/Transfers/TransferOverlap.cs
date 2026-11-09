using System.Buffers;
using Msync.Core;

namespace Msync.Runtime;

internal static class TransferOverlap
{
    internal sealed record Node(string Source, string Relative, FileAttributes Attributes, NativeTimestamp Modified, UnixFileMode Mode, string? Link);

    internal static bool SameNode(string source, string target)
    {
        if (!Path.Exists(target)) return false;
        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return false;
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
        string normalizedRoot = ResourcePath.Resolve(root).TrimEnd(Path.DirectorySeparatorChar);
        string normalizedPath = ResourcePath.Resolve(path);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    internal static async Task CopyAsync(string source, string target, bool replace, CancellationToken cancellationToken, int bufferSize = 65536)
    {
        // Only an actually overlapping tree needs retained bytes before publication.
        IReadOnlyList<Node> nodes = await Task.Run(() => CaptureTree(source, cancellationToken), cancellationToken);
        string parent = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".msync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        string payload = Path.Combine(stage, "payload");
        try
        {
            await Task.Run(() => Materialize(nodes, payload, cancellationToken, bufferSize), cancellationToken);
            if (replace) TransferFiles.RemoveNode(target);
            await TransferFiles.CopyNodeAsync(payload, target, cancellationToken, bufferSize);
        }
        finally { MakeSnapshotWritable(nodes, payload); Directory.Delete(stage, recursive: true); }
    }

    internal static bool Related(string source, string target)
    {
        return RelatedNames(Path.GetFullPath(source), Path.GetFullPath(target)) || RelatedNames(ResourcePath.Resolve(source), ResourcePath.Resolve(target));
    }

    private static bool RelatedNames(string source, string target)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        source = source.TrimEnd(Path.DirectorySeparatorChar);
        target = target.TrimEnd(Path.DirectorySeparatorChar);
        return source.Equals(target, comparison) || source.StartsWith(target + Path.DirectorySeparatorChar, comparison) || target.StartsWith(source + Path.DirectorySeparatorChar, comparison);
    }

    internal static IReadOnlyList<Node> CaptureTree(string source, CancellationToken cancellationToken = default)
    {
        List<Node> nodes = [];
        Capture(source, "", nodes, cancellationToken);
        return nodes;
    }

    internal static void Materialize(IReadOnlyList<Node> nodes, string payload, CancellationToken cancellationToken, int bufferSize = 65536)
    {
        byte[]? buffer = OperatingSystem.IsWindows() ? null : ArrayPool<byte>.Shared.Rent(bufferSize);
        try { Materialize(nodes, payload, buffer is null ? [] : buffer.AsSpan(0, bufferSize), cancellationToken); }
        finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer); }
    }
    private static void Materialize(IReadOnlyList<Node> nodes, string payload, Span<byte> buffer, CancellationToken cancellationToken)
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
                node.Modified.Apply(snapshot, link: true);
            }
            else if ((node.Attributes & FileAttributes.Directory) != 0) Directory.CreateDirectory(snapshot);
            else TransferFiles.CopySnapshotFile(node.Source, snapshot, buffer, cancellationToken);
        }
        for (int index = nodes.Count - 1; index >= 0; --index)
        {
            Node node = nodes[index];
            if (node.Link is not null) continue;
            string snapshot = Path.Combine(payload, node.Relative);
            node.Modified.Apply(snapshot, (node.Attributes & FileAttributes.Directory) != 0);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(snapshot, node.Mode);
        }
    }

    internal static void MakeSnapshotWritable(IReadOnlyList<Node> nodes, string payload)
    {
        foreach (var node in nodes)
        {
            if (node.Link is not null) continue;
            string path = Path.Combine(payload, node.Relative);
            if ((node.Attributes & FileAttributes.Directory) != 0)
            {
                if (!OperatingSystem.IsWindows() && Directory.Exists(path))
                    File.SetUnixFileMode(path, node.Mode | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else if (OperatingSystem.IsWindows() && (node.Attributes & FileAttributes.ReadOnly) != 0 && File.Exists(path))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
        }
    }

    private static void Capture(string source, string relative, ICollection<Node> nodes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileMetadata metadata = NativeFileMetadata.Capture(source);
        FileAttributes attributes = metadata.Attributes;
        bool directory = (attributes & FileAttributes.Directory) != 0;
        bool link = (attributes & FileAttributes.ReparsePoint) != 0;
        FileSystemInfo info = directory ? new DirectoryInfo(source) : new FileInfo(source);
        nodes.Add(new(source, relative, attributes, metadata.Modified, metadata.Mode, link ? info.LinkTarget : null));
        if (!directory || link) return;
        foreach (string child in Directory.EnumerateFileSystemEntries(source).Select(path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return path;
        }).Order(TextSemantics.Ordinal))
            Capture(child, Path.Combine(relative, Path.GetFileName(child)), nodes, cancellationToken);
    }
}
