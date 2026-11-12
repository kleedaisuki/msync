using System.Collections.Immutable;

namespace Msync.Optimization.Execution;

/// <summary>A private ordered Number recurrence over the yielded Scope rows.</summary>
public sealed record ReadonlyNumberFold(int Exit, int BodyLength, int Accumulator, ImmutableArray<int> StoreSlots,
    ImmutableArray<int> InputSlots, ImmutableArray<int> Fields,
    ImmutableArray<ReadonlyNumberFold.Step> Steps, int Result)
{
    public ImmutableArray<int> Admissions { get; init; } = [];

    // Arithmetic operands name earlier steps in canonical push order. Field
    // and Input use Left to name their respective input array entries.
    public readonly record struct Step(Operation Op, int Left = 0, int Right = 0, double Constant = 0);
    public enum Operation : byte { Accumulator, Field, Input, Constant, Add, Subtract, Multiply, Divide, Negate }

    internal static bool MayStart(ReadOnlySpan<Instruction> code, int entry)
    {
        if (code[entry..] is not [
            { Op: OpCode.GetIterator }, { Op: OpCode.InitializeLocal, A: var local },
            { Op: OpCode.IteratorNext, A: var iterator, B: var exit }, ..]
            || local != iterator || exit <= entry + 3 || exit > code.Length) return false;

        // Most iterators never enter the CFG importer. This is only a cheap
        // shape filter; actual stack values and edges are verified below.
        bool projected = false;
        for (int pc = entry + 3; pc < exit; pc++)
        {
            var instruction = code[pc];
            switch (instruction.Op)
            {
                case OpCode.LoadField:
                    projected = true;
                    break;
                case OpCode.Jump:
                    return projected && instruction.A == entry + 2;
                case OpCode.LoadLocal or OpCode.InitializeLocal or OpCode.StoreLocal or OpCode.Constant
                    or OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide or OpCode.Negate
                    or OpCode.Pop or OpCode.Dup or OpCode.Nop or OpCode.AdmitScope:
                    break;
                default: return false;
            }
        }
        return false;
    }

    internal static ReadonlyNumberFold? Find(Module image, FunctionCode function, Instruction[] code,
        ExecutionRegion graph, int entry)
    {
        int headerPc = entry + 2;
        int iterator = code[headerPc].A;
        int exit = code[headerPc].B;
        if (graph.OperationAt(entry) is not { StackBefore.Depth: 1 } get
            || graph.OperationAt(entry + 1) is not { } init || init.Inputs[0] != get.Result
            || graph.BlockAt(headerPc) is not { Parameters.Length: 0 } header || header.StartPc != headerPc
            || header.Operations is not [{ Instruction.Op: OpCode.IteratorNext }]
            || header.Successors is not [
                { Kind: ExecutionEdgeKind.IteratorExhausted, Arguments.Depth: 0 },
                { Kind: ExecutionEdgeKind.IteratorYield, Arguments.Depth: 1 }
            ] || header.Successors[0].TargetPc != exit || header.Successors[1].TargetPc != headerPc + 1
            || graph.BlockAt(headerPc + 1) is not { Parameters.Length: 1, Successors.Length: 1 } row
            || row.StartPc != headerPc + 1 || row.Successors[0] is not
                { Kind: ExecutionEdgeKind.Jump, Arguments.Depth: 0, TargetPc: var backedge } || backedge != headerPc)
            return null;

        int accumulator = FindAccumulator(function, code, row, iterator, exit, out var writes);
        if (accumulator < 0) return null;

        var recurrence = new Recurrence(image, row.Parameters[0], accumulator, writes);
        foreach (var op in row.Operations)
            if (!recurrence.Append(op)) return null;
        return recurrence.Finish(exit, row.Operations.Length);
    }

    private static int FindAccumulator(FunctionCode function, ReadOnlySpan<Instruction> code,
        ExecutionBlock row, int iterator, int exit, out HashSet<int> writes)
    {
        writes = [iterator];
        foreach (var op in row.Operations)
        {
            if (op.Instruction is not { Op: OpCode.InitializeLocal or OpCode.StoreLocal, A: var slot }) continue;
            if (slot == iterator) return -1;
            writes.Add(slot);
        }

        int firstPrivate = function.CaptureCount + function.ParameterCount;
        int accumulator = -1;
        foreach (int slot in writes)
        {
            if (slot < firstPrivate || slot >= function.LocalCount) return -1;
            if (!BoundLocalLiveness.ReadsBeforeWrite(code, exit, slot)) continue;
            if (accumulator >= 0 || slot == iterator) return -1;
            accumulator = slot;
        }

        foreach (var instruction in code)
            if (instruction.Op is OpCode.LoadCell or OpCode.LoadStorage && writes.Contains(instruction.A))
                return -1;
        return accumulator;
    }

    private sealed class Recurrence(Module image, ExecutionValueId row, int accumulator, HashSet<int> writes)
    {
        private const byte UsesRow = 1;
        private const byte UsesAccumulator = 2;
        private static readonly Symbol Row = new(-1, UsesRow);
        private readonly record struct Symbol(int Step, byte Uses)
        {
            internal bool IsNumber => Step >= 0;
        }

        private readonly Dictionary<ExecutionValueId, Symbol> values = new() { [row] = Row };
        private readonly Dictionary<int, Symbol> locals = [];
        private readonly Dictionary<int, int> inputs = [];
        private readonly Dictionary<int, int> fields = [];
        private readonly HashSet<int> admissions = [];
        private readonly List<Step> steps = [];

        internal bool Append(ExecutionOperation op)
        {
            var instruction = op.Instruction;
            Symbol Input(int index) => values[op.Inputs[index]];
            Symbol result;
            switch (instruction.Op)
            {
                case OpCode.LoadLocal:
                    if (!TryLoad(instruction.A, out result)) return false;
                    break;
                case OpCode.InitializeLocal or OpCode.StoreLocal:
                    locals[instruction.A] = Input(0);
                    return true;
                case OpCode.AdmitScope when Input(0) == Row:
                    admissions.Add(instruction.A);
                    result = Row;
                    break;
                case OpCode.LoadField when Input(0) == Row:
                    result = Add(new(Operation.Field, Index(fields, instruction.A)), UsesRow);
                    break;
                case OpCode.Constant when image.Constants[instruction.A] is NumberValue number:
                    result = Add(new(Operation.Constant, Constant: number.Data));
                    break;
                case OpCode.Add or OpCode.Subtract or OpCode.Multiply or OpCode.Divide:
                    Symbol left = Input(0), right = Input(1);
                    if (!left.IsNumber || !right.IsNumber) return false;
                    var kind = instruction.Op switch
                    {
                        OpCode.Add => Operation.Add,
                        OpCode.Subtract => Operation.Subtract,
                        OpCode.Multiply => Operation.Multiply,
                        _ => Operation.Divide
                    };
                    result = Add(new(kind, left.Step, right.Step), (byte)(left.Uses | right.Uses));
                    break;
                case OpCode.Negate when Input(0) is { IsNumber: true } input:
                    result = Add(new(Operation.Negate, input.Step), input.Uses);
                    break;
                case OpCode.Dup or OpCode.Pop or OpCode.Nop or OpCode.Jump:
                    return true;
                default: return false;
            }
            values.Add(op.Result!.Value, result);
            return true;
        }

        internal ReadonlyNumberFold? Finish(int exit, int bodyLength)
        {
            if (!locals.TryGetValue(accumulator, out var result) || !result.IsNumber
                || result.Uses != (UsesRow | UsesAccumulator)) return null;

            // Guard even InitializeLocal: eliding it may leave an old Cell
            // for a subsequent canonical StoreLocal to write through.
            return new(exit, bodyLength, accumulator, [.. writes.Order()], OrderedKeys(inputs), OrderedKeys(fields), [.. steps], result.Step) { Admissions = [.. admissions] };
        }

        private bool TryLoad(int slot, out Symbol value)
        {
            if (locals.TryGetValue(slot, out value)) return true;
            if (slot == accumulator)
            {
                value = Add(new(Operation.Accumulator), UsesAccumulator);
                return true;
            }
            // Reading an unwritten temporary would make it an additional
            // loop-carried state; only the accumulator may carry across rows.
            if (writes.Contains(slot)) return false;
            value = Add(new(Operation.Input, Index(inputs, slot)));
            return true;
        }

        private Symbol Add(Step step, byte uses = 0)
        {
            int index = steps.Count;
            steps.Add(step);
            return new(index, uses);
        }

        private static int Index<T>(Dictionary<T, int> table, T key) where T : notnull
        {
            if (table.TryGetValue(key, out int index)) return index;
            index = table.Count;
            table.Add(key, index);
            return index;
        }

        private static ImmutableArray<T> OrderedKeys<T>(Dictionary<T, int> table) where T : notnull =>
            [.. table.OrderBy(entry => entry.Value).Select(entry => entry.Key)];
    }
}
