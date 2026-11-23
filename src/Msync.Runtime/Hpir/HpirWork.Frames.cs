using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Msync.Runtime.Hpir;

internal sealed partial class HpirWork
{
    private Dictionary<nint, FrameWork>? frameOwners;
    internal nint InnermostFrame { get; private set; }

    private struct FrameWork
    {
        internal Job? First;
        internal Job? Last;
        internal int Roots;
    }

    internal int FrameRootCandidates(nint frame) => frameOwners?.GetValueOrDefault(frame).Roots ?? 0;

    internal unsafe nint ScopeFrame(HpirStack.Frame* active) =>
        active->Parent != null || Guests?.Active == true ? (nint)active : 0;

    internal void TrackGuestJob(int id, nint frame, nint cell, HpirHeap heap)
    {
        Job job = jobs[id];
        if (frame == 0) job.Cell = cell;
        else Attach(job, frame, cell);
        heap.TrackWork(this);
    }

    private void Attach(Job job, nint frame, nint cell)
    {
        frameOwners ??= [];
        ref FrameWork owner = ref CollectionsMarshal.GetValueRefOrAddDefault(frameOwners, frame, out _);
        job.PreviousInFrame = owner.Last;
        if (owner.Last is { } last) last.NextInFrame = job;
        else owner.First = job;
        owner.Last = job;
        if (cell != 0) ++owner.Roots;
        InnermostFrame = frame;
        job.Frame = frame;
        job.Cell = cell;
    }

    private unsafe void Detach(Job job)
    {
        nint frame = job.Frame;
        if (frame == 0) return;
        ref FrameWork owner = ref CollectionsMarshal.GetValueRefOrNullRef(frameOwners!, frame);
        if (job.PreviousInFrame is { } previous) previous.NextInFrame = job.NextInFrame;
        else owner.First = job.NextInFrame;
        if (job.NextInFrame is { } next) next.PreviousInFrame = job.PreviousInFrame;
        else owner.Last = job.PreviousInFrame;
        if (job.Cell != 0) --owner.Roots;
        ClearOwner(job);
        if (owner.First is not null) return;
        frameOwners!.Remove(frame);
        if (InnermostFrame == frame) ResumeFrame(((HpirStack.Frame*)frame)->Parent);
    }

    private static void ClearOwner(Job job)
    {
        job.Frame = job.Cell = 0;
        job.PreviousInFrame = job.NextInFrame = null;
    }

    // Drop weak observations before reuse releases their addresses. A dead
    // cell is not a job root; its owning frame still settles the admission.
    internal (int Process, int FileRead) ForgetUnreachableCells(HpirHeap heap)
    {
        List<int>? released = null;
        int processes = 0, fileReads = 0;
        foreach (var (id, job) in jobs)
        {
            if (job.Cell != 0 && !heap.IsMarked(job.Cell))
            {
                if (job.Frame != 0) --CollectionsMarshal.GetValueRefOrNullRef(frameOwners!, job.Frame).Roots;
                job.Cell = 0;
                job.CellExpired = true;
            }
            if (!job.CellExpired || job.Frame != 0) continue;
            if (job.Kind == HpirWorkKind.Process && job.Completion.IsCompletedSuccessfully) processes++;
            else if (job.Kind == HpirWorkKind.FileRead && (job.Completion.IsCompletedSuccessfully
                || job.Observed && job.Completion.IsCompleted)) fileReads++;
            else continue;
            (released ??= []).Add(id);
        }
        if (released is null) return default;
        foreach (int id in released)
        {
            jobs[id].Dispose();
            jobs.Remove(id);
        }
        return (processes, fileReads);
    }

    internal void SettleFrame(nint frame, nint parent, HpirWorkSite site, RuntimeTimelineScope? trace)
    {
        if (frameOwners?.Remove(frame, out FrameWork owner) != true) return;
        List<Exception>? failures = null;
        int promoted = 0, required = 0, joined = 0, discarded = 0;
        for (Job? current = owner.First; current is { } job;)
        {
            current = job.NextInFrame;
            nint liveCell = job.Cell;
            ClearOwner(job);
            if (liveCell != 0 || job.RequiredByEffect)
            {
                if (parent != 0) Attach(job, parent, liveCell);
                else if (job.Kind == HpirWorkKind.Process) job.Cell = liveCell;
                if (job.RequiredByEffect) required++;
                promoted++;
                continue;
            }
            if (job.CancelDiscarded(ref failures)) discarded++;
            try
            {
                trace?.Link("work.submit", job.Completion);
                if (!job.Completion.IsCompleted && Guests is { HasHostExports: true } guests)
                    guests.PumpHostDemand(job.Completion, job.Scope);
                job.Completion.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (job.Discarded || job.Scope.IsCancellationRequested) { }
            catch (Exception error)
            {
                if (!job.Observed) (failures ??= []).Add(job.Attribute(error));
            }
            finally
            {
                joined++;
                jobs.Remove(job.Id);
                job.Dispose();
            }
        }
        trace?.SetMetric("work.promoted", promoted);
        trace?.SetMetric("work.promoted_effect_dependencies", required);
        trace?.SetMetric("work.joined", joined);
        trace?.SetMetric("work.discarded", discarded);
        if (failures is null) return;
        Exception errorToThrow = failures.Count == 1 ? failures[0]
            : new AggregateException($"HPIR function {site.Function} work cleanup failed.", failures);
        ExceptionDispatchInfo.Capture(errorToThrow).Throw();
    }

    internal unsafe void ResumeFrame(HpirStack.Frame* caller)
    {
        InnermostFrame = 0;
        if (frameOwners is not { Count: > 0 } owners) return;
        for (; caller != null; caller = caller->Parent)
        {
            if (!owners.ContainsKey((nint)caller)) continue;
            InnermostFrame = (nint)caller;
            return;
        }
    }
}
