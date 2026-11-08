using System.Text;

namespace Msync.Runtime;

internal static class TransferFiles
{
    internal static async Task CopyNodeAsync(string source, string target, CancellationToken cancellationToken, int bufferSize = 65536)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            CopyLink(source, target, (attributes & FileAttributes.Directory) != 0);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.CreateDirectory(target);
            foreach (string child in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
                await CopyNodeAsync(child, Path.Combine(target, Path.GetFileName(child)), cancellationToken, bufferSize);
            ApplyMetadata(source, target);
            return;
        }
        await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await WriteFileAsync(input, target, source, cancellationToken, bufferSize: bufferSize);
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
        File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(target, File.GetUnixFileMode(source));
    }

    internal static void CopyLink(string source, string target, bool directory)
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
