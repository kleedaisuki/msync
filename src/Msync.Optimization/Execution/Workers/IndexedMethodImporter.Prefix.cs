using System.Collections;
using System.Collections.Immutable;
using static Msync.Optimization.Execution.IndexedMethodPlan;

namespace Msync.Optimization.Execution;

internal sealed partial class IndexedMethodImporter
{
    private sealed record Prefix(HashSet<int> Blocks, HashSet<int> Stops);
    private readonly List<(int Pc, List<(int Local, Value Value)> Live)> exits = [];
    private int guestResult = -1;

    private Prefix? SelectPrefix()
    {
        HashSet<int> reached = [], stops = [];
        Queue<int> pending = new();
        pending.Enqueue(0);
        while (pending.TryDequeue(out int pc))
        {
            if (!reached.Add(pc) || graph.BlockAt(pc) is not { } block || block.StartPc != pc) continue;
            if (block.Operations.Any(HasBoundary))
            {
                if (pc == 0 || !block.Parameters.IsEmpty) return null;
                stops.Add(pc);
                continue;
            }
            foreach (var edge in block.Successors)
                if (edge.TargetPc != graph.ExitPc) pending.Enqueue(edge.TargetPc);
        }
        return stops.Count == 0 ? null : new(reached, stops);
    }

    private bool HasBoundary(ExecutionOperation op)
    {
        if (op.RequiresCanonicalDispatch
            || IndexedMethodPreparation.StopsReadonlyVersion(op.Effects) && !AdmissibleStore(op.Instruction)) return true;
        if (op.Instruction.Op != OpCode.Call) return false;
        return !TryCall(op.Instruction, out int target, out _)
            || PredicateOperation(target) is null && InlineBody(target) is null;
    }

    private bool PrepareExit(int pc, BitArray defined)
    {
        var code = body.Instructions as Instruction[] ?? body.Instructions.ToArray();
        var live = new List<(int Local, Value Value)>();
        for (int slot = InputCount; slot < locals.Length; slot++)
        {
            if (!BoundLocalLiveness.ReadsBeforeWrite(code, pc, slot)) continue;
            if (!defined[slot] || locals[slot].Kind is not (ValueKind.Scalar or ValueKind.Projection))
                return false;
            live.Add((slot, locals[slot]));
        }
        exits.Add((pc, live));
        ReturnStatus(exits.Count);
        return true;
    }

    private void ReturnStatus(int status)
    {
        if (result < 0) result = NewSlot(NativeWorkerType.Index);
        int discriminator = NewSlot(NativeWorkerType.Index);
        Emit(new(NativeWorkerOp.IndexConstant, discriminator, Data: status));
        Emit(new(NativeWorkerOp.Return, Left: discriminator));
    }

    private ImmutableArray<PrefixExit> PrepareExitMaps() =>
        [.. exits.Select(exit => new PrefixExit(exit.Pc, [.. exit.Live.Select(live =>
            live.Value.Kind == ValueKind.Projection ? new PrefixLocal(live.Local, -1, live.Value.Id)
                : new PrefixLocal(live.Local, live.Value.Id))]))];
}
