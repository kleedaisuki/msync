using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Native;

namespace Msync.Runtime.Hpir;

internal readonly record struct HpirConnectionValue(nint Word)
{
    internal ConnectionKind Kind => HpirConnectionAbi.Kind(Word);
    internal nint Address => Word & ~HpirConnectionAbi.Mask;
    internal ReadOnlySpan<char> AddressText => HpirTextStorage.Read(Address);
    internal NativeConnection ToHost() => new(Kind, AddressText.ToString());
}
