using Msync.Core;
using Msync.Core.Hpir;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal readonly record struct HpirSubmissionOutcome(HpirSessionValue Value, int FailedTransfers);

// The session owns memory independently of compiled submissions and host text
// presentation. A new submission always starts a fresh guest stack.
internal sealed class HpirExecutionSession(ExecutionOptions? options = null) : IDisposable
{
    private readonly HpirSessionState state = new(options);
    private readonly Lock gate = new();
    private bool disposed;

    internal string WorkingDirectory
    {
        get { lock (gate) return state.Directory.WorkingDirectory; }
    }

    internal void ChangeWorkingDirectory(string path)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            _ = state.Directory.Change(new NativePathValue(path), new(options?.Timeline, null), publish: false);
        }
    }

    internal HpirSessionValue Execute(HpirSessionSubmission submission, CancellationToken token = default) =>
        ExecuteWithOutcome(submission, token).Value;

    internal HpirSubmissionOutcome ExecuteWithOutcome(HpirSessionSubmission submission, CancellationToken token = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            token.ThrowIfCancellationRequested();
            return ExecuteCore(submission, token);
        }
    }

    private unsafe HpirSubmissionOutcome ExecuteCore(HpirSessionSubmission submission, CancellationToken token)
    {
        byte* resultRecord = stackalloc byte[submission.ReturnBytes];
        state.Begin(submission, submission.ReturnBytes == 0 ? 0 : (nint)resultRecord);
        using RuntimeTimelineScope? display = submission.StreamsRange ? options?.Timeline?.Begin("hpir.session.presentation.range") : null;
        if (submission.StreamsRange) state.BeginPresentation();
        try
        {
            var bound = HpirBinding.Prepare(submission.Program, state.Directory.CurrentBinding(), options, token);
            HpirRunOutcome result = HpirExecutor.RunSession(bound, state, options, token);
            return new(Present(submission, result), result.FailedTransfers);
        }
        catch (Exception error) { display?.Fail(error); throw; }
        finally
        {
            display?.SetMetric("items.displayed", state.ItemsDisplayed);
            state.End();
        }
    }

    private HpirSessionValue Present(HpirSessionSubmission submission, HpirRunOutcome result)
    {
        using RuntimeTimelineScope? trace = options?.Timeline?.Begin("hpir.session.presentation");
        trace?.SetAttribute("result.kind", submission.ResultKind.ToString());
        trace?.SetMetric("result.record_bytes", submission.ReturnBytes);
        try { return state.ObserveResult(submission.ResultKind, result.Value, trace); }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    internal bool IsInitialized(int slot)
    {
        lock (gate) return !disposed && state.IsInitialized(slot);
    }

    internal HpirSessionValue? ObserveBinding(int slot)
    {
        lock (gate) return disposed ? null : state.ObserveBinding(slot);
    }

    internal HpirSessionValue? ObserveFailedBinding(int slot)
    {
        lock (gate) return disposed ? null : state.ObserveBinding(slot, recoverScopes: true);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            state.Dispose();
        }
    }
}
