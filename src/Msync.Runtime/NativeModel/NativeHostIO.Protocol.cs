using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Msync.Core;

namespace Msync.Runtime;

internal static class NativeTransferProtocol
{
    private const string ArchiveNameEncoding = "flags=rsh;s,%,%25,g;s,\n,%0A,g";
    private const string ArchiveNameDecoding = "flags=rsh;s,%0A,\n,g;s,%25,%,g";

    private sealed class ArchiveStreamException(IOException inner)
        : IOException("The archive payload could not be completed on its native channel.", inner);

    private static async Task WriteArchiveNodeAsync(TarWriter writer, string source, string name, CancellationToken cancellationToken,
        PlannedTransferKind kind = PlannedTransferKind.GenericCopy, RuntimeTimeline? timeline = null)
    {
        using var activity = timeline?.Begin("transport.archive.write_entry");
        activity?.SetAttribute("source.resolved_path", source);
        activity?.SetAttribute("archive.member", name);
        try
        {
            FileMetadata metadata = NativeFileMetadata.Capture(source);
            bool directory = kind == PlannedTransferKind.CopyDirectory || kind == PlannedTransferKind.GenericCopy && metadata.IsDirectory;
            bool link = kind == PlannedTransferKind.CopyLink || kind == PlannedTransferKind.GenericCopy && metadata.IsLink;
            TarEntryType type = link ? TarEntryType.SymbolicLink : directory ? TarEntryType.Directory : TarEntryType.RegularFile;
            activity?.SetAttribute("archive.kind", type.ToString());
            try { await writer.WriteEntryAsync(new PaxGlobalExtendedAttributesTarEntry([KeyValuePair.Create("mtime", metadata.Modified.Pax)]), cancellationToken); }
            catch (IOException error) { throw new ArchiveStreamException(error); }
            // .NET 10 rewrites local PAX mtime through 100ns DateTimeOffset. A standard
            // global PAX timestamp plus a GNU member retains the native decimal value.
            GnuTarEntry entry = new(type, name) { ModificationTime = DateTimeOffset.UnixEpoch };
            if (!OperatingSystem.IsWindows() && !link) entry.Mode = metadata.Mode;
            if (link)
            {
                FileSystemInfo info = metadata.IsDirectory ? new DirectoryInfo(source) : new FileInfo(source);
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
                    try { await writer.WriteEntryAsync(entry, cancellationToken); }
                    catch (IOException error) { throw new ArchiveStreamException(error); }
                    activity?.SetMetric("bytes.payload", contents.Length);
                }
                else
                {
                    entry.DataStream = bytes;
                    try { await writer.WriteEntryAsync(entry, cancellationToken); }
                    catch (IOException error) { throw new ArchiveStreamException(error); }
                    activity?.SetMetric("bytes.payload", bytes.Position);
                }
                return;
            }
            try { await writer.WriteEntryAsync(entry, cancellationToken); }
            catch (IOException error) { throw new ArchiveStreamException(error); }
            if (!directory || link) return;
            foreach (string child in Directory.EnumerateFileSystemEntries(source).Order(TextSemantics.Ordinal))
                await WriteArchiveNodeAsync(writer, child, name + "/" + Path.GetFileName(child), cancellationToken, timeline: timeline);
        }
        catch (Exception error) { activity?.Fail(error); throw; }
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

    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static ProcessStartInfo CreateRemoteProcessStartInfo(ConnectionKind kind, string address, string executable, IReadOnlyList<string> arguments, Native.NativeRuntimeContext? context = null)
    {
        const string launch = ShellLibrary + "\n" + """
            executable=$(expand "$1"; printf .); executable=${executable%.}
            shift
            exec "$executable" "$@"
            """;
        return CreateShellProcessStartInfo(kind, address, ShellProgram.Source(launch), [executable, .. arguments], redirectInput: false, context);
    }

    internal static ProcessStartInfo CreateShellProcessStartInfo(ConnectionKind kind, string endpoint, string script,
        IReadOnlyList<string> args, bool redirectInput, Native.NativeRuntimeContext? context = null) =>
        (context?.Connections ?? new Native.NativeConnections()).Get(new(kind, endpoint)).Shell(script, args, redirectInput);

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
        install() {
            [ -d "${2%/*}/" ] || mkdir -p -- "${2%/*}/"
            install_node "$@"
        }
        install_node() {
            if [ ! -d "$1" ] || [ -L "$1" ]; then
                mv -fT -- "$1" "$2"
                install_leaves=$((${install_leaves:-0} + 1))
                return
            fi
            if [ "${3:-}" = rename ] && [ ! -e "$2" ] && [ ! -L "$2" ]; then
                if [ -z "${install_rename_supported:-}" ]; then
                    case $(LC_ALL=C mv --help) in
                        *--no-copy*) install_rename_supported=yes ;;
                        *) install_rename_supported=no ;;
                    esac
                fi
                # A skipped no-clobber move can report success. The stage still
                # owns the source in that case; merge instead of dropping it.
                if [ "$install_rename_supported" = yes ] &&
                    mv --no-copy -nT -- "$1" "$2" 2>/dev/null && [ ! -d "$1" ]; then
                    install_subtrees=$((${install_subtrees:-0} + 1))
                    return
                fi
                install_rename_fallbacks=$((${install_rename_fallbacks:-0} + 1))
            fi
            install_mtime=$(stat -c '%y' -- "$1")
            set -- "$1" "$2" "$install_mtime"
            install_directories=$((${install_directories:-0} + 1))
            [ -d "$2" ] || mkdir -p -- "$2"
            for install_child in "$1"/* "$1"/.[!.]* "$1"/..?*; do
                [ -e "$install_child" ] || [ -L "$install_child" ] || continue
                install_node "$install_child" "$2/${install_child##*/}"
            done
            chmod --reference="$1" -- "$2"
            touch -d "$3" -- "$2"
        }
        install_receipt() {
            if [ "${msync_observe_install:-}" = yes ]; then
                printf 'MSYNC-INSTALL %s %s %s %s\n' "${install_leaves:-0}" "${install_directories:-0}" \
                    "${install_subtrees:-0}" "${install_rename_fallbacks:-0}"
            fi
            printf 'MSYNC-DONE\n'
        }
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
        # Unlike peer copies, an upload archive has no foreign directory
        # owners to preserve. Only its fresh root shares the stage's parent.
        install "$payload" "$target" rename
        install_receipt
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
        script.Append("exec tar --no-unquote --format=pax --transform=").Append(Quote(ArchiveNameEncoding)).Append(" -cpf -");
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
        install_receipt
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
        tar --transform=__MSYNC_ARCHIVE_NAME_DECODING__ -xpf - -C "$stage/payload"
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
        install_receipt
        """).Replace("__MSYNC_ARCHIVE_NAME_DECODING__", Quote(ArchiveNameDecoding), StringComparison.Ordinal);

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

    private static string WithArchiveReceipt(string program) => "\nset -eu\n(\n" + program + "\n)\n" + ArchiveReceipt;

    private static string ArchiveReceipt => field ??= ShellProgram.Source("""
        IFS= read -r received
        if [ "$received" != MSYNC-ARCHIVE-RECEIVED ]; then
            printf 'Archive receiver did not complete\n' >&2
            exit 1
        fi
        """);

    private static async Task AcknowledgeArchiveAsync(Process producer, CancellationToken cancellationToken)
    {
        await producer.StandardInput.BaseStream.WriteAsync("MSYNC-ARCHIVE-RECEIVED\n"u8.ToArray(), cancellationToken);
        await producer.StandardInput.BaseStream.FlushAsync(cancellationToken);
        producer.StandardInput.Close();
    }

    private static async Task PumpArchiveAsync(Process producer, Stream destination, bool receipt, int bufferSize, CancellationToken cancellationToken)
    {
        Stream source = producer.StandardOutput.BaseStream;
        if (!receipt)
        {
            await source.CopyToAsync(destination, bufferSize, cancellationToken);
            return;
        }
        using var forwarding = new ArchiveForwardStream(source, destination);
        using (var reader = new TarReader(forwarding, leaveOpen: true))
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken) is { } entry)
                if (entry.DataStream is { } contents) await contents.CopyToAsync(Stream.Null, bufferSize, cancellationToken);
        await AcknowledgeArchiveAsync(producer, cancellationToken);
        await forwarding.CopyToAsync(Stream.Null, bufferSize, cancellationToken);
    }

    private sealed class ArchiveForwardStream(Stream source, Stream destination) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = source.Read(buffer, offset, count);
            destination.Write(buffer, offset, read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            await destination.WriteAsync(buffer[..read], cancellationToken);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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

    private static string SelectionPrelude => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        directory=$(expand "$1"; printf .); directory=${directory%.}
        destination_raw=$2
        selection_mode=$3
        if [ ! -d "$directory" ]; then printf 'Selector source is not a directory: %s\n' "$directory" >&2; exit 1; fi
        if [ "$4" = recursive ]; then find "$directory" -mindepth 1 -printf '%P\0'; else find "$directory" -mindepth 1 -maxdepth 1 -printf '%P\0'; fi
        printf '\0'
        """);

    private static string SelectionDecode => field ??= ShellProgram.Source("\n" + """
        set --
        while IFS= read -r encoded; do
            [ "$encoded" = MSYNC-END ] && break
            if [ "$encoded" = MSYNC-QUERY ]; then query; continue; fi
            source=$(printf '%b.' "$encoded"); source=${source%.}
            set -- "$@" "$source"
        done
        """);

    private static string SelectionArchive => field ??= ShellProgram.Source("\n" + """
        if [ "$#" -eq 0 ]; then exec tar --no-unquote --format=pax -cpf - --files-from /dev/null; fi
        for raw in "$@"; do
            source=$(expand "$raw"; printf .); source=${source%.}
            while [ "${source%/}" != "$source" ] && [ "$source" != / ]; do source=${source%/}; done
            parent=${source%/*}
            name=${source##*/}
            shift
            set -- "$@" -C "$parent" "./$name"
        done
        exec tar --no-unquote --format=pax --transform=__MSYNC_ARCHIVE_NAME_ENCODING__ -cpf - "$@"
        """).Replace("__MSYNC_ARCHIVE_NAME_ENCODING__", Quote(ArchiveNameEncoding), StringComparison.Ordinal);
    private static string CohortInstallScript => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        destination=$(expand "$1"; printf .); destination=${destination%.}
        mode=$2
        shift 2
        if [ "$#" -eq 0 ]; then install_receipt; exit 0; fi
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
        install_receipt
        """);

    private static async Task<string> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        StringBuilder header = new();
        byte[] current = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(current, cancellationToken);
            if (current[0] == (byte)'\n') return header.ToString();
            header.Append((char)current[0]);
        }
    }

    private static async Task WriteEncodedLineAsync(Stream stream, string text, CancellationToken cancellationToken)
    {
        StringBuilder encoded = new();
        foreach (byte value in Encoding.UTF8.GetBytes(text))
            encoded.Append("\\0").Append(Convert.ToString(value, 8).PadLeft(3, '0'));
        encoded.Append('\n');
        await stream.WriteAsync(Encoding.ASCII.GetBytes(encoded.ToString()), cancellationToken);
    }

    private static string SelectionQueryScript => field ??= ShellProgram.Source("\n" + """
        query() (
            IFS= read -r encoded_script
            query_script=$(printf '%b.' "$encoded_script"); query_script=${query_script%.}
            IFS= read -r count
            set --
            while [ "$count" -gt 0 ]; do
                IFS= read -r encoded_argument
                argument=$(printf '%b.' "$encoded_argument"); argument=${argument%.}
                set -- "$@" "$argument"
                count=$((count - 1))
            done
            output=$(mktemp "${TMPDIR:-/tmp}/.msync-query-XXXXXXXXXX")
            trap 'rm -f -- "$output"' EXIT HUP INT TERM
            if sh -c "$query_script" sh "$@" < /dev/null > "$output"; then status=0; else status=$?; fi
            bytes=$(wc -c < "$output")
            printf 'MSYNC-QUERY %s %s\n' "$status" "$bytes"
            cat -- "$output"
        )
        """);

    internal static string NativeUploadProgram => UploadScript;
    internal static string NativeRelayProgram => RelayScript;
    internal static string NativeCopyProgram(bool cohort) => cohort ? CohortInstallScript : CopyScript;
    internal static string NativeDownloadProgram(int count, bool receipt) => receipt
        ? WithArchiveReceipt(DownloadScript(count)) : DownloadScript(count);
    internal static string NativeSelectionProgram(bool sameEndpoint, bool receipt) =>
        SelectionPrelude + SelectionQueryScript + SelectionDecode + (sameEndpoint
            ? "\nset -- \"$destination_raw\" \"$selection_mode\" \"$@\"\n" + CohortInstallScript
            : receipt ? WithArchiveReceipt(SelectionArchive) : SelectionArchive);
    internal static Task NativeWriteArchiveNodeAsync(TarWriter writer, string source, string name,
        CancellationToken token, RuntimeTimeline? timeline, PlannedTransferKind kind = PlannedTransferKind.GenericCopy) =>
        WriteArchiveNodeAsync(writer, source, name, token, kind, timeline);
    internal static void NativeRestoreArchiveNames(TarEntry entry) => RestoreArchiveNames(entry);
    internal static void NativeApplyArchiveMetadata(TarEntry entry, string destination) => ApplyArchiveMetadata(entry, destination);
    internal static void NativeCreateArchiveLink(string destination, string link, TarEntry entry) => CreateArchiveLink(destination, link, entry);
    internal static Task NativeAcknowledgeArchiveAsync(Process process, CancellationToken token) => AcknowledgeArchiveAsync(process, token);
    internal static Task NativePumpArchiveAsync(Process producer, Stream destination, bool receipt,
        int bufferSize, CancellationToken token) => PumpArchiveAsync(producer, destination, receipt, bufferSize, token);
    internal static Task<IReadOnlyList<string>> NativeReadSelectionNamesAsync(Stream stream, string pattern, CancellationToken token) =>
        ReadSelectionNamesAsync(stream, pattern, token);
    internal static Task NativeWriteSelectionNamesAsync(Stream stream, IReadOnlyList<string> names, CancellationToken token) =>
        WriteSelectionNamesAsync(stream, names, token);
    internal static Task NativeWriteEncodedLineAsync(Stream stream, string value, CancellationToken token) => WriteEncodedLineAsync(stream, value, token);
    internal static Task<string> NativeReadHeaderAsync(Stream stream, CancellationToken token) => ReadHeaderAsync(stream, token);

    internal static string NativeRecordedCopyProgram(PlannedTransferKind kind) =>
        CheckRecordedSource(kind, 4) + FinalMode(CopyScript);
    internal static string NativeRecordedDownloadProgram(PlannedTransferKind kind, bool receipt) =>
        CheckRecordedSource(kind, 1) + NativeDownloadProgram(1, receipt);
    internal static string NativeRecordedUploadProgram => FinalMode(UploadScript);
    internal static string NativeRecordedRelayProgram => FinalMode(RelayScript);

    private static string FinalMode(string program) => program
        .Replace("if [ \"$mode\" = replace ]; then\n", "if [ \"$mode\" = replace ] || [ \"$mode\" = replace-final ]; then\n", StringComparison.Ordinal)
        .Replace("if [ \"$mode\" = replace ]; then rm -rf", "if [ \"$mode\" = replace ] || [ \"$mode\" = replace-final ]; then rm -rf", StringComparison.Ordinal);

    private static string CheckRecordedSource(PlannedTransferKind kind, int argument)
    {
        string? test = kind switch
        {
            PlannedTransferKind.CopyFile => "[ -f \"$recorded_source\" ] && [ ! -L \"$recorded_source\" ]",
            PlannedTransferKind.CopyDirectory => "[ -d \"$recorded_source\" ] && [ ! -L \"$recorded_source\" ]",
            PlannedTransferKind.CopyLink => "[ -L \"$recorded_source\" ]",
            _ => null
        };
        return test is null ? "" : ShellProgram.Source(ShellLibrary) + "\n"
            + $"recorded_source=$(expand \"${argument}\"; printf .); recorded_source=${{recorded_source%.}}\n"
            + $"if ! ( {test} ); then printf '%s\\n' \"Recorded {kind} requires its admitted source kind: $recorded_source\" >&2; exit 1; fi\n";
    }
}
