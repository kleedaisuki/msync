using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal readonly record struct SemanticInputRole(int Value)
{
    public static SemanticInputRole SharedEnvironment => new(-1);
}

internal enum SemanticInputEdge : byte
{
    Root, Field, Element, Alternative, Capture, ValueCapture, WriteValue,
    WriteElement, ExternalResource
}

internal sealed class SemanticInputRoles
{
    private readonly Dictionary<Edge, SemanticInputRole> roles = [];
    private readonly record struct Edge(SemanticInputRole Parent, SemanticInputEdge Kind,
        string? Name, int Index);

    public SemanticInputRole Root(int index) => Get(new(-1), SemanticInputEdge.Root, index: index);
    public SemanticInputRole Field(SemanticInputRole parent, string name) => Get(parent, SemanticInputEdge.Field, name);
    public SemanticInputRole Element(SemanticInputRole parent) => Get(parent, SemanticInputEdge.Element);
    public SemanticInputRole Alternative(SemanticInputRole parent, HirTypeId type) =>
        Get(parent, SemanticInputEdge.Alternative, index: type.Value);
    public SemanticInputRole Capture(HirLocationId cell) => Get(new(-1), SemanticInputEdge.Capture, index: cell.Value);
    public SemanticInputRole ValueCapture(SemanticInputRole parent, HirLocationId cell) =>
        Get(parent, SemanticInputEdge.ValueCapture, index: cell.Value);
    public SemanticInputRole WriteValue(SemanticInputRole parent) => Get(parent, SemanticInputEdge.WriteValue);
    public SemanticInputRole WriteElement(SemanticInputRole parent) => Get(parent, SemanticInputEdge.WriteElement);
    public SemanticInputRole ExternalResource(HirTypeId element) =>
        Get(new(-1), SemanticInputEdge.ExternalResource, index: element.Value);

    private SemanticInputRole Get(SemanticInputRole parent, SemanticInputEdge kind, string? name = null, int index = 0)
    {
        Edge edge = new(parent, kind, name, index);
        if (roles.TryGetValue(edge, out var role)) return role;
        role = new(roles.Count);
        roles.Add(edge, role);
        return role;
    }
}
