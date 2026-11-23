namespace Msync.Core.Hpir;

public enum HpirTransferSource : byte { Path, Text, Selection }

// Keep the two lexical spellings until binding selects the execution host.
public readonly record struct HpirLocalPath(string Raw, string? WindowsSpelling = null);

// An observation reaches the binder only when the producer retained a
// contextual read with no intervening resource writer.
public sealed record HpirContextCheck(HpirFileMetadata Operation, HpirLocalPath Path, SourceSpan? Source,
    bool? Value = null);

public sealed record HpirTransferInput(HpirTransferSource Kind, HpirLocalPath? Path, string? Text,
    HpirLocalPath Destination, string? Pattern, SourceSpan? Source);

// All paths are anchored by binding. A selection with a pattern still
// observes its bound directory at execution; without one, Paths is its frozen membership.
public sealed record HpirPreparedTransfer(HpirTransferSource Kind, IReadOnlyList<string> Paths, string? Text,
    string Destination, string? Pattern, SourceSpan? Source)
{
    public IReadOnlyList<int> Predecessors { get; init; } = [];
}

// A source-owned request to freeze selected local iterator membership. null
// is an unobserved recipe; an empty member list is an observed empty selector.
public sealed record HpirIteratorSelection(HpirLocalPath Directory, string Pattern, bool ExplicitLocal,
    SourceSpan? Source, IReadOnlyList<HpirLocalPath>? Members = null);
