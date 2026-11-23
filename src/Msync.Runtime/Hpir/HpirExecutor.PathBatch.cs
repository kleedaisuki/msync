using System.Runtime.ExceptionServices;
using Msync.Core.Hpir;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void LaunchPathBatch(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirCensus? census, RuntimeProfile.HpirFunctionCensus? counts,
        int pc, HpirCall call, int target, int destination, nint* literals, nint* globals, HpirPathAnchors anchors,
        ExecutionOptions? options, CancellationToken token)
    {
        work ??= new(token);
        HpirHeap memory = heap ??= new(program.Layouts);
        byte* frame = HpirStack.Data(active);
        nint plan = Read<nint>(frame, call.Arguments[0]);
        nint snapshot = *(nint*)plan;
        int length = checked((int)*(ulong*)snapshot);
        var path = PathOperand(frame, call.Arguments[1], false, default).ToHost(contextual: true);
        int layout = Read<int>(frame, call.Arguments[2]), completion = Read<int>(frame, call.Arguments[3]);
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        int inherited = work.Mark;
        nint cell = destination < 0 ? 0 : Allocate(program, ref heap, active, pc, completion, 1, diagnostics, counts, clear: false);
        var input = new HpirPathBatchInput();
        bool execute = length != 0 && options?.DryRun != true;
        if (!execute) input.Paths.SetResult([]);
        var job = work.SubmitPathBatch(input, path, execute, options, anchors.Binding(program.Context), site, out Task? prerequisites);
        if (cell != 0)
        {
            HpirFuture.PublishBool(cell, job);
            Write(frame, destination, cell);
        }
        work.TrackTransfer(job.JobId, (nint)active, cell, heap);
        if (!execute) return;
        try
        {
            nint data = AllocatePayload(program, memory, active, pc, layout, length, diagnostics, counts, clear: true);
            GuestChildren guests = work.Guests ??= new(program, memory, work, literals, globals, diagnostics, census, options, token);
            memory.TrackWork(work);
            guests.LaunchPathBatch(target, active, pc, plan, data, length, inherited, input, prerequisites, site, anchors);
        }
        catch (Exception error)
        {
            input.Paths.TrySetException(error);
            throw;
        }
    }

    internal sealed unsafe partial class GuestChildren
    {
        private sealed record PathBatch(nint Plan, nint Data, int Length, int Inherited, Child? Owner,
            HpirPathBatchInput Input)
        {
            internal bool AwaitingAdmission = true;
        }

        private int pathBatches;
        internal bool HasHostExports => HasNumberExports || pathBatches != 0;
        internal bool KeepsFrontier(int job, object? owner) => running is not { } active || InFrontier(active, job, owner);

        private static bool InFrontier(Child child, int job, object? owner) => ReferenceEquals(owner, child)
            || child.Batch is not { } batch || job <= batch.Inherited
                && (batch.Owner is null || InFrontier(batch.Owner, job, owner));

        internal void LaunchPathBatch(int target, HpirStack.Frame* caller, int pc, nint plan, nint data, int length,
            int inherited, HpirPathBatchInput input, Task? prerequisites, HpirWorkSite site, HpirPathAnchors anchors)
        {
            int id = checked(++next);
            HpirFunction entry = program.Functions[target];
            using RuntimeTimelineScope? trace = site.Begin("hpir.guest.path_batch_launch");
            trace?.SetMetric("guest.id", id);
            trace?.SetMetric("guest.path_items", length);
            trace?.SetMetric("guest.inherited_jobs_through", inherited);
            if (prerequisites is not null) trace?.Link("guest.admission", prerequisites);

            var child = new Child(id, new(diagnostics), CancellationTokenSource.CreateLinkedTokenSource(token), site, anchors.Fork())
            {
                Blocked = prerequisites,
                Batch = new(plan, data, length, inherited, running, input)
            };
            byte* frame = child.Stack.Enter(target, entry.FrameBytes, -1, null);
            child.Root = (nint)child.Stack.Current;
            Write(frame, entry.Parameters[0].Offset, plan);
            Write(frame, entry.Parameters[1].Offset, data);
            children.Add(id, child);
            ++pathBatches;
            census?.For(target).EnterFrame();
            // A previously admitted dependency may be a guest child. Let the
            // existing owner service it; never start this worker twice.
            if (prerequisites is null || prerequisites.IsCompleted) Advance(child, caller, pc);
        }

        private void BeginPathBatch(Child child)
        {
            if (child.Batch is not { AwaitingAdmission: true } batch) return;
            child.Blocked?.GetAwaiter().GetResult();
            batch.AwaitingAdmission = false;
        }

        private void CompletePathBatch(Child child, RuntimeTimelineScope? trace)
        {
            if (child.Batch is not { } batch || !child.Complete.Task.IsCompleted) return;
            try
            {
                work.SettleFrame(child.Root, 0, child.Source, trace);
                if (child.Failed is { } failure) ExceptionDispatchInfo.Capture(failure).Throw();
                var paths = new NativePathValue[batch.Length];
                for (int index = 0; index < paths.Length; ++index)
                    paths[index] = ((HpirPathValue*)batch.Data)[index].ToHost(contextual: true);
                trace?.SetMetric("guest.path_items_delivered", paths.Length);
                batch.Input.Paths.SetResult(paths);
            }
            catch (Exception failure)
            {
                batch.Input.Paths.TrySetException(failure);
                trace?.Fail(failure);
            }
            finally
            {
                children.Remove(child.Id);
                --pathBatches;
                child.Stack.Dispose();
                child.Lifetime.Dispose();
            }
        }
    }
}

internal sealed class HpirPathBatchInput
{
    internal readonly TaskCompletionSource<NativePathValue[]> Paths = new(TaskCreationOptions.RunContinuationsAsynchronously);
}
