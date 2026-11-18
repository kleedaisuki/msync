namespace Msync.Core.Native;

// A source storage contract fits in the slot itself. Metadata objects are
// only reconstructed by callers that still use the materialized address ABI.
internal static class NativeStorageTypeWord
{
    internal const int Tag = 2;
    private static readonly NativeType?[] UnionMetadata = new NativeType[1 << ((int)NativeKind.Address + 1)];

    internal static bool IsType(nint word) => word > 0 && (word & 3) == Tag;
    internal static nint Encode(NativeType type) => ((nint)type.Kinds << 2) | Tag;

    internal static NativeType Decode(nint word)
    {
        ulong kinds = (ulong)word >> 2;
        if (kinds == 0 || System.Numerics.BitOperations.IsPow2(kinds)) return NativeType.FromKinds(kinds);
        ref NativeType? metadata = ref UnionMetadata[kinds];
        return Volatile.Read(ref metadata) ?? Intern(ref metadata, kinds);
    }

    private static NativeType Intern(ref NativeType? metadata, ulong kinds)
    {
        NativeType type = NativeType.FromKinds(kinds);
        return Interlocked.CompareExchange(ref metadata, type, null) ?? type;
    }
}
