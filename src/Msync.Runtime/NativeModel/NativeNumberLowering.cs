using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal static class NativeNumberLowering
{
    private const ulong ExactLimit = 1UL << 53;
    private readonly record struct Range(ulong Min, ulong Max, bool Known = true)
    {
        internal static Range Join(Range a, Range b) => a.Known && b.Known
            ? new(Math.Min(a.Min, b.Min), Math.Max(a.Max, b.Max)) : default;
    }

    internal static NativeNumberForm[] Analyze(NativeProgram program, NativeFunctionCode function, NativeExecutionInstruction[] code, CompilationTrace? trace = null)
    {
        NativeNumberForm[] result = new NativeNumberForm[code.Length];
        if (code.Length == 0) return result;
        NativeControlFlow flow = new(function.Instructions);
        List<int>[] predecessors = Enumerable.Range(0, flow.Blocks.Count).Select(_ => new List<int>()).ToArray();
        for (int block = 0; block < flow.Blocks.Count; block++)
            foreach (int successor in flow.Blocks[block].Successors) predecessors[successor].Add(block);
        Range[]?[] exits = new Range[flow.Blocks.Count][];
        Range[]?[] entries = new Range[flow.Blocks.Count][];
        Dictionary<(int From, int To), (int Slot, Range Range)[]> refinements = [];
        HashSet<int> exposed = function.Instructions.Where(item => item.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(item => item.A).Concat(Enumerable.Range(0, function.CaptureCount)).ToHashSet();
        for (int slot = 0; slot < function.Slots.Count; slot++)
            if (function.Slots[slot].Escapes || function.Storage!.Slots[slot].HasFlag(NativeSlotStorage.Escapes)) exposed.Add(slot);
        if (function.Storage!.Instructions.Any(item => item.HasFlag(NativeInstructionStorage.FrameAlias)))
            exposed.UnionWith(Enumerable.Range(0, function.Slots.Count));
        int rounds = 0, transfers = 0;
        bool changed;
        do
        {
            rounds++;
            changed = false;
            for (int block = 0; block < flow.Blocks.Count; block++)
            {
                Range[]? state = block == 0 ? new Range[function.Slots.Count] : null;
                foreach (int predecessor in predecessors[block])
                {
                    if (exits[predecessor] is not { } exit) continue;
                    Range[] candidate = (Range[])exit.Clone();
                    if (refinements.TryGetValue((predecessor, block), out var edge))
                        foreach ((int slot, Range value) in edge) candidate[slot] = value;
                    if (state is null) state = candidate;
                    else for (int slot = 0; slot < state.Length; slot++) state[slot] = Range.Join(state[slot], candidate[slot]);
                }
                if (state is null) continue;
                if (entries[block] is { } previous && predecessors[block].Any(pred => pred >= block))
                    for (int slot = 0; slot < state.Length; slot++)
                        if (previous[slot].Known && state[slot].Known)
                            state[slot] = new(state[slot].Min < previous[slot].Min ? 0 : state[slot].Min,
                                state[slot].Max > previous[slot].Max ? ExactLimit : state[slot].Max);
                if (entries[block] is { } before && before.SequenceEqual(state)) continue;
                entries[block] = (Range[])state.Clone();
                NativeControlFlow.Block body = flow.Blocks[block];
                var oldEdges = body.Successors.ToDictionary(successor => successor, successor => refinements.GetValueOrDefault((block, successor)));
                foreach (int successor in body.Successors) refinements.Remove((block, successor));
                Dictionary<int, int> aliases = [];
                Dictionary<int, (int Slot, int Origin, NativeBinary Op, ulong Limit)> comparisons = [];
                for (int pc = body.Start; pc < body.End; pc += code[pc].Span)
                {
                    NativeExecutionInstruction instruction = code[pc];
                    if (instruction.Op == NativeOp.Branch && comparisons.TryGetValue(instruction.A, out var test))
                    {
                        foreach (int successor in body.Successors)
                        {
                            bool positive = flow.Blocks[successor].Start == instruction.B;
                            Range refined = Refine(state[test.Slot], test.Op, test.Limit, positive);
                            int originSlot = test.Origin >= 0 ? test.Origin : test.Slot;
                            refinements[(block, successor)] = aliases.Where(pair => pair.Value == originSlot)
                                .Select(pair => pair.Key).Append(test.Slot).Append(originSlot).Distinct()
                                .Select(slot => (slot, refined)).ToArray();
                        }
                    }
                    int origin = instruction.Op is NativeOp.Copy or NativeOp.Move
                        ? aliases.GetValueOrDefault(instruction.A, instruction.A) : -1;
                    foreach (int definition in NativeInstructionFacts.Definitions(new(instruction.Op, instruction.Result, instruction.A)))
                    {
                        aliases.Remove(definition);
                        foreach (int key in aliases.Where(pair => pair.Value == definition).Select(pair => pair.Key).ToArray()) aliases.Remove(key);
                        foreach (int key in comparisons.Where(pair => pair.Key == definition || pair.Value.Slot == definition || pair.Value.Origin == definition).Select(pair => pair.Key).ToArray()) comparisons.Remove(key);
                    }
                    if (instruction.Op == NativeOp.Binary && state[instruction.A].Known && state[instruction.B] is { Known: true } limit
                        && limit.Min == limit.Max && (NativeBinary)instruction.Immediate is NativeBinary.Less or NativeBinary.LessEqual or NativeBinary.Greater or NativeBinary.GreaterEqual)
                        comparisons[instruction.Result] = (instruction.A, aliases.GetValueOrDefault(instruction.A, -1), (NativeBinary)instruction.Immediate, limit.Min);
                    if (WritesMemory(instruction))
                    {
                        foreach (int key in aliases.Where(pair => exposed.Contains(pair.Key) || exposed.Contains(pair.Value)).Select(pair => pair.Key).ToArray()) aliases.Remove(key);
                        foreach (int key in comparisons.Where(pair => exposed.Contains(pair.Value.Slot) || exposed.Contains(pair.Value.Origin)).Select(pair => pair.Key).ToArray()) comparisons.Remove(key);
                    }
                    transfers++;
                    Transfer(program, instruction, state, exposed, out _);
                    if (origin >= 0 && instruction.Result != origin) aliases[instruction.Result] = origin;
                }
                foreach (int successor in body.Successors)
                    if (!(oldEdges[successor] ?? []).SequenceEqual(refinements.GetValueOrDefault((block, successor)) ?? [])) changed = true;
                if (exits[block] is null || !exits[block]!.SequenceEqual(state)) changed = true;
                exits[block] = state;
            }
        } while (changed);
        for (int block = 0; block < flow.Blocks.Count; block++)
        {
            if (entries[block] is not { } state) continue;
            for (int pc = flow.Blocks[block].Start; pc < flow.Blocks[block].End; pc += code[pc].Span)
                Transfer(program, code[pc], state, exposed, out result[pc]);
        }
        trace?.Metric("blocks", flow.Blocks.Count);
        trace?.Metric("fixed_point.rounds", rounds);
        trace?.Metric("instructions.transferred", transfers);
        trace?.Metric("alias.exposed_slots", exposed.Count);
        return result;
    }

    private static Range Refine(Range value, NativeBinary op, ulong limit, bool positive)
    {
        if (!value.Known) return value;
        if (!positive) op = op switch
        {
            NativeBinary.Less => NativeBinary.GreaterEqual, NativeBinary.LessEqual => NativeBinary.Greater,
            NativeBinary.Greater => NativeBinary.LessEqual, _ => NativeBinary.Less
        };
        ulong min = value.Min, max = value.Max;
        if (op == NativeBinary.Less && limit > 0) max = Math.Min(max, limit - 1);
        if (op == NativeBinary.LessEqual) max = Math.Min(max, limit);
        if (op == NativeBinary.Greater && limit < ExactLimit) min = Math.Max(min, limit + 1);
        if (op == NativeBinary.GreaterEqual) min = Math.Max(min, limit);
        return min <= max ? new(min, max) : default;
    }

    private static bool WritesMemory(NativeExecutionInstruction code) =>
        code.Op == NativeOp.Store || NativeInstructionFacts.MemoryBarrier(new(code.Op));

    private static void Transfer(NativeProgram program, NativeExecutionInstruction code, Range[] state,
        HashSet<int> exposed, out NativeNumberForm form)
    {
        form = NativeNumberForm.Float64;
        if (code.Handler == NativeInterpreter.NopHandler) return;
        Range result = default;
        bool frameStore = code.Handler == NativeInterpreter.FrameStoreHandler;
        if (code.Op == NativeOp.Constant && program.Constants[code.A] is { Kind: NativeKind.Number } constant)
        {
            double value = constant.Number;
            if (value >= 0 && value <= ExactLimit && value == Math.Truncate(value) && BitConverter.DoubleToInt64Bits(value) >= 0)
                result = new((ulong)value, (ulong)value);
        }
        else if (code.Op is NativeOp.Copy or NativeOp.Move or NativeOp.Narrow || frameStore) result = state[code.A];
        else if (code.Op == NativeOp.Binary)
        {
            Range a = state[code.A], b = state[code.B];
            NativeBinary op = (NativeBinary)code.Immediate;
            if (a.Known && b.Known)
            {
                result = op switch
                {
                    NativeBinary.Add when a.Max <= ExactLimit - b.Max => new(a.Min + b.Min, a.Max + b.Max),
                    NativeBinary.Subtract when a.Min >= b.Max => new(a.Min - b.Max, a.Max - b.Min),
                    NativeBinary.Multiply when b.Max == 0 || a.Max <= ExactLimit / b.Max => new(a.Min * b.Min, a.Max * b.Max),
                    NativeBinary.Remainder when b.Min > 0 => new(0, Math.Min(a.Max, b.Max - 1)),
                    _ => default
                };
                if (result.Known || op is >= NativeBinary.Equal and <= NativeBinary.GreaterEqual)
                    form = NativeNumberForm.UnsignedA | NativeNumberForm.UnsignedB;
            }
        }
        if (code.Op is NativeOp.Copy or NativeOp.Move && result.Known) form |= NativeNumberForm.UnsignedA;
        if (WritesMemory(code))
            foreach (int slot in exposed) state[slot] = default;
        if (code.Op is NativeOp.Move or NativeOp.Drop or NativeOp.BindLocation) state[code.A] = default;
        if (code.Result >= 0) state[code.Result] = result;
        if (result.Known) form |= NativeNumberForm.UnsignedResult;
    }
}
