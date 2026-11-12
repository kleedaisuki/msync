using Msync.Core;

namespace Msync.Hir;

/// <summary>Project finalized HIR types and logical places into one portable code image.</summary>
internal sealed class HirPlaceLowering(HirModule hir, HirLoweringSession session)
{
    private readonly record struct TypeKey(TypeKind Kind, int Element, HirScopeDomain Scope);
    private readonly record struct FieldKey(string Name, HirScopeDomain Scope);

    private readonly Dictionary<TypeKey, int> types = [];
    private readonly List<CodeType> typeTable = [];
    private readonly Dictionary<HirTypeId, int> hirTypes = [];
    private readonly Dictionary<FieldKey, int> projections = [];
    private readonly List<CodeFieldProjection> projectionTable = [];
    private HashSet<HirLocationId>? opaqueLocations;

    internal int Type(HirTypeId id, bool admitsNull = false)
    {
        if (!hirTypes.TryGetValue(id, out int index))
        {
            var type = hir.Types[id.Value];
            int element = type.Element >= 0 ? Type(new HirTypeId(type.Element))
                : type.RuntimeType.Element is { } coarse ? Type(coarse) : -1;
            index = Intern(new(type.RuntimeType.Kind, element, type.ScopeDomain));
            hirTypes.Add(id, index);
        }
        var stored = typeTable[index];
        if (!admitsNull || (stored.Kind & TypeKind.Null) != 0) return index;
        return Intern(new(stored.Kind | TypeKind.Null, stored.Element, hir.Types[id.Value].ScopeDomain));
    }

    internal int Type(TypeRef runtime)
    {
        int element = runtime.Element is { } nested ? Type(nested) : -1;
        return Intern(new(runtime.Kind, element, default));
    }

    private int Intern(TypeKey key)
    {
        if (types.TryGetValue(key, out int index)) return index;
        int[]? layouts = null;
        if (key.Scope.Count != 0)
        {
            layouts = new int[key.Scope.Count];
            for (int i = 0; i < layouts.Length; i++) layouts[i] = Layout(key.Scope[i]);
            Array.Sort(layouts);
        }
        index = typeTable.Count;
        typeTable.Add(new(key.Kind, key.Element, layouts));
        types.Add(key, index);
        return index;
    }

    internal int LocationType(HirLocationId location)
    {
        var storage = hir.Locations[location.Value];
        return Type(storage.StorageType, storage.AdmitsNull);
    }

    internal int CellType(HirLocationId location)
    {
        var storage = hir.Locations[location.Value];
        return Structural(storage.StorageType, storage.AdmitsNull);

        int Structural(HirTypeId id, bool admitsNull = false)
        {
            var type = hir.Types[id.Value];
            int element = type.Element >= 0 ? Structural(new HirTypeId(type.Element))
                : type.RuntimeType.Element is { } coarse ? Type(coarse) : -1;
            // A shared mutable location can legally receive a structurally
            // compatible value from a different construction layout.
            var kind = admitsNull ? type.RuntimeType.Kind | TypeKind.Null : type.RuntimeType.Kind;
            return Intern(new(kind, element, default));
        }
    }

    internal bool HasScopeDomain(HirTypeId value) =>
        hir.Types[value.Value] is { RuntimeType.Kind: TypeKind.Scope, ScopeDomain.Count: > 0 };

    internal bool HasScopeLayouts(HirTypeId value) => hir.Types[value.Value].ScopeDomain.Count != 0;

    internal bool ProvesScope(HirTypeId required, HirTypeId supplied, bool admitsNull = false)
    {
        var want = hir.Types[required.Value].ScopeDomain;
        var have = hir.Types[supplied.Value];
        if (admitsNull || have.RuntimeType.Kind != TypeKind.Scope || have.ScopeDomain.Count == 0) return false;
        int j = 0;
        for (int i = 0; i < have.ScopeDomain.Count; i++)
        {
            int shape = have.ScopeDomain[i];
            while (j < want.Count && want[j] < shape) j++;
            if (j == want.Count || want[j] != shape) return false;
        }
        return true;
    }

    internal bool FieldProvesScope(HirTypeId required, int projection)
    {
        var cases = projectionTable[projection].Cases;
        if (cases.Count == 0) return false;
        foreach (var alternative in cases)
        {
            var field = hir.ScopeLayouts[session.CodeLayoutOrder[alternative.Layout]].Fields[alternative.Ordinal];
            if (!ProvesScope(required, field.StorageType, field.AdmitsNull)) return false;
        }
        return true;
    }

    internal bool HasOpaqueScopeSource(HirLocationId location)
    {
        if (opaqueLocations is null)
        {
            // A later narrow semantic read does not rewrite the value that
            // an opaque producer originally placed in the location.
            opaqueLocations = [];
            foreach (var closure in hir.Closures) Record(closure.Captures);
            foreach (var closure in hir.TemplateClosures.AsSpan()) Record(closure.Captures);
            foreach (var node in hir.Nodes)
            {
                if (node.Kind is HirKind.Declare or HirKind.Assign)
                {
                    var source = hir.Node(hir.Children(node)[^1]);
                    if (OpaqueScope(source.Type)) opaqueLocations.Add(new(node.Data));
                }
                else if (node.Kind == HirKind.For)
                {
                    var item = hir.Fors[node.Data].Item;
                    if (OpaqueScope(hir.Locations[item.Value].StorageType)) opaqueLocations.Add(item);
                }
            }
        }
        return opaqueLocations.Contains(location);

        void Record(System.Collections.Immutable.ImmutableArray<HirCapture> captures)
        {
            foreach (var capture in captures.AsSpan())
                if (capture.Mode == HirCaptureMode.Location) opaqueLocations!.Add(capture.SourceLocation);
        }

        bool OpaqueScope(HirTypeId id) => (hir.Types[id.Value].RuntimeType.Kind & TypeKind.Scope) != 0 && !HasScopeLayouts(id);
    }

    internal int Layout(int shape)
    {
        if (session.CodeLayoutSlots.TryGetValue(shape, out int index)) return index;
        index = session.CodeLayoutOrder.Count;
        session.CodeLayoutSlots.Add(shape, index);
        session.CodeLayoutOrder.Add(shape);
        return index;
    }

    internal int Static(int site, int shape)
    {
        int layout = Layout(shape);
        if (session.CodeStaticSlots.TryGetValue(site, out int index))
        {
            if (session.CodeStaticScopes[index].Layout != layout)
                throw new InvalidOperationException("A named Scope changed its fixed HIR layout.");
            return index;
        }
        index = session.CodeStaticScopes.Count;
        session.CodeStaticSlots.Add(site, index);
        session.CodeStaticScopes.Add(new(layout));
        return index;
    }

    internal int Projection(HirNode node, HirNodeId receiver)
    {
        string name = node.Text!;
        var domain = hir.Types[hir.Node(receiver).Type.Value].ScopeDomain;
        FieldKey key = new(name, domain);
        if (projections.TryGetValue(key, out int index)) return index;
        CodeFieldCase[] cases = new CodeFieldCase[domain.Count];
        for (int i = 0; i < cases.Length; i++)
        {
            int shape = domain[i];
            cases[i] = new(Layout(shape), Field(shape, name, node.Data));
        }
        Array.Sort(cases, static (left, right) => left.Layout.CompareTo(right.Layout));
        index = projectionTable.Count;
        projectionTable.Add(new(name, cases));
        projections.Add(key, index);
        return index;
    }

    internal int Field(int shape, string name, int ordinal)
    {
        var fields = hir.ScopeLayouts[shape].Fields;
        if ((uint)ordinal < (uint)fields.Length && fields[ordinal].Name == name) return ordinal;
        // A joined member's symbolic ordinal may differ across its HIR
        // alternatives. Resolve those names here, not in the reader or VM.
        for (int i = 0; i < fields.Length; i++)
            if (fields[i].Name == name) return i;
        throw new InvalidOperationException($"Known HIR Scope layout #{shape} has no '{name}' field.");
    }

    internal Module Finish(Module module)
    {
        List<CodeLayout> layouts = [];
        // Reserve layout IDs without chasing their field types; walking the
        // growing worklist handles recursive aggregate types exactly once.
        for (int i = 0; i < session.CodeLayoutOrder.Count; i++)
        {
            var fields = hir.ScopeLayouts[session.CodeLayoutOrder[i]].Fields;
            layouts.Add(new(fields.Select(field => new CodeField(field.Name, Type(field.StorageType, field.AdmitsNull))).ToArray()));
        }
        return module with
        {
            CodeTypes = typeTable.ToArray(),
            ScopeLayouts = layouts.ToArray(),
            FieldProjections = projectionTable.ToArray(),
            StaticScopes = session.CodeStaticScopes.ToArray()
        };
    }
}
