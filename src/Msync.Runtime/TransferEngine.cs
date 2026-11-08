using System.Diagnostics;
using System.Formats.Tar;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    public static async Task<bool> CopyAsync(Value source, Value destination, RuntimeContext context, CancellationToken cancellationToken = default)
    {
        PathValue target = context.AsPath(destination);
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"{source} -> {target}");
            return true;
        }
        while (source is RangeValue { Resolve: not null } deferred) source = await deferred.Resolve(cancellationToken);
        if (source is RangeValue { Selection: { Directory.Connection.Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } selection })
            return await CopySelectionAsync(selection, target, context, cancellationToken);
        if (Remote(target) is null && (source is StringValue || source is PathValue { Connection: null or { Kind: ConnectionKind.Local } }))
            return await CopyLocalAsync(source, destination, context, cancellationToken);
        List<Value> sources = [];
        bool cohort = source is RangeValue;
        if (source is RangeValue range)
        {
            await foreach (Value item in range.Items.WithCancellation(cancellationToken))
                sources.Add(item);
        }
        else sources.Add(source);
        if (sources.Count == 0) return true;
        bool container = cohort || EndsInSeparator(target);
        bool replace = IsReplacement(target);
        ValidateNames(sources, container);
        ConnectionValue? remoteTarget = Remote(target);
        PathValue[] remoteSources = sources.OfType<PathValue>().Where(path => Remote(path) is not null).ToArray();
        if (remoteTarget is not null)
        {
            if (remoteSources.Length == sources.Count && remoteSources.All(path => path.Connection == remoteTarget))
                await CopyRemoteAsync(remoteSources, target, container, replace, cohort, cancellationToken);
            else if (remoteSources.Length == 0)
                await UploadAsync(sources, target, context, container, replace, cohort, cancellationToken);
            else
                await RelayAsync(sources, target, context, container, replace, cohort, cancellationToken);
            return true;
        }
        if (cohort && replace && remoteSources.Length == 0)
        {
            await ReplaceLocalCohortAsync(sources, target, context, cancellationToken);
            return true;
        }
        if (remoteSources.Length == sources.Count && remoteSources.All(path => path.Connection == remoteSources[0].Connection))
        {
            await DownloadAsync(remoteSources, target, context, container, replace, cancellationToken);
            return true;
        }
        foreach (Value item in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string targetPath = TargetPath(target, item, context, container);
            if (item is PathValue path && Remote(path) is not null)
                await DownloadAsync([path], new PathValue(targetPath), context, false, replace, cancellationToken);
            else await CopyLocalItemAsync(item, targetPath, context, container, replace, cancellationToken);
        }
        return true;
    }

    public static async Task<bool> CopyLocalAsync(Value source, Value destination, RuntimeContext context, CancellationToken cancellationToken = default)
    {
        if (source is RangeValue { Resolve: not null } or RangeValue { Selection: not null })
            return await CopyAsync(source, destination, context, cancellationToken);
        PathValue target = context.AsPath(destination);
        bool container = source is RangeValue || EndsInSeparator(target);
        bool replace = IsReplacement(target);
        if (context.DryRun)
        {
            await context.Output.WriteLineAsync($"{source} -> {target}");
            return true;
        }
        if (source is not RangeValue range)
        {
            if (container) ValidateNames([source], true);
            await CopyLocalItemAsync(source, TargetPath(target, source, context, container), context, container, replace, cancellationToken);
            return true;
        }
        List<Value> items = [];
        await foreach (Value item in range.Items.WithCancellation(cancellationToken)) items.Add(item);
        ValidateNames(items, true);
        if (replace)
        {
            await ReplaceLocalCohortAsync(items, target, context, cancellationToken);
            return true;
        }
        foreach (Value item in items)
            await CopyLocalItemAsync(item, TargetPath(target, item, context, true), context, true, replace, cancellationToken);
        return true;
    }

    private static async Task CopyLocalItemAsync(Value source, string target, RuntimeContext context, bool container, bool replace, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source is StringValue text)
        {
            await TransferFiles.WriteLiteralAsync(text.Data, target, cancellationToken, context.BufferSize);
            return;
        }
        if (source is not PathValue path) throw new MsyncException($"Cannot transfer a value of type {source.Type}.");
        string sourcePath = path.LocalPath(context.WorkingDirectory, context.HomeDirectory);
        if (TransferOverlap.SameNode(sourcePath, target)) return;
        CheckTarget(sourcePath, target, container, replace);
        if (TransferOverlap.NeedsSnapshot(sourcePath, target, replace))
        {
            await TransferOverlap.CopyAsync(sourcePath, target, replace, cancellationToken, context.BufferSize);
            return;
        }
        if (replace) TransferFiles.RemoveNode(target);
        await TransferFiles.CopyNodeAsync(sourcePath, target, cancellationToken, context.BufferSize);
    }

    public static async Task<string> RunCommandAsync(ConnectionValue connection, string script, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (ActiveQuery.Value is { } selected && selected.Connection == connection)
            return await selected.InvokeAsync(script, args, cancellationToken);
        using Process process = Start(connection, script, args, redirectInput: false);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Kill(process));
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await FinishAsync(process, connection, cancellationToken);
        return output;
    }

    private static void ValidateNames(IReadOnlyList<Value> sources, bool container)
    {
        if (!container) return;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (Value source in sources)
        {
            if (source is not PathValue path)
                throw new MsyncException("A container transfer requires Path sources with basenames; a String source is literal data.");
            string name = Basename(path);
            if (!names.Add(name)) throw new MsyncException($"Selected sources collide at destination name '{name}'.");
        }
    }

    private static void CheckTarget(string source, string target, bool container, bool replace)
    {
        if (replace && !Directory.Exists(source))
            throw new IOException($"Tree replacement requires a directory source: '{source}'.");
        if (!container && !replace && Directory.Exists(target))
            throw new IOException($"Exact destination '{target}' is an existing directory; append '/' for container insertion or '/.' for replacement.");
    }

    private static string TargetPath(PathValue target, Value source, RuntimeContext context, bool container)
    {
        string path = target.LocalPath(context.WorkingDirectory, context.HomeDirectory);
        if (IsReplacement(target)) path = Path.GetDirectoryName(path)!;
        return container && source is PathValue item ? Path.Combine(path, Basename(item)) : path;
    }

    private static string Basename(PathValue path) => PathSemantics.FileName(path);
    private static bool EndsInSeparator(PathValue path) => path.Raw.EndsWith('/') || PathSemantics.UsesWindowsPaths(path) && path.Raw.EndsWith('\\');
    private static bool IsReplacement(PathValue path) => path.Raw.EndsWith("/.", StringComparison.Ordinal) || PathSemantics.UsesWindowsPaths(path) && path.Raw.EndsWith("\\.", StringComparison.Ordinal);
    private static ConnectionValue? Remote(PathValue path) => path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } connection ? connection : null;

    private static async Task UploadAsync(IReadOnlyList<Value> sources, PathValue target, RuntimeContext context, bool container, bool replace, bool cohort, CancellationToken cancellationToken)
    {
        ConnectionValue connection = Remote(target)!;
        string name = sources[0] is PathValue first ? Basename(first) : "";
        string[] args = [target.Raw, Mode(container, replace), name, cohort ? "cohort" : "single", sources[0] is StringValue ? "literal" : "filesystem"];
        using Process process = Start(connection, UploadScript, args, redirectInput: true);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Kill(process));
        try
        {
            await using (TarWriter writer = new(process.StandardInput.BaseStream, TarEntryFormat.Pax, leaveOpen: true))
            {
                if (cohort)
                {
                    await writer.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, "payload"), cancellationToken);
                    foreach (Value source in sources)
                    {
                        PathValue path = (PathValue)source;
                        await WriteArchiveNodeAsync(writer, path.LocalPath(context.WorkingDirectory, context.HomeDirectory), "payload/" + Basename(path), cancellationToken);
                    }
                }
                else if (sources[0] is PathValue path)
                    await WriteArchiveNodeAsync(writer, path.LocalPath(context.WorkingDirectory, context.HomeDirectory), "payload", cancellationToken);
                else if (sources[0] is StringValue text)
                {
                    using MemoryStream bytes = new(Encoding.UTF8.GetBytes(text.Data), writable: false);
                    PaxTarEntry entry = new(TarEntryType.RegularFile, "payload") { DataStream = bytes };
                    await writer.WriteEntryAsync(entry, cancellationToken);
                }
                else throw new MsyncException($"Cannot transfer {sources[0].Type}.");
            }
            process.StandardInput.Close();
            string result = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await FinishAsync(process, connection, cancellationToken);
            if (result != "MSYNC-DONE\n") throw new IOException($"Remote upload to {connection} returned no completion: {result}");
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task WriteArchiveNodeAsync(TarWriter writer, string source, string name, CancellationToken cancellationToken)
    {
        FileMetadata metadata = NativeFileMetadata.Capture(source);
        bool directory = metadata.IsDirectory;
        bool link = metadata.IsLink;
        TarEntryType type = link ? TarEntryType.SymbolicLink : directory ? TarEntryType.Directory : TarEntryType.RegularFile;
        await writer.WriteEntryAsync(new PaxGlobalExtendedAttributesTarEntry([KeyValuePair.Create("mtime", metadata.Modified.Pax)]), cancellationToken);
        // .NET 10 rewrites local PAX mtime through 100ns DateTimeOffset. A standard
        // global PAX timestamp plus a GNU member retains the native decimal value.
        GnuTarEntry entry = new(type, name) { ModificationTime = DateTimeOffset.UnixEpoch };
        if (!OperatingSystem.IsWindows() && !link) entry.Mode = metadata.Mode;
        if (link)
        {
            FileSystemInfo info = directory ? new DirectoryInfo(source) : new FileInfo(source);
            entry.LinkName = info.LinkTarget ?? throw new IOException($"Cannot read symbolic link '{source}'.");
        }
        if (type == TarEntryType.RegularFile)
        {
            await using FileStream bytes = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (metadata.Length == 0)
            {
                using MemoryStream contents = new();
                await bytes.CopyToAsync(contents, cancellationToken);
                contents.Position = 0; entry.DataStream = contents;
                await writer.WriteEntryAsync(entry, cancellationToken);
            }
            else
            {
                entry.DataStream = bytes;
                await writer.WriteEntryAsync(entry, cancellationToken);
            }
            return;
        }
        await writer.WriteEntryAsync(entry, cancellationToken);
        if (!directory || link) return;
        foreach (string child in Directory.EnumerateFileSystemEntries(source).Order(TextSemantics.Ordinal))
            await WriteArchiveNodeAsync(writer, child, name + "/" + Path.GetFileName(child), cancellationToken);
    }

    private static async Task DownloadAsync(IReadOnlyList<PathValue> sources, PathValue target, RuntimeContext context, bool container, bool replace, CancellationToken cancellationToken)
    {
        ConnectionValue connection = Remote(sources[0])!;
        bool receipt = RequiresArchiveReceipt(connection);
        string program = DownloadScript(sources.Count);
        using Process process = Start(connection, receipt ? WithArchiveReceipt(program) : program, sources.Select(path => path.Raw).ToArray(), redirectInput: receipt);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => Kill(process));
        try
        {
            await ReadArchiveAsync(process.StandardOutput.BaseStream, target, context, container, replace, cancellationToken);
            if (receipt) await AcknowledgeArchiveAsync(process, cancellationToken);
            await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            await FinishAsync(process, connection, cancellationToken);
        }
        catch
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task ReadArchiveAsync(Stream archive, PathValue target, RuntimeContext context, bool container, bool replace, CancellationToken cancellationToken)
    {
        using TarReader reader = new(archive, leaveOpen: true);
        string? root = null;
        string destination = target.LocalPath(context.WorkingDirectory, context.HomeDirectory);
        if (replace) destination = Path.GetDirectoryName(destination)!;
        bool initialized = false;
        List<(string Path, TarEntry Entry)> directories = [];
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
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
                else if (Directory.Exists(installed))
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
                    await TransferFiles.WriteFileAsync(entry.DataStream ?? Stream.Null, installed, null, cancellationToken, stage => ApplyArchiveMetadata(entry, stage), context.BufferSize, expectedLength: entry.Length);
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
        for (int index = directories.Count - 1; index >= 0; --index)
            ApplyArchiveMetadata(directories[index].Entry, directories[index].Path);
    }

    private static void ApplyArchiveMetadata(TarEntry entry, string target)
    {
        NativeTimestamp timestamp = entry is PaxTarEntry pax && pax.ExtendedAttributes.TryGetValue("mtime", out string? raw)
            ? NativeTimestamp.FromPax(raw) : NativeTimestamp.FromDateTime(entry.ModificationTime.UtcDateTime);
        timestamp.Apply(target, entry.EntryType == TarEntryType.Directory, entry.EntryType == TarEntryType.SymbolicLink);
        if (!OperatingSystem.IsWindows() && entry.EntryType != TarEntryType.SymbolicLink) File.SetUnixFileMode(target, entry.Mode);
    }

    private static void CreateArchiveLink(string target, string linkName, TarEntry entry)
    {
        string parent = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(parent);
        string stage = Path.Combine(parent, $".msync-{Guid.NewGuid():N}");
        try
        {
            File.CreateSymbolicLink(stage, linkName);
            ApplyArchiveMetadata(entry, stage);
            File.Move(stage, target, overwrite: true);
        }
        finally { File.Delete(stage); }
    }

    private static async Task CopyRemoteAsync(IReadOnlyList<PathValue> sources, PathValue target, bool container, bool replace, bool cohort, CancellationToken cancellationToken)
    {
        List<string> args = cohort ? [target.Raw, Mode(container, replace)] : [target.Raw, Mode(container, replace), "single"];
        args.AddRange(sources.Select(path => path.Raw));
        string output = await RunCommandAsync(Remote(target)!, cohort ? CohortInstallScript : CopyScript, args, cancellationToken);
        if (output != "MSYNC-DONE\n") throw new IOException($"Remote copy returned no completion: {output}");
    }

    private static async Task RelayAsync(IReadOnlyList<Value> sources, PathValue target, RuntimeContext context, bool container, bool replace, bool cohort, CancellationToken cancellationToken)
    {
        PathValue[] paths = sources.OfType<PathValue>().ToArray();
        if (paths.Length != sources.Count || paths.Any(path => Remote(path) != Remote(paths[0])))
            throw new MsyncException("A cross-endpoint finite transfer requires sources on one endpoint.");
        ConnectionValue sourceConnection = Remote(paths[0])!;
        ConnectionValue destinationConnection = Remote(target)!;
        bool receipt = RequiresArchiveReceipt(sourceConnection);
        string program = DownloadScript(paths.Length);
        using Process download = Start(sourceConnection, receipt ? WithArchiveReceipt(program) : program, paths.Select(path => path.Raw).ToArray(), redirectInput: receipt);
        string name = Basename(paths[0]);
        string[] args = [target.Raw, Mode(container, replace), name, cohort ? "cohort" : "single", "filesystem"];
        using Process upload = Start(destinationConnection, RelayScript, args, redirectInput: true);
        using CancellationTokenRegistration registration = cancellationToken.Register(() => { Kill(download); Kill(upload); });
        try
        {
            await PumpArchiveAsync(download, upload.StandardInput.BaseStream, receipt, context.BufferSize, cancellationToken);
            upload.StandardInput.Close();
            string result = await upload.StandardOutput.ReadToEndAsync(cancellationToken);
            await FinishAsync(download, sourceConnection, cancellationToken);
            await FinishAsync(upload, destinationConnection, cancellationToken);
            if (result != "MSYNC-DONE\n") throw new IOException($"Remote relay returned no completion: {result}");
        }
        catch
        {
            Kill(download); Kill(upload);
            await Task.WhenAll(download.WaitForExitAsync(CancellationToken.None), upload.WaitForExitAsync(CancellationToken.None));
            throw;
        }
    }

    private static string Mode(bool container, bool replace) => replace ? "replace" : container ? "container" : "exact";
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static ProcessStartInfo CreateRemoteProcessStartInfo(ConnectionValue connection, string executable, IReadOnlyList<string> arguments)
    {
        const string launch = ShellLibrary + "\n" + """
            executable=$(expand "$1"; printf .); executable=${executable%.}
            shift
            exec "$executable" "$@"
            """;
        return CreateShellProcessStartInfo(connection, ShellProgram.Source(launch), [executable, .. arguments], redirectInput: false);
    }

    private static Process Start(ConnectionValue connection, string script, IReadOnlyList<string> args, bool redirectInput)
    {
        ProcessStartInfo info = CreateShellProcessStartInfo(connection, script, args, redirectInput: true);
        Process process = new() { StartInfo = info };
        if (!process.Start()) throw new IOException($"Cannot start native transport {process.StartInfo.FileName} for {connection}.");
        if (!redirectInput) process.StandardInput.Close();
        return process;
    }

    private static ProcessStartInfo CreateShellProcessStartInfo(ConnectionValue connection, string script, IReadOnlyList<string> args, bool redirectInput)
    {
        ProcessStartInfo info = new() { UseShellExecute = false, RedirectStandardInput = redirectInput, RedirectStandardOutput = true };
        if (connection.Kind == ConnectionKind.Ssh)
        {
            info.FileName = "ssh";
            info.ArgumentList.Add("-T");
            if (Environment.GetEnvironmentVariable("MSYNC_SSH_CONFIG") is { Length: > 0 } configuration)
            {
                info.ArgumentList.Add("-F"); info.ArgumentList.Add(configuration);
            }
            string address = connection.Address;
            if (address.StartsWith("ssh://", StringComparison.Ordinal)) address = address[6..];
            if (Uri.TryCreate("ssh://" + address, UriKind.Absolute, out Uri? uri) && uri.Port > 0)
            {
                info.ArgumentList.Add("-p"); info.ArgumentList.Add(uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
                address = (uri.UserInfo.Length > 0 ? uri.UserInfo + "@" : "") + uri.Host;
            }
            info.ArgumentList.Add("--"); info.ArgumentList.Add(address);
            info.ArgumentList.Add("sh -c " + Quote(script) + " sh" + string.Concat(args.Select(arg => " " + Quote(arg))));
        }
        else if (connection.Kind == ConnectionKind.Wsl)
        {
            info.FileName = "wsl.exe";
            if (connection.Address.Length > 0) { info.ArgumentList.Add("--distribution"); info.ArgumentList.Add(connection.Address); }
            info.ArgumentList.Add("--exec"); info.ArgumentList.Add("sh"); info.ArgumentList.Add("-c"); info.ArgumentList.Add(script); info.ArgumentList.Add("sh");
            foreach (string arg in args) info.ArgumentList.Add(arg);
        }
        else throw new MsyncException($"Connection {connection} does not support a remote shell.");
        return info;
    }

    private static async Task FinishAsync(Process process, ConnectionValue connection, CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new IOException($"Native transport for {connection} failed with exit status {process.ExitCode}; see its diagnostic output.");
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private const string ShellLibrary = """
        set -eu
        expand() {
            case "$1" in
                '~') printf '%s' "$HOME" ;;
                '~/'*) printf '%s/%s' "$HOME" "${1#\~/}" ;;
                /*) printf '%s' "$1" ;;
                *) printf '%s/%s' "$PWD" "$1" ;;
            esac
        }
        install() (
            source=$1
            target=$2
            if [ -d "$source" ] && [ ! -L "$source" ]; then
                modified=$(stat -c '%y' -- "$source")
                mkdir -p -- "$target"
                for child in "$source"/* "$source"/.[!.]* "$source"/..?*; do
                    [ -e "$child" ] || [ -L "$child" ] || continue
                    install "$child" "$target/${child##*/}"
                done
                chmod --reference="$source" -- "$target"
                touch -d "$modified" -- "$target"
            else
                mkdir -p -- "${target%/*}"
                mv -fT -- "$source" "$target"
            fi
        )
        """;

    private static string UploadScript => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        destination=$(expand "$1"; printf .); destination=${destination%.}
        mode=$2
        basename=$3
        shape=$4
        source_kind=$5
        target=$destination
        if [ "$mode" = replace ]; then target=${destination%/.}; [ -n "$target" ] || target=/; fi
        if [ "$mode" = container ]; then
            mkdir -p -- "$destination"
            if [ "$shape" = single ]; then target=$destination/$basename; fi
        fi
        parent=${target%/*}
        mkdir -p -- "$parent"
        stage=$(mktemp -d "$parent/.msync-XXXXXXXXXX")
        trap 'rm -rf -- "$stage"' EXIT HUP INT TERM
        tar -xpf - -C "$stage"
        payload=$stage/payload
        [ -e "$payload" ] || [ -L "$payload" ]
        if [ "$mode" = replace ]; then
            [ -d "$payload" ] && [ ! -L "$payload" ]
            rm -rf -- "$target"
        elif [ "$mode" = exact ] && [ -d "$target" ]; then
            printf '%s\n' "Exact destination is an existing directory: $target; append / or /." >&2
            exit 1
        fi
        if [ "$source_kind" = literal ] && [ -e "$target" ]; then chmod --reference="$target" -- "$payload"; fi
        install "$payload" "$target"
        printf 'MSYNC-DONE\n'
        """);

    private static string DownloadScript(int count)
    {
        StringBuilder script = new(ShellProgram.Source(ShellLibrary) + "\n");
        for (int index = 0; index < count; ++index)
        {
            script.Append(System.Globalization.CultureInfo.InvariantCulture, $"s{index}=$(expand \"${{{index + 1}}}\"; printf .); s{index}=${{s{index}%.}}\n");
            script.Append(System.Globalization.CultureInfo.InvariantCulture, $"[ -e \"$s{index}\" ] || [ -L \"$s{index}\" ]\n");
            script.Append(System.Globalization.CultureInfo.InvariantCulture, $"while [ \"${{s{index}%/}}\" != \"$s{index}\" ] && [ \"$s{index}\" != / ]; do s{index}=${{s{index}%/}}; done\n");
            script.Append(System.Globalization.CultureInfo.InvariantCulture, $"p{index}=${{s{index}%/*}}; n{index}=${{s{index}##*/}}\n");
        }
        script.Append("exec tar --format=pax -cpf -");
        for (int index = 0; index < count; ++index)
            script.Append(System.Globalization.CultureInfo.InvariantCulture, $" -C \"$p{index}\" \"./$n{index}\"");
        return script.ToString();
    }

    private static string CopyScript => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        destination=$(expand "$1"; printf .); destination=${destination%.}
        mode=$2
        shape=$3
        shift 3
        for raw in "$@"; do
            source=$(expand "$raw"; printf .); source=${source%.}
            while [ "${source%/}" != "$source" ] && [ "$source" != / ]; do source=${source%/}; done
            [ -e "$source" ] || [ -L "$source" ]
            target=$destination
            if [ "$mode" = replace ]; then target=${destination%/.}; [ -n "$target" ] || target=/; fi
            if [ "$mode" = container ]; then
                basename=${source##*/}
                target=$destination/$basename
            fi
            if [ -e "$target" ] && [ "$source" -ef "$target" ] && [ ! -L "$source" ]; then continue; fi
            if [ "$mode" = replace ]; then
                [ -d "$source" ] && [ ! -L "$source" ]
            elif [ "$mode" = exact ] && [ -d "$target" ]; then
                printf '%s\n' "Exact destination is an existing directory: $target; append / or /." >&2
                exit 1
            fi
            parent=${target%/*}
            scratch_parent=$parent
            if [ -d "$source" ] && [ ! -L "$source" ]; then
                source_real=$(realpath -- "$source"; printf .); source_real=${source_real%.}; source_real=${source_real%?}
                target_real=$(realpath -m -- "$target"; printf .); target_real=${target_real%.}; target_real=${target_real%?}
                case "$target_real/" in "$source_real/"*) scratch_parent=${source_real%/*}; [ -n "$scratch_parent" ] || scratch_parent=/ ;; esac
            fi
            mkdir -p -- "$scratch_parent"
            stage=$(mktemp -d "$scratch_parent/.msync-XXXXXXXXXX")
            trap 'rm -rf -- "$stage"' EXIT HUP INT TERM
            cp -a -- "$source" "$stage/payload"
            if [ "$mode" = replace ]; then rm -rf -- "$target"; fi
            install "$stage/payload" "$target"
            rm -rf -- "$stage"
            trap - EXIT HUP INT TERM
        done
        printf 'MSYNC-DONE\n'
        """);

    private static string RelayScript => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        destination=$(expand "$1"; printf .); destination=${destination%.}
        mode=$2
        basename=$3
        shape=$4
        target=$destination
        if [ "$mode" = replace ]; then target=${destination%/.}; [ -n "$target" ] || target=/; fi
        if [ "$mode" = container ]; then
            mkdir -p -- "$destination"
            if [ "$shape" = single ]; then target=$destination/$basename; fi
        fi
        parent=${target%/*}
        mkdir -p -- "$parent"
        stage=$(mktemp -d "$parent/.msync-XXXXXXXXXX")
        trap 'rm -rf -- "$stage"' EXIT HUP INT TERM
        mkdir "$stage/payload"
        tar -xpf - -C "$stage/payload"
        payload=$stage/payload
        if [ "$shape" = single ]; then payload=$payload/$basename; fi
        if [ "$mode" = replace ]; then
            [ -d "$payload" ] && [ ! -L "$payload" ]
            rm -rf -- "$target"
        elif [ "$mode" = exact ] && [ -d "$target" ]; then
            printf '%s\n' "Exact destination is an existing directory: $target; append / or /." >&2
            exit 1
        fi
        install "$payload" "$target"
        printf 'MSYNC-DONE\n'
        """);
}
