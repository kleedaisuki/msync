using System.Runtime.ExceptionServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private readonly record struct SliceOutcome(ulong Value, Task? Waiting = null);

    private static Task? WaitGuestImport(HpirProgram program, HpirHeap? heap, HpirWork? work, HpirStack.Frame* frame,
        int pc, HpirImport import, HpirCall call, HpirDiagnostics diagnostics,
        RuntimeProfile.HpirFunctionCensus? counts, ExecutionOptions? options, CancellationToken token)
    {
        if (import.Service == HpirService.Work && import.Binding == (int)HpirWorkOperation.Join)
            return work?.FrontierWaiting(new(options?.Timeline, diagnostics.Source(frame->Function, pc), frame->Function, pc));
        if (import.Service is not (HpirService.FutureDemand or HpirService.DemandText)) return null;
        nint cell = Read<nint>(HpirStack.Data(frame), call.Arguments[0]);
        if (cell == 0) return null;
        var context = new HpirTextDemandContext(program, heap!, frame, pc, diagnostics, counts);
        if (import.Service == HpirService.DemandText) return context.Waiting(cell, work);
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(frame->Function, pc), frame->Function, pc);
        return HpirFuture.PrepareGuestDemand(cell, work, site, token, context);
    }

    private static void LaunchNumber(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirCensus? census, RuntimeProfile.HpirFunctionCensus? counts,
        int pc, HpirCall call, int target, int destination, nint* literals, nint* globals, HpirPathAnchors anchors,
        ExecutionOptions? options, CancellationToken token)
    {
        work ??= new(token);
        heap ??= new(program.Layouts);
        GuestChildren children = work.Guests ??= new(program, heap, work, literals, globals, diagnostics, census, options, token);
        heap.TrackWork(work);
        int id = children.Launch(target, active, pc, call.Arguments, anchors);
        byte* frame = HpirStack.Data(active);
        int layout = Read<int>(frame, call.Arguments[^1]);
        nint future = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
        HpirFuture.PublishGuestNumber(future, id);
        Write(frame, destination, future);
    }

    // A child is an owned VM stack, never a Task executing guest code. The
    // completion signal exists solely so another child can park on its result.
    internal sealed unsafe partial class GuestChildren(HpirProgram program, HpirHeap heap, HpirWork work,
        nint* literals, nint* globals, HpirDiagnostics diagnostics, RuntimeProfile.HpirCensus? census,
        ExecutionOptions? options, CancellationToken token) : IDisposable
    {
        private readonly Dictionary<int, Child> children = [];
        private int next;
        private Child? running;
        private RootSite parent, hostDemand;

        private readonly record struct RootSite(nint Frame, int Instruction);

        private sealed class Child(int id, HpirStack stack, CancellationTokenSource lifetime, HpirWorkSite source, HpirPathAnchors anchors)
        {
            internal readonly int Id = id;
            internal HpirStack Stack = stack;
            internal PathBatch? Batch;
            internal int Pc;
            internal nint Root;
            internal Task? Blocked;
            internal ulong Value;
            internal Exception? Failed;
            internal CancellationTokenSource Lifetime { get; } = lifetime;
            internal HpirWorkSite Source { get; } = source;
            internal HpirPathAnchors Anchors { get; } = anchors;
            internal readonly TaskCompletionSource Complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        internal CancellationToken? ActiveToken => running?.Lifetime.Token;
        internal bool Active => running is not null;
        internal object? ActiveIdentity => running;
        internal Task? Waiting(int id) => children[id].Complete.Task is { IsCompleted: false } pending ? pending : null;

        internal void SetHostDemand(HpirStack.Frame* frame, int instruction) => hostDemand = new((nint)frame, instruction);

        internal void PumpHostDemand(Task requested, CancellationToken cancellation)
        {
            if (running is not null || hostDemand.Frame == 0) return;
            while (!requested.IsCompleted && (children.Count != 0 || HasHostExports))
                if (!WaitForAdvance((HpirStack.Frame*)hostDemand.Frame, hostDemand.Instruction, cancellation, requested)) return;
        }

        internal int Launch(int target, HpirStack.Frame* caller, int pc, IReadOnlyList<int> arguments, HpirPathAnchors anchors)
        {
            int id = checked(++next);
            HpirFunction entry = program.Functions[target];
            var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(caller->Function, pc), caller->Function, pc);
            using RuntimeTimelineScope? trace = site.Begin("hpir.guest.launch");
            trace?.SetMetric("guest.id", id);
            trace?.SetAttribute("target.function.name", entry.Name);
            var child = new Child(id, new(diagnostics), CancellationTokenSource.CreateLinkedTokenSource(token), site, anchors.Fork());
            children.Add(id, child);
            if (Read<byte>(HpirStack.Data(caller), arguments[^2]) == 0)
            {
                child.Failed = new MsyncException("ranges.front requires a nonempty Range", site.Source);
                child.Complete.SetResult();
                trace?.SetAttribute("guest.state", "empty");
                return id;
            }
            byte* frame = child.Stack.Enter(target, entry.FrameBytes, -1, null);
            child.Root = (nint)child.Stack.Current;
            CopyArguments(HpirStack.Data(caller), frame, entry.Parameters, arguments);
            census?.For(target).EnterFrame();
            Advance(child, caller, pc);
            return id;
        }

        private void Advance(Child child, HpirStack.Frame* caller, int pc)
        {
            using RuntimeTimelineScope? trace = child.Source.Begin(child.Blocked is null ? "hpir.guest.slice" : "hpir.guest.resume");
            if (trace is not null)
            {
                trace.SetAttribute("target.function.name", program.Functions[child.Stack.Current->Function].Name);
                trace.SetMetric("resume.instruction", child.Pc);
                if (child.Blocked is { } dependency) trace.Link("guest.wait", dependency);
            }
            Child? previous = running;
            RootSite pause = parent;
            parent = new((nint)caller, pc);
            running = child;
            work.ResumeFrame(child.Stack.Current);
            try
            {
                BeginPathBatch(child);
                HpirHeap? sharedHeap = heap;
                HpirWork? sharedWork = work;
                MachineExecution physical = default;
                SliceOutcome outcome = Execute(program, HpirStack.Data(child.Stack.Current), literals, globals, child.Anchors,
                    ref child.Stack, ref sharedHeap, ref sharedWork, ref child.Pc, ref physical, diagnostics, census, options,
                    child.Lifetime.Token, mayPark: true);
                child.Blocked = outcome.Waiting;
                if (outcome.Waiting is { } pending)
                {
                    trace?.SetAttribute("guest.state", "parked");
                    trace?.SetMetric("wait.instruction", child.Pc);
                    trace?.Link("guest.wait", pending);
                }
                else
                {
                    child.Value = outcome.Value;
                    child.Complete.SetResult();
                    trace?.SetAttribute("guest.state", "complete");
                }
            }
            catch (Exception failure)
            {
                child.Failed = failure;
                child.Complete.SetResult();
                trace?.Fail(failure);
                trace?.SetAttribute("guest.state", "failed");
            }
            finally
            {
                try { CompletePathBatch(child, trace); }
                finally
                {
                    running = previous;
                    parent = pause;
                    work.ResumeFrame(caller);
                }
            }
        }

        internal double Demand(int id, HpirTextDemandContext caller, CancellationToken cancellation)
        {
            Child child = children[id];
            using RuntimeTimelineScope? trace = child.Source.Begin("hpir.guest.demand");
            trace?.SetMetric("guest.id", id);
            try
            {
                while (!child.Complete.Task.IsCompleted)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!WaitForAdvance(caller.Active, caller.Instruction, cancellation))
                        throw new InvalidOperationException("HPIR guest continuation has no resumable dependency");
                }
                if (heap.CanCollect && work.FrameRootCandidates(child.Root) != 0)
                    Collect(program.Functions, heap, caller.Active, caller.Instruction, -1,
                        diagnostics, census?.For(caller.Active->Function));
                work.SettleFrame(child.Root, 0, child.Source, trace);
                if (child.Failed is MsyncException { Span: not null } or OperationCanceledException)
                    ExceptionDispatchInfo.Capture(child.Failed).Throw();
                if (child.Failed is { } failure)
                    throw new MsyncException(failure.Message, child.Source.Source, failure);
                return BitConverter.UInt64BitsToDouble(child.Value);
            }
            catch (Exception failure)
            {
                trace?.Fail(failure);
                throw;
            }
            finally
            {
                if (child.Complete.Task.IsCompleted)
                {
                    children.Remove(id);
                    child.Stack.Dispose();
                    child.Lifetime.Dispose();
                }
                work.ResumeFrame(caller.Active);
            }
        }

        private bool WaitForAdvance(HpirStack.Frame* caller, int instruction, CancellationToken cancellation, Task? requested = null)
        {
            List<Task>? blocked = null;
            if (AdvanceNumberExports(caller, instruction, cancellation, ref blocked)) return true;
            foreach (Child child in children.Values)
            {
                if (child.Complete.Task.IsCompleted || child == running || child.Blocked is not { } wait) continue;
                if (wait.IsCompleted)
                {
                    // Advance can itself submit another child.
                    Advance(child, caller, instruction);
                    return true;
                }
                (blocked ??= []).Add(wait);
            }
            if (blocked is null) return false;
            if (requested is not null) blocked.Add(requested);
            _ = Task.WhenAny(blocked).WaitAsync(cancellation).GetAwaiter().GetResult();
            return true;
        }

        internal void ScanParked(ref RootCensus roots)
        {
            roots.EffectNumbers += MarkNumberExports();
            roots.Marked += roots.EffectNumbers;
            if (parent.Frame != 0)
                ScanStackRoots(program.Functions, heap, (HpirStack.Frame*)parent.Frame, parent.Instruction, diagnostics, ref roots);
            foreach (Child child in children.Values)
            {
                if (child.Batch is { } batch)
                {
                    if (heap.Mark(batch.Plan)) ++roots.Marked;
                    if (heap.Mark(batch.Data)) ++roots.Marked;
                }
                if (child != running && !child.Complete.Task.IsCompleted && child.Blocked is not null
                    && child.Batch?.AwaitingAdmission != true)
                    ScanStackRoots(program.Functions, heap, child.Stack.Current, child.Pc, diagnostics, ref roots);
            }
        }

        internal void Stop(bool abort, ref List<Exception>? failures)
        {
            using RuntimeTimelineScope? trace = options?.Timeline?.Begin("hpir.guest.settle");
            CancelNumberExports();
            int stopped = 0;
            foreach (Child child in children.Values)
            {
                if (!abort && child.Failed is { } failure && failure is not OperationCanceledException)
                    (failures ??= []).Add(failure);
                child.Batch?.Input.Paths.TrySetCanceled();
                try { child.Lifetime.Cancel(); }
                catch (Exception error) { (failures ??= []).Add(error); }
                stopped++;
            }
            trace?.SetMetric("guest.continuations_stopped", stopped);
        }

        public void Dispose()
        {
            foreach (Child child in children.Values)
            {
                child.Stack.Dispose();
                child.Lifetime.Dispose();
            }
            children.Clear();
        }
    }
}
