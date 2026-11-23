using System.Runtime.CompilerServices;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void FinishFrameWork(HpirProgram program, HpirHeap? heap, HpirWork work, HpirStack.Frame* frame,
        int pc, HpirInstruction result, ulong returned, HpirDiagnostics diagnostics,
        RuntimeProfile.HpirFunctionCensus? counts, ExecutionOptions? options)
    {
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(frame->Function, pc), frame->Function, pc);
        using RuntimeTimelineScope? trace = site.Begin("hpir.work.frame_exit");
        HpirStack.Frame* parent = frame->Parent;
        try
        {
            int candidates = work.FrameRootCandidates((nint)frame);
            trace?.SetMetric("work.return_root_candidates", candidates);
            // CALL root maps describe the old caller frame, not the result
            // about to be stored into it. Publish that root explicitly.
            if (candidates != 0 && heap!.CanCollect)
            {
                PublishReturnRoots(program, heap!, frame, result, returned, trace);
                Collect(program.Functions, heap!, parent, frame->ResumeAt - 1, -1, diagnostics, counts);
            }
            HpirHeap.CollectionPause collection = default;
            if (work.Guests is { HasHostExports: true } guests)
            {
                guests.SetHostDemand(parent, frame->ResumeAt - 1);
                collection = heap!.DeferCollection();
            }
            using (collection)
                work.SettleFrame((nint)frame, work.ScopeFrame(parent), site, trace);
            work.ResumeFrame(parent);
        }
        catch (Exception error)
        {
            trace?.Fail(error);
            throw;
        }
    }

    private static void PublishReturnRoots(HpirProgram program, HpirHeap heap, HpirStack.Frame* frame,
        HpirInstruction result, ulong returned, RuntimeTimelineScope? trace)
    {
        long root = result.Immediate;
        if (root == (long)HpirReturnRoot.Exact) heap.Mark((nint)returned);
        else if (root == (long)HpirReturnRoot.Borrowed) heap.MarkBorrow((nint)returned);
        else if (root >= (long)HpirReturnRoot.AggregateLayout)
        {
            int layout = (int)(root - (long)HpirReturnRoot.AggregateLayout);
            HpirLayout shape = program.Layouts[layout];
            byte* buffer = (byte*)Read<nint>(HpirStack.Data(frame), result.A);
            foreach (int offset in shape.PointerOffsets) heap.Mark(Unsafe.ReadUnaligned<nint>(buffer + offset));
            foreach (int offset in shape.BorrowOffsets) heap.MarkBorrow(Unsafe.ReadUnaligned<nint>(buffer + offset));
            trace?.SetMetric("work.return_aggregate_layout", layout);
            trace?.SetMetric("work.return_aggregate_root_lanes", shape.PointerOffsets.Count + shape.BorrowOffsets.Count);
        }
    }
}
