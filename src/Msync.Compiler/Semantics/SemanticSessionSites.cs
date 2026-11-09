using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed class SemanticSessionSites(BoundProgram program, HirLoweringLayout layout)
{
    private Dictionary<int, HirScopeConstruction>? scopes;

    public HirScopeConstruction? Find(int runtimeSite)
    {
        if (scopes is null)
        {
            scopes = [];
            foreach (var construction in program.Hir.Scopes)
                if (construction.Site is { } site && layout.AllocationSites.TryGetValue(site, out int physical))
                    scopes.TryAdd(physical, construction);
        }
        return scopes.GetValueOrDefault(runtimeSite);
    }
}
