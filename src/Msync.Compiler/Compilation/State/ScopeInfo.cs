using Msync.Core;

namespace Msync.Compiler;

internal sealed record ScopeInfo(IReadOnlyDictionary<string, Info> Members, bool Named = false,
    IReadOnlyDictionary<string, IReadOnlyList<EditorLocation>>? Origins = null,
    IReadOnlySet<string>? NullableMembers = null, IReadOnlyDictionary<string, TypeRef?>? StorageTypes = null)
{
    public object Identity { get; init; } = new();
    public bool IsInitializing { get; set; }
    public ScopeMemory Memory { get; init; } = new();
    public IReadOnlyList<ScopeInfo>? Sources { get; init; }
    public object? MemoryVersion { get; init; }
    public IReadOnlyDictionary<string, StorageConstraint> Storage { get; init; } =
        Members.ToDictionary(pair => pair.Key, pair => new StorageConstraint(StorageTypes?.GetValueOrDefault(pair.Key)
            ?? (pair.Value.Type.WithoutNull.Kind == TypeKind.Never ? null : pair.Value.Type),
            pair.Value.Type.WithoutNull == TypeRef.Function ? pair.Value with { Storage = null } : null, pair.Value.Storage));
    public IReadOnlyDictionary<string, Binding> FieldBindings { get; init; } = new Dictionary<string, Binding>();
    public int AllocationSite { get; init; }
}
internal sealed class ScopeMemory
{
    public ScopeInfo? View { get; set; }
    public object Clock { get; set; } = new();
}
