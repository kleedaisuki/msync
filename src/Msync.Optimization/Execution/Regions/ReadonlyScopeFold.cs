using System.Collections.Immutable;

namespace Msync.Optimization.Execution;

/// <summary>Private scalar live-out of a certified crossing-parity row loop.</summary>
public sealed record ReadonlyScopeFold(int Exit, int X, int Y, int Parity,
    int Ax, int Ay, int Bx, int By,
    ImmutableArray<int> StoreSlots)
{
    public ImmutableArray<int> Admissions { get; init; } = [];

    private const int Row = -1;

    internal static ReadonlyScopeFold?[]? Find(FunctionCode function, Instruction[] code,
        ExecutionRegion graph, GuardedScopeRegion?[] scopes)
    {
        ReadonlyScopeFold?[]? sites = null;
        foreach (var header in graph.Blocks)
        {
            if (header.Operations is not [{ Instruction.Op: OpCode.IteratorNext }]) continue;
            if (Match(function, code, graph, scopes, header) is { } plan)
                (sites ??= new ReadonlyScopeFold?[code.Length])[header.StartPc - 2] = plan;
        }
        return sites;
    }

    private static ReadonlyScopeFold? Match(FunctionCode function, Instruction[] code, ExecutionRegion graph,
        GuardedScopeRegion?[] scopes, ExecutionBlock header)
    {
        if (!TryIterator(graph, header, out int iterator, out int exit, out var row)) return null;
        int branch = row.Operations[^1].Pc;
        int compare = branch - GuardedScopeRegion.Width;
        if (compare < row.StartPc || scopes[compare] is not { Exit: { } exits, Crossing: { } cross } prefix
            || graph.OperationAt(compare)?.StackBefore.Depth != 0) return null;

        int skip = exits.OnTrue ? exits.Target : branch + 1;
        int selected = exits.OnTrue ? branch + 1 : exits.Target;
        if (selected != cross.Entry || !FalseEdge(graph, skip, cross.Exit)
            || !TryParity(graph, cross.Exit, header.StartPc, out int parity)) return null;

        var aliases = new RowAliases(row.Parameters[0]);
        if (!aliases.Read(row.Operations.AsSpan(0, compare - row.StartPc))
            || aliases.Origin(prefix.Receiver) != Row) return null;
        int x = aliases.Origin(cross.X), y = aliases.Origin(prefix.Threshold);
        if (x < 0 || y < 0 || !aliases.UsesOnly(x, y)) return null;

        var writes = aliases.Writes;
        writes.Add(prefix.Left.Destination);
        writes.Add(prefix.Right.Destination);
        writes.UnionWith(cross.DeadStores);
        if (writes.Contains(x) || writes.Contains(y) || writes.Contains(iterator) || writes.Contains(parity)
            || parity == x || parity == y || iterator == x || iterator == y || iterator == parity) return null;

        writes.Add(iterator);
        if (!PrivateAtExit(function, code, writes, parity, exit)) return null;
        // A skipped InitializeLocal must not leave an old Cell for a later
        // StoreLocal to write through, even when the slot is dead here.
        writes.Add(parity);
        return new(exit, x, y, parity, cross.Ax, prefix.Left.Projection,
            cross.Bx, prefix.Right.Projection, [.. writes.Order()]) { Admissions = [.. aliases.Admissions] };
    }

    private static bool TryIterator(ExecutionRegion graph, ExecutionBlock header, out int iterator, out int exit,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExecutionBlock? row)
    {
        iterator = header.Operations[0].Instruction.A;
        exit = header.Operations[0].Instruction.B;
        row = null;
        int entry = header.StartPc - 2;
        if (entry < 0 || exit <= header.StartPc || header.Parameters.Length != 0
            || graph.OperationAt(entry) is not { Instruction.Op: OpCode.GetIterator, StackBefore.Depth: 1 } get
            || graph.OperationAt(entry + 1) is not { Instruction.Op: OpCode.InitializeLocal } init
            || init.Instruction.A != iterator || init.Inputs[0] != get.Result
            || header.Successors is not [
                { Kind: ExecutionEdgeKind.IteratorExhausted, Arguments.Depth: 0 },
                { Kind: ExecutionEdgeKind.IteratorYield, Arguments.Depth: 1 }
            ] || header.Successors[0].TargetPc != exit
            || graph.BlockAt(header.Successors[1].TargetPc) is not { Parameters.Length: 1 } selected)
            return false;
        row = selected;
        return true;
    }

    private static bool FalseEdge(ExecutionRegion graph, int start, int join)
    {
        if (graph.BlockAt(start) is not { Parameters.Length: 0, Successors.Length: 1 } arm || arm.StartPc != start
            || arm.Operations is not ([{ Instruction.Op: OpCode.False }]
                or [{ Instruction.Op: OpCode.False }, { Instruction.Op: OpCode.Jump }])) return false;
        var edge = arm.Successors[0];
        if (edge.TargetPc != join || edge.Arguments.Depth != 1) return false;
        Span<ExecutionValueId> result = stackalloc ExecutionValueId[1];
        graph.CopyStack(edge.Arguments, result);
        return result[0] == arm.Operations[0].Result;
    }

    private static bool TryParity(ExecutionRegion graph, int start, int header, out int slot)
    {
        slot = -1;
        if (graph.BlockAt(start) is not { Parameters.Length: 1, Successors.Length: 2 } join || join.StartPc != start
            || join.Operations is not [{ Instruction.Op: OpCode.JumpIfFalse or OpCode.JumpIfTrue } branch]
            || branch.Inputs[0] != join.Parameters[0]) return false;
        bool onTrue = branch.Instruction.Op == OpCode.JumpIfTrue;
        var toggleEdge = join.Successors[onTrue ? 0 : 1];
        var skipEdge = join.Successors[onTrue ? 1 : 0];
        if (toggleEdge.Arguments.Depth != 0 || skipEdge.Arguments.Depth != 0
            || !Backedge(graph, skipEdge.TargetPc, header)
            || graph.BlockAt(toggleEdge.TargetPc) is not { Parameters.Length: 0, Successors.Length: 1 } toggle
            || toggle.StartPc != toggleEdge.TargetPc || toggle.Operations.Length is not (3 or 4)
            || toggle.Operations.AsSpan(0, 3) is not [
                { Instruction.Op: OpCode.LoadLocal } load,
                { Instruction.Op: OpCode.Not } negate,
                { Instruction.Op: OpCode.StoreLocal } store
            ] || negate.Inputs[0] != load.Result || store.Inputs[0] != negate.Result || store.Instruction.A != load.Instruction.A
            || toggle.Operations.Length == 4 && toggle.Operations[3].Instruction.Op != OpCode.Jump)
            return false;
        var edge = toggle.Successors[0];
        if (edge.Arguments.Depth != 0 || edge.TargetPc != header && edge.TargetPc != skipEdge.TargetPc) return false;
        slot = load.Instruction.A;
        return true;
    }

    private static bool Backedge(ExecutionRegion graph, int pc, int header) =>
        graph.BlockAt(pc) is { Parameters.Length: 0 } block && block.StartPc == pc
        && block.Operations is [{ Instruction.Op: OpCode.Jump, Instruction.A: var target }] && target == header;

    private static bool PrivateAtExit(FunctionCode function, ReadOnlySpan<Instruction> code,
        HashSet<int> dead, int parity, int exit)
    {
        int firstPrivate = function.CaptureCount + function.ParameterCount;
        foreach (int slot in dead)
            if (slot < firstPrivate || slot >= function.LocalCount
                || BoundLocalLiveness.ReadsBeforeWrite(code, exit, slot)) return false;
        if (parity < firstPrivate || parity >= function.LocalCount) return false;
        foreach (var instruction in code)
            if (instruction.Op is OpCode.LoadCell or OpCode.LoadStorage
                && (dead.Contains(instruction.A) || instruction.A == parity)) return false;
        return true;
    }

    // The prelude only binds the yielded row and copies the ready X/Y
    // inputs. Track every stored origin, not just the last value of a slot:
    // a transient Cell copied from an unrelated slot could make a later
    // StoreLocal observable even if that slot is otherwise dead.
    private sealed class RowAliases(ExecutionValueId parameter)
    {
        private readonly Dictionary<int, int> values = new() { [parameter.Index] = Row };
        private readonly Dictionary<int, int> locals = [];
        private readonly HashSet<int> origins = [];
        internal HashSet<int> Writes { get; } = [];
        internal HashSet<int> Admissions { get; } = [];
        internal int Origin(int slot) => locals.GetValueOrDefault(slot, slot);
        internal bool UsesOnly(int x, int y) => origins.All(value => value == Row || value == x || value == y);

        internal bool Read(ReadOnlySpan<ExecutionOperation> operations)
        {
            foreach (var operation in operations)
            {
                var instruction = operation.Instruction;
                switch (instruction.Op)
                {
                    case OpCode.LoadLocal:
                        values.Add(operation.Result!.Value.Index, Origin(instruction.A));
                        break;
                    case OpCode.AdmitScope:
                        if (values.GetValueOrDefault(operation.Inputs[0].Index) != Row) return false;
                        Admissions.Add(instruction.A);
                        values.Add(operation.Result!.Value.Index, Row);
                        break;
                    case OpCode.InitializeLocal:
                    case OpCode.StoreLocal:
                        if (!values.TryGetValue(operation.Inputs[0].Index, out int value)) return false;
                        locals[instruction.A] = value;
                        origins.Add(value);
                        Writes.Add(instruction.A);
                        break;
                    default: return false;
                }
            }
            return true;
        }
    }
}
