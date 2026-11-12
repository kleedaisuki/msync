using System.Collections.Immutable;

namespace Msync.Optimization.Execution;

/// <summary>A guarded, non-suspending continuation of a Scope comparison.</summary>
public sealed record ScopeCrossingArm(int Entry, int Exit, int X, int Ax, int Bx,
    ImmutableArray<int> DeadStores, int EntryLength, ScopeCrossingArm.Arm Positive, ScopeCrossingArm.Arm Negative)
{
    public readonly record struct Arm(int Start, int Length);

    private const int MaxInstructions = 48;

    internal static bool MayStart(ReadOnlySpan<Instruction> code, int start, int receiver) =>
        (uint)start < (uint)code.Length && code[start..] is [
            { Op: OpCode.LoadLocal, A: var slot }, { Op: OpCode.LoadField }, ..] && slot == receiver;

    internal static ScopeCrossingArm? Build(Module image, ExecutionRegion region, int start, GuardedScopeRegion prefix,
        ReadOnlySpan<Instruction> code)
    {
        if (region.BlockAt(start) is not { } entry || entry.StartPc != start || entry.Parameters.Length != 0
            || entry.Successors.Length != 2 || entry.Operations[^1].Instruction.Op != OpCode.JumpIfFalse)
            return null;
        var taken = entry.Successors[0];
        var fall = entry.Successors[1];
        if (taken.Kind != ExecutionEdgeKind.Taken || fall.Kind != ExecutionEdgeKind.Fallthrough
            || taken.Arguments.Depth != 0 || fall.Arguments.Depth != 0 || fall.TargetPc <= start || taken.TargetPc <= fall.TargetPc
            || region.BlockAt(taken.TargetPc) is not { } negative || negative.StartPc != taken.TargetPc
            || region.BlockAt(fall.TargetPc) is not { } positive || positive.StartPc != fall.TargetPc
            || negative.Successors.Length != 1 || positive.Successors.Length != 1) return null;
        var negExit = negative.Successors[0];
        var posExit = positive.Successors[0];
        int exit = posExit.TargetPc;
        if (exit != negExit.TargetPc || exit <= taken.TargetPc || exit - start > MaxInstructions
            || posExit.Arguments.Depth != 1 || negExit.Arguments.Depth != 1 || region.BlockAt(exit)?.StartPc != exit)
            return null;

        var reads = new Expressions(image, region.ValueCount, prefix.Receiver);
        if (!reads.Read(entry, allowStores: true) || !reads.Read(positive, allowStores: false)
            || !reads.Read(negative, allowStores: false)) return null;

        Span<ExecutionValueId> result = stackalloc ExecutionValueId[1];
        region.CopyStack(posExit.Arguments, result);
        var pos = reads[result[0]];
        region.CopyStack(negExit.Arguments, result);
        var neg = reads[result[0]];
        var condition = reads[entry.Operations[^1].Inputs[0]];
        var ay = new Expr(OpCode.LoadLocal, prefix.Left.Destination);
        var by = new Expr(OpCode.LoadLocal, prefix.Right.Destination);
        var y = new Expr(OpCode.LoadLocal, prefix.Threshold);
        var dy = new Expr(OpCode.Subtract, Left: by, Right: ay);
        if (condition is null || neg is null || condition != new Expr(OpCode.Greater, Left: dy, Right: new Expr(OpCode.Constant))
            || pos is not { Op: OpCode.Less,
                Left: { Op: OpCode.Multiply, Left: { Op: OpCode.Subtract,
                    Left: { Op: OpCode.LoadLocal, Operand: var x },
                    Right: { Op: OpCode.LoadField, Operand: var ax } }, Right: { } lhsDy } lhs,
                Right: { Op: OpCode.Multiply, Left: { Op: OpCode.Subtract,
                    Left: { Op: OpCode.LoadField, Operand: var bx }, Right: { } rhsAx }, Right: { } rhsDy } rhs }
            || lhsDy != dy || rhsAx != new Expr(OpCode.LoadField, ax)
            || rhsDy != new Expr(OpCode.Subtract, Left: y, Right: ay)
            || neg != new Expr(OpCode.Greater, Left: lhs, Right: rhs)
            || !reads.AllUsed(condition, pos, neg)) return null;

        if (reads.Stores.ContainsKey(prefix.Receiver) || reads.Stores.ContainsKey(prefix.Left.Destination)
            || reads.Stores.ContainsKey(prefix.Right.Destination) || reads.Stores.ContainsKey(prefix.Threshold)
            || reads.Stores.ContainsKey(x)) return null;
        foreach (int slot in reads.Stores.Keys)
            if (BoundLocalLiveness.ReadsBeforeWrite(code, exit, slot)) return null;
        return new(start, exit, x, ax, bx, [.. reads.Stores.Keys], entry.Operations.Length,
            new(positive.StartPc, positive.Operations.Length), new(negative.StartPc, negative.Operations.Length));
    }

    private sealed record Expr(OpCode Op, int Operand = 0, Expr? Left = null, Expr? Right = null);

    private sealed class Expressions(Module image, int valueCount, int receiver)
    {
        private readonly Expr?[] values = new Expr?[valueCount];
        private readonly Expr projectionBase = new(OpCode.LoadLocal, receiver);
        internal Dictionary<int, Expr> Stores { get; } = [];
        internal Expr? this[ExecutionValueId value] => values[value.Index];

        internal bool Read(ExecutionBlock block, bool allowStores)
        {
            foreach (var operation in block.Operations)
            {
                var instruction = operation.Instruction;
                Expr? Input(int index) => this[operation.Inputs[index]];
                Expr result;
                switch (instruction.Op)
                {
                    case OpCode.LoadLocal:
                        result = Stores.GetValueOrDefault(instruction.A) ?? new(OpCode.LoadLocal, instruction.A);
                        break;
                    case OpCode.LoadField when Input(0) == projectionBase:
                        result = new(OpCode.LoadField, instruction.A);
                        break;
                    case OpCode.Constant when image.Constants[instruction.A] is NumberValue number
                        && BitConverter.DoubleToInt64Bits(number.Data) == 0:
                        result = new(OpCode.Constant);
                        break;
                    case OpCode.Subtract or OpCode.Multiply or OpCode.Less or OpCode.Greater
                        when Input(0) is { } left && Input(1) is { } right:
                        result = new(instruction.Op, Left: left, Right: right);
                        break;
                    case OpCode.StoreLocal when allowStores && Input(0) is { } stored:
                        if (!Stores.TryAdd(instruction.A, stored)) return false;
                        continue;
                    case OpCode.Jump or OpCode.JumpIfFalse:
                        continue;
                    default:
                        return false;
                }
                values[operation.Result!.Value.Index] = result;
            }
            return true;
        }

        internal bool AllUsed(Expr condition, Expr positive, Expr negative)
        {
            HashSet<Expr> used = [projectionBase];
            Include(condition, used);
            Include(positive, used);
            Include(negative, used);
            foreach (var value in values)
                if (value is not null && !used.Contains(value)) return false;
            return true;
        }

        private static void Include(Expr value, HashSet<Expr> used)
        {
            if (!used.Add(value)) return;
            if (value.Left is { } left) Include(left, used);
            if (value.Right is { } right) Include(right, used);
        }
    }
}
