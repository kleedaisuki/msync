using System.Formats.Tar;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static void RestoreArchiveNames(TarEntry entry)
    {
        // PAX is length-delimited, but .NET 10 rejects embedded LF in its values.
        // Remote archive names use reversible transport encoding, not renamed paths.
        entry.Name = DecodeArchiveName(entry.Name);
        if (entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink)
            entry.LinkName = DecodeArchiveName(entry.LinkName);
    }

    private static string DecodeArchiveName(string value) => value.Contains('%')
        ? value.Replace("%0A", "\n", StringComparison.Ordinal).Replace("%25", "%", StringComparison.Ordinal) : value;

    private sealed class ArchiveInstallation
    {
        private readonly RuntimeContext context;
        private readonly bool container;
        private readonly bool replace;
        private readonly bool merge;
        private readonly string destination;
        private readonly List<(string Path, TarEntry Entry)> directories = [];
        private string? root;
        private bool initialized;

        internal ArchiveInstallation(PathValue target, RuntimeContext context, bool container, bool replace,
            TransferDestinationMode mode = TransferDestinationMode.Exact)
        {
            this.context = context;
            this.container = container;
            this.replace = replace;
            merge = mode == TransferDestinationMode.Merge;
            string path = target.LocalPath(context.WorkingDirectory, context.HomeDirectory);
            destination = replace && mode == TransferDestinationMode.Exact ? Path.GetDirectoryName(path)! : path;
        }

        internal async Task WriteAsync(TarEntry entry, CancellationToken cancellationToken)
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
                    await TransferFiles.WriteFileAsync(entry.DataStream ?? Stream.Null, installed, null, cancellationToken,
                        stage => ApplyArchiveMetadata(entry, stage), context.BufferSize, expectedLength: entry.Length);
                    break;
                case TarEntryType.SymbolicLink:
                    CreateArchiveLink(installed, entry.LinkName, entry);
                    break;
                case TarEntryType.HardLink:
                    string linkedName = entry.LinkName;
                    while (linkedName.StartsWith("./", StringComparison.Ordinal)) linkedName = linkedName[2..];
                    string linkedRelative = container ? linkedName : linkedName == root ? "" : linkedName[(root.Length + 1)..];
                    await TransferFiles.CopyNodeAsync(Path.Combine(destination, linkedRelative), installed, cancellationToken, context.BufferSize);
                    break;
                default:
                    throw new IOException($"Unsupported archive entry '{entry.Name}' of type {entry.EntryType}.");
            }
        }

        internal void Complete()
        {
            for (int index = directories.Count - 1; index >= 0; --index)
                ApplyArchiveMetadata(directories[index].Entry, directories[index].Path);
        }
    }
}
