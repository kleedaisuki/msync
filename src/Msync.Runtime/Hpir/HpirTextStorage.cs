using System.Runtime.CompilerServices;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe class HpirTextStorage
{
    internal static int Elements(int chars) =>
        checked(chars + HpirScalarImports.TextHeaderBytes / HpirScalarImports.TextElementBytes);

    internal static ReadOnlySpan<char> Read(nint address) =>
        new((char*)(address + HpirScalarImports.TextHeaderBytes), Unsafe.ReadUnaligned<int>((byte*)address));

    internal static void Write(nint address, ReadOnlySpan<char> text)
        => text.CopyTo(Initialize(address, text.Length));

    internal static Span<char> Initialize(nint address, int length)
    {
        Unsafe.WriteUnaligned((byte*)address, length);
        return new((char*)(address + HpirScalarImports.TextHeaderBytes), length);
    }
}
