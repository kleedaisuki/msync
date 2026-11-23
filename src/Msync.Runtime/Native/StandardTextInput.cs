namespace Msync.Runtime.Native;

// ReadLineAsync may finish inline but must not block the caller for input.
internal interface IAsyncLineInput { }

internal static class StandardTextInput
{
    // A deferred range and another submission can share the same reader.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextReader, SemaphoreSlim> turns = new();

    internal static SemaphoreSlim Turn(TextReader reader) => turns.GetValue(reader, static _ => new(1));

    internal static ValueTask<string?> ReadLine(TextReader reader, RuntimeTimeline? timeline, CancellationToken token) =>
        ReadLine(reader, Turn(reader), timeline, token);

    internal static async ValueTask<string> ReadToEnd(TextReader reader, CancellationToken token)
    {
        SemaphoreSlim turn = Turn(reader);
        await turn.WaitAsync(token).ConfigureAwait(false);
        Task<string> read = StartRead(async () =>
        {
            try { token.ThrowIfCancellationRequested(); return await reader.ReadToEndAsync(token).ConfigureAwait(false); }
            finally { turn.Release(); }
        });
        return await read.WaitAsync(token).ConfigureAwait(false);
    }

    internal static async ValueTask<string?> ReadLine(TextReader reader, SemaphoreSlim turn, RuntimeTimeline? timeline,
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
            if (!inline)
                return await StartRead(() => ReadOwned(reader, turn, token, null).AsTask()).WaitAsync(token).ConfigureAwait(false);

            ValueTask<string?> read = ReadOwned(reader, turn, token, span);
            return read.IsCompletedSuccessfully ? read.Result : await read.AsTask().WaitAsync(token).ConfigureAwait(false);
        }
        catch (Exception error) { span?.Fail(error); throw; }
    }

    // Arbitrary TextReaders may block inside ReadLineAsync. Start off the evaluator.
    private static async ValueTask<string?> ReadOwned(TextReader reader, SemaphoreSlim turn, CancellationToken token,
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

    private static Task<T> StartRead<T>(Func<Task<T>> read)
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(read);
        using AsyncFlowControl flow = ExecutionContext.SuppressFlow();
        return Task.Run(read);
    }
}
