namespace Msync.Optimization.Execution;

internal static class ExecutionArenaOperation
{
    internal static ArenaOperation Bind(Instruction instruction)
    {
        if (instruction.Op != OpCode.NativeCall || instruction.Text is not { } name
            || !NativeSignatures.TryGet(name, out var signature) || instruction.A != signature.Parameters.Count)
            return ArenaOperation.None;
        return signature.Arena switch
        {
            ArenaOperation.Root or ArenaOperation.Size or ArenaOperation.Key or ArenaOperation.Child
                or ArenaOperation.Parent or ArenaOperation.Red or ArenaOperation.SetChild
                or ArenaOperation.SetParent or ArenaOperation.SetRed => signature.Arena,
            _ => ArenaOperation.None,
        };
    }

    internal static bool Writes(ArenaOperation primitive) =>
        primitive is ArenaOperation.SetChild or ArenaOperation.SetParent or ArenaOperation.SetRed;
}

internal sealed partial class IndexedMethodImporter
{
    private bool arenaRows;
    private int arenaStores;

    private bool Arena(ExecutionOperation op, ArenaOperation primitive)
    {
        var owner = Operand(op, 0);
        if (owner.Kind != ValueKind.Projection || storage >= 0 && (!arenaRows || storage != owner.Id)) return false;
        storage = owner.Id;
        arenaRows = true;
        if (primitive is ArenaOperation.Root or ArenaOperation.Size)
            return Set(op, Project(new(primitive == ArenaOperation.Root ? IndexedMethodPlan.AccessKind.ArenaRoot
                : IndexedMethodPlan.AccessKind.ArenaSize, owner.Id, 0)));

        if (!Scalar(Operand(op, 1), out var index) || !Require(index.Id, NativeWorkerType.Index)) return false;
        if (ExecutionArenaOperation.Writes(primitive)) return ArenaStore(op, primitive, index.Id);
        NativeWorkerType type = primitive switch
        {
            ArenaOperation.Key => NativeWorkerType.Number,
            ArenaOperation.Red => NativeWorkerType.Bool,
            _ => NativeWorkerType.Index
        };
        int resultSlot = NewSlot(type);
        if (primitive == ArenaOperation.Child)
        {
            if (!ArenaChild(op, NativeWorkerOp.LoadIndex, resultSlot, index.Id, 0)) return false;
        }
        else
        {
            int offset = primitive switch
            {
                ArenaOperation.Key => ArenaNumberRowLayout.Key,
                ArenaOperation.Parent => ArenaNumberRowLayout.Parent,
                _ => ArenaNumberRowLayout.Color
            };
            Emit(new(type == NativeWorkerType.Number ? NativeWorkerOp.LoadNumber : NativeWorkerOp.LoadIndex,
                resultSlot, index.Id, Data: offset));
        }
        return Set(op, new(ValueKind.Scalar, resultSlot));
    }

    private bool ArenaStore(ExecutionOperation op, ArenaOperation primitive, int index)
    {
        if (!writeArena) return false;
        int input = primitive == ArenaOperation.SetChild ? 3 : 2;
        var type = primitive == ArenaOperation.SetRed ? NativeWorkerType.Bool : NativeWorkerType.Index;
        if (!Scalar(Operand(op, input), out var value) || !Require(value.Id, type)) return false;
        if (primitive == ArenaOperation.SetChild)
        {
            if (!ArenaChild(op, NativeWorkerOp.StoreIndex, 0, index, value.Id)) return false;
        }
        else Emit(new(NativeWorkerOp.StoreIndex, Left: index, Right: value.Id,
            Data: primitive == ArenaOperation.SetParent ? ArenaNumberRowLayout.Parent : ArenaNumberRowLayout.Color));
        arenaStores++;
        return Set(op, new(ValueKind.Null, -1));
    }

    private bool ArenaChild(ExecutionOperation operation, NativeWorkerOp op, int destination, int row, int value)
    {
        if (!Scalar(Operand(operation, 2), out var direction) || !Require(direction.Id, NativeWorkerType.Bool))
            return false;
        foreach (var definition in code)
        {
            if (definition.Op != NativeWorkerOp.BoolConstant || definition.Destination != direction.Id) continue;
            Emit(new(op, destination, row, value, definition.Data == 0 ? ArenaNumberRowLayout.Right : ArenaNumberRowLayout.Left));
            return true;
        }

        // Negative branch operands name worker PCs, not original bytecode PCs.
        // Only the selected physical field access executes.
        int branch = code.Count;
        Emit(new(NativeWorkerOp.BranchIfFalse, Left: direction.Id, Data: ~(branch + 3)));
        Emit(new(op, destination, row, value, ArenaNumberRowLayout.Left));
        Emit(new(NativeWorkerOp.Jump, Data: ~(branch + 4)));
        Emit(new(op, destination, row, value, ArenaNumberRowLayout.Right));
        return true;
    }
}
