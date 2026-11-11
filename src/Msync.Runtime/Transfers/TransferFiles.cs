using System.Buffers;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

internal static class TransferFiles
{
    internal static Task CopyNodeAsync(string source, string target, CancellationToken cancellationToken, int bufferSize = 65536,
        RuntimeTimeline? timeline = null)
        => Task.Run(() => CopyTree(source, target, cancellationToken, bufferSize, timeline), cancellationToken);

    internal static void CopyPreparedNode(string source, string target, PlannedTransferKind kind,
        Span<byte> buffer, CancellationToken cancellationToken, RuntimeTimeline? timeline = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        if (kind == PlannedTransferKind.GenericCopy)
        {
            CopyNode(source, target, buffer, cancellationToken, timeline);
            return;
        }
        FileMetadata metadata = NativeFileMetadata.Capture(source);
        switch (kind)
        {
            case PlannedTransferKind.CopyFile:
                CopyFile(source, target, metadata, buffer, cancellationToken, timeline);
                break;
            case PlannedTransferKind.CopyLink:
                CopyLink(source, target, metadata.IsDirectory, metadata.Modified, timeline);
                break;
            case PlannedTransferKind.CopyDirectory:
                Directory.CreateDirectory(target);
                foreach (string child in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
                    CopyNode(child, Path.Combine(target, Path.GetFileName(child)), buffer, cancellationToken, timeline);
                metadata.Apply(target);
                break;
            default:
                CopyNode(source, target, buffer, cancellationToken, timeline);
                break;
        }
    }

    internal static void WriteLiteral(string value, string target, Span<byte> buffer, CancellationToken cancellationToken,
        RuntimeTimeline? timeline = null)
    {
        using var activity = BeginFile(timeline, "file.write", null, target);
        string parent = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".msync-{Guid.NewGuid():N}");
        try
        {
            using (FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            {
                Span<byte> encodingBuffer = buffer.Length >= 4 ? buffer : stackalloc byte[4];
                ReadOnlySpan<char> remaining = value.AsSpan();
                Encoder encoder = Encoding.UTF8.GetEncoder();
                bool completed = false;
                while (!completed)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    encoder.Convert(remaining, encodingBuffer, flush: true, out int characters, out int bytes, out completed);
                    output.Write(encodingBuffer[..bytes]);
                    remaining = remaining[characters..];
                }
                if (File.Exists(target) && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stage, File.GetUnixFileMode(target));
                activity?.SetMetric("bytes.written", output.Position);
                Flush(output, stage, timeline);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Publish(stage, target, timeline);
        }
        catch (Exception error) { activity?.Fail(error); throw; }
        finally { File.Delete(stage); }
    }

    private static void CopyTree(string source, string target, CancellationToken cancellationToken, int bufferSize, RuntimeTimeline? timeline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        byte[]? buffer = OperatingSystem.IsWindows() ? null : ArrayPool<byte>.Shared.Rent(bufferSize);
        try { CopyNode(source, target, buffer is null ? [] : buffer.AsSpan(0, bufferSize), cancellationToken, timeline); }
        finally { if (buffer is not null) ArrayPool<byte>.Shared.Return(buffer); }
    }

    private static void CopyNode(string source, string target, Span<byte> buffer, CancellationToken cancellationToken, RuntimeTimeline? timeline)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileMetadata info = NativeFileMetadata.Capture(source);
        FileAttributes attributes = info.Attributes;
        if (info.IsLink)
        {
            CopyLink(source, target, (attributes & FileAttributes.Directory) != 0, info.Modified, timeline);
            return;
        }
        if ((attributes & FileAttributes.Directory) != 0)
        {
            Directory.CreateDirectory(target);
            foreach (string child in Directory.EnumerateFileSystemEntries(source).Order(StringComparer.Ordinal))
                CopyNode(child, Path.Combine(target, Path.GetFileName(child)), buffer, cancellationToken, timeline);
            info.Apply(target);
            return;
        }
        CopyFile(source, target, info, buffer, cancellationToken, timeline);
    }

    private static void CopyFile(string source, string target, FileMetadata metadata, Span<byte> buffer, CancellationToken cancellationToken,
        RuntimeTimeline? timeline)
    {
        using var activity = BeginFile(timeline, "file.copy", source, target);
        activity?.SetMetric("source.size_bytes", metadata.Length);
        string stage = Path.Combine(Path.GetDirectoryName(target) ?? ".", $".msync-{Guid.NewGuid():N}");
        try
        {
            WriteFileContents(source, stage, metadata, buffer, cancellationToken, timeline, activity);
            cancellationToken.ThrowIfCancellationRequested();
            Publish(stage, target, timeline);
        }
        catch (Exception error)
        {
            activity?.Fail(error);
            if (OperatingSystem.IsWindows() && File.Exists(stage))
                File.SetAttributes(stage, File.GetAttributes(stage) & ~FileAttributes.ReadOnly);
            File.Delete(stage);
            throw;
        }
    }

    private static void WriteFileContents(string source, string stage, FileMetadata metadata, Span<byte> buffer, CancellationToken cancellationToken,
        RuntimeTimeline? timeline, RuntimeTimelineScope? activity)
    {
        if (OperatingSystem.IsWindows())
        {
            activity?.SetAttribute("file.backend", "windows_native_copy");
            WindowsFileCopy.Copy(source, stage, cancellationToken);
            return;
        }
        activity?.SetAttribute("file.backend", "buffered");
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        CopyBuffered(input, output, buffer, cancellationToken);
        metadata.Apply(stage);
        activity?.SetMetric("bytes.written", output.Position);
        Flush(output, stage, timeline);
    }

    internal static void CopySnapshotFile(string source, string target, Span<byte> buffer, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) { WindowsFileCopy.Copy(source, target, cancellationToken, flush: false); return; }
        using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        CopyBuffered(input, output, buffer, cancellationToken);
    }
    private static void CopyBuffered(FileStream input, FileStream output, Span<byte> buffer, CancellationToken cancellationToken)
    {
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.Write(buffer[..count]);
        }
    }

    internal static async Task WriteLiteralAsync(string value, string target, CancellationToken cancellationToken, int bufferSize = 65536,
        RuntimeTimeline? timeline = null)
    {
        using MemoryStream input = new(Encoding.UTF8.GetBytes(value), writable: false);
        await WriteFileAsync(input, target, null, cancellationToken, bufferSize: bufferSize, timeline: timeline);
    }

    internal static async Task WriteFileAsync(Stream input, string target, string? metadataSource, CancellationToken cancellationToken, Action<string>? applyMetadata = null, int bufferSize = 65536, long? expectedLength = null,
        RuntimeTimeline? timeline = null)
    {
        using var activity = BeginFile(timeline, "file.write", metadataSource, target);
        string parent = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".msync-{Guid.NewGuid():N}");
        try
        {
            await using (FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, bufferSize, cancellationToken);
                activity?.SetMetric("bytes.written", output.Position);
                if (expectedLength is { } declared && output.Position != declared)
                    throw new EndOfStreamException($"Archive member '{target}' declared {declared} bytes, received {output.Position}");
                if (applyMetadata is not null)
                    applyMetadata(stage);
                else if (metadataSource is not null)
                    ApplyMetadata(metadataSource, stage);
                else if (File.Exists(target) && !OperatingSystem.IsWindows())
                    File.SetUnixFileMode(stage, File.GetUnixFileMode(target));
                Flush(output, stage, timeline);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Publish(stage, target, timeline);
        }
        catch (Exception error) { activity?.Fail(error); throw; }
        finally
        {
            File.Delete(stage);
        }
    }

    internal static void ApplyMetadata(string source, string target)
    {
        NativeFileMetadata.Capture(source).Apply(target);
    }

    internal static void CopyLink(string source, string target, bool directory, NativeTimestamp? modified = null,
        RuntimeTimeline? timeline = null)
    {
        using var activity = BeginFile(timeline, "file.link", source, target);
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
            NativeFileIdentity.MoveLink(stage, target);
        }
        catch (Exception error) { activity?.Fail(error); throw; }
        finally
        {
            NativeFileIdentity.DeleteLink(stage, directory);
        }
    }

    private static RuntimeTimelineScope? BeginFile(RuntimeTimeline? timeline, string name, string? source, string target)
    {
        var activity = timeline?.Begin(name);
        if (activity is null) return null;
        if (source is not null) activity.SetAttribute("source.resolved_path", source);
        activity.SetAttribute("destination.resolved_path", target);
        return activity;
    }

    private static void Flush(FileStream output, string stage, RuntimeTimeline? timeline)
    {
        using var activity = timeline?.Begin("file.sync");
        activity?.SetAttribute("file.stage_path", stage);
        try { output.Flush(flushToDisk: true); }
        catch (Exception error) { activity?.Fail(error); throw; }
    }

    private static void Publish(string stage, string target, RuntimeTimeline? timeline)
    {
        using var activity = BeginFile(timeline, "file.publish", null, target);
        activity?.SetAttribute("file.stage_path", stage);
        try { File.Move(stage, target, overwrite: true); }
        catch (Exception error) { activity?.Fail(error); throw; }
    }

    internal static void RemoveNode(string target)
    {
        if (!Path.Exists(target))
        {
            File.Delete(target);
            return;
        }
        FileAttributes attributes = File.GetAttributes(target);
        if ((attributes & FileAttributes.Directory) != 0)
            Directory.Delete(target, recursive: !NativeFileMetadata.IsLink(target, attributes));
        else
            File.Delete(target);
    }
}
