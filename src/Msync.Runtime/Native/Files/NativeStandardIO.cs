using System.Runtime.CompilerServices;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static class InputTurns
    {
        // A deferred range and a later submission can share the same reader.
        internal static readonly ConditionalWeakTable<TextReader, SemaphoreSlim> Gates = new();
    }

    private static ValueTask<Value>? StandardIO(string operation, IReadOnlyList<Value> arguments,
        RuntimeContext context, CancellationToken token) => operation switch
    {
        "read_line" => ReadStandardLine(context.Input, token),
        "read_stdin" => ReadStandardInput(context.Input, token),
        "stdin_lines" => ValueTask.FromResult<Value>(new RangeValue(StandardLines(context.Input), TypeRef.String)),
        "print" => WriteStandard(context.Output, Text(arguments[0]), false, token),
        "println" => WriteStandard(context.Output, Text(arguments[0]), true, token),
        "eprint" => WriteStandard(context.Error, Text(arguments[0]), false, token),
        "eprintln" => WriteStandard(context.Error, Text(arguments[0]), true, token),
        "flush" => FlushStandard(context.Output, token),
        "flush_error" => FlushStandard(context.Error, token),
        _ => null
    };

    private static SemaphoreSlim InputTurn(TextReader reader) => InputTurns.Gates.GetValue(reader, static _ => new(1));

    private static async ValueTask<Value> ReadStandardLine(TextReader reader, CancellationToken token)
    {
        string? line = await ReadLine(reader, InputTurn(reader), token);
        return line is null ? Value.Null : new StringValue(line);
    }

    private static async ValueTask<Value> ReadStandardInput(TextReader reader, CancellationToken token)
    {
        SemaphoreSlim turn = InputTurn(reader);
        await turn.WaitAsync(token);
        try { return new StringValue(await reader.ReadToEndAsync(token)); }
        finally { turn.Release(); }
    }

    private static async ValueTask<string?> ReadLine(TextReader reader, SemaphoreSlim turn, CancellationToken token)
    {
        await turn.WaitAsync(token);
        try { return await reader.ReadLineAsync(token); }
        finally { turn.Release(); }
    }

    private static async IAsyncEnumerable<Value> StandardLines(TextReader reader,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        SemaphoreSlim turn = InputTurn(reader);
        while (await ReadLine(reader, turn, token) is { } line)
            yield return new StringValue(line);
    }

    private static async ValueTask<Value> WriteStandard(TextWriter writer, string text, bool newline,
        CancellationToken token)
    {
        if (newline) await writer.WriteLineAsync(text.AsMemory(), token);
        else await writer.WriteAsync(text.AsMemory(), token);
        return Value.Null;
    }

    private static async ValueTask<Value> FlushStandard(TextWriter writer, CancellationToken token)
    {
        await writer.FlushAsync(token);
        return Value.Null;
    }
}
