using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static class InputTurns
    {
        // A deferred range and a later submission can share the same reader.
        internal static readonly ConditionalWeakTable<TextReader, SemaphoreSlim> Gates = new();
    }

    private static ValueTask<NativeValue>? StandardIO(string operation, IReadOnlyList<NativeValue> arguments,
        NativeRuntimeContext context, CancellationToken token) => operation switch
        {
            "read_line" => ReadStandardLine(context.Input, token),
            "read_stdin" => ReadStandardInput(context.Input, token),
            "stdin_lines" => ValueTask.FromResult<NativeValue>(NativeValue.FromRange(new NativeRange(StandardLines(context.Input), TypeRef.String))),
            "print" => WriteStandard(context.Output, Text(arguments[0]), false, token),
            "println" => WriteStandard(context.Output, Text(arguments[0]), true, token),
            "eprint" => WriteStandard(context.Error, Text(arguments[0]), false, token),
            "eprintln" => WriteStandard(context.Error, Text(arguments[0]), true, token),
            "flush" => FlushStandard(context.Output, token),
            "flush_error" => FlushStandard(context.Error, token),
            _ => null
        };

    private static SemaphoreSlim InputTurn(TextReader reader) => InputTurns.Gates.GetValue(reader, static _ => new(1));

    private static async ValueTask<NativeValue> ReadStandardLine(TextReader reader, CancellationToken token)
    {
        string? line = await ReadLine(reader, InputTurn(reader), token);
        return line is null ? NativeValue.Null : NativeValue.FromString(line);
    }

    private static async ValueTask<NativeValue> ReadStandardInput(TextReader reader, CancellationToken token)
    {
        SemaphoreSlim turn = InputTurn(reader);
        await turn.WaitAsync(token);
        try { return NativeValue.FromString(await reader.ReadToEndAsync(token)); }
        finally { turn.Release(); }
    }

    private static async ValueTask<string?> ReadLine(TextReader reader, SemaphoreSlim turn, CancellationToken token)
    {
        await turn.WaitAsync(token);
        try { return await reader.ReadLineAsync(token); }
        finally { turn.Release(); }
    }

    private static async IAsyncEnumerable<NativeValue> StandardLines(TextReader reader,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        SemaphoreSlim turn = InputTurn(reader);
        while (await ReadLine(reader, turn, token) is { } line)
            yield return NativeValue.FromString(line);
    }

    private static async ValueTask<NativeValue> WriteStandard(TextWriter writer, string text, bool newline,
        CancellationToken token)
    {
        if (newline) await writer.WriteLineAsync(text.AsMemory(), token);
        else await writer.WriteAsync(text.AsMemory(), token);
        return NativeValue.Null;
    }

    private static async ValueTask<NativeValue> FlushStandard(TextWriter writer, CancellationToken token)
    {
        await writer.FlushAsync(token);
        return NativeValue.Null;
    }
}
