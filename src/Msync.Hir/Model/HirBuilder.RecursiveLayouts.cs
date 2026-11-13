using Msync.Core;

namespace Msync.Hir;

public sealed partial class HirBuilder
{
    private readonly Dictionary<string, HirTypeId> recursiveScopeTypes = new(StringComparer.Ordinal);
    private readonly HashSet<int> reservedScopeLayouts = [];

    public HirTypeId RecursiveScope(string identity, Func<HirTypeId, HirScopeLayout> fields)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(fields);
        if (recursiveScopeTypes.TryGetValue(identity, out var existing)) return existing;

        using Transaction pending = BeginTransaction();
        int layout = ScopeLayouts.Count;
        reservedScopeLayouts.Add(layout);
        ScopeLayouts.Add(new([]));
        HirTypeId type = Type(new HirType(TypeRef.Scope, Shape: layout));
        recursiveScopeTypes.Add(identity, type);
        HirScopeLayout completed = fields(type);
        ScopeLayouts[layout] = completed;
        reservedScopeLayouts.Remove(layout);
        if (scopeLayoutIndex is not null && scopeLayoutIndex.TryAdd(completed, layout))
            RecordScopeLayout(completed, layout);
        pending.Commit();
        return type;
    }

    private void RewindRecursiveScopes(int count)
    {
        foreach (var pair in recursiveScopeTypes.Where(pair => pair.Value.Value >= count).ToArray())
            recursiveScopeTypes.Remove(pair.Key);
    }
}
