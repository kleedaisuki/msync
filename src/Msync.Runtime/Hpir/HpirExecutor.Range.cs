using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    private static void CallNumericRange(HpirProgram program, ref HpirHeap? heap, HpirWork? work,
        HpirStack.Frame* active, HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc,
        HpirImport import, HpirCall call, byte* frame, int destination, ExecutionOptions? options, CancellationToken token)
    {
        IReadOnlyList<int> args = call.Arguments;
        int function = active->Function;
        switch (import.Service)
        {
            case HpirService.RangeOpen:
                HpirNumericRange plan = ReadPlan(Read<nint>(frame, args[0]), import.Binding, work,
                    new(program, heap!, active, pc, diagnostics, counts), diagnostics, options?.Timeline, function, pc, token);
                *(HpirNumericCursor*)Read<nint>(frame, args[1]) = new(plan);
                return;
            case HpirService.RangeDeferredSequence:
                CaptureRange(program, ref heap, active, diagnostics, counts, pc, import.Binding, args, frame, destination, options);
                return;
            case HpirService.RangeSequence:
                var ready = HpirNumericRange.Sequence(Read<double>(frame, args[0]), Read<double>(frame, args[1]),
                    Read<double>(frame, args[2]), diagnostics.Source(function, pc));
                PublishPlan(program, ref heap, active, diagnostics, counts, pc, import.Binding, frame, destination, ready);
                return;
            default:
                var (layout, mode) = HpirRangeImports.DecodeWindow(import.Binding);
                var text = new HpirTextDemandContext(program, heap!, active, pc, diagnostics, counts);
                var input = ReadPlan(Read<nint>(frame, args[0]), layout, work, text, diagnostics, options?.Timeline, function, pc, token);
                int countIndex = 1;
                var count = FutureInput<double>(frame, args, mode, ref countIndex);
                var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(function, pc), function, pc);
                double value = count.Pending == 0 ? count.Value : HpirFuture.Demand<double>(count.Pending, work, site, token, text);
                var window = input.Window(import.Service, value, site.Source);
                PublishPlan(program, ref heap, active, diagnostics, counts, pc, layout, frame, destination, window);
                return;
        }
    }

    private static void CaptureRange(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, int binding,
        IReadOnlyList<int> args, byte* frame, int destination, ExecutionOptions? options)
    {
        var (layout, firstMode, limitMode, stepMode) = HpirRangeImports.DecodeSequence(binding);
        int index = 0;
        var first = FutureInput<double>(frame, args, firstMode, ref index);
        var limit = FutureInput<double>(frame, args, limitMode, ref index);
        var step = FutureInput<double>(frame, args, stepMode, ref index);
        int function = active->Function;
        var plan = new HpirDeferredNumericRange(first, limit, step, function, pc);
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(function, pc), function, pc);
        using RuntimeTimelineScope? trace = site.Begin("hpir.range.capture");
        trace?.SetMetric("range.pending_bounds", plan.Pending);
        try
        {
            // A mixed ABI that carried only ready Numbers still validates at
            // construction. A truly pending plan moves no demand before use.
            if (plan.Pending == 0) HpirNumericRange.Sequence(plan.First, plan.Limit, plan.Step, site.Source);
            nint address = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
            *(HpirDeferredNumericRange*)address = plan;
            Write(frame, destination, address);
        }
        catch (Exception error) { trace?.Fail(error); throw; }
    }

    private static HpirNumericRange ReadPlan(nint address, int readyLayout, HpirWork? work, HpirTextDemandContext text,
        HpirDiagnostics diagnostics, RuntimeTimeline? timeline, int function, int pc, CancellationToken token)
    {
        if (HpirHeap.HasLayout(address, readyLayout)) return *(HpirNumericRange*)address;

        ref var deferred = ref *(HpirDeferredNumericRange*)address;
        if (deferred.Pending == 0) return HpirNumericRange.Ready(deferred.First, deferred.Limit, deferred.Step);
        var site = new HpirWorkSite(timeline, diagnostics.Source(function, pc), function, pc);
        using RuntimeTimelineScope? trace = site.Begin("hpir.range.resolve");
        trace?.SetMetric("range.pending_bounds", deferred.Pending);
        trace?.SetMetric("range.producer.function", deferred.Function);
        trace?.SetMetric("range.producer.instruction", deferred.Instruction);
        try
        {
            var plan = HpirNumericRange.Sequence(Resolve(deferred.FirstPending, deferred.First), Resolve(deferred.LimitPending, deferred.Limit),
                Resolve(deferred.StepPending, deferred.Step), diagnostics.Source(deferred.Function, deferred.Instruction) ?? site.Source);
            deferred.Resolve(plan);
            return plan;
        }
        catch (Exception error) { trace?.Fail(error); throw; }

        double Resolve(nint pending, double ready) => pending == 0 ? ready : HpirFuture.Demand<double>(pending, work, site, token, text);
    }

    private static void PublishPlan(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active, HpirDiagnostics diagnostics,
        RuntimeProfile.HpirFunctionCensus? counts, int pc, int layout, byte* frame, int destination, HpirNumericRange plan)
    {
        nint address = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
        *(HpirNumericRange*)address = plan;
        Write(frame, destination, address);
    }
}
