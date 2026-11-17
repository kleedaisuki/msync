using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private unsafe void PublishMachineCaptures(NativeEnvironment environment)
    {
        NativeAddress storage = environment.Address;
        NativeAllocation owner = storage.Allocation;
        if (!scopedGuestValues || !NativeCompiledFunction.MachineCallsSupported
            || tierOptions.Policy is not (NativeJitPolicy.Auto or NativeJitPolicy.Force)
            || storage.Offset != default || !ReferenceEquals(owner.NativeHeapOwner, guestHeap)
            || environment.MachineCaptures != 0 || environment.Count is 0 or > 96)
            return;
        Span<NativeCaptureVector> captures = stackalloc NativeCaptureVector[environment.Count];
        captures.Clear();
        int locations = 0, values = 0;
        for (int index = 0; index < environment.Count; index++)
        {
            NativeCaptureLayout capture = environment.Layout.Captures[index];
            if (capture.Type.IsUnion || capture.Type.Kind is not (NativeKind.Number or NativeKind.Bool)) continue;
            NativeAddress address = environment.CaptureAddress(index);
            if (capture.Location)
            {
                if (!address.IsInitialized) continue;
                address = NativeStorage.LoadAddressDirect(address);
            }
            ref NativeStorage.Data data = ref address.Allocation.Storage;
            if (data.RawAddress == 0 || !ReferenceEquals(address.Allocation.NativeHeapOwner, guestHeap)
                || address.StorageType is not { } type || !type.Equals(capture.Type)) continue;
            captures[index] = new(data.RawAddress + address.Offset.Bytes, data.InitializationAddress + address.Offset.Bytes,
                NativeCaptureVector.Shape(capture.Type.Kind, capture.Location));
            if (capture.Location) locations++;
            else values++;
        }
        if (locations + values == 0) return;
        int bytes = 8 + captures.Length * NativeCaptureVector.EntryBytes;
        NativeGuestHeap.Allocation block = guestHeap.AllocateMetadata(bytes);
        byte* vector = (byte*)block.Address;
        *(int*)vector = captures.Length;
        captures.CopyTo(new Span<NativeCaptureVector>(vector + 8, captures.Length));
        Interlocked.CompareExchange(ref owner.MachineCaptures, (nint)vector, 0);
        if (profile is not null)
            (currentWork.Value ?? executionWork).Counters.NativeCaptureVector(bytes, locations, values, block.Reserved);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeCaptureVector(nint Data, nint Initialized, int Kind)
{
    internal const int EntryBytes = 24;
    internal static int Shape(NativeKind kind, bool location) => (int)kind | (location ? 256 : 0);
}
