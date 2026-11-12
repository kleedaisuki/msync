namespace Msync.Optimization.Execution;

internal sealed partial class IndexedMethodImporter
{
    private readonly record struct WordInput(int Slot, bool ShiftCount);
    private Dictionary<int, WordInput>? wordInputs;
    private Dictionary<int, bool>? scalarAssignments;
    private int scalarIntrinsics;

    private IEnumerable<int> WordParameters() => wordInputs is { } words
        ? words.Values.Select(value => value.Slot) : [];

    private IEnumerable<IndexedMethodPlan.Input> WordGuards() => wordInputs is { } words
        ? words.Select(pair => new IndexedMethodPlan.Input(pair.Key, NativeWorkerType.Word32, pair.Value.ShiftCount)) : [];

    private void TrackScalarWrite(int slot)
    {
        var assignments = scalarAssignments ??= [];
        if (!assignments.TryAdd(slot, true)) assignments[slot] = false;
    }

    private bool CanAliasScalar(int slot, Value value) =>
        value.Kind is ValueKind.Projection or ValueKind.NumberLiteral
        && scalarAssignments?.GetValueOrDefault(slot) == true;

    private bool ScalarIntrinsic(ExecutionOperation operation)
    {
        if (ExecutionNumericOperation.Bind(operation.Instruction) is not
            { Representation: NumericRepresentation.Word32, Arity: 1 or 2 } primitive)
            return false;
        var instruction = new NativeWorkerInstruction(NativeWorkerOp.Word32Intrinsic, Intrinsic: primitive.Operation);
        if (!Word(Operand(operation, 0), shiftCount: false, out int left)) return false;
        int right = 0;
        if (primitive.Arity == 2 && !Word(Operand(operation, 1), instruction.ReadsWord32Count, out right))
            return false;
        int resultSlot = NewSlot(NativeWorkerType.Word32);
        Emit(instruction with { Destination = resultSlot, Left = left, Right = right });
        scalarIntrinsics++;
        return Set(operation, new(ValueKind.Word, resultSlot));
    }

    private bool Word(Value value, bool shiftCount, out int register)
    {
        register = value.Id;
        if (value.Kind == ValueKind.Word) return true;
        if (value.Kind == ValueKind.NumberLiteral)
        {
            double number = ((NumberValue)image.Constants[value.Id]).Data;
            double maximum = shiftCount ? 31 : uint.MaxValue;
            if (!(number >= 0 && number <= maximum && number == Math.Truncate(number))) return false;
            register = NewSlot(NativeWorkerType.Word32);
            Emit(new(NativeWorkerOp.Word32Constant, register, Data: unchecked((int)(uint)number)));
            return true;
        }
        if (value.Kind != ValueKind.Projection) return false;
        var words = wordInputs ??= [];
        if (!words.TryGetValue(value.Id, out var input))
        {
            input = new(NewSlot(NativeWorkerType.Word32), shiftCount);
            words.Add(value.Id, input);
        }
        else if (shiftCount && !input.ShiftCount) words[value.Id] = input with { ShiftCount = true };
        register = input.Slot;
        return true;
    }

    private readonly record struct WordRange(uint Min, uint Max)
    {
        internal static readonly WordRange Unknown = new(0, uint.MaxValue);
        internal WordRange Join(WordRange other) => new(Math.Min(Min, other.Min), Math.Max(Max, other.Max));
    }

    private static bool ProveWordCounts(NativeWorkerPlan plan, IReadOnlyList<IndexedMethodPlan.Input> inputs)
    {
        if (!plan.Instructions.Any(op => op.ReadsWord32Count)) return true;
        var code = plan.Instructions;
        var states = new WordRange[]?[code.Length];
        var entry = new WordRange[plan.Slots.Length];
        Array.Fill(entry, WordRange.Unknown);
        for (int i = 0; i < inputs.Count; i++)
            if (inputs[i].ShiftCount) entry[plan.Parameters[i]] = new(0, 31);
        Queue<int> pending = new();
        Merge(0, entry);
        while (pending.TryDequeue(out int pc))
        {
            var outgoing = (WordRange[])states[pc]!.Clone();
            var operation = code[pc];
            switch (operation.Op)
            {
                case NativeWorkerOp.Word32Constant:
                    uint constant = unchecked((uint)operation.Data);
                    outgoing[operation.Destination] = new(constant, constant);
                    break;
                case NativeWorkerOp.Move when plan.Slots[operation.Destination] == NativeWorkerType.Word32:
                    outgoing[operation.Destination] = outgoing[operation.Left];
                    break;
                case NativeWorkerOp.Word32Intrinsic:
                    outgoing[operation.Destination] = WordBounds(operation.Intrinsic,
                        outgoing[operation.Left], outgoing[operation.Right]);
                    break;
                case NativeWorkerOp.Return:
                    continue;
                case NativeWorkerOp.Jump:
                    Merge(operation.Data, outgoing);
                    continue;
                case NativeWorkerOp.BranchIfFalse or NativeWorkerOp.BranchIfTrue:
                    Merge(operation.Data, outgoing);
                    break;
            }
            Merge(pc + 1, outgoing);
        }
        for (int pc = 0; pc < code.Length; pc++)
            if (code[pc] is { ReadsWord32Count: true } op && states[pc] is { } state && state[op.Right].Max > 31)
                return false;
        return true;

        void Merge(int pc, WordRange[] incoming)
        {
            if (states[pc] is not { } known)
            {
                states[pc] = (WordRange[])incoming.Clone();
                pending.Enqueue(pc);
                return;
            }
            bool changed = false;
            for (int i = 0; i < known.Length; i++)
            {
                var joined = known[i].Join(incoming[i]);
                if (known[i] == joined) continue;
                known[i] = joined;
                changed = true;
            }
            if (changed) pending.Enqueue(pc);
        }
    }

    private static WordRange WordBounds(NumericIntrinsic operation, WordRange left, WordRange right) => operation switch
    {
        NumericIntrinsic.And => new(0, Math.Min(left.Max, right.Max)),
        NumericIntrinsic.Or => new(Math.Max(left.Min, right.Min), BitCover(left.Max) | BitCover(right.Max)),
        NumericIntrinsic.Xor => new(0, BitCover(left.Max) | BitCover(right.Max)),
        NumericIntrinsic.Not => new(~left.Max, ~left.Min),
        NumericIntrinsic.ShiftRight when right.Max < 32
            => new(left.Min >> (int)right.Max, left.Max >> (int)right.Min),
        NumericIntrinsic.ShiftRightSigned when right.Max < 32 && left.Max <= int.MaxValue
            => new(left.Min >> (int)right.Max, left.Max >> (int)right.Min),
        NumericIntrinsic.PopCount or NumericIntrinsic.LeadingZeroCount or NumericIntrinsic.TrailingZeroCount => new(0, 32),
        _ => WordRange.Unknown,
    };

    private static uint BitCover(uint upper) => upper == 0 ? 0U
        : uint.MaxValue >> System.Numerics.BitOperations.LeadingZeroCount(upper);
}
