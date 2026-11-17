using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

// ReadLineAsync may finish inline but must not block the caller for input.
internal interface IAsyncLineInput { }

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
            "read_line" => ReadStandardLine(context.Input, context.Timeline, token),
            "read_stdin" => ReadStandardInput(context.Input, token),
            "stdin_lines" => ValueTask.FromResult<NativeValue>(NativeValue.FromRange(new NativeRange(StandardLines(context.Input, context.Timeline), TypeRef.String))),
            "print" => WriteStandard(context.Output, Text(arguments[0]), false, token),
            "println" => WriteStandard(context.Output, Text(arguments[0]), true, token),
            "eprint" => WriteStandard(context.Error, Text(arguments[0]), false, token),
            "eprintln" => WriteStandard(context.Error, Text(arguments[0]), true, token),
            "flush" => FlushStandard(context.Output, token),
            "flush_error" => FlushStandard(context.Error, token),
            _ => null
        };

    private static SemaphoreSlim InputTurn(TextReader reader) => InputTurns.Gates.GetValue(reader, static _ => new(1));

    private static async ValueTask<NativeValue> ReadStandardLine(TextReader reader, RuntimeTimeline? timeline,
        CancellationToken token)
    {
        string? line = await ReadLine(reader, InputTurn(reader), timeline, token).ConfigureAwait(false);
        return line is null ? NativeValue.Null : NativeValue.FromString(line);
    }

    private static async ValueTask<NativeValue> ReadStandardInput(TextReader reader, CancellationToken token)
    {
        SemaphoreSlim turn = InputTurn(reader);
        await turn.WaitAsync(token);
        Task<string> read = StartInputRead(async () =>
        {
            try { token.ThrowIfCancellationRequested(); return await reader.ReadToEndAsync(token).ConfigureAwait(false); }
            finally { turn.Release(); }
        });
        return NativeValue.FromString(await read.WaitAsync(token).ConfigureAwait(false));
    }

    private static async ValueTask<string?> ReadLine(TextReader reader, SemaphoreSlim turn, RuntimeTimeline? timeline,
        CancellationToken token)
    {
        using RuntimeTimelineScope? span = timeline?.Begin("input.read_line");
        bool inline = reader is IAsyncLineInput;
        span?.SetAttribute("input.dispatch", inline ? "direct" : "thread_pool");
        try
        {
            Task admission = turn.WaitAsync(token);
            span?.SetMetric("input.turn_pending", admission.IsCompleted ? 0 : 1);
            await admission.ConfigureAwait(false);
            if (!inline) return await QueueLineRead(reader, turn, token).WaitAsync(token).ConfigureAwait(false);

            ValueTask<string?> read = ReadOwnedLine(reader, turn, token, span);
            return read.IsCompletedSuccessfully ? read.Result : await read.AsTask().WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) { span?.Fail(error); throw; }
    }

    // Arbitrary TextReaders may block inside ReadLineAsync. Keep their start off the evaluator.
    private static Task<string?> QueueLineRead(TextReader reader, SemaphoreSlim turn, CancellationToken token) =>
        StartInputRead(() => ReadOwnedLine(reader, turn, token, null).AsTask());

    private static async ValueTask<string?> ReadOwnedLine(TextReader reader, SemaphoreSlim turn, CancellationToken token,
        RuntimeTimelineScope? span)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ValueTask<string?> read = reader.ReadLineAsync(token);
            span?.SetMetric("input.reader_pending", read.IsCompleted ? 0 : 1);
            return await read.ConfigureAwait(false);
        }
        finally { turn.Release(); }
    }

    private static Task<T> StartInputRead<T>(Func<Task<T>> read)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(read);
        using AsyncFlowControl flow = ExecutionContext.SuppressFlow();
        return Task.Run(read);
    }

    private static async IAsyncEnumerable<NativeValue> StandardLines(TextReader reader, RuntimeTimeline? timeline,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        SemaphoreSlim turn = InputTurn(reader);
        while (await ReadLine(reader, turn, timeline, token).ConfigureAwait(false) is { } line)
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
