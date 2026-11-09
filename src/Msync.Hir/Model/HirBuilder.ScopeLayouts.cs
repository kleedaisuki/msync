namespace Msync.Hir;

public sealed partial class HirBuilder
{
    private Dictionary<HirScopeLayout, int>? scopeLayoutIndex;
    private List<(HirScopeLayout Layout, int Index)>? scopeLayoutKeys;
    private int indexedScopeLayouts;

    private sealed class ScopeLayoutComparer : IEqualityComparer<HirScopeLayout>
    {
        public static readonly ScopeLayoutComparer Instance = new();
        public bool Equals(HirScopeLayout? first, HirScopeLayout? second) => ReferenceEquals(first, second)
            || first is not null && second is not null && first.Fields.AsSpan().SequenceEqual(second.Fields.AsSpan());

        public int GetHashCode(HirScopeLayout layout)
        {
            HashCode hash = new();
            foreach (var field in layout.Fields)
            {
                hash.Add(field.Name, StringComparer.Ordinal);
                hash.Add(field.StorageType);
                hash.Add(field.AdmitsNull);
            }
            return hash.ToHashCode();
        }
    }

    public int InternScopeLayout(HirScopeLayout layout)
    {
        var indexByLayout = scopeLayoutIndex ??= new(ScopeLayoutComparer.Instance);
        var keys = scopeLayoutKeys ??= [];
        // Binding can append open rows directly. Index each such row once,
        // retaining its original ID and the first canonical structural match.
        while (indexedScopeLayouts < ScopeLayouts.Count)
        {
            int index = indexedScopeLayouts++;
            var existing = ScopeLayouts[index];
            if (indexByLayout.TryAdd(existing, index)) keys.Add((existing, index));
        }
        if (indexByLayout.TryGetValue(layout, out int found)) return found;
        int result = ScopeLayouts.Count;
        ScopeLayouts.Add(layout);
        indexByLayout.Add(layout, result);
        keys.Add((layout, result));
        indexedScopeLayouts++;
        return result;
    }

    private void RewindScopeLayouts(int count)
    {
        if (scopeLayoutKeys is null) return;
        while (scopeLayoutKeys.Count != 0 && scopeLayoutKeys[^1].Index >= count)
        {
            scopeLayoutIndex!.Remove(scopeLayoutKeys[^1].Layout);
            scopeLayoutKeys.RemoveAt(scopeLayoutKeys.Count - 1);
        }
        indexedScopeLayouts = Math.Min(indexedScopeLayouts, count);
    }
}
