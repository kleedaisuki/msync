using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeFrame
{
    internal NativeInterpreter.Activation? Activation;
    private readonly FrameAllocation layout;
    private readonly NativeAllocation storage;
    private readonly NativeMemory memory;
    private readonly NativeGuestHeap? heap;
    private readonly Dictionary<int, NativeAddress>? sessionLocations;
    private NativeLayout? liveSnapshot;
    private NativeFrameStack.Lease? stackLease;
    internal NativeFrameStack.Lease? StackLease => stackLease;

    internal void EnterStack(in NativeArguments arguments = default) =>
        stackLease = NativeFrameStack.Current.Push(storage, heap?.References, arguments.Window);

    internal void AttachStack(NativeFrameStack.Lease lease)
    {
        lease.Stack.AttachReserved(lease, storage, heap!.References!);
        stackLease = lease;
    }

    internal void LeaveStack()
    {
        if (stackLease is not { } lease) return;
        lease.Stack.Pop(lease, storage);
        stackLease = null;
    }

    internal NativeArguments SnapshotArguments(IReadOnlyList<int> inputs, int pc)
    {
        ClearArguments();
        NativeLayout packet = layout.SnapshotAt(pc);
        liveSnapshot = packet;
        for (int index = 0; index < inputs.Count; index++)
            CopyTo(inputs[index], SnapshotCell(packet[index]));
        return new NativeArguments(this, packet);
    }

    internal NativeAddress SnapshotCell(NativeFieldLayout field) =>
        storage.AddressAt(layout.SnapshotBase + field.Offset, field.Type);

    internal void ClearArguments()
    {
        if (liveSnapshot is not { } packet) return;
        liveSnapshot = null;
        foreach (NativeFieldLayout field in packet.Fields)
            memory.Clear(SnapshotCell(field));
    }

    internal NativeFrame(NativeImage image, int functionIndex, NativeEnvironment? environment,
        IReadOnlyList<NativeDatum> arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations = null)
        : this(image, functionIndex, environment, new NativeArguments(arguments), memory, sessionLocations, null) { }

    internal NativeFrame(NativeImage image, int functionIndex, NativeEnvironment? environment,
        NativeArguments arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations = null, NativeAllocation? allocation = null, bool stack = false,
        NativeGuestHeap? heap = null, RuntimeProfile.NativeFrameCounters counters = default, int deferredLocationCaptures = 0)
    {
        this.memory = memory;
        this.heap = heap;
        this.sessionLocations = sessionLocations;
        layout = image.Frame(functionIndex);
        storage = allocation ?? (stack
            ? new(layout.Layout, allocate: false)
            : Allocate(layout.Layout, counters));
        if (stack) EnterStack(arguments);
        try { Initialize(environment, arguments, sessionLocations, counters, deferredLocationCaptures); }
        catch
        {
            if (stack) { memory.ResetFrame(storage); LeaveStack(); }
            throw;
        }
    }

    internal NativeFrame(FrameAllocation layout, NativeMemory memory, NativeGuestHeap heap)
    {
        this.layout = layout;
        this.memory = memory;
        this.heap = heap;
        storage = new(layout.Layout, allocate: false);
    }

    internal void Reset(NativeEnvironment? environment, NativeArguments arguments,
        Dictionary<int, NativeAddress>? sessionLocations = null, RuntimeProfile.NativeFrameCounters counters = default,
        int deferredLocationCaptures = 0)
    {
        Initialize(environment, arguments, sessionLocations, counters, deferredLocationCaptures);
    }

    private NativeAllocation Allocate(NativeLayout layout, RuntimeProfile.NativeFrameCounters counters) =>
        heap?.NewObject(layout, -1, 1, 0, counters) ?? new(layout);

    private void Initialize(NativeEnvironment? environment, NativeArguments arguments,
        Dictionary<int, NativeAddress>? activeSessionLocations, RuntimeProfile.NativeFrameCounters counters, int deferredLocationCaptures)
    {
        if (layout.Code.IteratorCount != 0)
        {
            storage.Storage.RawSpan.Slice(layout.IteratorOffset, layout.Code.IteratorCount * NativeSequenceCursor.Bytes).Clear();
            PlanCursors.Clear();
        }
        foreach (int index in layout.BindingSlots)
        {
            NativeFrameSlot slot = layout.Slots[index];
            if (slot.Persistent && activeSessionLocations is not null)
            {
                if (!activeSessionLocations.TryGetValue(index, out NativeAddress address))
                {
                    address = Allocate(new(slot.Type), counters).Field(0);
                    activeSessionLocations.Add(index, address);
                }
                StoreBinding(slot.Binding, address);
            }
            else if (slot.HeapLayout is { } escaped) StoreBinding(slot.Binding, Allocate(escaped, counters).Field(0));
        }
        if (deferredLocationCaptures != layout.Code.CaptureCount)
            InitializeCaptures(environment!, deferLocations: deferredLocationCaptures != 0);
        NativeWindowDisposition window = stackLease?.Window ?? NativeWindowDisposition.None;
        if (window == NativeWindowDisposition.Adopted)
        {
            NativeArgumentMove[]? staged = arguments.StagedMoves;
            counters.ArgumentWindow(window, arguments.Count, staged?.Length ?? 0);
            if (staged is not null) arguments.PlaceFrameMoves(this, memory, counters, staged, 0);
            return;
        }
        if (window != NativeWindowDisposition.None) counters.ArgumentWindow(window, arguments.Count, 0);
        arguments.InitializeFrame(this, memory, counters);
    }

    private void InitializeCaptures(NativeEnvironment environment, bool deferLocations)
    {
        for (int index = 0; index < layout.Code.CaptureCount; index++)
        {
            if (layout.Code.Signature.Captures[index].Mode == NativeCaptureMode.Location)
            {
                if (!deferLocations) BindCaptureLocation(environment, index);
            }
            else memory.Copy(environment.CaptureAddress(index), Location(index));
        }
    }

    internal void BindCaptureLocations(NativeEnvironment environment)
    {
        for (int index = 0; index < layout.Code.CaptureCount; index++)
            if (layout.Code.Signature.Captures[index].Mode == NativeCaptureMode.Location)
                BindCaptureLocation(environment, index);
    }

    private void BindCaptureLocation(NativeEnvironment environment, int index) =>
        StoreBinding(layout.Slots[index].Binding, NativeStorage.LoadAddress(environment.CaptureAddress(index)));

    internal void RecordHeapLocations(RuntimeProfile.NativeFrameCounters counters)
    {
        counters.FrameBindings(layout.BindingSlots.Length, storage.EdgeTable is not null);
        foreach (int index in layout.BindingSlots)
            if (layout.Slots[index].HeapLayout is { } heap) counters.EscapingLocation(heap);
    }
    internal int CaptureCount => layout.Code.CaptureCount;
    private void StoreBinding(int index, NativeAddress address) => NativeStorage.StoreBinding(storage, layout.BindingAt(index), address);
    internal void BorrowParameter(int slot, NativeAddress address) => StoreBinding(layout.Slots[slot].Binding, address);
    internal void CopyArgument(NativeAddress source, int slot) => memory.CopyCompiled(source, Location(slot));

    internal bool TryPlaceArguments(NativeFrame caller, NativeArgumentMove[] moves, out bool pending)
    {
        pending = false;
        NativeGuestObjectTable? arena = storage.EdgeTable;
        // A pushed frame's private cells have empty reference words and no
        // callable mirror. Replacing an arbitrary cell needs the full copy.
        if (stackLease is null || arena is null || !ReferenceEquals(arena, caller.storage.EdgeTable)) return false;
        NativeLocation source = caller.NativeBase(), destination = NativeBase();
        if (!source.IsNative || !destination.IsNative) return false;
        if (source.HasPending || destination.HasPending)
        {
            pending = true;
            return false;
        }
        foreach (ref readonly NativeArgumentMove move in moves.AsSpan())
            source.PlaceArgument(move.Source, destination, move.Destination, move.Representation, arena);
        return true;
    }

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
        return Location(slot);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private NativeAddress Location(in NativeFrameSlot slot)
    {
        if (slot.Binding >= 0)
        {
            NativeAddress binding = NativeStorage.LoadBinding(storage, layout.BindingAt(slot.Binding));
            if (!binding.IsNull) return binding;
        }
        return storage.AddressAt(slot.Offset, slot.Type);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe NativeLocation NativeBase()
    {
        ref NativeStorage.Data data = ref storage.Storage;
        if (data.PendingCell == 0) return default;
        return data.StackAddressable ? NativeLocation.Stack((NativeStackHeader*)data.PendingCell)
            : new(data.RawAddress, data.InitializationAddress, data.roots.GuestAddress, data.RawAddress, data.PendingCell);
    }

    internal bool TryNativeLocation(int index, out NativeLocation location, out NativeType type)
    {
        ref readonly NativeFrameSlot slot = ref layout.Slots[index];
        type = slot.Type;
        NativeLocation basis = NativeBase();
        location = default;
        if (!basis.IsNative) return false;
        if (slot.Binding >= 0)
        {
            NativeLocation binding = basis.At(layout.BindingAt(slot.Binding));
            if (binding.TryAddress(0, basis, out location, out nint word))
            {
                if (!NativeStorageTypeWord.IsType(word)) return false;
                if (word != NativeStorageTypeWord.Encode(type)) type = NativeStorageTypeWord.Decode(word);
                return true;
            }
            if (!binding.IsEmptyAddress) return false;
        }
        location = basis.At(slot.Offset);
        return true;
    }

    internal bool MaybeText(int slot) =>
        (layout.Slots[slot].Type.Kinds & ((1UL << (int)NativeKind.String) | (1UL << (int)NativeKind.Path))) != 0;

    internal bool TryNativePointer(int slot, out NativeLocation location, out nint type, out bool pending)
    {
        location = default;
        type = 0;
        pending = false;
        if (!TryNativeLocation(slot, out var source, out NativeType carrier)) return false;
        pending = source.HasPending;
        return !pending && !carrier.IsMachine && !carrier.IsNever
            && (source.IsInitialized || IsPrivate(slot))
            && (carrier.IsUnion ? source.Kind : carrier.Kind) == NativeKind.Address
            && source.TryAddress(carrier.Representation.PayloadOffset, default, out location, out type);
    }

    internal NativeType? Pointee(int slot) => layout.Slots[slot].Pointee;

    internal NativeAllocation Allocation => storage;
    internal ref NativeSequenceCursor SequenceCursor(int index) =>
        ref MemoryMarshal.Cast<byte, NativeSequenceCursor>(storage.Storage.RawSpan[layout.IteratorOffset..])[index];

    private Span<NativeRangeCursor> PlanCursors =>
        MemoryMarshal.Cast<byte, NativeRangeCursor>(storage.Storage.RawSpan.Slice(layout.PlanCursorOffset,
            layout.Code.IteratorCount * Unsafe.SizeOf<NativeRangeCursor>()));
    internal ref NativeRangeCursor RangeCursor(int index) => ref PlanCursors[index];
    internal void TraceRangeCursors(NativeGuestHeap heap)
    {
        if (layout.Code.IteratorCount == 0) return;
        foreach (ref NativeRangeCursor cursor in PlanCursors)
        {
            if (cursor.Root != 0) heap.TraceObject(cursor.Root);
            if (cursor.Scratch != 0) heap.TraceObject(cursor.Scratch);
        }
    }

    internal void CloseRangeCursors()
    {
        if (layout.Code.IteratorCount == 0) return;
        foreach (ref NativeRangeCursor cursor in PlanCursors) cursor.Close();
    }

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
        StoreBinding(slot.Binding, address);
        if (slot.Persistent && sessionLocations is not null) sessionLocations[index] = address;
    }

    internal NativeAddress Pointer(int index)
    {
        ref readonly NativeFrameSlot slot = ref layout.Slots[index];
        NativeAddress location = Location(slot);
        bool isExactAddress = slot.Binding < 0 ? slot.ExactAddress : location.Type.Kinds == (1UL << (int)NativeKind.Address);
        NativeAddress address = isExactAddress && IsReady(index, location)
            ? NativeStorage.LoadAddressDirect(location) : GetDatum(index).Value.AsAddress();
        return address.StorageType is not null || slot.Pointee is null ? address
            : address.OffsetBy(default, slot.Pointee);
    }

    internal void SetPointer(int index, NativeAddress address)
    {
        ref readonly NativeFrameSlot slot = ref layout.Slots[index];
        NativeAddress destination = Location(slot);
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
        (address.IsInitialized || IsPrivate(slot)) && !memory.IsPending(address);

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
