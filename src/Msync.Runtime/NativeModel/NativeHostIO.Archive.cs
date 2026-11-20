using System.Formats.Tar;
using Msync.Core;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeHostIO
{
    private static async Task ReadArchiveAsync(Stream input, NativePathValue target, NativeRuntimeContext context,
        bool container, bool replace, CancellationToken token, TransferDestinationMode mode = TransferDestinationMode.Exact)
    {
        using TarReader reader = new(input, leaveOpen: true);
        using TransferFiles.PublicationQueue? queue = OperatingSystem.IsLinux() ? new(token, context.Timeline) : null;
        ArchiveInstallation installation = new(target, context, container, replace, mode, queue);
        while (true)
        {
            TarEntry? entry;
            try { entry = await reader.GetNextEntryAsync(copyData: false, token); }
            catch
            {
                queue?.Finish();
                throw;
            }
            if (entry is null) break;
            NativeTransferProtocol.NativeRestoreArchiveNames(entry);
            await installation.WriteAsync(entry, token);
        }
        installation.Complete();
    }

    private sealed class ArchiveInstallation
    {
        private readonly NativeRuntimeContext context;
        private readonly bool container;
        private readonly bool replace;
        private readonly bool merge;
        private readonly string destination;
        private readonly TransferFiles.PublicationQueue? queue;
        private readonly List<(string Path, TarEntry Entry)> directories = [];
        private string? root;
        private bool initialized;
        internal ArchiveInstallation(NativePathValue target, NativeRuntimeContext context, bool container, bool replace,
            TransferDestinationMode mode, TransferFiles.PublicationQueue? queue)
        {
            this.context = context;
            this.container = container;
            this.replace = replace;
            this.queue = queue;
            merge = mode == TransferDestinationMode.Merge;
            string path = context.LocalPath(target);
            destination = replace && mode == TransferDestinationMode.Exact ? Path.GetDirectoryName(path)! : path;
        }
        internal async Task WriteAsync(TarEntry entry, CancellationToken token)
        {
            string name = entry.Name;
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            name = name.TrimEnd('/');
            root ??= name.Split('/')[0];
            string relative = container || root.Length == 0 ? name : name == root ? "" : name[(root.Length + 1)..];
            string installed = Path.Combine(destination, relative);
            if (!initialized && replace && container)
            {
                TransferFiles.RemoveNode(destination);
                Directory.CreateDirectory(destination);
            }
            initialized = true;
            if (relative.Length == 0)
            {
                if (replace)
                {
                    if (entry.EntryType != TarEntryType.Directory) throw new IOException("Tree replacement requires a directory source.");
                    TransferFiles.RemoveNode(installed);
                }
                else if (!merge && Directory.Exists(installed))
                    throw new IOException($"Exact destination '{installed}' is an existing directory; append '/' or '/.'.");
            }
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    queue?.Finish();
                    Directory.CreateDirectory(installed);
                    directories.Add((installed, entry));
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    await WriteFileAsync(entry, installed, token);
                    break;
                case TarEntryType.SymbolicLink:
                    queue?.Finish();
                    NativeTransferProtocol.NativeCreateArchiveLink(installed, entry.LinkName, entry);
                    break;
                case TarEntryType.HardLink:
                    queue?.Finish();
                    string linkedName = entry.LinkName;
                    while (linkedName.StartsWith("./", StringComparison.Ordinal)) linkedName = linkedName[2..];
                    string linkedRelative = container ? linkedName : linkedName == root ? "" : linkedName[(root.Length + 1)..];
                    await TransferFiles.CopyNodeAsync(Path.Combine(destination, linkedRelative), installed, token,
                        context.BufferSize, context.Timeline);
                    break;
                default:
                    queue?.Finish();
                    throw new IOException($"Unsupported archive entry '{entry.Name}' of type {entry.EntryType}.");
            }
        }

        private Task WriteFileAsync(TarEntry entry, string installed, CancellationToken token)
        {
            Stream input = entry.DataStream ?? Stream.Null;
            void Metadata(string stage) => NativeTransferProtocol.NativeApplyArchiveMetadata(entry, stage);
            if (queue is not null && directories.Count != 0 && entry.Length <= TransferFiles.PublicationQueue.MaximumFileBytes)
                return TransferFiles.QueueArchiveFileAsync(queue, input, installed, Metadata, context.BufferSize, entry.Length, token);
            queue?.Finish();
            return TransferFiles.WriteFileAsync(input, installed, null, token, Metadata, context.BufferSize,
                expectedLength: entry.Length, timeline: context.Timeline);
        }

        internal void Complete()
        {
            queue?.Finish();
            for (int index = directories.Count - 1; index >= 0; index--)
                NativeTransferProtocol.NativeApplyArchiveMetadata(directories[index].Entry, directories[index].Path);
        }
    }
}
