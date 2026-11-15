using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal enum NativeSubmissionKind : byte { Process, Input, FileRead, Transfer, Stream }

internal sealed class NativeSubmission(NativeSubmissionKind kind, CancellationToken token, SourceSpan? source, RuntimeTimeline? timeline)
{
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
    private readonly TaskCompletionSource<NativeValue> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool finished;
    private bool aborted;
    internal bool IsAborted => Volatile.Read(ref aborted);

    internal Task<NativeValue> Completion => result.Task;
    internal NativeSubmissionKind Kind => kind;

    internal void Start(Func<CancellationToken, ValueTask<NativeValue>> launch, Action? onCompleted = null)
    {
        if (kind == NativeSubmissionKind.Input) _ = RunAsync(launch, onCompleted);
        else _ = Task.Run(() => RunAsync(launch, onCompleted));
    }

    private async Task RunAsync(Func<CancellationToken, ValueTask<NativeValue>> launch, Action? onCompleted)
    {
        CancellationToken cancellation = lifetime.Token;
        using RuntimeTimelineScope? operation = timeline?.Begin(kind switch { NativeSubmissionKind.Process => "process.background", NativeSubmissionKind.Input => "input.background", NativeSubmissionKind.FileRead => "file_read.background", NativeSubmissionKind.Stream => "stream.background", _ => "transfer.background" });
        operation?.SetSource(source);
        try
        {
            // Launch owns admission cleanup even when cancellation won the queue race.
            NativeValue value = await launch(cancellation).ConfigureAwait(false);
            result.TrySetResult(value);
            if (kind == NativeSubmissionKind.Process)
            {
                NativeProcess completed = await value.AsProcess().WaitAsync(cancellation).ConfigureAwait(false);
                operation?.SetMetric("process.exit_code", completed.ExitCode);
            }
        }
        catch (OperationCanceledException error)
        {
            operation?.Fail(error);
            result.TrySetCanceled(cancellation);
        }
        catch (Exception error)
        {
            operation?.Fail(error);
            result.TrySetException(error is MsyncException { Span: not null } ? error
                : new MsyncException(error.Message, source, error));
        }
        finally
        {
            lock (gate)
            {
                finished = true;
                lifetime.Dispose();
            }
            onCompleted?.Invoke();
        }
    }

    internal void RecordOwnership(bool promoted, bool job, int futures, int processes)
    {
        using RuntimeTimelineScope? decision = timeline?.Begin("work.disposition");
        decision?.SetSource(source);
        decision?.SetAttribute("work.kind", kind.ToString());
        decision?.SetMetric("promoted", promoted ? 1 : 0);
        decision?.SetMetric("owner.job", job ? 1 : 0);
        decision?.SetMetric("reachable.futures", futures);
        decision?.SetMetric("reachable.processes", processes);
    }

    internal void RecordEscape(SourceSpan? childSource, Task? child, bool process)
    {
        using RuntimeTimelineScope? edge = timeline?.Begin("work.escape");
        edge?.SetSource(childSource ?? source);
        edge?.Link("creator", Completion);
        if (child is not null) edge?.Link("escaped", child);
        edge?.SetMetric(process ? "process" : "future", 1);
    }

    internal void Abort()
    {
        lock (gate)
        {
            if (finished) return;
            aborted = true;
            if (kind != NativeSubmissionKind.Process || !Completion.IsCompletedSuccessfully) { lifetime.Cancel(); return; }
            NativeProcess process = Completion.Result.AsProcess();
            if (process.Control is { IsRunning: true } control) control.Kill();
            else if (process.Completion is { IsCompleted: false }) process.AbortOwnedTransport?.Invoke();
        }
    }
}
