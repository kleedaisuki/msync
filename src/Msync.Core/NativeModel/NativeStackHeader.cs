namespace Msync.Core.Native;

internal unsafe struct NativeStackHeader
{
    internal static int Bytes => sizeof(NativeStackHeader);
    internal const int X64DataOffset = 8, X64InitializationOffset = 16, X64ReferencesOffset = 24, X64PreviousOffset = 32;
    internal const int X64ArenaOffset = 40, X64ReferenceCountOffset = 48, X64Bytes = 56;

    // The address token and the pending index both name the first word.
    internal nint Pending;
    internal nint Data;
    internal nint Initialization;
    internal nint References;
    internal NativeStackHeader* Previous;
    internal long Arena;
    internal int ReferenceCount;

    internal readonly ReadOnlySpan<nint> Roots => new((nint*)References, ReferenceCount);
}
