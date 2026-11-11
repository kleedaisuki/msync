using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private enum InlineNative : byte { BufferGet, BufferSet, BufferSize, SnapshotFront, SnapshotAt, Floor }

    private bool TryInlineNative<TFrame>(Module image, Instruction instruction, TFrame frame,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken) where TFrame : struct, RuntimeProfile.IFrame
    {
        InlineNative operation;
        List<Value>? items = null;
        RangeValue? range = null;
        IReadOnlyList<Value>? snapshot = null;
        double operand = 0;
        // Only selected, already available representations use this path. A
        // miss leaves every operand intact for generic normalization/deferral.
        switch (instruction.Text, instruction.A)
        {
            case ("storage.buffer_get", 2):
                if (stack.PeekReference(1) is not RangeValue receiver || !IndexedStorageOperations.TryGetItems(receiver, out items)
                    || !stack.TryPeekNumber(0, out operand)) return false;
                operation = InlineNative.BufferGet;
                break;
            case ("storage.buffer_set", 3):
                if (stack.PeekReference(2) is not RangeValue target || !IndexedStorageOperations.TryGetItems(target, out items)
                    || stack.PeekReference(0) is CellValue or DeferredValue || !stack.TryPeekNumber(1, out operand)) return false;
                range = target;
                operation = InlineNative.BufferSet;
                break;
            case ("storage.buffer_size", 1):
                if (stack.PeekReference(0) is not RangeValue sized || !IndexedStorageOperations.TryGetItems(sized, out items))
                    return false;
                operation = InlineNative.BufferSize;
                break;
            case ("ranges.front" or "ranges.first", 1):
                if (stack.PeekReference(0) is not RangeValue { OwnedSnapshot: { } owned }) return false;
                snapshot = owned;
                operation = InlineNative.SnapshotFront;
                break;
            case ("ranges.at", 2):
                if (stack.PeekReference(1) is not RangeValue { OwnedSnapshot: { } indexed }
                    || !stack.TryPeekNumber(0, out operand)) return false;
                snapshot = indexed;
                operation = InlineNative.SnapshotAt;
                break;
            case ("math.floor", 1):
                if (!stack.TryPeekNumber(0, out operand)) return false;
                operation = InlineNative.Floor;
                break;
            default: return false;
        }

        using var span = frame.BeginInlineNative(image, instruction);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var replacement = operation == InlineNative.BufferSet ? stack.Pop() : null;
            stack.Discard(instruction.A - (replacement is null ? 0 : 1));
            switch (operation)
            {
                case InlineNative.BufferGet: stack.Add(IndexedStorageOperations.Get(items!, operand)); break;
                case InlineNative.BufferSet: stack.Add(IndexedStorageOperations.Set(range!, items!, operand, replacement!, context)); break;
                case InlineNative.BufferSize: stack.AddNumber(items!.Count); break;
                case InlineNative.SnapshotFront: stack.Add(NativeRegistry.SnapshotFront(snapshot!, instruction.Text! == "ranges.front" ? "front" : "first")); break;
                case InlineNative.SnapshotAt: stack.Add(NativeRegistry.SnapshotAt(snapshot!, operand)); break;
                case InlineNative.Floor: stack.AddNumber(Math.Floor(operand)); break;
            }
            return true;
        }
        catch (Exception error)
        {
            span?.Fail(error);
            if (error is not OperationCanceledException) ReportNativeFailure(instruction.Text!, error);
            throw;
        }
    }
}
