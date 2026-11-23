using System.Text;
using Msync.Runtime.Native;

namespace Msync.Cli;

// One lazy buffer owns command and program reads from the redirected stdin stream.
internal sealed class RedirectedTextInput : TextReader, IAsyncLineInput
{
    // Amortize async pipe refills without acquiring a second stdin reader.
    private const int BufferBytes = 8 * 1024;
    private StreamReader? reader;
    private StreamReader Reader => reader ??= new(Console.OpenStandardInput(),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true),
        detectEncodingFromByteOrderMarks: false, bufferSize: BufferBytes);

    public static RedirectedTextInput? Open() => Console.IsInputRedirected ? new() : null;

    public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) =>
        Reader.ReadLineAsync(cancellationToken);

    public override Task<string> ReadToEndAsync(CancellationToken cancellationToken) =>
        Reader.ReadToEndAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing) reader?.Dispose();
        base.Dispose(disposing);
    }
}
