using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private void ExecutePlan(Activation activation, int index, CancellationToken token)
    {
        NativeExecutionPlan plan = image.Program.ExecutionPlans?[index]
            ?? throw new InvalidOperationException("Missing prepared execution plan.");
        using RuntimeTimelineScope? submission = context.Timeline?.Begin("scheduler.plan");
        submission?.SetMetric("plan.nodes", plan.Transfers.Count);
        submission?.SetMetric("plan.recorded_batches", plan.Batches.Count);
        submission?.SetMetric("plan.eliminated_transfers", plan.EliminatedTransfers);
        RecordPlanSubmission(plan);
        Dictionary<int, NativeDatum> byIdentity = [];
        Dictionary<int, NativePlannedTransfer> transfersById = plan.Transfers.ToDictionary(transfer => transfer.Id);
        NativeDatum[] results = new NativeDatum[plan.Transfers.Count];
        try
        {
            for (int transfer = 0; transfer < results.Length; transfer++) results[transfer] = Submit(plan.Transfers[transfer].Id);
            (activation.Plans ??= [])[index] = results;
        }
        catch (Exception error) { submission?.Fail(error); throw; }
        finally { submission?.SetMetric("plan.dispatch_units", byIdentity.Count); }

        NativeDatum Submit(int identity)
        {
            if (byIdentity.TryGetValue(identity, out NativeDatum existing)) return existing;
            NativePlannedTransfer transfer = transfersById[identity];
            Task[] predecessors = transfer.Predecessors.Select(predecessor => Submit(predecessor).Future!.Completion).ToArray();
            NativeValue source = PlanValue(transfer.Source);
            NativeDatum result = ScheduleTransfer(new(source), new(NativeValue.FromPath(transfer.Destination)),
                transfer.Span, activation.Work, token, transfer, predecessors);
            byIdentity.Add(identity, result);
            return result;
        }
    }

    private NativeDatum PlanResult(Activation activation, NativeInstruction instruction)
    {
        if (activation.Plans is null || !activation.Plans.TryGetValue(instruction.A, out NativeDatum[]? results))
            throw new InvalidOperationException("Prepared plan result was read before submission.");
        if ((uint)instruction.B < results.Length) return results[instruction.B];
        NativeExecutionPlan plan = image.Program.ExecutionPlans![instruction.A];
        IReadOnlyList<int> selected = plan.ResultTransfers ?? plan.Transfers.Select(transfer => transfer.Id).ToArray();
        Dictionary<int, int> indices = plan.Transfers.Select((transfer, index) => (transfer.Id, index)).ToDictionary(pair => pair.Id, pair => pair.index);
        NativeDatum result = new(NativeValue.True);
        foreach (int transfer in selected)
            result = Combine(result, results[indices[transfer]], static (first, second) => NativeValue.FromBool(first.AsBool() && second.AsBool()), instruction.Source);
        return result;
    }

    private NativeValue PlanValue(NativePlanValue value) => value.Kind switch
    {
        NativeKind.Path => NativeValue.FromPath(value.Path ?? throw new InvalidOperationException("Missing prepared Path.")),
        NativeKind.Range => NativeValue.FromRange(NativeRange.Snapshot(value.Items!.Select(PlanValue), value.ItemType!)),
        _ => NativeImage.Literal(value.Literal ?? throw new InvalidOperationException("Missing prepared literal."), context)
    };
}
