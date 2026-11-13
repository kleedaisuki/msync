using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeFrameAllocation(NativeOffset Offset, int Layout);
internal readonly record struct NativeFrameRegion(NativeOffset Offset, NativeLayout Layout);
internal readonly record struct NativeFrameSlot(NativeOffset Offset, NativeType Type, int Binding, bool Persistent);

internal readonly struct FrameAllocation
{
    private readonly Dictionary<int, NativeFrameAllocation>? allocations;

    internal FrameAllocation(NativeProgram program, int function, NativeLayout registers,
        IReadOnlyList<NativeAllocationPlan> candidates, Func<int, NativeLayout> layout, NativeAddressAnalysis.ProgramFacts provenance)
    {
        allocations = null;
        Registers = registers;
        NativeFunctionCode code = program.Functions[function];
        HashSet<int> bound = code.Instructions.Where(instruction => instruction.Op == NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        for (int index = 0; index < code.CaptureCount; index++)
            if (code.Signature.Captures[index].Mode == NativeCaptureMode.Location) bound.Add(index);
        HashSet<int> persistent = program.PreserveLocals && function == program.EntryPoint
            ? code.Slots.Select((slot, index) => (slot, index))
                .Where(item => item.slot.Role != NativeSlotRole.Temporary && !registers[item.index].Type.IsNever)
                .Select(item => item.index).Concat(program.SessionRoots ?? []).ToHashSet() : [];
        bound.UnionWith(persistent);
        BindingSlots = bound.Order().ToArray();
        Slots = new NativeFrameSlot[code.Slots.Count];
        for (int index = 0; index < Slots.Length; index++)
        {
            NativeFieldLayout field = registers[index];
            Slots[index] = new(field.Offset, field.Type, -1, persistent.Contains(index));
        }
        for (int index = 0; index < BindingSlots.Length; index++)
        {
            int slot = BindingSlots[index];
            Slots[slot] = Slots[slot] with { Binding = index };
        }
        if (candidates.Count == 0)
        {
            Layout = registers;
            return;
        }
        IReadOnlySet<int> proven = NativeAllocationSafety.FrameSafe(program, function, provenance);
        NativeAllocationPlan[] selected = candidates.Where(plan => proven.Contains(plan.Instruction)
            && program.Functions[function].Instructions[plan.Instruction].A == plan.Layout)
            .DistinctBy(plan => plan.Instruction).OrderBy(plan => plan.Instruction).ToArray();
        if (selected.Length == 0)
        {
            Layout = registers;
            return;
        }
        NativeLayout[] parts = new NativeLayout[selected.Length + 1];
        parts[0] = registers;
        for (int index = 0; index < selected.Length; index++) parts[index + 1] = layout(selected[index].Layout);
        Layout = NativeLayout.Compose(parts, out IReadOnlyList<NativeOffset> offsets);
        allocations = new(selected.Length);
        for (int index = 0; index < selected.Length; index++)
            allocations.Add(selected[index].Instruction, new(offsets[index + 1], selected[index].Layout));
    }

    internal NativeFrameSlot[] Slots { get; }
    internal int[] BindingSlots { get; }
    internal NativeLayout Layout { get; }
    internal NativeLayout Registers { get; }
    internal IEnumerable<NativeFrameAllocation> Regions => allocations is null ? [] : allocations.Values;
    internal NativeFrameAllocation? At(int instruction) => allocations is not null
        && allocations.TryGetValue(instruction, out var region) ? region : null;
}
