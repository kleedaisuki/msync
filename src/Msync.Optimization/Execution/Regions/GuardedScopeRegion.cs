namespace Msync.Optimization.Execution;

/// <summary>Two ready field projections and an ordered comparison parity test.</summary>
public sealed record GuardedScopeRegion(int Receiver, GuardedScopeRegion.Field Left, GuardedScopeRegion.Field Right, int Threshold)
{
    public readonly record struct Field(int Projection, int Destination);

    public const int Width = 13;

    public readonly record struct BranchExit(int Target, bool OnTrue, byte TakenWrites, byte FallthroughWrites);
    public BranchExit? Exit { get; init; }
    public ScopeCrossingArm? Crossing { get; init; }

    internal GuardedScopeRegion FollowBranch(ReadOnlySpan<Instruction> code, int pc)
    {
        if (pc == code.Length) return this;
        var branch = code[pc];
        if (branch.Op is not (OpCode.JumpIfFalse or OpCode.JumpIfTrue)) return this;
        return this with { Exit = new(branch.A, branch.Op == OpCode.JumpIfTrue,
            WritesAt(code, branch.A), WritesAt(code, pc + 1)) };
    }

    private byte WritesAt(ReadOnlySpan<Instruction> code, int successor)
    {
        int writes = BoundLocalLiveness.ReadsBeforeWrite(code, successor, Left.Destination) ? 1 : 0;
        if (BoundLocalLiveness.ReadsBeforeWrite(code, successor, Right.Destination)) writes |= 2;
        return (byte)writes;
    }

    internal static GuardedScopeRegion? Match(ReadOnlySpan<Instruction> remaining)
    {
        if (remaining.Length < Width || remaining[..Width] is not [
            { Op: OpCode.LoadLocal, A: var receiver },
            { Op: OpCode.LoadField, A: var leftField },
            { Op: OpCode.StoreLocal, A: var left },
            { Op: OpCode.LoadLocal, A: var receiverAgain },
            { Op: OpCode.LoadField, A: var rightField },
            { Op: OpCode.StoreLocal, A: var right },
            { Op: OpCode.LoadLocal, A: var leftRead },
            { Op: OpCode.LoadLocal, A: var threshold },
            { Op: OpCode.Greater },
            { Op: OpCode.LoadLocal, A: var rightRead },
            { Op: OpCode.LoadLocal, A: var thresholdAgain },
            { Op: OpCode.Greater },
            { Op: OpCode.Equal }
        ]) return null;

        if (receiver != receiverAgain || left != leftRead || right != rightRead || threshold != thresholdAgain
            || left == right || left == receiver || right == receiver || left == threshold || right == threshold)
            return null;
        return new(receiver, new(leftField, left), new(rightField, right), threshold);
    }
}
