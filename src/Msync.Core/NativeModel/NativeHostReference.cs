using System.Collections.Concurrent;

namespace Msync.Core.Native;

// The address of a marked native slot is its key at the host boundary.
// No CLR address or host object representation is stored in guest memory.
internal sealed class NativeHostReference
{
    internal const nint Marker = -1;
    internal static bool IsHost(nint edge) => edge < 0;

    private ConcurrentDictionary<nint, NativeRoot>? slots;

    internal void Set(nint slot, NativeRoot root) =>
        LazyInitializer.EnsureInitialized(ref slots)[slot] = root;

    internal NativeRoot Read(nint slot) =>
        Volatile.Read(ref slots) is { } table && table.TryGetValue(slot, out NativeRoot root) ? root : default;

    internal void Remove(nint slot) => Volatile.Read(ref slots)?.TryRemove(slot, out _);
    internal void Clear() => Volatile.Write(ref slots, null);
}
