using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal interface IFlowProgramView
{
    int LocationCount { get; }
    HirNode Node(HirNodeId id);
    ReadOnlySpan<HirNodeId> Children(HirNode node);
    TypeRef RuntimeType(HirTypeId id);
    HirLiteral Literal(int index);
    HirClosure Closure(int index);
    HirTemplateClosure TemplateClosure(int index);
    HirScopeConstruction Scope(int index);
    HirFor For(int index);
    HirMatch Match(int index);
}

internal sealed class FrozenFlowProgramView(HirModule module) : IFlowProgramView
{
    public int LocationCount => module.Locations.Length;
    public HirNode Node(HirNodeId id) => module.Node(id);
    public ReadOnlySpan<HirNodeId> Children(HirNode node) => module.Children(node);
    public TypeRef RuntimeType(HirTypeId id) => module.RuntimeType(id);
    public HirLiteral Literal(int index) => module.Literals[index];
    public HirClosure Closure(int index) => module.Closures[index];
    public HirTemplateClosure TemplateClosure(int index) => module.TemplateClosures[index];
    public HirScopeConstruction Scope(int index) => module.Scopes[index];
    public HirFor For(int index) => module.Fors[index];
    public HirMatch Match(int index) => module.Matches[index];
}

internal sealed class BoundFlowProgramView(BoundProgram program) : IFlowProgramView
{
    public int LocationCount => program.Hir.Locations.Count;
    public HirNode Node(HirNodeId id) => program.Node(id);
    public ReadOnlySpan<HirNodeId> Children(HirNode node) =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(program.Hir.Edges)
            .Slice(node.Children.Start, node.Children.Count);
    public TypeRef RuntimeType(HirTypeId id) => program.Hir.Types[id.Value].RuntimeType;
    public HirLiteral Literal(int index) => program.Hir.Literals[index];
    public HirClosure Closure(int index) => program.Hir.Closures[index];
    public HirTemplateClosure TemplateClosure(int index) => program.Hir.TemplateClosures[index];
    public HirScopeConstruction Scope(int index) => program.Hir.Scopes[index];
    public HirFor For(int index) => program.Hir.Fors[index];
    public HirMatch Match(int index) => program.Hir.Matches[index];
}
