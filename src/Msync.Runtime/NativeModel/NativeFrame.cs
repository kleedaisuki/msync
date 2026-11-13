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
    {
        this.image = image;
        this.functionIndex = functionIndex;
        this.memory = memory;
        this.sessionLocations = sessionLocations;
        function = image.Program.Functions[functionIndex];
        FrameAllocation layout = image.Frame(functionIndex);
        slots = layout.Slots;
        storage = new(layout.Layout);
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
                bindings[slots[index].Binding] = environment![index].Location;
            else SetDatum(index, memory.Read(environment!.CaptureAddress(index)));
        }
        for (int index = 0; index < arguments.Count; index++) SetDatum(function.CaptureCount + index, arguments[index]);
    }

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
    internal bool Owns(NativeAddress address) => ReferenceEquals(storage, address.Allocation);
    internal int Type(int index) => function.Slots[index].Type;
    internal void Bind(int index, NativeAddress address)
    {
        ref readonly NativeFrameSlot slot = ref slots[index];
        bindings[slot.Binding] = address;
        if (slot.Persistent && sessionLocations is not null) sessionLocations[index] = address;
    }

    internal NativeAddress Pointer(int index)
    {
        NativeAddress location = Location(index);
        NativeAddress address = location.IsInitialized && location.Type.Equals(NativeType.Of(NativeKind.Address))
            ? NativeStorage.LoadAddress(location) : GetDatum(index).Value.AsAddress();
        return address.StorageType is not null || image.Program.Types[function.Slots[index].Type].Pointee < 0 ? address
            : address.OffsetBy(default, image.Type(image.Program.Types[function.Slots[index].Type].Pointee));
    }

    internal void SetPointer(int index, NativeAddress address)
    {
        NativeAddress destination = Location(index);
        if (destination.Type.Equals(NativeType.Of(NativeKind.Address))) memory.WriteAddress(destination, address);
        else Set(index, NativeValue.FromAddress(address));
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
