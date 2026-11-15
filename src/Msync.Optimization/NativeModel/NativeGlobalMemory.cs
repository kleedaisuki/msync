using Msync.Core.Native;

namespace Msync.Optimization;

internal static class NativeGlobalMemory
{
    private readonly record struct Stored(int Source, int Type);
    private readonly record struct FieldKey(int BaseSlot, int Field);

    private sealed class BlockState
    {
        internal readonly Dictionary<NativeAddressAnalysis.Target, Stored> Targets;
        internal readonly Dictionary<int, FieldKey> Pointers;
        internal readonly Dictionary<FieldKey, Stored> Fields;

        internal BlockState()
        {
            Targets = [];
            Pointers = [];
            Fields = [];
        }

        internal BlockState(BlockState other)
        {
            Targets = new(other.Targets);
            Pointers = new(other.Pointers);
            Fields = new(other.Fields);
        }

        internal bool EqualsState(BlockState other) =>
            Targets.Count == other.Targets.Count
            && Pointers.Count == other.Pointers.Count
            && Fields.Count == other.Fields.Count
            && Targets.All(p => other.Targets.TryGetValue(p.Key, out Stored v) && v == p.Value)
            && Pointers.All(p => other.Pointers.TryGetValue(p.Key, out FieldKey v) && v == p.Value)
            && Fields.All(p => other.Fields.TryGetValue(p.Key, out Stored v) && v == p.Value);
    }

    internal static bool Run(NativeProgram program, int index, NativeInstruction[] code,
        NativeControlFlow flow, NativeEscapeAnalysis escape, NativeOptimizationProfile? profile = null)
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
        BlockState?[] outgoing = new BlockState?[flow.Blocks.Count];
        bool changed;
        do
        {
            changed = false;
            for (int block = 0; block < flow.Blocks.Count; block++)
            {
                BlockState? state = Incoming(block, previous, outgoing);
                if (state is null) continue;
                NativeControlFlow.Block body = flow.Blocks[block];
                for (int pc = body.Start; pc < body.End; pc++) Transfer(pc, code[pc], state);
                if (outgoing[block] is { } before && before.EqualsState(state)) continue;
                outgoing[block] = state; changed = true;
            }
        } while (changed);
        bool rewritten = false;
        int eliminatedLoads = 0;
        for (int block = 0; block < flow.Blocks.Count; block++)
        {
            BlockState? state = Incoming(block, previous, outgoing);
            if (state is null) continue;
            NativeControlFlow.Block body = flow.Blocks[block];
            for (int pc = body.Start; pc < body.End; pc++)
            {
                NativeInstruction instruction = code[pc];
                if (instruction.Op == NativeOp.Load && instruction.Result >= 0)
                {
                    bool matchedTarget = false;
                    if (!addresses[instruction.A].Unknown)
                    {
                        NativeAddressAnalysis.Target[] targets = addresses.MemoryTargets(instruction).ToArray();
                        if (targets.Length == 1)
                        {
                            matchedTarget = true;
                            if (state.Targets.TryGetValue(targets[0], out Stored value)
                                && value.Type == instruction.Type
                                && NativeTypeRelations.Accepts(program.Types, function.Slots[instruction.Result].Type, function.Slots[value.Source].Type))
                            {
                                code[pc] = instruction = new(NativeOp.Copy, instruction.Result, value.Source, Type: instruction.Type, Source: instruction.Source);
                                rewritten = true;
                                eliminatedLoads++;
                            }
                        }
                    }
                    if (!matchedTarget && state.Pointers.TryGetValue(instruction.A, out FieldKey fieldKey)
                        && state.Fields.TryGetValue(fieldKey, out Stored fieldValue)
                        && fieldValue.Type == instruction.Type
                        && NativeTypeRelations.Accepts(program.Types, function.Slots[instruction.Result].Type, function.Slots[fieldValue.Source].Type))
                    {
                        code[pc] = instruction = new(NativeOp.Copy, instruction.Result, fieldValue.Source, Type: instruction.Type, Source: instruction.Source);
                        rewritten = true;
                        eliminatedLoads++;
                    }
                }
                Transfer(pc, instruction, state);
            }
        }
        if (eliminatedLoads > 0) profile?.Trace.Metric("global_memory.eliminated_loads", eliminatedLoads);
        return rewritten;

        bool Private(NativeAddressAnalysis.Target target) => target.Allocation >= 0
            ? target.Field >= 0 && !flow.Cyclic[target.Allocation] && !escape.EscapingAllocations.Contains(target.Allocation)
            : target.Slot >= 0 && !bound.Contains(target.Slot) && !function.Slots[target.Slot].Escapes
                && function.Slots[target.Slot].Role != NativeSlotRole.Capture && !escape.EscapingLocations.Contains(target.Slot);

        void Transfer(int pc, NativeInstruction instruction, BlockState state)
        {
            foreach (int definition in NativeInstructionFacts.Definitions(instruction))
            {
                foreach (NativeAddressAnalysis.Target target in state.Targets.Where(pair => pair.Value.Source == definition
                    || pair.Key.Slot == definition).Select(pair => pair.Key).ToArray()) state.Targets.Remove(target);
                foreach (int ptr in state.Pointers.Where(pair => pair.Key == definition || pair.Value.BaseSlot == definition)
                    .Select(pair => pair.Key).ToArray()) state.Pointers.Remove(ptr);
                foreach (FieldKey fk in state.Fields.Where(pair => pair.Key.BaseSlot == definition || pair.Value.Source == definition)
                    .Select(pair => pair.Key).ToArray()) state.Fields.Remove(fk);
                NativeAddressAnalysis.Points binding = addresses.Binding(definition);
                if (binding.Unknown) { state.Targets.Clear(); state.Fields.Clear(); }
                else foreach (NativeAddressAnalysis.Target target in binding.Targets) state.Targets.Remove(target);
            }
            bool registerDrop = instruction.Op == NativeOp.Drop && stable.Contains(instruction.A)
                && function.Slots[instruction.A].Role == NativeSlotRole.Temporary;
            if (instruction.Op == NativeOp.BindLocation || NativeInstructionFacts.MemoryBarrier(instruction) && !registerDrop)
            { state.Targets.Clear(); state.Fields.Clear(); return; }
            if (instruction.Op == NativeOp.Allocate)
            {
                foreach (NativeAddressAnalysis.Target target in state.Targets.Keys.Where(target => target.Allocation == pc).ToArray())
                    state.Targets.Remove(target);
                return;
            }
            if (instruction.Op == NativeOp.FrameAddress && instruction.Result >= 0
                && stable.Contains(instruction.Result) && !bound.Contains(instruction.A)
                && !function.Slots[instruction.A].Escapes && !escape.EscapingLocations.Contains(instruction.A)
                && (function.Slots[instruction.A].Role != NativeSlotRole.Capture
                    || (instruction.A < function.CaptureCount && function.Signature.Captures[instruction.A].Mode == NativeCaptureMode.Value)))
            {
                state.Pointers[instruction.Result] = new(instruction.A, -1);
                return;
            }
            if (instruction.Op == NativeOp.ProjectAddress && instruction.Result >= 0
                && stable.Contains(instruction.Result) && stable.Contains(instruction.A))
            {
                state.Pointers[instruction.Result] = new(instruction.A, instruction.B);
                return;
            }
            if (instruction.Op == NativeOp.Load && instruction.Result >= 0 && stable.Contains(instruction.Result))
            {
                bool recorded = false;
                if (!addresses[instruction.A].Unknown)
                {
                    NativeAddressAnalysis.Target[] targets = addresses.MemoryTargets(instruction).ToArray();
                    if (targets.Length == 1)
                    {
                        recorded = true;
                        if (!state.Targets.ContainsKey(targets[0]))
                            state.Targets[targets[0]] = new(instruction.Result, instruction.Type);
                    }
                }
                if (!recorded && state.Pointers.TryGetValue(instruction.A, out FieldKey fk) && !state.Fields.ContainsKey(fk))
                    state.Fields[fk] = new(instruction.Result, instruction.Type);
                return;
            }
            if (instruction.Op != NativeOp.Store) return;
            if (addresses[instruction.A].Unknown) { state.Targets.Clear(); state.Fields.Clear(); return; }
            NativeAddressAnalysis.Target[] writtenTargets = addresses.MemoryTargets(instruction).ToArray();
            foreach (NativeAddressAnalysis.Target written in writtenTargets)
            {
                foreach (NativeAddressAnalysis.Target target in state.Targets.Keys.Where(target => MayAlias(target, written)).ToArray())
                    state.Targets.Remove(target);
                if (written.Field < 0) state.Fields.Clear();
                else foreach (FieldKey fk in state.Fields.Keys.Where(k => k.Field == written.Field).ToArray())
                    state.Fields.Remove(fk);
            }
            if (writtenTargets.Length == 1 && Private(writtenTargets[0]) && stable.Contains(instruction.B))
                state.Targets[writtenTargets[0]] = new(instruction.B, instruction.Type);
        }
    }

    private static bool MayAlias(NativeAddressAnalysis.Target left, NativeAddressAnalysis.Target right) =>
        left.Allocation == right.Allocation && left.Slot == right.Slot
        && (left.Field == right.Field || left.Field < 0 || right.Field < 0);

    private static BlockState? Incoming(int block, List<int>[] previous, BlockState?[] outgoing)
    {
        BlockState? result = block == 0 ? new() : null;
        foreach (int predecessor in previous[block])
        {
            if (outgoing[predecessor] is not { } state) continue;
            if (result is null) { result = new(state); continue; }
            foreach (NativeAddressAnalysis.Target target in result.Targets.Keys.ToArray())
                if (!state.Targets.TryGetValue(target, out Stored value) || value != result.Targets[target]) result.Targets.Remove(target);
            foreach (int ptr in result.Pointers.Keys.ToArray())
                if (!state.Pointers.TryGetValue(ptr, out FieldKey value) || value != result.Pointers[ptr]) result.Pointers.Remove(ptr);
            foreach (FieldKey fk in result.Fields.Keys.ToArray())
                if (!state.Fields.TryGetValue(fk, out Stored value) || value != result.Fields[fk]) result.Fields.Remove(fk);
        }
        return result;
    }
}
