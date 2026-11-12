using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private bool TryGuardedScopeRegion<TFrame, TLayout>(int function, int pc, Instruction[] code, BoundScopePlaces places, ref FrameSlots slots,
        ref OperandStack<TFrame> stack, ref TFrame frame, CancellationToken cancellationToken, out int next)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        int width = GuardedScopeRegion.Width;
        next = pc + width;
        if (sessionImage is not null || slots.Borrowed) return false;
        var plan = executionTargets.ScopeRegion(function, pc);
        if (slots.Peek<TLayout>(plan.Receiver) is not ScopeValue receiver
            || slots.Peek<TLayout>(plan.Left.Destination) is CellValue
            || slots.Peek<TLayout>(plan.Right.Destination) is CellValue
            || !TryProjectNumber(places, receiver, plan.Left.Projection, out double left, out var leftBox)
            || !TryProjectNumber(places, receiver, plan.Right.Projection, out double right, out var rightBox)
            || !slots.TryNumber(plan.Threshold, readCell: false, out double threshold)) return false;

        // The region is non-suspending and its only writes are private slots.
        // Poll again before committing both stores and the comparison result.
        bool equal = (left > threshold) == (right > threshold);
        cancellationToken.ThrowIfCancellationRequested();
        byte writes = 3;
        if (plan.Exit is { } exit)
        {
            bool taken = equal == exit.OnTrue;
            writes = taken ? exit.TakenWrites : exit.FallthroughWrites;
            next = taken ? exit.Target : next + 1;
            width++;
        }
        else stack.Add(equal ? Value.True : Value.False);
        if ((writes & 1) != 0)
            CommitProjection<TFrame, TLayout>(plan.Left.Destination, left, leftBox, ref slots, ref frame);
        if ((writes & 2) != 0)
            CommitProjection<TFrame, TLayout>(plan.Right.Destination, right, rightBox, ref slots, ref frame);
        if (typeof(TFrame) != typeof(RuntimeProfile.DisabledFrame))
            frame.GuardedScopeRegion(code.AsSpan(pc, width));
        if (plan.Crossing is { } arm && next == arm.Entry)
        {
            // The prefix has already committed. A rejected arm resumes at
            // its own entry, not at the first Y projection.
            if (TryGuardedScopeCrossing<TFrame, TLayout>(arm, places, receiver, left, right, threshold, code, ref slots,
                ref stack, ref frame, cancellationToken))
                next = arm.Exit;
            else frame.GuardedScopeCrossingMiss();
        }
        return true;
    }

    private static bool TryGuardedScopeCrossing<TFrame, TLayout>(ScopeCrossingArm arm, BoundScopePlaces places, ScopeValue receiver,
        double ay, double by, double y, Instruction[] code, ref FrameSlots slots, ref OperandStack<TFrame> stack,
        ref TFrame frame, CancellationToken cancellationToken)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryProjectNumber(places, receiver, arm.Ax, out double ax, out _)
            || !TryProjectNumber(places, receiver, arm.Bx, out double bx, out _)
            || !slots.TryNumber(arm.X, readCell: false, out double x)) return false;
        // A dead private slot is not a proof about a Cell reached by an alias.
        foreach (int slot in arm.DeadStores)
            if (slots.Peek<TLayout>(slot) is CellValue) return false;

        double dy = by - ay;
        double lhs = (x - ax) * dy;
        double rhs = (bx - ax) * (y - ay);
        bool positive = dy > 0;
        bool crossed = positive ? lhs < rhs : lhs > rhs;
        cancellationToken.ThrowIfCancellationRequested();
        stack.Add(crossed ? Value.True : Value.False);
        if (typeof(TFrame) != typeof(RuntimeProfile.DisabledFrame))
        {
            var branch = positive ? arm.Positive : arm.Negative;
            frame.GuardedScopeCrossing(code.AsSpan(arm.Entry, arm.EntryLength), code.AsSpan(branch.Start, branch.Length));
        }
        return true;
    }

    private static bool TryProjectNumber(BoundScopePlaces places, ScopeValue scope, int projection, out double number,
        out NumberValue? box)
    {
        number = 0;
        box = null;
        if (!places.TryField(scope.Layout, projection, out int offset)) return false;
        var value = scope.GetOperandField(offset, out number);
        if (value is null) return true;
        box = value as NumberValue;
        if (box is null) return false;
        number = box.Data;
        return true;
    }

    private static void CommitProjection<TFrame, TLayout>(int slot, double number, NumberValue? box,
        ref FrameSlots slots, ref TFrame frame) where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        if (typeof(TLayout) != typeof(FrameSlots.Raw) && box is not null) slots.Initialize(slot, box, ref frame);
        else slots.CommitNumber(slot, number, ref frame);
    }
}
