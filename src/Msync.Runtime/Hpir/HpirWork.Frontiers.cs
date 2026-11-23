using System.Runtime.ExceptionServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    private Task? publishedFrontier;
    private int finiteThrough;

    internal int Mark => nextJob;
    internal Task? Ordering => publishedFrontier is { IsCompletedSuccessfully: false } pending ? pending : null;

    internal void PublishFrontier(int mark, HpirWorkSite site, RuntimeTimelineScope? trace)
    {
        List<Task>? dependencies = null;
        Task? previous = Ordering;
        int end = nextJob;
        mark = Math.Clamp(mark, 0, end);
        // Finite work is considered once per execution. An outer region
        // opened before an inner publication can still own older flows.
        int first = Math.Min(finiteThrough, mark);
        int finite = 0, connected = 0;
        for (int id = first; id < end;)
        {
            if (!jobs.TryGetValue(++id, out Job? job) || job.Completion.IsCompletedSuccessfully) continue;
            if (job.Kind != HpirWorkKind.Transfer) continue;
            if (job.ProcessDependency is null)
            {
                if (id <= finiteThrough) continue;
                ++finite;
            }
            else
            {
                if (id <= mark || !ReferenceEquals(job.AdmissionFiber, Guests?.ActiveIdentity)) continue;
                ++connected;
            }
            trace?.Link("work.predecessor", job.Completion);
            (dependencies ??= []).Add(job.Completion);
        }
        finiteThrough = end;
        trace?.SetMetric("work.examined", end - first);
        trace?.SetMetric("work.finite", finite);
        trace?.SetMetric("work.process_flows", connected);
        if (previous is not null)
        {
            trace?.Link("work.predecessor", previous);
            if (dependencies is null) return;
            dependencies.Add(previous);
        }
        if (dependencies is null)
        {
            publishedFrontier = null;
            return;
        }
        Task publication = dependencies.Count == 1 ? dependencies[0] : Task.WhenAll(dependencies);
        publishedFrontier = publication;
        if (trace is not null && dependencies.Count > 1) site.Timeline!.Register(publication, trace);
    }

    internal void JoinFrontier(Task<NativeProcess>? process, CancellationToken token, RuntimeTimelineScope? trace)
    {
        // Copy the currently selected owners before pumping guest work;
        // resumption can mutate jobs. Afterwards follow just new admissions.
        int through = nextJob;
        var active = jobs.Values.Where(Selected).ToArray();
        int joined = 0, waited = 0, followed = 0;
        List<Exception>? failures = null;
        foreach (Job job in active) Await(job);
        while (through != nextJob)
        {
            int end = nextJob;
            while (through < end)
            {
                if (!jobs.TryGetValue(++through, out Job? admitted) || !Selected(admitted)) continue;
                ++followed;
                Await(admitted);
            }
        }
        trace?.SetMetric("work.joined", joined);
        trace?.SetMetric("work.waited", waited);
        if (followed != 0) trace?.SetMetric("work.followed_admissions", followed);
        if (failures is { Count: > 0 }) ExceptionDispatchInfo.Capture(failures.Count == 1 ? failures[0]
            : new AggregateException("HPIR acquired work failed.", failures)).Throw();

        bool Selected(Job job) => process is null ? InGuestFrontier(job) : ReferenceEquals(job.ProcessDependency, process);

        void Await(Job job)
        {
            ++joined;
            trace?.Link("work.submit", job.Completion);
            try
            {
                if (!job.Completion.IsCompleted)
                {
                    ++waited;
                    Guests?.PumpHostDemand(job.Completion, token);
                }
                job.Completion.WaitAsync(token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                if (!job.Observed) (failures ??= []).Add(job.Attribute(error));
                job.Observed = true;
            }
        }
    }

    internal Task? FrontierWaiting(HpirWorkSite site)
    {
        List<Task>? pending = null;
        foreach (Job job in jobs.Values)
            if (!job.Completion.IsCompleted && InGuestFrontier(job))
                (pending ??= []).Add(job.Completion);
        if (pending is null) return null;
        using RuntimeTimelineScope? trace = site.Begin("hpir.work.guest_frontier_wait");
        trace?.SetMetric("work.waited", pending.Count);
        if (trace is not null)
            foreach (Task dependency in pending) trace.Link("work.submit", dependency);
        return pending.Count == 1 ? pending[0] : Task.WhenAll(pending);
    }

    private bool InGuestFrontier(Job job) => Guests?.KeepsFrontier(job.Id, job.AdmissionFiber) != false;

    private static void AddOrdering(HashSet<Task> predecessors, Task? ordering, RuntimeTimelineScope? trace)
    {
        if (ordering is not null && predecessors.Add(ordering)) trace?.Link("work.lexical_predecessor", ordering);
    }

    private void ResetWorkFrontier()
    {
        publishedFrontier = null;
        finiteThrough = nextJob;
    }
}
