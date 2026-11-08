using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static async Task<bool> CopySelectionAsync(PathSelection selection, PathValue destination, RuntimeContext context, CancellationToken cancellationToken)
    {
        ConnectionValue sourceConnection = Remote(selection.Directory)!;
        ConnectionValue? targetConnection = Remote(destination);
        bool sameEndpoint = sourceConnection == targetConnection;
        bool replace = IsReplacement(destination);
        string script = SelectionPrelude + SelectionQueryScript + SelectionDecode + (sameEndpoint ? "\nset -- \"$destination_raw\" \"$selection_mode\" \"$@\"\n" + CohortInstallScript : SelectionArchive);
        using Process source = Start(sourceConnection, script, [selection.Directory.Raw, destination.Raw, Mode(container: true, replace), selection.Recursive ? "recursive" : "flat"], redirectInput: true);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Kill(source));
        try
        {
            IReadOnlyList<string> names = await ReadSelectionNamesAsync(source.StandardOutput.BaseStream, selection.Pattern, cancellationToken);
            IReadOnlyList<Value> selected = await ApplySelectionAsync(selection, names, source, cancellationToken);
            bool originalEndpoint = selected.All(value => value is PathValue path && Remote(path) == sourceConnection);
            if (originalEndpoint) ValidateNames(selected, container: true);
            IReadOnlyList<string> paths = originalEndpoint ? selected.Cast<PathValue>().Select(path => path.Raw).ToArray() : [];
            await WriteSelectionNamesAsync(source.StandardInput.BaseStream, paths, cancellationToken);
            source.StandardInput.Close();
            if (!originalEndpoint)
            {
                await source.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
                await FinishAsync(source, sourceConnection, cancellationToken);
                return await CopyAsync(new RangeValue(selected, selected.Aggregate(new TypeRef(TypeKind.Never), (type, value) => TypeRef.Union(type, value.Type))), destination, context, cancellationToken);
            }
            if (sameEndpoint)
            {
                string result = await source.StandardOutput.ReadToEndAsync(cancellationToken);
                await FinishAsync(source, sourceConnection, cancellationToken);
                if (result != "MSYNC-DONE\n") throw new IOException($"Remote selector copy returned no completion: {result}");
                return true;
            }
            if (targetConnection is null)
            {
                await ReadArchiveAsync(source.StandardOutput.BaseStream, destination, context, container: true, replace, cancellationToken);
                await source.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
                await FinishAsync(source, sourceConnection, cancellationToken);
                return true;
            }
            if (paths.Count == 0)
            {
                await source.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
                await FinishAsync(source, sourceConnection, cancellationToken);
                return true;
            }
            using Process target = Start(targetConnection, RelayScript,
                [destination.Raw, Mode(container: true, replace), "", "cohort", "filesystem"], redirectInput: true);
            using CancellationTokenRegistration targetRegistration = cancellationToken.Register(() => Kill(target));
            try
            {
                await source.StandardOutput.BaseStream.CopyToAsync(target.StandardInput.BaseStream, context.BufferSize, cancellationToken);
                target.StandardInput.Close();
                string result = await target.StandardOutput.ReadToEndAsync(cancellationToken);
                await FinishAsync(source, sourceConnection, cancellationToken);
                await FinishAsync(target, targetConnection, cancellationToken);
                if (result != "MSYNC-DONE\n") throw new IOException($"Remote selector relay returned no completion: {result}");
            }
            catch
            {
                Kill(target);
                await target.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            return true;
        }
        catch (Exception error)
        {
            Kill(source);
            await source.WaitForExitAsync(CancellationToken.None);
            if (error is OperationCanceledException) throw;
            throw new IOException($"Cannot transfer selector '{selection.Directory}/<{selection.Pattern}>' to '{destination}': {error.Message}", error);
        }
    }

    private static async Task ReplaceLocalCohortAsync(IReadOnlyList<Value> items, PathValue destination, RuntimeContext context, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;
        string target = TargetPath(destination, items[0], context, container: false);
        (PathValue Path, string Source)[] sources = items.Cast<PathValue>().Select(path => (path, path.LocalPath(context.WorkingDirectory, context.HomeDirectory))).ToArray();
        if (!sources.Any(source => TransferOverlap.Related(source.Source, target)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransferFiles.RemoveNode(target);
            Directory.CreateDirectory(target);
            foreach (var source in sources)
                await TransferFiles.CopyNodeAsync(source.Source, Path.Combine(target, Basename(source.Path)), cancellationToken, context.BufferSize);
            return;
        }
        var retained = await Task.Run(() => sources.Select(source =>
            (Name: Basename(source.Path), Nodes: TransferOverlap.CaptureTree(source.Source, cancellationToken))).ToArray(), cancellationToken);
        string parent = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".msync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            await Task.Run(() =>
            {
                foreach (var source in retained) TransferOverlap.Materialize(source.Nodes, Path.Combine(stage, source.Name), cancellationToken, context.BufferSize);
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            TransferFiles.RemoveNode(target);
            await TransferFiles.CopyNodeAsync(stage, target, cancellationToken, context.BufferSize);
        }
        finally
        {
            foreach (var source in retained) TransferOverlap.MakeSnapshotWritable(source.Nodes, Path.Combine(stage, source.Name));
            Directory.Delete(stage, recursive: true);
        }
    }

    private static async Task<IReadOnlyList<string>> ReadSelectionNamesAsync(Stream stream, string pattern, CancellationToken cancellationToken)
    {
        Regex regex = new(pattern, RegexOptions.CultureInvariant);
        List<string> selected = [];
        using MemoryStream name = new();
        byte[] buffer = new byte[4096];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) throw new EndOfStreamException("Remote selector discovery ended without its name terminator.");
            for (int index = 0; index < count; ++index)
            {
                byte current = buffer[index];
                if (current != 0) { name.WriteByte(current); continue; }
                if (name.Length == 0)
                {
                    selected.Sort(TextSemantics.Ordinal);
                    return selected;
                }
                string text = Encoding.UTF8.GetString(name.GetBuffer().AsSpan(0, checked((int)name.Length)));
                if (regex.IsMatch(text)) selected.Add(text);
                name.SetLength(0);
            }
        }
    }

    private static async Task WriteSelectionNamesAsync(Stream stream, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        foreach (string name in names) await WriteEncodedLineAsync(stream, name, cancellationToken);
        await stream.WriteAsync("MSYNC-END\n"u8.ToArray(), cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private const string SelectionPrelude = ShellLibrary + "\n" + """
        directory=$(expand "$1"; printf .); directory=${directory%.}
        destination_raw=$2
        selection_mode=$3
        if [ ! -d "$directory" ]; then printf 'Selector source is not a directory: %s\n' "$directory" >&2; exit 1; fi
        if [ "$4" = recursive ]; then find "$directory" -mindepth 1 -printf '%P\0'; else find "$directory" -mindepth 1 -maxdepth 1 -printf '%P\0'; fi
        printf '\0'
        """;

    private const string SelectionDecode = "\n" + """
        set --
        while IFS= read -r encoded; do
            [ "$encoded" = MSYNC-END ] && break
            if [ "$encoded" = MSYNC-QUERY ]; then query; continue; fi
            source=$(printf '%b.' "$encoded"); source=${source%.}
            set -- "$@" "$source"
        done
        """;

    private const string SelectionArchive = "\n" + """
        if [ "$#" -eq 0 ]; then exec tar --format=pax -cpf - --files-from /dev/null; fi
        for raw in "$@"; do
            source=$(expand "$raw"; printf .); source=${source%.}
            while [ "${source%/}" != "$source" ] && [ "$source" != / ]; do source=${source%/}; done
            parent=${source%/*}
            name=${source##*/}
            shift
            set -- "$@" -C "$parent" "./$name"
        done
        exec tar --format=pax -cpf - "$@"
        """;
    private const string CohortInstallScript = ShellLibrary + "\n" + """
        destination=$(expand "$1"; printf .); destination=${destination%.}
        mode=$2
        shift 2
        if [ "$#" -eq 0 ]; then printf 'MSYNC-DONE\n'; exit 0; fi
        target=$destination
        if [ "$mode" = replace ]; then target=${destination%/.}; [ -n "$target" ] || target=/; fi
        parent=${target%/*}
        scratch_parent=$parent
        scratch_real=$(realpath -m -- "$scratch_parent"; printf .); scratch_real=${scratch_real%.}; scratch_real=${scratch_real%?}
        for source in "$@"; do
            source=$(expand "$source"; printf .); source=${source%.}
            if [ -d "$source" ] && [ ! -L "$source" ]; then
                source_real=$(realpath -- "$source"; printf .); source_real=${source_real%.}; source_real=${source_real%?}
                case "$scratch_real/" in "$source_real/"*) scratch_parent=${source_real%/*}; [ -n "$scratch_parent" ] || scratch_parent=/; scratch_real=$scratch_parent ;; esac
            fi
        done
        mkdir -p -- "$scratch_parent"
        stage=$(mktemp -d "$scratch_parent/.msync-XXXXXXXXXX")
        trap 'rm -rf -- "$stage"' EXIT HUP INT TERM
        mkdir "$stage/payload"
        for source in "$@"; do
            source=$(expand "$source"; printf .); source=${source%.}
            while [ "${source%/}" != "$source" ] && [ "$source" != / ]; do source=${source%/}; done
            basename=${source##*/}
            cp -a -- "$source" "$stage/payload/$basename"
        done
        if [ "$mode" = replace ]; then rm -rf -- "$target"; fi
        install "$stage/payload" "$target"
        printf 'MSYNC-DONE\n'
        """;
}

