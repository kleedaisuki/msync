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
        if (!ReferencesPlaceholder(completed, type.Value, layout)
            && TryFindScopeLayout(completed, out int existingLayout))
        {
            pending.Commit();
            HirTypeId canonical = Type(new HirType(TypeRef.Scope, Shape: existingLayout));
            recursiveScopeTypes[identity] = canonical;
            return canonical;
        }
        if (EnsureScopeLayoutsIndexed().TryAdd(completed, layout))
            RecordScopeLayout(completed, layout);
        pending.Commit();
        return type;
    }

    private bool ReferencesPlaceholder(HirScopeLayout completed, int typeId, int layoutId)
    {
        foreach (var field in completed.Fields)
            if (field.StorageType.Value >= typeId && ReferencesPlaceholderType(field.StorageType.Value)) return true;
        for (int i = typeId + 1; i < Types.Count; i++)
            if (ReferencesPlaceholderDirect(Types[i])) return true;
        for (int i = layoutId + 1; i < ScopeLayouts.Count; i++)
            foreach (var field in ScopeLayouts[i].Fields)
                if (field.StorageType.Value == typeId) return true;
        return false;

        bool ReferencesPlaceholderType(int index)
        {
            if (index == typeId) return true;
            return index > typeId && ReferencesPlaceholderDirect(Types[index]);
        }

        bool ReferencesPlaceholderDirect(HirType candidate)
        {
            if (candidate.Shape == layoutId || candidate.Element == typeId) return true;
            foreach (var alt in candidate.Alternatives.AsSpan())
                if (alt.Value == typeId) return true;
            for (int j = 0; j < candidate.ScopeDomain.Count; j++)
                if (candidate.ScopeDomain[j] == layoutId) return true;
            return false;
        }
    }

    private void RewindRecursiveScopes(int count)
    {
        foreach (var pair in recursiveScopeTypes.Where(pair => pair.Value.Value >= count).ToArray())
            recursiveScopeTypes.Remove(pair.Key);
    }
}
