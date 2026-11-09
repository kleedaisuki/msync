namespace Msync.Optimization;

internal sealed record FunctionMemoryEffects(IReadOnlyList<MemoryAccess> Reads, IReadOnlyList<MemoryAccess> Writes,
    IReadOnlyList<MemoryAccess> Escapes, IReadOnlyList<MemoryAccess> ReturnAliases,
    bool UnknownReads = false, bool UnknownWrites = false, bool UnknownReturnAlias = false, bool HasDeferredInvocation = false)
{
    internal IReadOnlyList<MemoryAccess> Enumerates { get; init; } = [];
    internal bool ReturnsCallbackFreeRange { get; init; }
    internal static readonly FunctionMemoryEffects Empty = new([], [], [], []);
}
