using System.Runtime.InteropServices;

namespace Msync.Core.Native;

// Only GC references overlap. Raw bits are in a separate allocation region.
[StructLayout(LayoutKind.Explicit)]
internal struct NativeRoot
{
    [FieldOffset(0)] internal string? Text;
    [FieldOffset(0)] internal NativeAllocation? Allocation;
    [FieldOffset(0)] internal NativeEnvironment? Environment;
    [FieldOffset(0)] internal NativeRange? Range;
    [FieldOffset(0)] internal NativeProcess? Process;
    [FieldOffset(0)] internal TypeRef? Type;
    [FieldOffset(0)] internal NativeType? StorageType;
}
