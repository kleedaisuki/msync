using System.Runtime.ExceptionServices;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Hpir;

internal enum HpirWorkKind : byte { Process, Input, FileRead, Transfer, Stream, FileWrite }

internal readonly record struct HpirWorkSite(RuntimeTimeline? Timeline, SourceSpan? Source, int Function = -1, int Instruction = -1)
{
    internal RuntimeTimelineScope? Begin(string name)
    {
        if (Timeline is null) return null;
        RuntimeTimelineScope scope = Timeline.Begin(name);
        scope.SetSource(Source);
        if (Function >= 0) scope.SetMetric("function.id", Function);
        if (Instruction >= 0) scope.SetMetric("instruction", Instruction);
        return scope;
    }
}

internal readonly record struct HpirHostResult<T>(int JobId, T Value)
{
    internal bool Ready => JobId == 0;
    internal static HpirHostResult<T> Pending(int id) => new(id, default!);
}

// The VM thread owns admission, observation and disposal. A worker receives
// only copied host arguments and a cancellation token, never a guest address.
internal sealed partial class HpirWork(CancellationToken executionToken, bool sessionWork = false)
{
    private readonly SortedDictionary<int, Job> jobs = [];
    private CancellationTokenSource? lifetime;
    private CancellationTokenSource? sessionLifetime;
    private CancellationToken submissionToken;
    private int nextJob;

    internal int Outstanding => jobs.Count;
    internal bool SessionWork { get; } = sessionWork;
    internal HpirExecutor.GuestChildren? Guests { get; set; }

    internal void BeginSubmission(CancellationToken token)
    {
        submissionToken = token;
        failedTransfers = 0;
    }

    internal Task? Waiting(int id)
    {
        Task completion = jobs[id].Completion;
        return completion.IsCompleted ? null : completion;
    }

    internal HpirHostResult<T> Submit<T>(HpirWorkKind kind, Func<CancellationToken, ValueTask<T>> start, HpirWorkSite site,
        bool retainResult = false, Task<NativeProcess>? processDependency = null)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.work.submit");
        trace?.SetAttribute("work.kind", kind.ToString());
        bool discardable = kind is HpirWorkKind.Input or HpirWorkKind.FileRead or HpirWorkKind.Stream;
        var inherited = AdmissionToken(kind, trace);
        var lifetimeOfJob = discardable ? CancellationTokenSource.CreateLinkedTokenSource(inherited) : null;
        var token = lifetimeOfJob?.Token ?? inherited;
        ValueTask<T> work;
        try
        {
            token.ThrowIfCancellationRequested();
            work = start(token);
        }
        catch (Exception error) { work = ValueTask.FromException<T>(error); }
        if (!retainResult && work.IsCompletedSuccessfully)
        {
            T value = work.Result;
            lifetimeOfJob?.Dispose();
            trace?.SetAttribute("work.outcome", "ready");
            return new(0, value);
        }

        int id = checked(++nextJob);
        var job = new Job<T>(id, kind, work.AsTask(), lifetimeOfJob, inherited, site, retainResult);
        job.ProcessDependency = processDependency;
        job.AdmissionFiber = Guests?.ActiveIdentity;
        Admit(job, site, trace);
        return new(id, default!);
    }

    private CancellationToken AdmissionToken(HpirWorkKind kind, RuntimeTimelineScope? trace)
    {
        bool persistent = SessionWork && kind is HpirWorkKind.Process or HpirWorkKind.FileRead;
        var owner = persistent ? sessionLifetime ??= new()
            : lifetime ??= CancellationTokenSource.CreateLinkedTokenSource(SessionWork ? submissionToken : executionToken);
        if (persistent) trace?.SetAttribute("work.owner", "session");
        return Guests?.ActiveToken ?? owner.Token;
    }

    private void Admit(Job job, HpirWorkSite site, RuntimeTimelineScope? trace)
    {
        jobs.Add(job.Id, job);
        if (trace is not null)
        {
            trace.SetMetric("work.id", job.Id);
            trace.SetMetric("work.outstanding", jobs.Count);
            site.Timeline!.Register(job.Completion, trace);
        }
    }

    internal T Demand<T>(int id, CancellationToken token, RuntimeTimelineScope? trace)
    {
        var job = (Job<T>)jobs[id];
        T result = Observe(job, job.Result, token, trace);
        if (!job.RetainResult && jobs.Remove(id))
        {
            Detach(job);
            job.Dispose();
        }
        return result;
    }

    private T Observe<T>(Job job, Task<T> result, CancellationToken token, RuntimeTimelineScope? trace)
    {
        trace?.Link("work.submit", result);
        try
        {
            if (!result.IsCompleted) Guests?.PumpHostDemand(result, token);
            return result.WaitAsync(token).GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            job.Observed |= result.IsCompleted && (error is not OperationCanceledException || result.IsCanceled);
            ExceptionDispatchInfo.Capture(job.Attribute(error)).Throw();
            throw;
        }
    }

    internal Task<T> UseInEffect<T>(int id)
    {
        var job = (Job<T>)jobs[id];
        job.RequiredByEffect = true;
        // The dependent effect now owns reporting the source failure even
        // if no guest value ever observes this completion.
        job.Observed = true;
        return job.Result;
    }

    // Join admitted process/transfer work. Discardable work is cancelled
    // when nobody outside this execution can retain it. When unwinding, cancel
    // all work but still join its host cleanup before releasing VM resources.
    internal Exception? Settle(bool abort, HpirWorkSite site, bool keepSessionWork = false)
    {
        using RuntimeTimelineScope? trace = site.Begin("hpir.work.settle");
        trace?.SetMetric("work.outstanding", jobs.Count);
        List<Exception>? failures = null;
        Guests?.Stop(abort, ref failures);
        int cancelled = 0, joined = 0, retained = 0, pending = 0, reads = 0;
        if (abort && lifetime is { } owner)
        {
            try { owner.Cancel(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (abort && !keepSessionWork && sessionLifetime is { } session)
        {
            try { session.Cancel(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        int abandonedWrites = AbortFileBytes();
        if (abandonedWrites != 0) trace?.SetMetric("file_byte_writes.abandoned", abandonedWrites);
        CloseTextRanges(ref failures, abort, trace);
        CloseDirectories(ref failures, abort, trace);
        foreach (var (_, job) in jobs)
            if (!Keep(job) && job.CancelDiscarded(ref failures, force: abort && SessionWork && job.Kind == HpirWorkKind.FileRead)) cancelled++;
        List<int>? settled = null;
        foreach (var (_, job) in jobs)
        {
            if (Keep(job))
            {
                nint cell = job.Cell;
                ClearOwner(job);
                job.Cell = cell;
                job.Published = false;
                if (job.Kind == HpirWorkKind.FileRead) ++reads;
                else
                {
                    ++retained;
                    if (!job.Completion.IsCompleted) ++pending;
                }
                continue;
            }
            try
            {
                trace?.Link("work.submit", job.Completion);
                job.Completion.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (abort || job.Discarded || job.Scope.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!job.Observed) (failures ??= []).Add(job.Attribute(error));
            }
            finally
            {
                ++joined;
                job.Dispose();
                if (keepSessionWork) (settled ??= []).Add(job.Id);
            }
        }
        if (!keepSessionWork) jobs.Clear();
        else if (settled is not null)
            foreach (int id in settled) jobs.Remove(id);
        frameOwners?.Clear();
        InnermostFrame = 0;
        Guests?.Dispose();
        Guests = null;
        ResetFileTransfers(reads != 0);
        ResetWorkFrontier();
        lifetime?.Dispose();
        lifetime = null;
        submissionToken = default;
        if (!keepSessionWork)
        {
            sessionLifetime?.Dispose();
            sessionLifetime = null;
        }
        trace?.SetMetric("work.joined", joined);
        trace?.SetMetric("work.discarded", cancelled);
        if (keepSessionWork)
        {
            trace?.SetMetric("work.session_processes", retained);
            trace?.SetMetric("work.session_pending", pending);
            trace?.SetMetric("work.session_file_reads", reads);
        }
        Exception? failure = failures?.Count switch
        {
            null or 0 => null,
            1 => failures[0],
            _ => new AggregateException("HPIR work cleanup failed.", failures)
        };
        if (failure is not null) trace?.Fail(failure);
        return failure;

        bool Keep(Job job) => keepSessionWork && (job.Kind == HpirWorkKind.Process || job.Published);
    }

    internal bool RetainFileRead(int id, nint address)
    {
        Job job = jobs[id];
        if (job.Published) return false;
        job.Published = true;
        if (job.Cell == 0) job.Cell = address;
        return true;
    }

    private abstract class Job(int id, HpirWorkKind kind, CancellationTokenSource? lifetime, CancellationToken scope,
        HpirWorkSite site) : IDisposable
    {
        internal abstract Task Completion { get; }
        internal int Id { get; } = id;
        internal HpirWorkKind Kind { get; } = kind;
        internal CancellationToken Scope { get; } = scope;
        internal Job? PreviousInFrame { get; set; }
        internal Job? NextInFrame { get; set; }
        internal nint Frame { get; set; }
        internal nint Cell { get; set; }
        internal bool CellExpired { get; set; }
        internal bool Observed { get; set; }
        internal bool Published { get; set; }
        internal bool RequiredByEffect { get; set; }
        internal Task<NativeProcess>? ProcessDependency { get; set; }
        internal object? AdmissionFiber { get; set; }
        internal bool Discarded { get; private set; }
        internal Exception Attribute(Exception error) => error is OperationCanceledException or MsyncException { Span: not null }
            ? error : new MsyncException($"HPIR {Kind} work failed: {error.Message}", site.Source, error);

        internal bool CancelDiscarded(ref List<Exception>? failures, bool force = false)
        {
            if (lifetime is null || !force && RequiredByEffect || Completion.IsCompleted) return false;
            Discarded = true;
            try { lifetime.Cancel(); }
            catch (Exception error) { (failures ??= []).Add(Attribute(error)); }
            return true;
        }

        public void Dispose() => lifetime?.Dispose();
    }

    private sealed class Job<T>(int id, HpirWorkKind kind, Task<T> task, CancellationTokenSource? lifetime,
        CancellationToken scope, HpirWorkSite site, bool retainResult) : Job(id, kind, lifetime, scope, site)
    {
        internal Task<T> Result => task;
        internal bool RetainResult { get; } = retainResult;
        internal override Task Completion => task;
    }
}
