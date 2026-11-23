using Msync.Core.Native;

namespace Msync.Core.Hpir;

/// <summary>An in-process submission whose persistent addresses belong to one session.</summary>
public sealed record HpirSessionSubmission(HpirProgram Program, int Generation, TypeKind ResultKind,
    IReadOnlyDictionary<int, HpirSessionCell> CellRelocations)
{
    internal object? Owner { get; init; }
    internal IReadOnlyList<TypeRef> Witnesses { get; init; } = [];
    internal IReadOnlyDictionary<int, HpirSessionField[]> ScopeLayouts { get; init; } =
        new Dictionary<int, HpirSessionField[]>();
    internal IReadOnlySet<int> TextSlots { get; init; } = new HashSet<int>();
    internal IReadOnlyDictionary<int, HpirSessionSnapshot> SnapshotLayouts { get; init; } = new Dictionary<int, HpirSessionSnapshot>();
    internal IReadOnlyDictionary<int, HpirSessionPathLiteral>? PathRelocations { get; init; }
    internal IReadOnlyCollection<HpirPathOrigin>? PreparedPathOrigins { get; init; }
    internal int ReturnBytes { get; init; }
    internal HpirSessionUnion? ResultUnion { get; init; }
    internal bool StartsProcesses { get; init; }
    internal bool StreamsRange { get; init; }
    internal bool DisplaysFutureScope { get; init; }
}

public readonly record struct HpirSessionCell(int Slot, TypeKind Kind, bool Aliased)
{
    internal HpirSessionUnion? Union { get; init; }
}

internal readonly record struct HpirSessionField(string Name, int Offset, TypeKind Kind, HpirSessionUnion? Union,
    int Completion = -1);
internal readonly record struct HpirSessionUnion(int RawOffset, int PointerOffset);
internal readonly record struct HpirSessionAllocation(int Layout, int Site);
internal readonly record struct HpirSessionSnapshot(TypeKind Item, bool Empty, int Callback = -1);
internal readonly record struct HpirPathOrigin(int Source, int Offset);
internal readonly record struct HpirSessionPathLiteral(HpirPathOrigin Origin, string Raw);

/// <summary>A typed observation at the session presentation boundary, never in guest storage.</summary>
public readonly record struct HpirSessionValue(TypeKind Kind, ulong Bits, string? Text = null,
    TypeRef? Type = null, NativePath? Path = null, HpirSessionScope? Scope = null, bool RequiresDemand = false,
    HpirSessionProcess? Process = null, NativeConnection? Connection = null)
{
    internal HpirSessionSnapshot? Snapshot { get; init; }
    public double Number => BitConverter.UInt64BitsToDouble(Bits);
    public bool Boolean => Bits != 0;
}

public sealed record HpirSessionScope(IReadOnlyList<HpirSessionMember> Members, bool Cycle = false)
{
    internal nint Identity { get; init; }
    internal int Site { get; init; }
}
public readonly record struct HpirSessionMember(string Name, HpirSessionValue Value);
public enum HpirSessionProcessState : byte { Running, Exited, Cancelled, Failed }
public readonly record struct HpirSessionProcess(HpirSessionProcessState State, int? ProcessId, int ExitCode = 0);
