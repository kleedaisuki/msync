namespace Msync.Hir;

/// <summary>Named bindings used only as a ready lexical field receiver.</summary>
internal sealed class HirStaticNamespaces
{
    internal sealed record Namespace(HirNodeId Declaration, int Construction, int Statement)
    {
        internal bool Escapes { get; set; }
    }

    private readonly Dictionary<HirNodeId, Namespace> declarations = [];
    private readonly Dictionary<int, Namespace> locations = [];

    internal bool ContainsDeclaration(HirNodeId id) =>
        declarations.TryGetValue(id, out var candidate) && !candidate.Escapes;

    internal bool TryGetReceiver(int location, out int construction)
    {
        if (locations.TryGetValue(location, out var candidate) && !candidate.Escapes)
        {
            construction = candidate.Construction;
            return true;
        }
        construction = -1;
        return false;
    }

    internal static HirStaticNamespaces? Find(HirModule module, HirNodeId body)
    {
        HirStaticNamespaces? plan = null;
        int nextStatement = 0;
        FindDeclaration(body);
        if (plan is not null) plan.Scan(module, body);
        return plan;

        void FindDeclaration(HirNodeId id)
        {
            if (id.IsNone) return;
            var declaration = module.Node(id);
            if (declaration.Kind is HirKind.Block or HirKind.Discard)
            {
                foreach (var child in module.Children(declaration)) FindDeclaration(child);
                return;
            }
            int statement = nextStatement++;
            if (declaration.Kind != HirKind.Declare) return;
            var initializer = module.Node(module.Children(declaration)[0]);
            if (initializer.Kind != HirKind.Scope || module.Scopes[initializer.Data] is not { Singleton: >= 0 } scope) return;
            plan ??= new();
            Namespace candidate = new(id, initializer.Data, statement);
            if (!plan.declarations.TryAdd(id, candidate))
            {
                plan.declarations[id].Escapes = true;
                return;
            }
            plan.AddLocation(declaration.Data, candidate);
            if (scope.Self is { } self && self.Value != declaration.Data) plan.AddLocation(self.Value, candidate);
        }
    }

    private void AddLocation(int location, Namespace candidate)
    {
        if (locations.TryAdd(location, candidate)) return;
        locations[location].Escapes = candidate.Escapes = true;
    }

    private void Scan(HirModule module, HirNodeId body)
    {
        Stack<(HirNodeId Id, bool Receiver)> pending = [];
        HashSet<(HirNodeId Id, bool Receiver)> visited = [];
        int nextStatement = 0;
        ScanLinear(body);

        void ScanLinear(HirNodeId id)
        {
            if (id.IsNone) return;
            var node = module.Node(id);
            if (node.Kind is HirKind.Block or HirKind.Discard)
            {
                foreach (var child in module.Children(node)) ScanLinear(child);
                return;
            }
            ScanStatement(id, nextStatement++);
        }

        void ScanStatement(HirNodeId id, int statement)
        {
            visited.Clear();
            pending.Push((id, false));
            while (pending.TryPop(out var use))
            {
                if (use.Id.IsNone || !visited.Add(use)) continue;
                var node = module.Node(use.Id);
                switch (node.Kind)
                {
                    case HirKind.ReadLocation:
                        if (locations.TryGetValue(node.Data, out var selected)
                            && (!use.Receiver || statement <= selected.Statement)) selected.Escapes = true;
                        break;
                    case HirKind.ReadStorage or HirKind.Assign:
                        Escape(node.Data);
                        break;
                    case HirKind.Declare:
                        if (locations.TryGetValue(node.Data, out var declared) && use.Id != declared.Declaration)
                            declared.Escapes = true;
                        break;
                    case HirKind.Scope:
                        foreach (var field in module.Scopes[node.Data].Fields) Escape(field.Value);
                        break;
                    case HirKind.Closure:
                        ScanCaptures(module.Closures[node.Data].Captures);
                        break;
                    case HirKind.TemplateClosure:
                        ScanCaptures(module.TemplateClosures[node.Data].Captures);
                        break;
                    case HirKind.Match:
                        foreach (var arm in module.Matches[node.Data].Arms) pending.Push((arm.Value, false));
                        break;
                }
                var children = module.Children(node);
                for (int i = 0; i < children.Length; i++)
                    pending.Push((children[i], i == 0 && node.Kind is HirKind.MemberRead or HirKind.MemberAssign));
            }
        }

        void Escape(int location)
        {
            if (locations.TryGetValue(location, out var candidate)) candidate.Escapes = true;
        }

        void ScanCaptures(System.Collections.Immutable.ImmutableArray<HirCapture> captures)
        {
            foreach (var capture in captures)
                if (capture.Mode == HirCaptureMode.Location) Escape(capture.SourceLocation.Value);
                else pending.Push((capture.SourceValue, false));
        }
    }
}
