using System.Formats.Tar;
using System.Globalization;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static FileMetadata DirectoryMetadata(string source)
    {
        var metadata = NativeFileMetadata.Capture(source);
        if (!metadata.IsDirectory || metadata.IsLink)
            throw new IOException($"Directory operation requires a directory source: '{source}'.");
        return metadata;
    }

    private static void ApplyDirectoryAction(TransferBatchItem item, FileMetadata metadata, RuntimeContext context)
    {
        string target = item.Destination.LocalPath(context.WorkingDirectory, context.HomeDirectory);
        if (item.Kind == PlannedTransferKind.ApplyDirectoryMetadata)
        {
            metadata.Apply(target);
            return;
        }
        if (item.Mode == TransferDestinationMode.Replace) TransferFiles.RemoveNode(target);
        else if (item.Mode == TransferDestinationMode.Exact && Directory.Exists(target)
            && !NativeFileMetadata.IsLink(target, File.GetAttributes(target)))
            throw new IOException($"Exact destination '{target}' is an existing directory; append '/' for container insertion or '/.' for replacement.");
        Directory.CreateDirectory(target);
    }

    private static async Task<IReadOnlyList<TransferBatchOutcome>> CopyDirectoryActionsAsync(
        IReadOnlyList<TransferBatchItem> items, RuntimeContext context, CancellationToken token)
    {
        var outcomes = new TransferBatchOutcome[items.Count];
        List<RemoteTransferSession> owned = [];
        List<IDisposable> scopes = [];
        try
        {
            var connections = items.SelectMany(item => new[]
            {
                item.Source is PathValue path ? Remote(path) : null, Remote(item.Destination)
            }).Where(connection => connection is not null).Distinct();
            foreach (var connection in connections)
            {
                if (CurrentSession(connection!) is not null) continue;
                var session = OpenSession(connection!, token);
                owned.Add(session);
                scopes.Add(UseSession(session));
            }
            for (int index = 0; index < items.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    await ExecuteDirectoryActionAsync(items[index], context, token);
                    outcomes[index] = new(true);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                { outcomes[index] = new(false, error); }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            for (int index = 0; index < items.Count; index++) outcomes[index] ??= new(false, error);
        }
        finally
        {
            for (int index = scopes.Count - 1; index >= 0; index--) scopes[index].Dispose();
            foreach (var session in owned) await session.DisposeAsync();
        }
        return outcomes;
    }

    private static async Task ExecuteDirectoryActionAsync(TransferBatchItem item, RuntimeContext context, CancellationToken token)
    {
        var source = item.Source as PathValue;
        var sourceConnection = source is null ? null : Remote(source);
        FileMetadata metadata;
        if (source is null && item.Kind == PlannedTransferKind.EnsureDirectory && item.Source is NullValue)
            metadata = default;
        else if (source is null)
            throw new IOException("Directory metadata requires a Path source.");
        else if (sourceConnection is null)
        {
            metadata = DirectoryMetadata(source.LocalPath(context.WorkingDirectory, context.HomeDirectory));
            if (OperatingSystem.IsWindows())
                metadata = metadata with { Mode = new GnuTarEntry(TarEntryType.Directory, "payload").Mode };
        }
        else
        {
            string data = await CurrentSession(sourceConnection)!.QueryAsync(DirectoryAttributesProgram, [source.Raw], token);
            string[] fields = data.Split('\0');
            string fractional = fields[2][(fields[2].IndexOf('.') + 1)..].Split(' ')[0];
            metadata = new(FileAttributes.Directory, 0, (UnixFileMode)Convert.ToInt32(fields[0], 8),
                new(long.Parse(fields[1], CultureInfo.InvariantCulture), long.Parse(fractional, CultureInfo.InvariantCulture)), false);
        }
        var targetConnection = Remote(item.Destination);
        if (targetConnection is null) ApplyDirectoryAction(item, metadata, context);
        else
        {
            string mode = item.Mode.ToString();
            string permissions = Convert.ToString((int)metadata.Mode, 8);
            await CurrentSession(targetConnection)!.QueryAsync(DirectoryActionProgram,
                [item.Destination.Raw, item.Kind.ToString(), mode, permissions, metadata.Modified.Pax], token);
        }
    }

    private static string DirectoryAttributesProgram => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        source=$(expand "$1"; printf .); source=${source%.}
        if [ ! -d "$source" ] || [ -L "$source" ]; then
            printf 'Directory operation requires a directory source: %s\n' "$source" >&2
            exit 1
        fi
        stat --printf='%a\0%Y\0%y' -- "$source"
        """);

    private static string DirectoryActionProgram => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        target=$(expand "$1"; printf .); target=${target%.}
        if [ "$2" = EnsureDirectory ]; then
            if [ "$3" = Replace ]; then rm -rf -- "$target"
            elif [ "$3" = Exact ] && [ -d "$target" ] && [ ! -L "$target" ]; then
                printf 'Exact destination is an existing directory: %s; append / or /.\n' "$target" >&2
                exit 1
            fi
            mkdir -p -- "$target"
        else
            chmod "$4" -- "$target"
            touch -d "@$5" -- "$target"
        fi
        """);
}
