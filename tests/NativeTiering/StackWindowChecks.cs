using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class StackWindowChecks
{
    internal static unsafe void Run()
    {
        using NativeFrameStack? owner = NativeFrameStack.Open();
        NativeFrameStack stack = NativeFrameStack.Current;
        NativeGuestObjectTable arena = new();
        NativeType number = NativeType.Of(NativeKind.Number);
        NativeLayout one = new(number);
        NativeAllocation caller = new(one.AppendRaw(64 * 1024 - 8, 8, out _), allocate: false);
        NativeAllocation callee = new(new NativeLayout(number, number), allocate: false);
        NativeFrameStack.Lease before = stack.Push(caller, arena);
        NativeStackHeader* current = stack.FramePointer;
        caller.Field(0).Store(NativeValue.FromNumber(17));
        NativeOffset full = stack.Cursor->Top;
        if (stack.TryReserve(new(callee.Layout), arena, out _) || stack.FramePointer != current || stack.Cursor->Top != full)
            throw new InvalidOperationException("A failed unattached reservation changed the active segment.");
        NativeFrameStack.Lease child = stack.Push(callee, arena, new(caller, new(64 * 1024 - 8, 0)));
        if (child.Window != NativeWindowDisposition.Capacity || stack.FramePointer->Previous != current
            || stack.FramePointer->Data == current->Data + 64 * 1024 - 8 || caller.Field(0).Load().AsNumber() != 17)
            throw new InvalidOperationException("A window that crosses the segment boundary changed its caller.");
        stack.Pop(child, callee);
        if (stack.FramePointer != current || caller.Field(0).Load().AsNumber() != 17)
            throw new InvalidOperationException("RET did not restore the frame after a capacity miss.");
        stack.Pop(before, caller);
        if (stack.FramePointer != null) throw new InvalidOperationException("Native frame chain is not empty after RET.");

        CheckNested(stack, arena, number, one);
        CheckAttachment(stack, arena, one);
    }

    private static unsafe void CheckAttachment(NativeFrameStack stack, NativeGuestObjectTable arena, NativeLayout layout)
    {
        NativeAllocation caller = new(layout, allocate: false);
        NativeFrameStack.Lease outer = stack.Push(caller, arena);
        NativeStackHeader* parent = stack.FramePointer;
        NativeOffset prior = stack.Cursor->Top;
        if (!stack.TryPlanReservation(new(layout), arena, out NativeFrameStack.Lease reservation, out NativeOffset start))
            throw new InvalidOperationException("A small frame did not fit in the active segment.");
        if (stack.FramePointer != parent || stack.Cursor->Top != prior)
            throw new InvalidOperationException("Reservation planning published the child frame.");
        stack.PublishReservation(reservation, start, arena);
        NativeStackHeader* child = stack.FramePointer;
        *(double*)child->Data = 31;
        *(byte*)child->Initialization = 1;
        NativeAllocation view = new(layout, allocate: false);
        stack.AttachReserved(reservation, view, arena);
        if (stack.FramePointer != child || view.Storage.PendingCell != (nint)child || view.Field(0).Load().AsNumber() != 31)
            throw new InvalidOperationException("Attaching a frame moved or reinitialized its storage.");
        view.Storage.ResetFrame();
        stack.Pop(reservation, view);
        if (stack.FramePointer != parent) throw new InvalidOperationException("The attached frame did not restore its caller.");
        stack.Pop(outer, caller);
    }

    private static unsafe void CheckNested(NativeFrameStack stack, NativeGuestObjectTable arena, NativeType number, NativeLayout leaf)
    {
        NativeLayout logical = new(number, number);
        var producers = new HashSet<int> { 1 };
        NativeLayout prefix = logical.WithoutWindowProducers(producers, out int[] keep);
        NativeLayout layout = prefix.AppendArgumentWindow(logical, keep, [1], producers, out NativeOffset outgoing, out _);
        NativeAllocation caller = new(layout, allocate: false);
        NativeAllocation child = new(layout, allocate: false);
        NativeAllocation grandchild = new(leaf, allocate: false);
        NativeFrameStack.Lease before = stack.Push(caller, arena);
        NativeStackHeader* first = stack.FramePointer;
        caller.Field(0).Store(NativeValue.FromNumber(17));
        caller.Field(1).Store(NativeValue.FromNumber(29));

        NativeFrameStack.Lease nested = stack.Push(child, arena, new(caller, outgoing));
        NativeStackHeader* second = stack.FramePointer;
        if (nested.Window != NativeWindowDisposition.Adopted || second->Previous != first
            || second->Data != first->Data + outgoing.Bytes || child.Field(0).Load().AsNumber() != 29)
            throw new InvalidOperationException("CALL did not place the child header in the caller's reserved prefix.");
        child.Field(1).Store(NativeValue.FromNumber(31));

        NativeFrameStack.Lease inner = stack.Push(grandchild, arena, new(child, outgoing));
        if (inner.Window != NativeWindowDisposition.Adopted || stack.FramePointer->Previous != second
            || grandchild.Field(0).Load().AsNumber() != 31 || second->Previous != first)
            throw new InvalidOperationException("A nested adoption overwrote a caller's live header or parameter.");
        stack.Pop(inner, grandchild);
        if (stack.FramePointer != second || child.Field(0).Load().AsNumber() != 29)
            throw new InvalidOperationException("RET did not restore the middle frame after nested adoption.");
        stack.Pop(nested, child);
        if (stack.FramePointer != first || caller.Field(0).Load().AsNumber() != 17)
            throw new InvalidOperationException("RET did not restore the first frame after nested adoption.");
        stack.Pop(before, caller);
    }
}
