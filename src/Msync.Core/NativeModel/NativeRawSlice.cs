using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

internal readonly unsafe struct NativeRawSlice<T> where T : unmanaged
{
    private readonly T* pointer;
    internal readonly T[] Buffer;
    internal readonly int Offset;
    internal readonly int Length;

    internal NativeRawSlice(T[] buffer) : this(buffer, 0, buffer.Length) { }

    internal NativeRawSlice(T[] buffer, int offset, int length) =>
        (Buffer, Offset, Length) = (buffer, offset, length);

    internal NativeRawSlice(T* pointer, int length)
    {
        this.pointer = pointer;
        Buffer = [];
        Length = length;
    }

    internal nint Address => (nint)pointer;

    internal ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref (pointer == null ? ref Buffer[Offset + index] : ref pointer[index]);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> Slice(int start, int length) =>
        pointer == null ? Buffer.AsSpan(Offset + start, length) : new(pointer + start, length);

    internal void Clear() => Slice(0, Length).Clear();
}
