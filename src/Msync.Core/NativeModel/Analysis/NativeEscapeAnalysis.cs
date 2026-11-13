namespace Msync.Core.Native;

internal sealed class NativeEscapeAnalysis
{
    private readonly NativeAddressAnalysis addresses;
    private readonly Dictionary<int, (HashSet<int> Allocations, HashSet<int> Locations)> reachable = [];
    internal HashSet<int> EscapingAllocations { get; } = [];
    internal HashSet<int> EscapingLocations { get; } = [];
    internal IReadOnlyDictionary<int, int> Layouts { get; }
    internal IReadOnlySet<int> AllocationsOf(int register) => Reachable(register).Allocations;
    internal IReadOnlySet<int> LocationsOf(int register) => Reachable(register).Locations;
    internal bool IsUnknown(int register) => addresses[register].Unknown;
    internal NativeAddressAnalysis Addresses => addresses;

    internal NativeEscapeAnalysis(NativeProgram program, int functionIndex, NativeAddressAnalysis.ProgramFacts? facts = null)
    {
        NativeFunctionCode function = program.Functions[functionIndex];
        addresses = (facts ?? new NativeAddressAnalysis.ProgramFacts(program)).Function(functionIndex);
        Layouts = function.Instructions.Select((instruction, pc) => (instruction, pc))
            .Where(pair => pair.instruction.Op == NativeOp.Allocate)
            .ToDictionary(pair => pair.pc, pair => pair.instruction.A);
        HashSet<int> roots = function.Slots.Select((slot, index) => (slot, index))
            .Where(pair => pair.slot.Escapes).Select(pair => pair.index).ToHashSet();
        if (functionIndex == program.EntryPoint && program.PreserveLocals)
            roots.UnionWith(program.SessionRoots ?? Enumerable.Range(0, function.Slots.Count)
                .Where(slot => function.Slots[slot].Role != NativeSlotRole.Temporary).ToArray());
        NativeAddressAnalysis.Points retained = new();
        foreach (int root in roots) { retained.Merge(addresses[root]); retained.Merge(addresses.Binding(root)); }
        for (int pc = 0; pc < function.Instructions.Count; pc++)
        {
            NativeInstruction instruction = function.Instructions[pc];
            if (NativeInstructionFacts.EscapingUse(instruction))
                foreach (int source in NativeInstructionFacts.ValueReads(instruction)) retained.Merge(addresses.At(pc, source));
            if (instruction.Op == NativeOp.Store && (addresses.At(pc, instruction.A).Unknown
                || addresses.At(pc, instruction.A).Targets.Count == 0)) retained.Merge(addresses.At(pc, instruction.B));
        }
        var escaping = Trace([retained]);
        EscapingAllocations.UnionWith(escaping.Allocations);
        EscapingLocations.UnionWith(escaping.Locations);
    }

    private (HashSet<int> Allocations, HashSet<int> Locations) Reachable(int register)
    {
        if (reachable.TryGetValue(register, out var cached)) return cached;
        cached = Trace([addresses[register], addresses.Binding(register)]);
        reachable.Add(register, cached);
        return cached;
    }

    private (HashSet<int> Allocations, HashSet<int> Locations) Trace(IEnumerable<NativeAddressAnalysis.Points> roots)
    {
        HashSet<int> allocations = [], locations = [];
        HashSet<NativeAddressAnalysis.Target> seen = [];
        Queue<NativeAddressAnalysis.Points> pending = new(roots);
        HashSet<NativeAddressAnalysis.Points> visited = [];
        while (pending.TryDequeue(out NativeAddressAnalysis.Points? points))
        {
            if (!visited.Add(points)) continue;
            foreach (NativeAddressAnalysis.Points retained in points.Retained.Concat(points.Elements)) pending.Enqueue(retained);
            foreach (NativeCallableOrigin callable in points.Callables)
                foreach (NativeAddressAnalysis.Points capture in callable.Captures) pending.Enqueue(capture);
            foreach (NativeAddressAnalysis.ForeignTarget target in points.Foreign)
                foreach (NativeAddressAnalysis.Points contents in target.Region.Fields.Values) pending.Enqueue(contents);
            foreach (NativeAddressAnalysis.Target target in points.Targets)
            {
                if (!seen.Add(target)) continue;
                if (target.Allocation >= 0) allocations.Add(target.Allocation);
                else locations.Add(target.Slot);
                foreach (NativeAddressAnalysis.Points contents in addresses.Contents(target)) pending.Enqueue(contents);
            }
        }
        return (allocations, locations);
    }
}
