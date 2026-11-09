namespace Msync.Hir;

public sealed partial class HirBuilder
{
    private int nextAllocationSite;
    private int checkedScopeSites;

    public int AddScope(HirScopeConstruction construction)
    {
        int index = Scopes.Count;
        var site = construction.Site ?? new(nextAllocationSite++);
        nextAllocationSite = Math.Max(nextAllocationSite, site.Value + 1);
        Scopes.Add(construction with { Site = site });
        return index;
    }

    private void PrepareScopeSites()
    {
        int start = Math.Min(checkedScopeSites, Scopes.Count);
        for (int index = start; index < Scopes.Count; index++)
            if (Scopes[index].Site is { } site)
                nextAllocationSite = Math.Max(nextAllocationSite, site.Value + 1);
        for (int index = start; index < Scopes.Count; index++)
            if (Scopes[index].Site is null)
                Scopes[index] = Scopes[index] with { Site = new(nextAllocationSite++) };
        checkedScopeSites = Scopes.Count;
    }
}
