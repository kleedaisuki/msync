using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeGlobalFacts
{
    internal static bool Run(NativeFunctionCode function, NativeInstruction[] code, NativeConstantPool constants)
    {
        NativeControlFlow flow = new(code);
        if (flow.Blocks.Count == 0) return false;
        HashSet<int> mutable = code.Where(instruction => instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(instruction => instruction.A).Concat(function.Slots.Select((slot, index) => (slot, index))
                .Where(item => item.slot.Escapes || item.slot.Role == NativeSlotRole.Capture).Select(item => item.index)).ToHashSet();
        List<int>[] previous = Enumerable.Range(0, flow.Blocks.Count).Select(_ => new List<int>()).ToArray();
        for (int block = 0; block < flow.Blocks.Count; block++)
            foreach (int successor in flow.Blocks[block].Successors) previous[successor].Add(block);
        Dictionary<int, NativeConstant>?[] outgoing = new Dictionary<int, NativeConstant>?[flow.Blocks.Count];
        bool changed;
        do
        {
            changed = false;
            for (int block = 0; block < flow.Blocks.Count; block++)
            {
                Dictionary<int, NativeConstant>? state = Incoming(block, previous, outgoing);
                if (state is null) continue;
                NativeControlFlow.Block body = flow.Blocks[block];
                for (int pc = body.Start; pc < body.End; pc++) Transfer(code[pc], state, mutable, constants);
                if (Same(outgoing[block], state)) continue;
                outgoing[block] = state;
                changed = true;
            }
        } while (changed);

        bool rewritten = false;
        for (int block = 0; block < flow.Blocks.Count; block++)
        {
            Dictionary<int, NativeConstant>? state = Incoming(block, previous, outgoing);
            if (state is null) continue;
            NativeControlFlow.Block body = flow.Blocks[block];
            for (int pc = body.Start; pc < body.End; pc++)
            {
                NativeInstruction original = code[pc];
                NativeInstruction result = Rewrite(original, state, constants);
                if (result != original) { code[pc] = result; rewritten = true; }
                Transfer(result, state, mutable, constants);
            }
        }
        return rewritten;
    }

    private static Dictionary<int, NativeConstant>? Incoming(int block, List<int>[] previous,
        Dictionary<int, NativeConstant>?[] outgoing)
    {
        Dictionary<int, NativeConstant>? result = block == 0 ? [] : null;
        foreach (int predecessor in previous[block])
        {
            if (outgoing[predecessor] is not { } state) continue;
            if (result is null) { result = new(state); continue; }
            foreach (int slot in result.Keys.ToArray())
                if (!state.TryGetValue(slot, out NativeConstant? value) || !NativeConstantPool.Same(result[slot], value))
                    result.Remove(slot);
        }
        return result;
    }

    private static void Transfer(NativeInstruction instruction, Dictionary<int, NativeConstant> state,
        HashSet<int> mutable, NativeConstantPool constants)
    {
        NativeConstant? result = instruction.Op switch
        {
            NativeOp.Constant => constants[instruction.A],
            NativeOp.Copy or NativeOp.Move => state.GetValueOrDefault(instruction.A),
            _ => NativeConstantFolding.Fold(instruction, state)
        };
        foreach (int definition in NativeInstructionFacts.Definitions(instruction)) state.Remove(definition);
        if (NativeInstructionFacts.MemoryBarrier(instruction)
            || instruction.Op is NativeOp.Store or NativeOp.BindLocation or NativeOp.PublishStatic)
            foreach (int slot in mutable) state.Remove(slot);
        if (result is not null && instruction.Result >= 0) state[instruction.Result] = result;
    }

    private static NativeInstruction Rewrite(NativeInstruction instruction, Dictionary<int, NativeConstant> state,
        NativeConstantPool constants)
    {
        if (instruction.Op == NativeOp.Branch && state.TryGetValue(instruction.A, out NativeConstant? condition)
            && condition.Kind == NativeKind.Bool)
            return new(NativeOp.Jump, A: condition.Boolean ? instruction.B : instruction.C, Source: instruction.Source);
        NativeConstant? value = instruction.Op == NativeOp.Copy ? state.GetValueOrDefault(instruction.A)
            : NativeConstantFolding.Fold(instruction, state);
        return value is null ? instruction : new(NativeOp.Constant, instruction.Result, constants.Add(value),
            Type: instruction.Type, Source: instruction.Source);
    }

    private static bool Same(Dictionary<int, NativeConstant>? left, Dictionary<int, NativeConstant> right) =>
        left is not null && left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out NativeConstant? value) && NativeConstantPool.Same(pair.Value, value));
}
