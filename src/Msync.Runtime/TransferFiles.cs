using System.Buffers;
using System.Text;

namespace Msync.Runtime;

internal static class TransferFiles
{
    internal static Task CopyNodeAsync(string source, string target, CancellationToken cancellationToken, int bufferSize = 65536)
        => Task.Run(() => CopyTree(source, target, cancellationToken, bufferSize), cancellationToken);

    private static void CopyTree(string source, string target, CancellationToken cancellationToken, int bufferSize)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        byte[]? buffer = OperatingSystem.IsWindows() ? null : ArrayPool<byte>.Shared.Rent(bufferSize);
        try { CopyNode(source, target, buffer is null ? [] : buffer.AsSpan(0, bufferSize), cancellationToken); }
        finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void CopyNode(string source, string target, Span<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileMetadata info = NativeFileMetadata.Capture(source);
        FileAttributes attributes = info.Attributes;
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            CopyLink(source, target, (attributes & FileAttributes.Directory) != 0, info.Modified);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.CreateDirectory(target);
            foreach (string child in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
                CopyNode(child, Path.Combine(target, Path.GetFileName(child)), buffer, cancellationToken);
            info.Apply(target);
            return;
        }
        CopyFile(source, target, info, buffer, cancellationToken);
    }

    private static void CopyFile(string source, string target, FileMetadata metadata, Span<byte> buffer, CancellationToken cancellationToken)
    {
        string stage = Path.Combine(Path.GetDirectoryName(target) ?? ".", $".msync-{Guid.NewGuid():N}");
        try
        {
            WriteFileContents(source, stage, metadata, buffer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stage, target, overwrite: true);
        }
        catch
        {
            if (OperatingSystem.IsWindows() && File.Exists(stage))
                File.SetAttributes(stage, File.GetAttributes(stage) & ~FileAttributes.ReadOnly);
            File.Delete(stage);
            throw;
        }
    }

    private static void WriteFileContents(string source, string stage, FileMetadata metadata, Span<byte> buffer, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) { WindowsFileCopy.Copy(source, stage, cancellationToken); return; }
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer[..count]);
        }
        metadata.Apply(stage);
        output.Flush(flushToDisk: true);
    }

    internal static async Task WriteLiteralAsync(string value, string target, CancellationToken cancellationToken, int bufferSize = 65536)
    {
        using MemoryStream input = new(Encoding.UTF8.GetBytes(value), writable: false);
        await WriteFileAsync(input, target, null, cancellationToken, bufferSize: bufferSize);
    }

    internal static async Task WriteFileAsync(Stream input, string target, string? metadataSource, CancellationToken cancellationToken, Action<string>? applyMetadata = null, int bufferSize = 65536)
    {
        string parent = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".msync-{Guid.NewGuid():N}");
        try
        {
            await using (FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, bufferSize, cancellationToken);
                if (applyMetadata is not null)
                    applyMetadata(stage);
                else if (metadataSource is not null)
                    ApplyMetadata(metadataSource, stage);
                else if (File.Exists(target) && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stage, File.GetUnixFileMode(target));
                output.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(stage, target, overwrite: true);
        }
        finally
        {
            File.Delete(stage);
        }
    }

    internal static void ApplyMetadata(string source, string target)
    {
        NativeFileMetadata.Capture(source).Apply(target);
    }

    internal static void CopyLink(string source, string target, bool directory, NativeTimestamp? modified = null)
    {
        FileSystemInfo info = directory ? new DirectoryInfo(source) : new FileInfo(source);
        string link = info.LinkTarget ?? throw new IOException($"Cannot read symbolic link '{source}'.");
        string parent = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".msync-{Guid.NewGuid():N}");
        try
        {
            if (directory)
                Directory.CreateSymbolicLink(stage, link);
            else
                File.CreateSymbolicLink(stage, link);
            modified?.Apply(stage, link: true);
            File.Move(stage, target, overwrite: true);
        }
        finally
        {
            File.Delete(stage);
        }
    }

    internal static void RemoveNode(string target)
    {
        if (!Path.Exists(target))
        {
            File.Delete(target);
            return;
        }
        FileAttributes attributes = File.GetAttributes(target);
        if ((attributes & FileAttributes.Directory) != 0 && (attributes & FileAttributes.ReparsePoint) == 0)
            Directory.Delete(target, recursive: true);
        else
            File.Delete(target);
    }
}
