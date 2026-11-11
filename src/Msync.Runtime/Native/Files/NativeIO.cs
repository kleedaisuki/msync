using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static readonly Encoding IOTextEncoding = new UTF8Encoding(false, true);

    private static async ValueTask<Value> IOOperationAsync(string name, IReadOnlyList<Value> args,
        RuntimeContext context, CancellationToken token)
    {
        if (StandardIO(name, args, context, token) is { } standard) return await standard;
        PathValue path = context.AsPath(args[0]);
        if (name == "read_lines") return new RangeValue(ReadLines(path, context, token), TypeRef.String);
        if (name == "read_bytes") return new RangeValue(ReadBytes(path, context, token), TypeRef.Number);
        if (name == "read_text")
        {
            await using IOInput input = OpenInput(path, context, token);
            using StreamReader reader = new(input.Stream, IOTextEncoding, true, context.BufferSize, leaveOpen: true);
            string text = await reader.ReadToEndAsync(token);
            await input.CompleteAsync(token);
            return new StringValue(text);
        }
        if (name == "file_size")
        {
            if (path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } connection)
            {
                string size = await TransferEngine.RunCommandAsync(connection, IOPrelude + "\nstat -L -c %s -- \"$p\"", [path.Raw], token);
                return new NumberValue(double.Parse(size, System.Globalization.CultureInfo.InvariantCulture));
            }
            return new NumberValue(new FileInfo(context.LocalPath(path)).Length);
        }
        bool append = name.StartsWith("append_", StringComparison.Ordinal);
        await using Stream inputBytes = name.EndsWith("_bytes", StringComparison.Ordinal)
            ? new RangeInputStream(args[1] as RangeValue ?? throw new MsyncException($"io.{name} expects Range<Number>"), token)
            : new MemoryStream(IOTextEncoding.GetBytes(Text(args[1])), writable: false);
        if (path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl)
            await WriteRemoteAsync(path, inputBytes, append, context, token);
        else if (append)
        {
            string local = context.LocalPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await using FileStream output = new(local, FileMode.Append, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, context.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await inputBytes.CopyToAsync(output, context.BufferSize, token);
            if (Status(local, false) == "regular") output.Flush(flushToDisk: true);
            else await output.FlushAsync(token);
        }
        else
        {
            string local = WritableFile(context.LocalPath(path));
            string kind = Status(local, false);
            if (kind is "regular" or "not_found")
                await TransferFiles.WriteFileAsync(inputBytes, local, null, token, bufferSize: context.BufferSize);
            else
            {
                await using FileStream output = new(local, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, context.BufferSize, FileOptions.Asynchronous);
                await inputBytes.CopyToAsync(output, context.BufferSize, token);
                await output.FlushAsync(token);
            }
        }
        return Value.Null;
    }

    private static string WritableFile(string local)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(local); }
        catch (FileNotFoundException) { return local; }
        catch (DirectoryNotFoundException) { return local; }
        return (attributes & FileAttributes.ReparsePoint) != 0
            ? File.ResolveLinkTarget(local, returnFinalTarget: true)?.FullName ?? local : local;
    }

    private static async IAsyncEnumerable<Value> ReadLines(PathValue path, RuntimeContext context,
        [EnumeratorCancellation] CancellationToken token)
    {
        await using IOInput input = OpenInput(path, context, token);
        using StreamReader reader = new(input.Stream, IOTextEncoding, true, context.BufferSize, leaveOpen: true);
        while (await reader.ReadLineAsync(token) is { } line) yield return new StringValue(line);
        await input.CompleteAsync(token);
    }

    private static async IAsyncEnumerable<Value> ReadBytes(PathValue path, RuntimeContext context,
        [EnumeratorCancellation] CancellationToken token)
    {
        await using IOInput input = OpenInput(path, context, token);
        byte[] buffer = new byte[context.BufferSize];
        int count;
        while ((count = await input.Stream.ReadAsync(buffer, token)) != 0)
            for (int index = 0; index < count; index++) yield return new NumberValue(buffer[index]);
        await input.CompleteAsync(token);
    }

    private static IOInput OpenInput(PathValue path, RuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (path.Connection is not { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl })
            return new(new FileStream(context.LocalPath(path), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, context.BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));
        ProcessStartInfo info = IOStart(path, "exec cat -- \"$p\"", false, context);
        return new(Process.Start(info) ?? throw new IOException($"Cannot start reader for '{path}'."), path);
    }

    private static ProcessStartInfo IOStart(PathValue path, string body, bool input, RuntimeContext context)
    {
        ProcessStartInfo info = TransferEngine.CreateRemoteProcessStartInfo(path.Connection!, "/bin/sh",
            ["-c", ShellProgram.Source(IOPrelude + "\n" + body), "msync-io", path.Raw]);
        info.UseShellExecute = false;
        info.WorkingDirectory = context.WorkingDirectory;
        info.RedirectStandardInput = input;
        if (input) info.StandardInputEncoding = IOTextEncoding;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        return info;
    }

    private static async Task WriteRemoteAsync(PathValue path, Stream input, bool append, RuntimeContext context, CancellationToken token)
    {
        string body = append ? "mkdir -p -- \"$(dirname -- \"$p\")\"\ncat >> \"$p\"\nif [ -f \"$p\" ]; then sync -d -- \"$p\"; fi" : """
            p=$(readlink -mn -- "$p"; printf .); p=${p%.}
            if [ -e "$p" ] && [ ! -f "$p" ]; then cat > "$p"; exit; fi
            parent=$(dirname -- "$p")
            mkdir -p -- "$parent"
            stage=$(mktemp "$parent/.msync-XXXXXXXXXX")
            trap 'rm -f -- "$stage"' EXIT HUP INT TERM
            cat > "$stage"
            if [ -f "$p" ]; then chmod --reference="$p" -- "$stage"; fi
            sync -d -- "$stage"
            mv -fT -- "$stage" "$p"
            """;
        using Process process = Process.Start(IOStart(path, body, true, context))
            ?? throw new IOException($"Cannot start writer for '{path}'.");
        using CancellationTokenRegistration cancel = token.Register(() => StopIO(process));
        Task<string> error = process.StandardError.ReadToEndAsync(token);
        Task<string> output = process.StandardOutput.ReadToEndAsync(token);
        try
        {
            await input.CopyToAsync(process.StandardInput.BaseStream, context.BufferSize, token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            await output;
            string diagnostic = await error;
            if (process.ExitCode != 0) throw new IOException($"Cannot write '{path}' (native exit {process.ExitCode}): {diagnostic.TrimEnd()}");
        }
        catch { StopIO(process); throw; }
    }

    private static void StopIO(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private const string IOPrelude = """
        set -eu
        expand() { case "$1" in '~') printf '%s' "$HOME";; '~/'*) printf '%s/%s' "$HOME" "${1#??}";; *) printf '%s' "$1";; esac; }
        p=$(expand "$1"; printf .); p=${p%.}
        """;

    private sealed class IOInput : IAsyncDisposable
    {
        internal Stream Stream { get; }
        private readonly Process? process;
        private readonly PathValue? path;
        private readonly Task<string>? error;
        private bool completed;

        internal IOInput(Stream stream) => Stream = stream;
        internal IOInput(Process process, PathValue path)
        {
            this.process = process; this.path = path;
            Stream = process.StandardOutput.BaseStream;
            error = process.StandardError.ReadToEndAsync();
        }
        internal async Task CompleteAsync(CancellationToken token)
        {
            if (process is null) return;
            using CancellationTokenRegistration cancel = token.Register(() => StopIO(process));
            await process.WaitForExitAsync(token);
            string diagnostic = await error!;
            completed = true;
            if (process.ExitCode != 0) throw new IOException($"Cannot read '{path}' (native exit {process.ExitCode}): {diagnostic.TrimEnd()}");
        }
        public async ValueTask DisposeAsync()
        {
            await Stream.DisposeAsync();
            if (process is null) return;
            if (!completed) StopIO(process);
            await process.WaitForExitAsync();
            await error!;
            process.Dispose();
        }
    }

    private sealed class RangeInputStream(RangeValue values, CancellationToken token) : Stream
    {
        private readonly IAsyncEnumerator<Value> iterator = values.Items.GetAsyncEnumerator(token);
        private bool ended;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = 0;
            while (!ended && count < buffer.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await iterator.MoveNextAsync()) { ended = true; break; }
                if (iterator.Current is not NumberValue number || !double.IsInteger(number.Data) || number.Data is < 0 or > 255)
                    throw new MsyncException("Byte ranges require Number values in the integral interval 0..255.");
                buffer.Span[count++] = (byte)number.Data;
            }
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask DisposeAsync() => iterator.DisposeAsync();
        protected override void Dispose(bool disposing)
        {
            if (disposing) iterator.DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, token).GetAwaiter().GetResult();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
