using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeGlobalMemory
{
    private readonly record struct Stored(int Source, int Type);

    internal static bool Run(NativeProgram program, int index, NativeInstruction[] code,
        NativeControlFlow flow, NativeEscapeAnalysis escape)
    {
        NativeFunctionCode function = program.Functions[index];
        NativeAddressAnalysis addresses = escape.Addresses;
        HashSet<int> addressed = code.Where(instruction => instruction.Op is NativeOp.FrameAddress or NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        HashSet<int> bound = code.Where(instruction => instruction.Op == NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        HashSet<int> stable = function.Slots.Select((slot, number) => (slot, number))
            .Where(item => !addressed.Contains(item.number) && !item.slot.Escapes && item.slot.Role != NativeSlotRole.Capture)
            .Select(item => item.number).ToHashSet();
        List<int>[] previous = Enumerable.Range(0, flow.Blocks.Count).Select(_ => new List<int>()).ToArray();
        for (int block = 0; block < flow.Blocks.Count; block++)
            foreach (int successor in flow.Blocks[block].Successors) previous[successor].Add(block);
        Dictionary<NativeAddressAnalysis.Target, Stored>?[] outgoing = new Dictionary<NativeAddressAnalysis.Target, Stored>?[flow.Blocks.Count];
        bool changed;
        do
        {
            changed = false;
            for (int block = 0; block < flow.Blocks.Count; block++)
            {
                Dictionary<NativeAddressAnalysis.Target, Stored>? state = Incoming(block, previous, outgoing);
                if (state is null) continue;
                NativeControlFlow.Block body = flow.Blocks[block];
                for (int pc = body.Start; pc < body.End; pc++) Transfer(pc, code[pc], state);
                if (outgoing[block] is { } before && before.Count == state.Count
                    && before.All(pair => state.TryGetValue(pair.Key, out Stored value) && value == pair.Value)) continue;
                outgoing[block] = state; changed = true;
            }
        } while (changed);
        bool rewritten = false;
        for (int block = 0; block < flow.Blocks.Count; block++)
        {
            Dictionary<NativeAddressAnalysis.Target, Stored>? state = Incoming(block, previous, outgoing);
            if (state is null) continue;
            NativeControlFlow.Block body = flow.Blocks[block];
            for (int pc = body.Start; pc < body.End; pc++)
            {
                NativeInstruction instruction = code[pc];
                if (instruction.Op == NativeOp.Load && !addresses[instruction.A].Unknown)
                {
                    NativeAddressAnalysis.Target[] targets = addresses.MemoryTargets(instruction).ToArray();
                    if (targets.Length == 1 && state.TryGetValue(targets[0], out Stored value)
                        && value.Type == instruction.Type && function.Slots[value.Source].Type == function.Slots[instruction.Result].Type)
                    {
                        code[pc] = instruction = new(NativeOp.Copy, instruction.Result, value.Source, Type: instruction.Type, Source: instruction.Source);
                        rewritten = true;
                    }
                }
                Transfer(pc, instruction, state);
            }
        }
        return rewritten;

        bool Private(NativeAddressAnalysis.Target target) => target.Allocation >= 0
            ? target.Field >= 0 && !flow.Cyclic[target.Allocation] && !escape.EscapingAllocations.Contains(target.Allocation)
            : target.Slot >= 0 && !bound.Contains(target.Slot) && !function.Slots[target.Slot].Escapes
                && function.Slots[target.Slot].Role != NativeSlotRole.Capture && !escape.EscapingLocations.Contains(target.Slot);

        void Transfer(int pc, NativeInstruction instruction, Dictionary<NativeAddressAnalysis.Target, Stored> state)
        {
            foreach (int definition in NativeInstructionFacts.Definitions(instruction))
            {
                foreach (NativeAddressAnalysis.Target target in state.Where(pair => pair.Value.Source == definition
                    || pair.Key.Slot == definition).Select(pair => pair.Key).ToArray()) state.Remove(target);
                NativeAddressAnalysis.Points binding = addresses.Binding(definition);
                if (binding.Unknown) state.Clear();
                else foreach (NativeAddressAnalysis.Target target in binding.Targets) state.Remove(target);
            }
            bool registerDrop = instruction.Op == NativeOp.Drop && stable.Contains(instruction.A)
                && function.Slots[instruction.A].Role == NativeSlotRole.Temporary;
            if (instruction.Op == NativeOp.BindLocation || NativeInstructionFacts.MemoryBarrier(instruction) && !registerDrop)
            { state.Clear(); return; }
            if (instruction.Op == NativeOp.Allocate)
                foreach (NativeAddressAnalysis.Target target in state.Keys.Where(target => target.Allocation == pc).ToArray()) state.Remove(target);
            if (instruction.Op != NativeOp.Store) return;
            if (addresses[instruction.A].Unknown) { state.Clear(); return; }
            NativeAddressAnalysis.Target[] targets = addresses.MemoryTargets(instruction).ToArray();
            foreach (NativeAddressAnalysis.Target written in targets)
                foreach (NativeAddressAnalysis.Target target in state.Keys.Where(target => MayAlias(target, written)).ToArray()) state.Remove(target);
            if (targets.Length == 1 && Private(targets[0]) && stable.Contains(instruction.B))
                state[targets[0]] = new(instruction.B, instruction.Type);
        }
    }

    private static bool MayAlias(NativeAddressAnalysis.Target left, NativeAddressAnalysis.Target right) =>
        left.Allocation == right.Allocation && left.Slot == right.Slot
        && (left.Field == right.Field || left.Field < 0 || right.Field < 0);

    private static Dictionary<NativeAddressAnalysis.Target, Stored>? Incoming(int block, List<int>[] previous,
        Dictionary<NativeAddressAnalysis.Target, Stored>?[] outgoing)
    {
        Dictionary<NativeAddressAnalysis.Target, Stored>? result = block == 0 ? [] : null;
        foreach (int predecessor in previous[block])
        {
            if (outgoing[predecessor] is not { } state) continue;
            if (result is null) { result = new(state); continue; }
            foreach (NativeAddressAnalysis.Target target in result.Keys.ToArray())
                if (!state.TryGetValue(target, out Stored value) || value != result[target]) result.Remove(target);
        }
        return result;
    }
}
