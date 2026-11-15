using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeRegisterValue(int slot, NativeKind kind, int start, bool unsigned = false)
{
    internal int Slot { get; } = slot;
    internal bool Unsigned { get; } = unsigned;
    internal NativeKind Kind { get; } = kind;
    internal int Start { get; } = start;
    internal int End { get; set; } = start;
    internal int Register { get; set; } = -1;
    internal int Spill { get; set; } = -1;
}

internal readonly record struct NativeRegisterOperation(int Pc, NativeFastOperation Op, int Result, int A, int B, NativeNumberForm Form = NativeNumberForm.Float64);

internal sealed class NativeRegisterRegion
{
    internal required int Start { get; init; }
    internal required int End { get; init; }
    internal required NativeRegisterValue[] Values { get; init; }
    internal required NativeRegisterOperation[] Operations { get; init; }
    internal required int[] Inputs { get; init; }
    internal required int[] Outputs { get; init; }
    internal required int[] PendingOutputs { get; init; }
    internal int Spills { get; private set; }

    internal void Allocate()
    {
        List<NativeRegisterValue> active = [];
        foreach (NativeRegisterValue value in Values.OrderBy(value => value.Start))
        {
            active.RemoveAll(previous => previous.End <= value.Start);
            int register = Enumerable.Range(2, 4).FirstOrDefault(candidate => active.All(previous => previous.Register != candidate), -1);
            if (register < 0) value.Spill = Spills++;
            else { value.Register = register; active.Add(value); }
        }
    }
}

internal static class NativeRegisterPlan
{
    internal static NativeRegisterRegion[] Create(NativeExecutionFunction function, NativeFastOperation[] operations)
    {
        NativeControlFlow flow = new(function.Metadata);
        HashSet<int>[] live = LiveAfter(function.Metadata, flow);
        bool[] pending = PendingSlots(function, operations);
        List<NativeRegisterRegion> regions = [];
        foreach (NativeControlFlow.Block block in flow.Blocks)
        {
            int pc = block.Start;
            while (pc < block.End)
            {
                if (!Eligible(function, operations, pc)) { pc += function.Instructions[pc].Span; continue; }
                int start = pc, visits = 0;
                Dictionary<int, NativeKind> kinds = [];
                // A region is a safepoint interval, not an unbounded cancellation-free trace.
                while (pc < block.End && Eligible(function, operations, pc) && visits + function.Instructions[pc].Span <= 256
                    && Compatible(function.Instructions[pc], operations[pc], kinds))
                {
                    kinds[function.Instructions[pc].Result] = ResultKind(operations[pc]);
                    visits += function.Instructions[pc].Span;
                    pc += function.Instructions[pc].Span;
                }
                regions.Add(Build(function, operations, start, pc, live[pc - 1], pending));
            }
        }
        return regions.ToArray();
    }

    private static bool Eligible(NativeExecutionFunction function, NativeFastOperation[] operations, int pc) =>
        operations[pc] is not (NativeFastOperation.Bridge or NativeFastOperation.Nop or NativeFastOperation.Jump
            or NativeFastOperation.Branch or NativeFastOperation.NumberReturn or NativeFastOperation.BoolReturn
            or NativeFastOperation.NumberMove or NativeFastOperation.BoolMove or NativeFastOperation.IsNull)
        && function.CanWriteRaw(function.Instructions[pc].Result);

    private static NativeKind ResultKind(NativeFastOperation op) =>
        op.IsBoolResult() ? NativeKind.Bool : NativeKind.Number;

    private static bool Compatible(NativeExecutionInstruction instruction, NativeFastOperation op, Dictionary<int, NativeKind> kinds)
    {
        if (op is NativeFastOperation.NumberConstant or NativeFastOperation.BoolConstant) return true;
        NativeKind expected = op.ExpectsBoolInputs() ? NativeKind.Bool : NativeKind.Number;
        if (kinds.TryGetValue(instruction.A, out NativeKind first) && first != expected) return false;
        if (op.TakesSecondOperand() && kinds.TryGetValue(instruction.B, out NativeKind second) && second != expected) return false;
        kinds.TryAdd(instruction.A, expected);
        if (op.TakesSecondOperand()) kinds.TryAdd(instruction.B, expected);
        return true;
    }

    private static NativeRegisterRegion Build(NativeExecutionFunction function, NativeFastOperation[] operations,
        int start, int end, HashSet<int> live, bool[] pending)
    {
        List<NativeRegisterValue> values = [];
        List<NativeRegisterOperation> code = [];
        Dictionary<int, int> current = [];
        HashSet<int> written = [];
        List<int> inputs = [];
        int Read(int slot, NativeKind kind, bool unsigned)
        {
            if (!current.TryGetValue(slot, out int value))
            {
                value = values.Count;
                values.Add(new(slot, kind, -1, unsigned));
                current.Add(slot, value);
                inputs.Add(value);
            }
            values[value].End = code.Count;
            return value;
        }
        for (int pc = start; pc < end; pc += function.Instructions[pc].Span)
        {
            NativeExecutionInstruction instruction = function.Instructions[pc];
            NativeFastOperation op = operations[pc];
            NativeKind inputKind = op.ExpectsBoolInputs() ? NativeKind.Bool : NativeKind.Number;
            NativeNumberForm form = function.NumberForm(pc);
            int a = op is NativeFastOperation.NumberConstant or NativeFastOperation.BoolConstant ? -1
                : Read(instruction.A, inputKind, form.HasFlag(NativeNumberForm.UnsignedA));
            int b = op.TakesSecondOperand() ? Read(instruction.B, inputKind, form.HasFlag(NativeNumberForm.UnsignedB)) : -1;
            int result = values.Count;
            values.Add(new(instruction.Result, op.IsBoolResult() ? NativeKind.Bool : NativeKind.Number, code.Count, form.HasFlag(NativeNumberForm.UnsignedResult)));
            current[instruction.Result] = result;
            written.Add(instruction.Result);
            code.Add(new(pc, op, result, a, b, form));
        }
        int[] outputs = written.Where(live.Contains).Select(slot => current[slot]).Order().ToArray();
        foreach (int output in outputs) values[output].End = code.Count;
        NativeRegisterRegion region = new()
        {
            Start = start, End = end, Values = values.ToArray(), Operations = code.ToArray(), Inputs = inputs.ToArray(), Outputs = outputs,
            PendingOutputs = outputs.Where(value => pending[values[value].Slot]).Select(value => values[value].Slot).ToArray()
        };
        region.Allocate();
        return region;
    }

    private static bool[] PendingSlots(NativeExecutionFunction function, NativeFastOperation[] operations)
    {
        bool[] pending = new bool[function.Definition.Slots.Count];
        Array.Fill(pending, true, 0, function.Definition.CaptureCount + function.Definition.ParameterCount);
        bool changed;
        do
        {
            changed = false;
            for (int pc = 0; pc < function.Metadata.Length; pc += function.Instructions[pc].Span)
            {
                NativeExecutionInstruction lowered = function.Instructions[pc];
                NativeInstruction instruction = function.Metadata[pc + lowered.Span - 1] with
                { Op = lowered.Op, Result = lowered.Result, A = lowered.A, B = lowered.B, C = lowered.C };
                bool mayDefer = operations[pc] == NativeFastOperation.Bridge
                    || instruction.Op != NativeOp.Constant && NativeInstructionFacts.ValueReads(instruction).Any(slot => pending[slot]);
                if (!mayDefer) continue;
                foreach (int slot in NativeInstructionFacts.Definitions(instruction))
                    if (!pending[slot]) { pending[slot] = true; changed = true; }
            }
        } while (changed);
        return pending;
    }

    internal static HashSet<int>[] LiveAfter(NativeInstruction[] metadata, NativeControlFlow flow)
    {
        int count = metadata.Length;
        HashSet<int>[] before = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        HashSet<int>[] after = Enumerable.Range(0, count).Select(_ => new HashSet<int>()).ToArray();
        List<int>[] predecessors = Enumerable.Range(0, count).Select(_ => new List<int>()).ToArray();
        for (int pc = 0; pc < count; pc++)
            foreach (int next in flow.Successors[pc]) predecessors[next].Add(pc);
        Queue<int> work = new(Enumerable.Range(0, count).Reverse());
        bool[] queued = Enumerable.Repeat(true, count).ToArray();
        while (work.TryDequeue(out int pc))
        {
            queued[pc] = false;
            HashSet<int> live = [];
            foreach (int next in flow.Successors[pc]) live.UnionWith(before[next]);
            after[pc] = live;
            HashSet<int> input = new(live);
            input.ExceptWith(NativeInstructionFacts.Definitions(metadata[pc]));
            input.UnionWith(NativeInstructionFacts.Reads(metadata[pc]));
            if (before[pc].SetEquals(input)) continue;
            before[pc] = input;
            foreach (int previous in predecessors[pc])
                if (!queued[previous]) { queued[previous] = true; work.Enqueue(previous); }
        }
        return after;
    }
}
