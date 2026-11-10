using Msync.Core;

namespace Msync.Runtime;

public sealed record RemotePathMetadata(PathValue Path, string Kind, string CanonicalPath, Exception? Error = null);

public static partial class TransferEngine
{
    public sealed partial class RemoteTransferSession
    {
        public async Task<IReadOnlyList<RemotePathMetadata>> ReadDirectoryMetadataAsync(PathValue directory,
            bool recursive = false, CancellationToken cancellationToken = default)
        {
            string data = await QueryAsync(DirectoryMetadataProgram, [directory.Raw, recursive ? "recursive" : "flat"], cancellationToken);
            string[] fields = data.Split('\0');
            string canonical = fields[0].TrimEnd('/');
            string raw = directory.Raw.TrimEnd('/');
            List<RemotePathMetadata> entries = [];
            for (int index = 1; index + 1 < fields.Length; index += 2)
            {
                string name = fields[index + 1];
                if (name.Length == 0) continue;
                string kind = fields[index] switch
                {
                    "f" => "regular",
                    "d" => "directory",
                    "l" => "symlink",
                    "b" => "block",
                    "c" => "character",
                    "p" => "fifo",
                    "s" => "socket",
                    _ => "unknown"
                };
                entries.Add(new(directory.WithRaw(raw + "/" + name), kind, canonical + "/" + name));
            }
            entries.Sort(static (left, right) => TextSemantics.Ordinal.Compare(left.Path.Raw, right.Path.Raw));
            return entries;
        }

        public async Task<IReadOnlyList<RemotePathMetadata>> ReadMetadataBatchAsync(IReadOnlyList<PathValue> paths,
            CancellationToken cancellationToken = default, bool includeKinds = true)
        {
            if (paths.Count == 0) return [];
            await serial.WaitAsync(cancellationToken);
            using var registration = cancellationToken.Register(Abort);
            var results = new RemotePathMetadata[paths.Count];
            try
            {
                await WriteCommandAsync(input, includeKinds ? "METADATA" : "NAMESPACE", paths.Count, cancellationToken);
                async Task Send()
                {
                    foreach (var path in paths) await WriteEncodedLineAsync(input, path.Raw, cancellationToken);
                    await input.FlushAsync(cancellationToken);
                }
                async Task Receive()
                {
                    for (int index = 0; index < paths.Count; index++)
                    {
                        var response = await ReadResponseAsync(output, index, cancellationToken);
                        if (response.Status != 0)
                        {
                            results[index] = new(paths[index], "unknown", "",
                                new IOException($"Cannot inspect {paths[index]} on {Connection}: {response.Detail}"));
                            continue;
                        }
                        int separator = response.Detail.IndexOf('\0');
                        if (separator < 0) throw new IOException("Native metadata response has no kind/path separator.");
                        results[index] = new(paths[index], response.Detail[..separator], response.Detail[(separator + 1)..]);
                    }
                }
                await CompleteDuplexAsync(Send(), Receive());
                return results;
            }
            finally { serial.Release(); }
        }
    }

    private static string DirectoryMetadataProgram => field ??= ShellProgram.Source(ShellLibrary + "\n" + """
        directory=$(expand "$1"; printf .); directory=${directory%.}
        if [ ! -d "$directory" ]; then printf 'Directory metadata requires a directory: %s\n' "$directory" >&2; exit 1; fi
        canonical=$(realpath -m -- "$directory" && printf .)
        canonical=${canonical%.}; canonical=${canonical%?}
        printf '%s\0' "$canonical"
        if [ "$2" = recursive ]; then
            find -H "$directory" -samefile /proc/self/fd/9 -prune -o -printf '%y\0%P\0'
        else
            find -H "$directory" -maxdepth 1 -samefile /proc/self/fd/9 -prune -o -printf '%y\0%P\0'
        fi
        """);
}
