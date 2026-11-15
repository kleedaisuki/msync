using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeFrameAllocation(NativeOffset Offset, int Layout);
internal readonly record struct NativeFrameRegion(NativeOffset Offset, NativeLayout Layout);
internal readonly record struct NativeFrameSlot(NativeOffset Offset, NativeType Type, NativeType? Pointee,
    int Binding, bool Persistent, bool ExactAddress, NativeSlotStorage Storage, NativeLayout? HeapLayout)
{
    internal bool Borrowable => !Persistent && Storage.HasFlag(NativeSlotStorage.ReadOnlyParameter);
}

internal sealed class FrameAllocation
{
    private readonly Dictionary<int, NativeFrameAllocation>? allocations;

    internal FrameAllocation(NativeProgram program, int function, NativeLayout registers,
        IReadOnlyList<NativeType> types, IReadOnlyList<NativeAllocationPlan> candidates,
        Func<int, NativeLayout> layout)
    {
        allocations = null;
        Registers = registers;
        NativeFunctionCode code = Code = program.Functions[function];
        HashSet<int> bound = code.Instructions.Where(instruction => instruction.Op == NativeOp.BindLocation)
            .Select(instruction => instruction.A).ToHashSet();
        for (int index = 0; index < code.CaptureCount; index++)
            if (code.Signature.Captures[index].Mode == NativeCaptureMode.Location) bound.Add(index);
        HashSet<int> persistent = program.PreserveLocals && function == program.EntryPoint
            ? code.Slots.Select((slot, index) => (slot, index))
                .Where(item => item.slot.Role != NativeSlotRole.Temporary && !registers[item.index].Type.IsNever)
                .Select(item => item.index).Concat(program.SessionRoots ?? []).ToHashSet() : [];
        bound.UnionWith(persistent);
        HashSet<int> escaped = Enumerable.Range(0, code.Slots.Count)
            .Where(slot => code.Storage!.Slots[slot].HasFlag(NativeSlotStorage.Escapes)).ToHashSet();
        bound.UnionWith(escaped);
        bool[] readOnly = Enumerable.Range(0, code.Slots.Count)
            .Select(slot => code.Storage!.Slots[slot].HasFlag(NativeSlotStorage.ReadOnlyParameter)).ToArray();
        Parameters = new NativeLayout(Enumerable.Range(0, code.ParameterCount)
            .Select(index => new NativeField(index.ToString(System.Globalization.CultureInfo.InvariantCulture), registers[code.CaptureCount + index].Type)));
        ParameterBorrows = Enumerable.Range(0, code.ParameterCount).Select(index => readOnly[code.CaptureCount + index]).ToArray();
        ParameterBorrowCount = ParameterBorrows.Count(value => value);
        for (int index = 0; index < code.ParameterCount; index++)
            if (readOnly[code.CaptureCount + index]) bound.Add(code.CaptureCount + index);
        bool CapturedLocation(int slot) => slot < code.CaptureCount && code.Signature.Captures[slot].Mode == NativeCaptureMode.Location;
        BindingSlots = bound.Order().ToArray();
        Slots = new NativeFrameSlot[code.Slots.Count];
        for (int index = 0; index < Slots.Length; index++)
        {
            NativeFieldLayout field = registers[index];
            int pointee = program.Types[code.Slots[index].Type].Pointee;
            bool isPersistent = persistent.Contains(index);
            Slots[index] = new(field.Offset, field.Type, pointee >= 0 ? types[pointee] : null,
                -1, isPersistent, field.Type.Kinds == (1UL << (int)NativeKind.Address), code.Storage!.Slots[index],
                escaped.Contains(index) && !isPersistent && !CapturedLocation(index) ? new NativeLayout(field.Type) : null);
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
        NativeAllocationPlan[] selected = candidates
            .Where(plan => code.Storage!.Instructions[plan.Instruction].HasFlag(NativeInstructionStorage.FrameAllocation)).ToArray();
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

    internal NativeFunctionCode Code { get; }
    internal NativeLayout Parameters { get; }
    internal bool[] ParameterBorrows { get; }
    internal int ParameterBorrowCount { get; }
    internal NativeFrameSlot[] Slots { get; }
    internal int[] BindingSlots { get; }
    internal NativeLayout Layout { get; }
    internal NativeLayout Registers { get; }
    internal IEnumerable<NativeFrameAllocation> Regions => allocations is null ? [] : allocations.Values;
    internal NativeFrameAllocation? At(int instruction) => allocations is not null
        && allocations.TryGetValue(instruction, out var region) ? region : null;
}
