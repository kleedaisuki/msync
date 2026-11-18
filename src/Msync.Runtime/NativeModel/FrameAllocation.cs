using System.Runtime.CompilerServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal readonly record struct NativeFrameAllocation(NativeOffset Offset, int Layout);
internal readonly record struct NativeFrameRegion(NativeOffset Offset, NativeLayout Layout);
internal readonly record struct NativeFrameSlot(NativeOffset Offset, NativeType Type, NativeType? Pointee,
    int Binding, bool Persistent, bool ExactAddress, NativeSlotStorage Storage, NativeLayout? HeapLayout)
{
    internal bool Borrowable => !Persistent && (Storage & NativeSlotStorage.ReadOnlyParameter) != 0;
}

internal sealed class FrameAllocation
{
    private readonly Dictionary<int, NativeFrameAllocation>? allocations;

    internal FrameAllocation(NativeProgram program, int function, NativeLayout registers,
        IReadOnlyList<NativeType> types, IReadOnlyList<NativeAllocationPlan> candidates,
        Func<int, NativeLayout> layout, bool incoming = false)
    {
        allocations = null;
        NativeFunctionCode code = Code = program.Functions[function];
        if (incoming && code.CaptureCount != 0)
            registers = registers.WithParameterPrefix(code.CaptureCount, code.ParameterCount);
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
        {
            int slot = code.CaptureCount + index;
            if (readOnly[slot]) bound.Add(slot);
            if (index < 64 && (code.Storage!.Slots[slot] & NativeSlotStorage.UnreadParameter) != 0)
                UnreadParameters |= 1UL << index;
        }
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
        NativeAllocationPlan[] selected = candidates.Count == 0 ? []
            : candidates.Where(plan => code.Storage!.Instructions[plan.Instruction].HasFlag(NativeInstructionStorage.FrameAllocation)).ToArray();
        IReadOnlyList<int>? outgoing = null;
        HashSet<int>? producers = null;
        foreach (NativeCallStorage site in code.Storage!.Calls)
        {
            NativeInstructionStorage plan = code.Storage.Instructions[site.Instruction];
            if ((plan & NativeInstructionStorage.OwnArguments) == 0) continue;
            outgoing = code.Instructions[site.Instruction].Inputs!;
            producers = outgoing.Where(slot => (code.Storage.Slots[slot] & NativeSlotStorage.WindowProducer) != 0).ToHashSet();
            break;
        }
        int[]? keep = null;
        Registers = producers is null ? registers : registers.WithoutWindowProducers(producers, out keep);
        NativeLayout frame = Registers;
        if (selected.Length != 0)
        {
            NativeLayout[] parts = new NativeLayout[selected.Length + 1];
            parts[0] = frame;
            for (int index = 0; index < selected.Length; index++) parts[index + 1] = layout(selected[index].Layout);
            frame = NativeLayout.Compose(parts, out IReadOnlyList<NativeOffset> offsets);
            allocations = new(selected.Length);
            for (int index = 0; index < selected.Length; index++)
                allocations.Add(selected[index].Instruction, new(offsets[index + 1], selected[index].Layout));
        }
        if (code.IteratorCount > 0)
        {
            frame = frame.AppendRaw(checked(code.IteratorCount * NativeSequenceCursor.Bytes), sizeof(double), out int iteratorOffset);
            IteratorOffset = iteratorOffset;
            frame = frame.AppendRaw(checked(code.IteratorCount * Unsafe.SizeOf<NativeRangeCursor>()), sizeof(long), out int planOffset);
            PlanCursorOffset = planOffset;
        }
        if (BindingSlots.Length != 0)
        {
            NativeLayout cells = NativeLayout.Repeat(BindingType, BindingSlots.Length);
            frame = frame.AppendScratch(cells.Extent, cells.Alignment, out bindingBase);
        }
        int snapshotBytes = 0, snapshotReferences = 0, snapshotAlignment = 1;
        foreach (NativeCallStorage call in code.Storage!.Calls)
        {
            if (!call.Snapshot) continue;
            IReadOnlyList<int> inputs = code.Instructions[call.Instruction].Inputs!;
            if (inputs.Count == 0) continue;
            NativeLayout packet = new(inputs.Select(slot => registers[slot].Type).ToArray());
            (snapshots ??= [])[call.Instruction] = packet;
            snapshotBytes = Math.Max(snapshotBytes, packet.RawSize);
            snapshotReferences = Math.Max(snapshotReferences, packet.ReferenceCount);
            snapshotAlignment = Math.Max(snapshotAlignment, packet.Alignment);
        }
        if (snapshotBytes > 0)
            frame = frame.AppendScratch(new(snapshotBytes, snapshotReferences), snapshotAlignment, out SnapshotBase);
        if (outgoing is not null)
        {
            frame = frame.AppendArgumentWindow(registers, keep!, outgoing, producers!, out NativeOffset window,
                out NativeLayout parameters);
            AppendedWindow = new NativeFrameRegion(window, parameters);
            ArgumentWindow = window;
            for (int slot = 0; slot < Slots.Length; slot++) Slots[slot] = Slots[slot] with { Offset = frame[slot].Offset };
        }
        Layout = frame;
    }

    internal static readonly NativeType BindingType = NativeType.Of(NativeKind.Address);
    private readonly NativeOffset bindingBase;
    private readonly Dictionary<int, NativeLayout>? snapshots;
    internal readonly NativeOffset SnapshotBase;
    internal NativeLayout SnapshotAt(int pc) => snapshots![pc];
    internal NativeOffset BindingAt(int binding) => bindingBase + new NativeOffset(binding * sizeof(ulong), binding * 2);
    internal NativeFunctionCode Code { get; }
    internal NativeLayout Parameters { get; }
    internal bool[] ParameterBorrows { get; }
    internal ulong UnreadParameters { get; }
    internal int ParameterBorrowCount { get; }
    internal NativeFrameSlot[] Slots { get; }
    internal int[] BindingSlots { get; }
    internal NativeLayout Layout { get; }
    internal NativeLayout Registers { get; }
    internal NativeOffset? ArgumentWindow { get; }
    internal NativeFrameRegion? AppendedWindow { get; }
    internal int IteratorOffset { get; }
    internal int PlanCursorOffset { get; }
    internal IEnumerable<NativeFrameAllocation> Regions => allocations is null ? [] : allocations.Values;
    internal NativeFrameAllocation? At(int instruction) => allocations is not null
        && allocations.TryGetValue(instruction, out var region) ? region : null;

}
