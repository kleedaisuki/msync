namespace Msync.Hir;

// Lexical constructors already provide field addresses. A frame binding is needed
// only when executable code names that field outside its constructor region.
internal sealed class HirFieldBindings(HirModule module)
{
    private readonly HashSet<HirLocationId> active = [];
    private readonly HashSet<HirLocationId> bindings = [];

    internal static HashSet<HirLocationId> Find(HirModule module)
    {
        HirFieldBindings analysis = new(module);
        HashSet<HirNodeId> bodies = [];
        foreach (HirFunction function in module.Functions)
            if (!function.Body.IsNone && bodies.Add(function.Body)) analysis.Visit(function.Body);
        return analysis.bindings;
    }

    private void Use(HirLocationId location)
    {
        if (module.Locations[location.Value].Kind == HirLocationKind.Field && !active.Contains(location))
            bindings.Add(location);
    }

    private void Capture(HirCapture capture)
    {
        if (capture.Mode == HirCaptureMode.Location) Use(capture.SourceLocation);
        else Visit(capture.SourceValue);
    }

    private void Visit(HirNodeId id)
    {
        HirNode node = module.Node(id);
        if (node.Kind == HirKind.Scope) { Scope(node); return; }
        switch (node.Kind)
        {
            case HirKind.ReadLocation: case HirKind.ReadStorage: case HirKind.Assign:
                Use(new(node.Data)); break;
            case HirKind.AllocateScope:
                bindings.UnionWith(module.Scopes[node.Data].Fields); break;
            case HirKind.Closure:
                foreach (HirCapture capture in module.Closures[node.Data].Captures) Capture(capture);
                break;
            case HirKind.TemplateClosure:
                foreach (HirCapture capture in module.TemplateClosures[node.Data].Captures) Capture(capture);
                break;
            case HirKind.Match:
                foreach (HirMatchArm arm in module.Matches[node.Data].Arms) Visit(arm.Value);
                break;
        }
        foreach (HirNodeId child in module.Children(node)) Visit(child);
    }

    private void Scope(HirNode node)
    {
        var fields = module.Scopes[node.Data].Fields;
        foreach (HirLocationId field in fields) active.Add(field);
        foreach (HirNodeId child in module.Children(node)) Visit(child);
        foreach (HirLocationId field in fields) active.Remove(field);
    }
}
