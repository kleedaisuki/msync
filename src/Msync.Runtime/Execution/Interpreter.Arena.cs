using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private bool TryInlineArena<TFrame, TLayout>(Module image, Instruction instruction, TFrame frame,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken, int bound)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        var operation = (ArenaOperation)(byte)bound;
        var transport = typeof(TLayout) == typeof(FrameSlots.RawWord) ? (WordTransport)(bound >> 8) : WordTransport.None;
        if (stack.PeekReference(instruction.A - 1) is not SetArena owner)
            return DeclineWordTransport(ref stack, instruction.A, transport);

        SetArena<double>? numeric = null;
        SetArena<double?>? nullable = null;
        if (operation is ArenaOperation.Key or ArenaOperation.Allocate)
        {
            numeric = owner as SetArena<double>;
            if (numeric is null) nullable = owner as SetArena<double?>;
        }
        double index = 0, operand = 0;
        uint wordIndex = 0, wordOperand = 0;
        bool rawIndex = (transport & WordTransport.Second) != 0;
        bool rawOperand = (transport & (WordTransport.Third | WordTransport.Fourth)) != 0;
        bool flag = false;
        Value? key = null;
        if (operation == ArenaOperation.Allocate && numeric is null)
        {
            if (nullable is null || !stack.TryPeekNumber(1, out index))
            {
                key = stack.PeekReference(1);
                if (key is null or CellValue or DeferredValue) return DeclineWordTransport(ref stack, instruction.A, transport);
            }
        }
        else if (instruction.A > 1 && (rawIndex
            ? !stack.TryPeekWord32(instruction.A - 2, out wordIndex)
            : !stack.TryPeekNumber(instruction.A - 2, out index))) return DeclineWordTransport(ref stack, instruction.A, transport);
        if (operation is ArenaOperation.Allocate or ArenaOperation.SetChild or ArenaOperation.SetParent
            && (rawOperand ? !stack.TryPeekWord32(0, out wordOperand) : !stack.TryPeekNumber(0, out operand))) return DeclineWordTransport(ref stack, instruction.A, transport);
        if (operation is ArenaOperation.Child or ArenaOperation.SetChild or ArenaOperation.SetRed)
        {
            if (stack.PeekReference(operation == ArenaOperation.SetChild ? 1 : 0) is not BoolValue bit)
                return DeclineWordTransport(ref stack, instruction.A, transport);
            flag = bit.Data;
        }

        using var span = frame.BeginInlineNative(image, instruction);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Value? reference = null;
            double number = 0;
            int integer = 0;
            if (operation == ArenaOperation.Allocate)
            {
                int parent = rawOperand ? ExactWordIndex(wordOperand) : SetArenaOperations.ExactIndex(operand);
                integer = key is not null ? owner.Allocate(SetArenaOperations.KeyFor(owner, key, context), parent)
                    : numeric is not null ? numeric.AllocateRaw(index, parent)
                    : nullable!.AllocateRaw(index, parent);
            }
            else
            {
                int row = rawIndex ? ExactWordIndex(wordIndex) : SetArenaOperations.ExactIndex(index);
                switch (operation)
                {
                    case ArenaOperation.Root: integer = owner.Root; break;
                    case ArenaOperation.Size: integer = owner.Count; break;
                    case ArenaOperation.Key:
                        if (numeric is not null) number = numeric.RawKey(row);
                        else if (nullable is null) reference = owner.Key(row);
                        else if (nullable.RawKey(row) is { } value) number = value;
                        else reference = Value.Null;
                        break;
                    case ArenaOperation.Child: integer = owner.Child(row, flag); break;
                    case ArenaOperation.Parent: integer = owner.Parent(row); break;
                    case ArenaOperation.Red: reference = owner.Red(row) ? Value.True : Value.False; break;
                    case ArenaOperation.Release: owner.Release(row); reference = Value.Null; break;
                    case ArenaOperation.Clear: owner.Clear(); reference = Value.Null; break;
                    case ArenaOperation.SetRoot: owner.SetRoot(row); reference = Value.Null; break;
                    case ArenaOperation.SetChild:
                        owner.SetChild(row, flag, rawOperand ? ExactWordIndex(wordOperand) : SetArenaOperations.ExactIndex(operand));
                        reference = Value.Null; break;
                    case ArenaOperation.SetParent:
                        owner.SetParent(row, rawOperand ? ExactWordIndex(wordOperand) : SetArenaOperations.ExactIndex(operand));
                        reference = Value.Null; break;
                    case ArenaOperation.SetRed: owner.SetRed(row, flag); reference = Value.Null; break;
                }
            }
            stack.Discard(instruction.A);
            if (reference is not null) stack.Add(reference);
            else if ((transport & WordTransport.Result) != 0) stack.AddWord32((uint)integer);
            else stack.AddNumber(operation == ArenaOperation.Key ? number : integer);
            if (rawIndex || rawOperand) frame.ManagedWordIndex((rawIndex ? 1 : 0) + (rawOperand ? 1 : 0));
            return true;
        }
        catch (Exception error)
        {
            span?.Fail(error);
            if (error is not OperationCanceledException) ReportNativeFailure(instruction.Text!, error);
            throw;
        }
    }
    private static int ExactWordIndex(uint word) => word <= int.MaxValue
        ? (int)word : SetArenaOperations.ExactIndex(word);
}
