namespace Msync.Optimization.Execution;

internal sealed partial class IndexedMethodImporter
{
    private Dictionary<int, Value[]>? blockParameters;

    private void EnterBlockParameters(ExecutionBlock block)
    {
        if (block.Parameters.IsEmpty) return;
        var byTarget = blockParameters ??= [];
        if (!byTarget.TryGetValue(block.StartPc, out var carried))
        {
            carried = new Value[block.Parameters.Length];
            for (int i = 0; i < carried.Length; i++) carried[i] = new(ValueKind.Scalar, NewSlot());
            byTarget.Add(block.StartPc, carried);
        }
        for (int i = 0; i < carried.Length; i++)
            values[block.Parameters[i].Index] = carried[i];
    }

    private bool LowerEdges(ExecutionBlock block, ExecutionOperation end)
    {
        var first = block.Successors[0];
        if (first.TargetPc == graph.ExitPc) return false;
        if (block.Successors.Length == 2)
        {
            var second = block.Successors[1];
            if (first.Kind != ExecutionEdgeKind.Taken || second.TargetPc == graph.ExitPc
                || first.Arguments != second.Arguments
                || !Scalar(Operand(end, 0), out var condition) || !Require(condition.Id, NativeWorkerType.Bool))
                return false;
            // A backedge can replace a block parameter used by the branch.
            int selected = condition.Id;
            if (first.Arguments.Depth != 0)
            {
                selected = NewSlot(NativeWorkerType.Bool);
                Move(selected, condition.Id);
            }
            if (!MoveEdges(first, second.TargetPc)) return false;
            Emit(new(end.Instruction.Op == OpCode.JumpIfTrue ? NativeWorkerOp.BranchIfTrue : NativeWorkerOp.BranchIfFalse,
                Left: selected, Data: first.TargetPc));
            Emit(new(NativeWorkerOp.Jump, Data: second.TargetPc));
            return true;
        }
        if (!MoveEdges(first)) return false;
        if (first.TargetPc != end.Pc + 1 || code.Count == entries[block.StartPc])
            Emit(new(NativeWorkerOp.Jump, Data: first.TargetPc));
        else if (pending.Count != 0)
            // A primitive can contain its own branch. Record the original
            // suffix at the join, not on one of its selected operations.
            Emit(new(NativeWorkerOp.Nop));
        return true;
    }

    private bool MoveEdges(ExecutionEdge edge, int secondTarget = -1)
    {
        int depth = edge.Arguments.Depth;
        if (depth == 0) return true;
        var sources = new ExecutionValueId[depth];
        graph.CopyStack(edge.Arguments, sources);
        var scratch = new Value[depth];
        for (int i = 0; i < depth; i++)
        {
            var source = values[sources[i].Index];
            if (source.Kind != ValueKind.Word && !Scalar(source, out source)) return false;
            int slot = NewSlot();
            Move(slot, source.Id);
            scratch[i] = new(source.Kind, slot);
        }
        return MoveToBlock(edge.TargetPc, scratch) && (secondTarget < 0 || MoveToBlock(secondTarget, scratch));
    }

    private bool MoveToBlock(int target, ReadOnlySpan<Value> scratch)
    {
        var byTarget = blockParameters ??= [];
        bool first = !byTarget.TryGetValue(target, out var carried);
        carried ??= new Value[scratch.Length];
        for (int i = 0; i < scratch.Length; i++)
        {
            if (first) carried[i] = new(scratch[i].Kind, NewSlot());
            else if (carried[i].Kind != scratch[i].Kind) return false;
            if (!Unify(carried[i].Id, scratch[i].Id)) return false;
            Move(carried[i].Id, scratch[i].Id);
        }
        if (first) byTarget.Add(target, carried);
        return true;
    }
}
