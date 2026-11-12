using Msync.Core;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private enum InlineNative : byte { SnapshotFront, SnapshotAt }
    private enum VectorOperation : byte { Get, Set, Push, Pop, Size, Insert, Remove }

    private bool TryInlineNative<TFrame>(Module image, Instruction instruction, TFrame frame,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken) where TFrame : struct, RuntimeProfile.IFrame
    {
        InlineNative operation;
        IReadOnlyList<Value>? snapshot = null;
        double operand = 0;
        // Only selected, already available representations use this path. A
        // miss leaves every operand intact for generic normalization/deferral.
        switch (instruction.Text, instruction.A)
        {
            case ("storage.vector_get", 2):
                return TryInlineVector(image, instruction, frame, ref stack, cancellationToken, VectorOperation.Get);
            case ("storage.vector_set", 3):
                return TryInlineVector(image, instruction, frame, ref stack, cancellationToken, VectorOperation.Set);
            case ("storage.vector_push", 2):
                return TryInlineVector(image, instruction, frame, ref stack, cancellationToken, VectorOperation.Push);
            case ("storage.vector_pop", 1):
                return TryInlineVector(image, instruction, frame, ref stack, cancellationToken, VectorOperation.Pop);
            case ("storage.vector_size", 1):
                return TryInlineVector(image, instruction, frame, ref stack, cancellationToken, VectorOperation.Size);
            case ("storage.vector_insert", 3):
                return TryInlineVector(image, instruction, frame, ref stack, cancellationToken, VectorOperation.Insert);
            case ("storage.vector_remove", 2):
                return TryInlineVector(image, instruction, frame, ref stack, cancellationToken, VectorOperation.Remove);
            case ("slots.get", 2) or ("slots.count", 1):
                return TryInlineSlots(image, instruction, frame, ref stack, cancellationToken);
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
            default: return false;
        }

        using var span = frame.BeginInlineNative(image, instruction);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            stack.Discard(instruction.A);
            switch (operation)
            {
                case InlineNative.SnapshotFront: stack.Add(NativeRegistry.SnapshotFront(snapshot!, instruction.Text! == "ranges.front" ? "front" : "first")); break;
                case InlineNative.SnapshotAt: stack.Add(NativeRegistry.SnapshotAt(snapshot!, operand)); break;
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

    private bool TryInlineVector<TFrame>(Module image, Instruction instruction, TFrame frame,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken, VectorOperation operation)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        if (stack.PeekReference(instruction.A - 1) is not DenseVectorValue owner) return false;
        double index = 0;
        int indexDepth = operation switch
        {
            VectorOperation.Get or VectorOperation.Remove => 0,
            VectorOperation.Set or VectorOperation.Insert => 1,
            _ => -1
        };
        if (indexDepth >= 0 && !stack.TryPeekNumber(indexDepth, out index)
            || operation is VectorOperation.Set or VectorOperation.Push or VectorOperation.Insert
                && stack.PeekReference(0) is CellValue or DeferredValue)
            return false;

        using var span = frame.BeginInlineNative(image, instruction);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            double number = 0;
            Value? result = null;
            int remaining = instruction.A;
            switch (operation)
            {
                case VectorOperation.Get:
                    result = owner.GetOperand(index, out number);
                    break;
                case VectorOperation.Set:
                    if (owner.NumberValues is not null && stack.TryPeekNumber(0, out double scalar))
                        owner.SetNumber(index, scalar);
                    else
                    {
                        owner.Set(index, stack.Pop(), context);
                        remaining--;
                    }
                    result = Value.Null;
                    break;
                case VectorOperation.Insert:
                    if (owner.NumberValues is not null && stack.TryPeekNumber(0, out double inserted))
                        owner.InsertNumber(index, inserted);
                    else
                    {
                        owner.Insert(index, stack.Pop(), context);
                        remaining--;
                    }
                    result = Value.Null;
                    break;
                case VectorOperation.Push:
                    if (owner.NumberValues is not null && stack.TryPeekNumber(0, out double appended))
                        number = owner.PushNumber(appended);
                    else
                    {
                        number = owner.Push(stack.Pop(), context);
                        remaining--;
                    }
                    break;
                case VectorOperation.Pop:
                    result = owner.PopOperand(out number);
                    break;
                case VectorOperation.Remove:
                    result = owner.RemoveOperand(index, out number);
                    break;
                case VectorOperation.Size:
                    number = owner.Count;
                    break;
            }
            stack.Discard(remaining);
            if (result is null) stack.AddNumber(number);
            else stack.Add(result);
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
