using System.Runtime.CompilerServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed class NativeFrame
{
    private readonly NativeImage image;
    private readonly int functionIndex;
    private readonly NativeFunctionCode function;
    private readonly NativeAllocation storage;
    private readonly NativeFrameSlot[] slots;
    private readonly NativeAddress[] bindings;
    private readonly NativeMemory memory;
    private readonly Dictionary<int, NativeAddress>? sessionLocations;

    internal NativeFrame(NativeImage image, int functionIndex, NativeEnvironment? environment,
        IReadOnlyList<NativeDatum> arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations = null)
        : this(image, functionIndex, environment, new NativeArguments(arguments), memory, sessionLocations, null) { }

    internal NativeFrame(NativeImage image, int functionIndex, NativeEnvironment? environment,
        NativeArguments arguments, NativeMemory memory,
        Dictionary<int, NativeAddress>? sessionLocations = null, NativeAllocation? allocation = null)
    {
        this.image = image;
        this.functionIndex = functionIndex;
        this.memory = memory;
        this.sessionLocations = sessionLocations;
        function = image.Program.Functions[functionIndex];
        FrameAllocation layout = image.Frame(functionIndex);
        slots = layout.Slots;
        storage = allocation ?? new(layout.Layout);
        bindings = layout.BindingSlots.Length == 0 ? [] : new NativeAddress[layout.BindingSlots.Length];
        foreach (int index in layout.BindingSlots)
        {
            NativeFrameSlot slot = slots[index];
            NativeAddress address = storage.AddressAt(slot.Offset, slot.Type);
            if (slot.Persistent && sessionLocations is not null)
            {
                if (!sessionLocations.TryGetValue(index, out address))
                {
                    address = new NativeAllocation(new(slot.Type)).Field(0);
                    sessionLocations.Add(index, address);
                }
            }
            bindings[slot.Binding] = address;
        }
        for (int index = 0; index < function.CaptureCount; index++)
        {
            if (function.Signature.Captures[index].Mode == NativeCaptureMode.Location)
                bindings[slots[index].Binding] = NativeStorage.LoadAddress(environment!.CaptureAddress(index));
            else memory.Copy(environment!.CaptureAddress(index), Location(index));
        }
        for (int index = 0; index < arguments.Count; index++) arguments.Initialize(this, index);
    }

    internal int CaptureCount => function.CaptureCount;
    internal void BorrowParameter(int slot, NativeAddress address)
    {
        bindings[slots[slot].Binding] = address;
    }
    internal void CopyArgument(NativeAddress source, int slot) => memory.Copy(source, Location(slot));

    internal void Set(int index, NativeValue value)
    {
        memory.Write(Location(index), new(value));
    }

    internal NativeDatum GetDatum(int index) => memory.Read(Location(index));
    internal void SetDatum(int index, NativeDatum value)
    {
        memory.Write(Location(index), value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal NativeAddress Location(int index)
    {
        ref readonly NativeFrameSlot slot = ref slots[index];
        return slot.Binding < 0 ? storage.AddressAt(slot.Offset, slot.Type) : bindings[slot.Binding];
    }
    internal NativeAllocation Allocation => storage;
    internal int Type(int index) => function.Slots[index].Type;
    internal void Bind(int index, NativeAddress address)
    {
        ref readonly NativeFrameSlot slot = ref slots[index];
        bindings[slot.Binding] = address;
        if (slot.Persistent && sessionLocations is not null) sessionLocations[index] = address;
    }

    internal NativeAddress Pointer(int index)
    {
        ref readonly NativeFrameSlot slot = ref slots[index];
        NativeAddress location = slot.Binding < 0 ? storage.AddressAt(slot.Offset, slot.Type) : bindings[slot.Binding];
        bool isExactAddress = slot.Binding < 0 ? slot.ExactAddress : location.Type.Kinds == (1UL << (int)NativeKind.Address);
        NativeAddress address = isExactAddress && location.IsInitialized
            ? NativeStorage.LoadAddressDirect(location) : GetDatum(index).Value.AsAddress();
        return address.StorageType is not null || slot.Pointee is null ? address
            : address.OffsetBy(default, slot.Pointee);
    }

    internal void SetPointer(int index, NativeAddress address)
    {
        ref readonly NativeFrameSlot slot = ref slots[index];
        NativeAddress destination = slot.Binding < 0 ? storage.AddressAt(slot.Offset, slot.Type) : bindings[slot.Binding];
        if (slot.Binding < 0 ? slot.ExactAddress : destination.Type.Kinds == (1UL << (int)NativeKind.Address))
            memory.WriteAddress(destination, address);
        else memory.Write(destination, new(NativeValue.FromAddress(address)));
    }

    internal void Copy(int source, int destination)
    {
        memory.Copy(Location(source), Location(destination));
    }
    internal void Copy(NativeType type, int source, int destination) => memory.Copy(type, Location(source), Location(destination));

    internal void Drop(int index)
    {
        memory.Clear(Location(index));
    }

    internal bool TryAllocate(int instruction, out NativeAddress address)
    {
        if (image.FrameAllocation(functionIndex, instruction) is not { } region) { address = default; return false; }
        address = storage.AddressAt(region.Offset);
        return true;
    }

}
