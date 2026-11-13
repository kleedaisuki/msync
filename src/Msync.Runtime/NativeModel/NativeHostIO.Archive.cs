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
        ArchiveInstallation installation = new(target, context, container, replace, mode);
        while (await reader.GetNextEntryAsync(copyData: false, token) is { } entry)
        {
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
        private readonly List<(string Path, TarEntry Entry)> directories = [];
        private string? root;
        private bool initialized;
        internal ArchiveInstallation(NativePathValue target, NativeRuntimeContext context, bool container, bool replace, TransferDestinationMode mode)
        {
            this.context = context;
            this.container = container;
            this.replace = replace;
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
                    Directory.CreateDirectory(installed);
                    directories.Add((installed, entry));
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                case TarEntryType.ContiguousFile:
                    await TransferFiles.WriteFileAsync(entry.DataStream ?? Stream.Null, installed, null, token,
                        stage => NativeTransferProtocol.NativeApplyArchiveMetadata(entry, stage), context.BufferSize,
                        expectedLength: entry.Length, timeline: context.Timeline);
                    break;
                case TarEntryType.SymbolicLink:
                    NativeTransferProtocol.NativeCreateArchiveLink(installed, entry.LinkName, entry);
                    break;
                case TarEntryType.HardLink:
                    string linkedName = entry.LinkName;
                    while (linkedName.StartsWith("./", StringComparison.Ordinal)) linkedName = linkedName[2..];
                    string linkedRelative = container ? linkedName : linkedName == root ? "" : linkedName[(root.Length + 1)..];
                    await TransferFiles.CopyNodeAsync(Path.Combine(destination, linkedRelative), installed, token,
                        context.BufferSize, context.Timeline);
                    break;
                default: throw new IOException($"Unsupported archive entry '{entry.Name}' of type {entry.EntryType}.");
            }
        }
        internal void Complete()
        {
            for (int index = directories.Count - 1; index >= 0; index--)
                NativeTransferProtocol.NativeApplyArchiveMetadata(directories[index].Entry, directories[index].Path);
        }
    }
}
