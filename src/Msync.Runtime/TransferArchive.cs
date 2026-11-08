using System.Diagnostics;
using System.Formats.Tar;
using Msync.Core;

namespace Msync.Runtime;

public static partial class TransferEngine
{
    private static bool RequiresArchiveReceipt(ConnectionValue connection) => OperatingSystem.IsWindows() && connection.Kind == ConnectionKind.Wsl;

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
}
