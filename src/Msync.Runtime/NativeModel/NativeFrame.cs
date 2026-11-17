using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeFrame
{
    internal NativeInterpreter.Activation? Activation;
    private readonly FrameAllocation layout;
    private readonly NativeAllocation storage;
    private readonly NativeAddress[] bindings;
    private readonly NativeMemory memory;
    private readonly NativeGuestHeap? heap;
    private readonly Dictionary<int, NativeAddress>? sessionLocations;
    private NativeDatum[] argumentSnapshot = [];
    private int argumentCount;
    private NativeFrameStack.Lease? stackLease;
    internal NativeFrameStack.Lease? StackLease => stackLease;

    internal void EnterStack() => stackLease = NativeFrameStack.Current.Push(storage, heap?.References);

    internal void LeaveStack()
    {
        if (stackLease is not { } lease) return;
        lease.Stack.Pop(lease, storage);
        stackLease = null;
    }

    internal NativeArguments SnapshotArguments(IReadOnlyList<int> inputs)
    {
        bool allocated = argumentSnapshot.Length < inputs.Count;
        if (allocated) argumentSnapshot = new NativeDatum[inputs.Count];
        else Array.Clear(argumentSnapshot, inputs.Count, Math.Max(0, argumentCount - inputs.Count));
        argumentCount = inputs.Count;
        for (int index = 0; index < argumentCount; index++) argumentSnapshot[index] = GetDatum(inputs[index]);
        return new(argumentSnapshot, argumentCount, allocated);
    }

    internal void ClearArguments()
    {
        Array.Clear(argumentSnapshot, 0, argumentCount);
        argumentCount = 0;
    }

    internal NativeFrame(NativeImage image, int functionIndex, NativeEnvironment? environment,
        IReadOnlyList<NativeDatum> arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations = null)
        : this(image, functionIndex, environment, new NativeArguments(arguments), memory, sessionLocations, null) { }

    internal NativeFrame(NativeImage image, int functionIndex, NativeEnvironment? environment,
        NativeArguments arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations = null, NativeAllocation? allocation = null, bool stack = false,
        NativeGuestHeap? heap = null, RuntimeProfile.NativeFrameCounters counters = default)
    {
        this.memory = memory;
        this.heap = heap;
        this.sessionLocations = sessionLocations;
        layout = image.Frame(functionIndex);
        storage = allocation ?? (stack
            ? new(layout.Layout, allocate: false)
            : Allocate(layout.Layout, counters));
        bindings = layout.BindingSlots.Length == 0 ? [] : new NativeAddress[layout.BindingSlots.Length];
        if (stack) EnterStack();
        try { Initialize(environment, arguments, sessionLocations, counters); }
        catch
        {
            if (stack) { memory.ResetFrame(storage); LeaveStack(); }
            throw;
        }
    }

    internal void Reset(NativeEnvironment? environment, NativeArguments arguments,
        Dictionary<int, NativeAddress>? sessionLocations = null, RuntimeProfile.NativeFrameCounters counters = default)
    {
        Initialize(environment, arguments, sessionLocations, counters);
    }

    private NativeAllocation Allocate(NativeLayout layout, RuntimeProfile.NativeFrameCounters counters) =>
        heap?.NewObject(layout, -1, 1, 0, counters) ?? new(layout);

    private void Initialize(NativeEnvironment? environment, NativeArguments arguments,
        Dictionary<int, NativeAddress>? activeSessionLocations, RuntimeProfile.NativeFrameCounters counters)
    {
        if (layout.Code.IteratorCount != 0) storage.Storage.RawSpan[layout.IteratorOffset..].Clear();
        foreach (int index in layout.BindingSlots)
        {
            NativeFrameSlot slot = layout.Slots[index];
            NativeAddress address = storage.AddressAt(slot.Offset, slot.Type);
            if (slot.Persistent && activeSessionLocations is not null)
            {
                if (!activeSessionLocations.TryGetValue(index, out address))
                {
                    address = Allocate(new(slot.Type), counters).Field(0);
                    activeSessionLocations.Add(index, address);
                }
            }
            else if (slot.HeapLayout is { } escaped) address = Allocate(escaped, counters).Field(0);
            bindings[slot.Binding] = address;
        }
        for (int index = 0; index < layout.Code.CaptureCount; index++)
        {
            if (layout.Code.Signature.Captures[index].Mode == NativeCaptureMode.Location)
                bindings[layout.Slots[index].Binding] = NativeStorage.LoadAddress(environment!.CaptureAddress(index));
            else memory.Copy(environment!.CaptureAddress(index), Location(index));
        }
        for (int index = 0; index < arguments.Count; index++) arguments.Initialize(this, index);
    }

    internal void ClearBindings() => Array.Clear(bindings);
    internal void RecordHeapLocations(RuntimeProfile.NativeFrameCounters counters)
    {
        foreach (int index in layout.BindingSlots)
            if (layout.Slots[index].HeapLayout is { } heap) counters.EscapingLocation(heap);
    }
    internal ReadOnlySpan<NativeAddress> BindingRoots => bindings;
    internal ReadOnlySpan<NativeDatum> ArgumentRoots => argumentSnapshot.AsSpan(0, argumentCount);
    internal int CaptureCount => layout.Code.CaptureCount;
    internal void BorrowParameter(int slot, NativeAddress address)
    {
        bindings[layout.Slots[slot].Binding] = address;
    }
    internal void CopyArgument(NativeAddress source, int slot) => memory.CopyCompiled(source, Location(slot));

    internal void Set(int index, NativeValue value)
    {
        memory.Write(Location(index), new(value));
    }

    internal NativeDatum GetDatum(int index)
    {
        if (!IsPrivate(index)) return memory.Read(Location(index));
        if (Activation?.Work is { } work) work.Counters.CompiledRead();
        return memory.ReadCompiled(Location(index));
    }
    internal void SetDatum(int index, NativeDatum value)
    {
        memory.Write(Location(index), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAddress Location(int index)
    {
        ref readonly NativeFrameSlot slot = ref layout.Slots[index];
        return slot.Binding < 0 ? storage.AddressAt(slot.Offset, slot.Type) : bindings[slot.Binding];
    }
    internal NativeAllocation Allocation => storage;
    internal ref NativeSequenceCursor SequenceCursor(int index) =>
        ref MemoryMarshal.Cast<byte, NativeSequenceCursor>(storage.Storage.RawSpan[layout.IteratorOffset..])[index];

    internal void SetSequenceValue(int slot, double value)
    {
        NativeAddress destination = Location(slot);
        if (destination.Type.Kinds == 1UL << (int)NativeKind.Number)
        {
            memory.RemovePending(destination);
            NativeStorage.StoreNumberCompiled(destination, value);
        }
        else memory.Write(destination, new(NativeValue.FromNumber(value)));
    }
    internal int Type(int index) => layout.Code.Slots[index].Type;
    internal void Bind(int index, NativeAddress address)
    {
        ref readonly NativeFrameSlot slot = ref layout.Slots[index];
        bindings[slot.Binding] = address;
        if (slot.Persistent && sessionLocations is not null) sessionLocations[index] = address;
    }

    internal NativeAddress Pointer(int index)
    {
        ref readonly NativeFrameSlot slot = ref layout.Slots[index];
        NativeAddress location = slot.Binding < 0 ? storage.AddressAt(slot.Offset, slot.Type) : bindings[slot.Binding];
        bool isExactAddress = slot.Binding < 0 ? slot.ExactAddress : location.Type.Kinds == (1UL << (int)NativeKind.Address);
        NativeAddress address = isExactAddress && IsReady(index, location)
            ? NativeStorage.LoadAddressDirect(location) : GetDatum(index).Value.AsAddress();
        return address.StorageType is not null || slot.Pointee is null ? address
            : address.OffsetBy(default, slot.Pointee);
    }

    internal void SetPointer(int index, NativeAddress address)
    {
        ref readonly NativeFrameSlot slot = ref layout.Slots[index];
        NativeAddress destination = slot.Binding < 0 ? storage.AddressAt(slot.Offset, slot.Type) : bindings[slot.Binding];
        if (slot.Binding < 0 ? slot.ExactAddress : destination.Type.Kinds == (1UL << (int)NativeKind.Address))
            memory.WriteAddress(destination, address);
        else memory.Write(destination, new(NativeValue.FromAddress(address)));
    }

    internal void Copy(int source, int destination)
        => CopyTo(source, Location(destination));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsPrivate(int slot) => (layout.Slots[slot].Storage & NativeSlotStorage.PrivateArgument) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool IsReady(int slot, NativeAddress address) =>
        address.IsInitialized || IsPrivate(slot) && !memory.IsPending(address);

    internal bool TryCopyReady(int source, NativeAddress destination)
    {
        NativeAddress address = Location(source);
        return IsReady(source, address) && memory.TryCopyValue(address, destination);
    }

    internal void CopyTo(int source, NativeAddress destination)
    {
        if (!IsPrivate(source))
        {
            memory.Copy(Location(source), destination);
            return;
        }
        if (Activation?.Work is { } work) work.Counters.CompiledCopy();
        memory.CopyCompiled(Location(source), destination);
    }
    internal void Copy(NativeType type, int source, int destination)
    {
        if (!IsPrivate(source))
        {
            memory.Copy(type, Location(source), Location(destination));
            return;
        }
        if (Activation?.Work is { } work) work.Counters.CompiledCopy();
        memory.CopyCompiled(type, Location(source), Location(destination));
    }

    internal void Drop(int index)
    {
        memory.Clear(Location(index));
    }

    internal bool TryAllocate(int instruction, out NativeAddress address)
    {
        if (layout.At(instruction) is not { } region) { address = default; return false; }
        address = storage.AddressAt(region.Offset);
        return true;
    }

}
