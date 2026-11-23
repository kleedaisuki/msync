using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallLiveNumberTransfer(HpirProgram program, ref HpirHeap? heap, ref HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirCensus? census,
        RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import, HpirCall call, byte* frame,
        int destination, nint* literals, nint* globals, HpirPathAnchors anchors,
        ExecutionOptions? options, CancellationToken token)
    {
        counts?.Import(pc, import);
        nint cell = Read<nint>(frame, call.Arguments[0]);
        double value = cell == 0 ? Read<double>(frame, call.Arguments[1]) : default;
        HpirNumberEffectInput? pending = cell != 0 && !HpirFuture.ReadyNumber(cell, out value) ? new() : null;
        var input = pending is null
            ? new HpirTransferTextInput(HpirTransferTextInput.NumberText(value))
            : new HpirTransferTextInput(null, Number: pending);
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        using RuntimeTimelineScope? trace = site.Begin("hpir.effect.number_arguments");
        trace?.SetAttribute("number.carrier", cell == 0 ? "inline" : pending is null ? "published" : "pending");
        if (trace is not null && pending is not null) site.Timeline!.Register(pending.Value.Task, trace);
        int job = CallLiveTransfer(program, ref heap, ref work, active, diagnostics, counts, pc, import, call,
            frame, destination, options, token, anchors.Binding(program.Context), input);
        if (pending is null || job == 0) return;
        GuestChildren children = work!.Guests ??= new(program, heap!, work, literals, globals,
            diagnostics, census, options, token);
        heap!.TrackWork(work);
        children.ExportNumber(cell, pending, work.TransferCompletion(job), site);
    }

    internal sealed unsafe partial class GuestChildren
    {
        private sealed class NumberExport(nint cell, HpirNumberEffectInput input, Task transfer, HpirWorkSite site)
        {
            internal readonly nint Cell = cell;
            internal readonly HpirNumberEffectInput Input = input;
            internal readonly Task Transfer = transfer;
            internal readonly HpirWorkSite Site = site;
            internal Task? Waiting;
            internal bool Running;
        }

        private List<NumberExport>? numberExports;
        internal bool HasNumberExports => numberExports is { Count: > 0 };

        internal void ExportNumber(nint cell, HpirNumberEffectInput input, Task transfer, HpirWorkSite site)
        {
            using RuntimeTimelineScope? trace = site.Begin("hpir.guest.effect_number.publish");
            (numberExports ??= []).Add(new(cell, input, transfer, site));
            if (trace is not null)
            {
                trace.SetMetric("guest.number_exports", numberExports.Count);
                trace.Link("effect.transfer", transfer);
            }
        }

        private bool AdvanceNumberExports(HpirStack.Frame* caller, int instruction, CancellationToken cancellation,
            ref List<Task>? blocked)
        {
            if (numberExports is not { Count: > 0 } exports) return false;
            for (int i = 0; i < exports.Count; ++i)
            {
                NumberExport export = exports[i];
                if (export.Running) continue;
                if (export.Transfer.IsCompleted)
                {
                    exports.RemoveAt(i);
                    return true;
                }
                if (!export.Input.Requested.Task.IsCompleted)
                {
                    (blocked ??= []).Add(export.Input.Requested.Task);
                    blocked.Add(export.Transfer);
                    continue;
                }
                if (export.Waiting is { IsCompleted: false } dependency)
                {
                    (blocked ??= []).Add(dependency);
                    blocked.Add(export.Transfer);
                    continue;
                }
                AdvanceNumberExport(export, caller, instruction, cancellation);
                if (export.Waiting is null) exports.Remove(export);
                return true;
            }
            return false;
        }

        private void AdvanceNumberExport(NumberExport export, HpirStack.Frame* caller, int instruction, CancellationToken cancellation)
        {
            using RuntimeTimelineScope? trace = export.Site.Begin("hpir.guest.effect_number.advance");
            trace?.Link("guest.effect_number", export.Input.Value.Task);
            if (export.Waiting is { } dependency) trace?.Link("guest.wait", dependency);
            export.Running = true;
            export.Waiting = null;
            try
            {
                var text = new HpirTextDemandContext(program, heap, caller, instruction, diagnostics, census?.For(caller->Function));
                if (HpirFuture.PrepareGuestDemand(export.Cell, work, export.Site, cancellation, text) is { } waiting)
                {
                    export.Waiting = waiting;
                    trace?.SetAttribute("guest.state", "parked");
                    trace?.Link("guest.wait", waiting);
                    return;
                }
                export.Input.Value.SetResult(HpirFuture.Demand<double>(export.Cell, work, export.Site, cancellation, text));
                trace?.SetAttribute("guest.state", "complete");
            }
            catch (Exception error)
            {
                export.Input.Value.TrySetException(error);
                trace?.Fail(error);
            }
            finally { export.Running = false; }
        }

        private int MarkNumberExports()
        {
            if (numberExports is not { Count: > 0 } exports) return 0;
            int marked = 0;
            foreach (NumberExport export in exports)
                if ((export.Running || !export.Transfer.IsCompleted) && heap.Mark(export.Cell)) ++marked;
            return marked;
        }

        internal void DrainHostExports(HpirStack.Frame* frame, int instruction, CancellationToken cancellation)
        {
            if (!HasHostExports) return;
            // The closing frame is at Return, not a stack-map safepoint.
            using var collection = heap.DeferCollection();
            SetHostDemand(frame, instruction);
            using RuntimeTimelineScope? trace = options?.Timeline?.Begin(pathBatches == 0
                ? "hpir.guest.effect_number.drain" : "hpir.guest.host_exports.drain");
            trace?.SetMetric("guest.number_exports", numberExports?.Count ?? 0);
            trace?.SetMetric("guest.path_batches", pathBatches);
            while (HasHostExports)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!WaitForAdvance(frame, instruction, cancellation))
                    throw new InvalidOperationException("HPIR effect delivery has no resumable dependency");
            }
        }

        private void CancelNumberExports()
        {
            if (numberExports is not { Count: > 0 } exports) return;
            foreach (NumberExport export in exports) export.Input.Value.TrySetCanceled();
            exports.Clear();
        }
    }
}
