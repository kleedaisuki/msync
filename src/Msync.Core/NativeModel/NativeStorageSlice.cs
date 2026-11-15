using System.Runtime.CompilerServices;

namespace Msync.Core.Native;

// Offsets are linked storage ABI facts. A field access should not construct and
// range-check the whole owning frame before addressing that field.
internal readonly struct NativeStorageSlice<T>
{
    internal readonly T[] Buffer;
    internal readonly int Offset;
    internal readonly int Length;

    internal NativeStorageSlice(T[] buffer) : this(buffer, 0, buffer.Length) { }
    internal NativeStorageSlice(T[] buffer, int offset, int length) =>
        (Buffer, Offset, Length) = (buffer, offset, length);

    internal ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref Buffer[Offset + index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<T> Slice(int start, int length) => Buffer.AsSpan(Offset + start, length);

    internal void Clear() => Buffer.AsSpan(Offset, Length).Clear();
}
