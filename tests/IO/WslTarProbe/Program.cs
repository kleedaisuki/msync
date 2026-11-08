using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

const string Script = """
    set -eu
    expand() {
        case "$1" in
            '~') printf '%s' "$HOME" ;;
            '~/'*) printf '%s/%s' "$HOME" "${1#\~/}" ;;
            /*) printf '%s' "$1" ;;
            *) printf '%s/%s' "$PWD" "$1" ;;
        esac
    }
    s0=$(expand "$1"; printf .); s0=${s0%.}
    [ -e "$s0" ] || [ -L "$s0" ]
    while [ "${s0%/}" != "$s0" ] && [ "$s0" != / ]; do s0=${s0%/}; done
    p0=${s0%/*}; n0=${s0##*/}
    exec tar --format=pax -cpf - -C "$p0" "./$n0"
    """;

string distribution = Option("--distribution")!;
string source = Option("--source")!;
string directory = Path.GetFullPath(Option("--work-directory") ?? Environment.CurrentDirectory);
string output = Path.GetFullPath(Option("--output-dir")!);
Directory.CreateDirectory(output);
List<Result> results = [];
foreach (string mode in new[] { "capture", "live" })
{
    var reads = new List<ReadResult>();
    var entries = new List<EntryResult>();
    string rawPath = Path.Combine(output, mode + ".tar");
    var info = new ProcessStartInfo("wsl.exe")
    {
        WorkingDirectory = directory,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true
    };
    foreach (string argument in new[] { "--distribution", distribution, "--exec", "sh", "-c", Script.Replace("\r\n", "\n", StringComparison.Ordinal), "sh", source }) info.ArgumentList.Add(argument);
    using var process = Process.Start(info)!;
    process.StandardInput.Close();
    string? error = null;
    using (var raw = new FileStream(rawPath, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous))
    using (var stream = new CaptureStream(process.StandardOutput.BaseStream, raw, reads))
    {
        try
        {
            if (mode == "capture") await stream.CopyToAsync(Stream.Null);
            else
            {
                await Parse(stream, Path.Combine(output, mode), entries);
                await stream.CopyToAsync(Stream.Null);
            }
        }
        catch (Exception failure)
        {
            error = failure.ToString();
            await stream.CopyToAsync(Stream.Null);
        }
    }
    await process.WaitForExitAsync();
    string? offline = null;
    var offlineEntries = new List<EntryResult>();
    try
    {
        using var file = File.OpenRead(rawPath);
        using var nonSeekable = new CaptureStream(file, Stream.Null, []);
        await Parse(nonSeekable, Path.Combine(output, mode + "-offline"), offlineEntries);
    }
    catch (Exception failure) { offline = failure.ToString(); }
    using var data = File.OpenRead(rawPath);
    results.Add(new(mode, process.ExitCode, data.Length, Convert.ToHexString(SHA256.HashData(data)), error, entries, reads, offline, offlineEntries));
}
File.WriteAllText(Path.Combine(output, "probe.json"), JsonSerializer.Serialize(new Report(Environment.OSVersion.VersionString, source, directory, results), ProbeJson.Default.Report));
Console.WriteLine(Path.Combine(output, "probe.json"));

string? Option(string name) { int index = Array.IndexOf(args, name); return index < 0 ? null : args[index + 1]; }
static async Task Parse(Stream stream, string output, List<EntryResult> results)
{
    Directory.CreateDirectory(output);
    using var reader = new TarReader(stream, leaveOpen: true);
    int index = 0;
    while (await reader.GetNextEntryAsync(copyData: false) is { } entry)
    {
        string path = Path.Combine(output, $"entry-{index++}.bin");
        if (entry.DataStream is { } body)
        {
            await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await body.CopyToAsync(file, 65536);
            file.Flush(flushToDisk: true);
        }
        using var contents = File.Exists(path) ? File.OpenRead(path) : Stream.Null;
        results.Add(new(entry.Name, entry.EntryType.ToString(), entry.Format.ToString(), entry.Length, contents.Length, Convert.ToHexString(SHA256.HashData(contents))));
    }
}


internal sealed record ReadResult(int Requested, int Returned, long Offset);
internal sealed record EntryResult(string Name, string Type, string Format, long DeclaredBytes, long CopiedBytes, string Sha256);
internal sealed record Result(string Mode, int ExitCode, long Bytes, string Sha256, string? Error, List<EntryResult> Entries, List<ReadResult> Reads, string? OfflineError, List<EntryResult> OfflineEntries);
internal sealed record Report(string Platform, string Source, string Directory, List<Result> Results);
[JsonSerializable(typeof(Report))]
internal sealed partial class ProbeJson : JsonSerializerContext;
internal sealed class CaptureStream(Stream source, Stream output, List<ReadResult> reads) : Stream
{
    private long offset;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int start, int count)
    {
        int size = source.Read(buffer, start, count);
        output.Write(buffer, start, size); reads.Add(new(count, size, offset)); offset += size; return size;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int size = await source.ReadAsync(buffer, cancellationToken);
        await output.WriteAsync(buffer[..size], cancellationToken);
        reads.Add(new(buffer.Length, size, offset)); offset += size; return size;
    }
    public override Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(start, count), token).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long position, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long length) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
}
