namespace Msync.Hir;

internal static class HirNamespaceErasure
{
    private readonly record struct Candidate(int Singleton, HirLocationId Binding, HirLocationId? Self);

    internal static HashSet<HirNodeId>? Find(HirModule module)
    {
        Dictionary<HirNodeId, Candidate>? candidates = null;
        for (int index = 0; index < module.Nodes.Length; index++)
        {
            var declaration = module.Nodes[index];
            if (declaration.Kind != HirKind.Declare) continue;
            var value = module.Node(module.Children(declaration)[0]);
            if (value.Kind != HirKind.Scope) continue;
            var scope = module.Scopes[value.Data];
            if (InertInitializer(module, value, scope))
                (candidates ??= []).Add(new(index), new(scope.Singleton, new(declaration.Data), scope.Self));
        }
        if (candidates is null) return null;

        var uses = new bool[module.Locations.Length];
        var visited = new bool[module.Nodes.Length];
        HashSet<int> liveSingletons = [];
        Stack<HirNodeId> pending = [];
        foreach (var function in module.Functions) Visit(function.Body);
        while (pending.TryPop(out var id))
        {
            if (id.IsNone || visited[id.Value]) continue;
            visited[id.Value] = true;
            var node = module.Node(id);
            switch (node.Kind)
            {
                case HirKind.ReadLocation or HirKind.ReadStorage or HirKind.Assign:
                    uses[node.Data] = true;
                    break;
                case HirKind.Scope:
                    // Construction consumes the field's existing storage,
                    // even when no explicit HIR read names that location.
                    foreach (var field in module.Scopes[node.Data].Fields) uses[field.Value] = true;
                    break;
                case HirKind.Closure:
                    VisitCaptures(module.Closures[node.Data].Captures);
                    break;
                case HirKind.TemplateClosure:
                    VisitCaptures(module.TemplateClosures[node.Data].Captures);
                    break;
                case HirKind.Match:
                    foreach (var arm in module.Matches[node.Data].Arms) Visit(arm.Value);
                    break;
            }
            bool namespaceDeclaration = candidates.ContainsKey(id);
            foreach (var child in module.Children(node)) Visit(child, namespaceDeclaration);
        }

        // Generic specializations share a declaration's first publication.
        // Eliding only one constructor could make a later initializer run.
        foreach (var (id, candidate) in candidates)
            if (candidate.Singleton >= 0 && Used(id, candidate))
                liveSingletons.Add(candidate.Singleton);
        HashSet<HirNodeId> erased = [];
        foreach (var (id, candidate) in candidates)
            if (candidate.Singleton >= 0 ? !liveSingletons.Contains(candidate.Singleton) : !Used(id, candidate))
                erased.Add(id);
        return erased.Count == 0 ? null : erased;

        bool Used(HirNodeId id, Candidate candidate) => visited[id.Value]
            && (uses[candidate.Binding.Value] || candidate.Self is { } self && uses[self.Value]);

        void Visit(HirNodeId child, bool namespaceDeclaration = false)
        {
            if (child.IsNone) return;
            var node = module.Node(child);
            if (!namespaceDeclaration && node.Kind == HirKind.Scope
                && module.Scopes[node.Data] is { Singleton: >= 0 } scope)
                liveSingletons.Add(scope.Singleton);
            pending.Push(child);
        }

        void VisitCaptures(System.Collections.Immutable.ImmutableArray<HirCapture> captures)
        {
            foreach (var capture in captures)
            {
                if (capture.Mode == HirCaptureMode.Location) uses[capture.SourceLocation.Value] = true;
                else Visit(capture.SourceValue);
            }
        }
    }

    private static bool InertInitializer(HirModule module, HirNode value, HirScopeConstruction scope)
    {
        if (value.Children.Count != scope.Fields.Length) return false;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var field in module.ScopeLayouts[scope.Layout].Fields)
            if (!names.Add(field.Name)) return false;
        foreach (var child in module.Children(value))
        {
            var field = module.Node(child);
            if (field.Kind != HirKind.Declare || !scope.Fields.Contains(new HirLocationId(field.Data))) return false;
            var initializer = module.Node(module.Children(field)[0]);
            if (initializer.Kind is HirKind.Literal or HirKind.TypeValue or HirKind.NativeFunction) continue;
            if (initializer.Kind == HirKind.TemplateClosure
                && module.TemplateClosures[initializer.Data].Captures.IsDefaultOrEmpty) continue;
            if (initializer.Kind == HirKind.Closure && module.Closures[initializer.Data].Captures.IsDefaultOrEmpty) continue;
            return false;
        }
        return true;
    }
}
