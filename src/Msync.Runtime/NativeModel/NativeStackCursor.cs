using System.Runtime.InteropServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeStackCursor
{
    internal const int X64FrameOffset = 0, X64TopOffset = 8;
    internal const int X64DataOffset = 16, X64InitializationOffset = 24, X64ReferencesOffset = 32;
    internal NativeStackHeader* Frame;
    internal NativeOffset Top;
    internal nint Data;
    internal nint Initialization;
    internal nint References;
    internal NativeOffset Limit;

    internal readonly bool Fits(NativeOffset start, NativeOffset size) =>
        size.Bytes <= Limit.Bytes - start.Bytes && size.References <= Limit.References - start.References;
}
