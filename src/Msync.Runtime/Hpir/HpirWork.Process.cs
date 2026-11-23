using Msync.Core.Hpir;
using Msync.Core.Native;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    internal int SubmitProcess(Func<CancellationToken, ValueTask<NativeProcess>> start, HpirWorkSite site)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.work.submit");
        trace?.SetAttribute("work.kind", nameof(HpirWorkKind.Process));
        var token = AdmissionToken(HpirWorkKind.Process, trace);
        Task<NativeProcess> started;
        try
        {
            token.ThrowIfCancellationRequested();
            if (Ordering is { } ordering)
            {
                trace?.Link("work.lexical_predecessor", ordering);
                started = StartOrderedProcess(start, ordering, token);
            }
            else started = start(token).AsTask();
        }
        catch (Exception error) { started = Task.FromException<NativeProcess>(error); }
        var job = new ProcessJob(checked(++nextJob), started, token, site) { AdmissionFiber = Guests?.ActiveIdentity };
        Admit(job, site, trace);
        if (trace is not null) site.Timeline!.Register(started, trace);
        return job.Id;
    }

    private static async Task<NativeProcess> StartOrderedProcess(Func<CancellationToken, ValueTask<NativeProcess>> start,
        Task ordering, CancellationToken token)
    {
        await ordering.WaitAsync(token).ConfigureAwait(false);
        return await start(token).ConfigureAwait(false);
    }

    internal Task<NativeProcess> ProcessCompletion(int id) => ((ProcessJob)jobs[id]).Finished;

    internal NativeProcess DemandProcess(int id, bool completed, CancellationToken token, RuntimeTimelineScope? trace)
    {
        var job = (ProcessJob)jobs[id];
        return Observe(job, completed ? job.Finished : job.Started, token, trace);
    }

    internal NativeProcess CompletedProcess(int id) => ((ProcessJob)jobs[id]).Finished.GetAwaiter().GetResult();

    internal HpirSessionProcess ObserveSessionProcess(int id)
    {
        var job = (ProcessJob)jobs[id];
        int? pid = job.Started.IsCompletedSuccessfully ? job.Started.Result.ProcessId : null;
        return job.Finished.Status switch
        {
            TaskStatus.RanToCompletion => new(HpirSessionProcessState.Exited, pid, job.Finished.Result.ExitCode),
            TaskStatus.Canceled => new(HpirSessionProcessState.Cancelled, pid),
            TaskStatus.Faulted => new(HpirSessionProcessState.Failed, pid),
            _ => new(HpirSessionProcessState.Running, pid)
        };
    }

    internal Task<NativeProcess> UseProcessInEffect(int id)
    {
        var job = (ProcessJob)jobs[id];
        job.RequiredByEffect = true;
        // The dependent effect reports a failed process even without a guest wait.
        job.Observed = true;
        return job.Finished;
    }

    private sealed class ProcessJob(int id, Task<NativeProcess> started, CancellationToken token, HpirWorkSite site)
        : Job(id, HpirWorkKind.Process, null, token, site)
    {
        internal Task<NativeProcess> Started { get; } = started;
        internal Task<NativeProcess> Finished { get; } = started.IsCompletedSuccessfully
            ? started.Result.Completion ?? started : Complete(started);
        internal override Task Completion => Finished;

        private static async Task<NativeProcess> Complete(Task<NativeProcess> started)
        {
            NativeProcess process = await started.ConfigureAwait(false);
            return await process.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
