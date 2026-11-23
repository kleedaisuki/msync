using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static readonly Encoding IOTextEncoding = new UTF8Encoding(false, true);

    internal static async ValueTask WriteFileText(NativePathValue path, string contents, bool append,
        NativeRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (context.DryRun) return;
        await using Stream bytes = new MemoryStream(IOTextEncoding.GetBytes(contents), writable: false);
        await WriteFile(path, bytes, append, context, token).ConfigureAwait(false);
    }

    internal static async ValueTask WriteFile(NativePathValue path, Stream inputBytes, bool append,
        NativeRuntimeContext context, CancellationToken token)
    {
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
                await TransferFiles.WriteFileAsync(inputBytes, local, null, token, bufferSize: context.BufferSize, timeline: context.Timeline);
            else
            {
                await using FileStream output = new(local, FileMode.Open, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, context.BufferSize, FileOptions.Asynchronous);
                await inputBytes.CopyToAsync(output, context.BufferSize, token);
                await output.FlushAsync(token);
            }
        }
    }

    private static string WritableFile(string local)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(local); }
        catch (FileNotFoundException) { return local; }
        catch (DirectoryNotFoundException) { return local; }
        return NativeFileMetadata.IsLink(local, attributes)
            ? File.ResolveLinkTarget(local, returnFinalTarget: true)?.FullName ?? local : local;
    }

    internal sealed class TextLineInput(NativePathValue path, NativeRuntimeContext context) : IAsyncDisposable
    {
        private IOInput? input;
        private StreamReader? reader;

        internal async ValueTask<string?> Read(CancellationToken token)
        {
            if (reader is null)
            {
                input = OpenInput(path, context, token);
                reader = new(input.Stream, IOTextEncoding, true, context.BufferSize, leaveOpen: true);
            }
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) await input!.CompleteAsync(token).ConfigureAwait(false);
            return line;
        }

        public async ValueTask DisposeAsync()
        {
            try { reader?.Dispose(); }
            finally
            {
                if (input is not null) await input.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    internal static async ValueTask<string> ReadFileText(NativePathValue path, NativeRuntimeContext context,
        CancellationToken token, Action<string>? refine = null)
    {
        token.ThrowIfCancellationRequested();
        IOInput input;
        if (path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
        {
            if (refine is not null)
                refine(await NativeHostIO.RunCommandAsync(remote, RemotePrelude + "\n" + RemoteStatus(false), [path.Raw], context, token).ConfigureAwait(false));
            input = OpenRemoteInput(path, context);
        }
        else
        {
            string local = context.LocalPath(path);
            string kind = InputKind(local);
            refine?.Invoke(kind);
            input = OpenLocalInput(local, kind, context.BufferSize);
        }
        await using (input)
        {
            using StreamReader reader = new(input.Stream, IOTextEncoding, true, context.BufferSize, leaveOpen: true);
            string text = await reader.ReadToEndAsync(token).ConfigureAwait(false);
            await input.CompleteAsync(token).ConfigureAwait(false);
            return text;
        }
    }

    private static IOInput OpenInput(NativePathValue path, NativeRuntimeContext context, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (path.Connection is not { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl })
        {
            string local = context.LocalPath(path);
            return OpenLocalInput(local, InputKind(local), context.BufferSize);
        }
        return OpenRemoteInput(path, context);
    }

    private static string InputKind(string path) => OperatingSystem.IsWindows() ? "regular" : NativeFileIdentity.Kind(path, false);

    private static IOInput OpenLocalInput(string path, string kind, int bufferSize) => kind is "fifo" or "character"
        ? new(new NativePosixInputStream(path))
        : new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan));

    private static IOInput OpenRemoteInput(NativePathValue path, NativeRuntimeContext context)
    {
        ProcessStartInfo info = IOStart(path, "exec cat -- \"$p\"", false, context);
        return new(Process.Start(info) ?? throw new IOException($"Cannot start reader for '{path}'."), path);
    }

    private static ProcessStartInfo IOStart(NativePathValue path, string body, bool input, NativeRuntimeContext context)
    {
        ProcessStartInfo info = NativeTransferProtocol.CreateRemoteProcessStartInfo(path.Connection!.Value.Kind, path.Connection.Value.Address, "/bin/sh",
            ["-c", ShellProgram.Source(IOPrelude + "\n" + body), "msync-io", path.Raw], context);
        info.UseShellExecute = false;
        info.WorkingDirectory = context.WorkingDirectory;
        info.RedirectStandardInput = input;
        if (input) info.StandardInputEncoding = IOTextEncoding;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        return info;
    }

    private static async Task WriteRemoteAsync(NativePathValue path, Stream input, bool append, NativeRuntimeContext context, CancellationToken token)
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
        private readonly NativePathValue? path;
        private readonly Task<string>? error;
        private bool completed;

        internal IOInput(Stream stream) => Stream = stream;
        internal IOInput(Process process, NativePathValue path)
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
}
