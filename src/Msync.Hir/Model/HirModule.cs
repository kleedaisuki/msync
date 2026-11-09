using System.Collections.Immutable;
using Msync.Core;

namespace Msync.Hir;

public sealed record HirSource(string Name, string? Text = null);
public sealed record HirType(TypeRef RuntimeType, int Shape = -1, int Signature = -1,
    int Element = -1, int TypeParameter = -1, int Concept = -1,
    bool Nullable = false, ImmutableArray<HirTypeId> Alternatives = default);
public enum HirLocationKind : byte { Local, Parameter, Field, Temporary, Import }
public sealed record HirLocation(string Name, HirTypeId StorageType,
    HirSourceRange Source, HirFunctionId Owner, HirLocationKind Kind = HirLocationKind.Local,
    bool Mutable = true, bool AdmitsNull = false, HirTemplateId? TemplateOwner = null);
public sealed record HirField(string Name, HirTypeId StorageType, bool AdmitsNull = false);
public sealed record HirScopeLayout(ImmutableArray<HirField> Fields);
public sealed record HirSignature(ImmutableArray<HirTypeId> Parameters, HirTypeId Result,
    bool Variadic = false);
public enum HirCaptureMode : byte { Value, Location }
public sealed record HirCapture(HirLocationId Target, HirCaptureMode Mode,
    HirLocationId SourceLocation, HirNodeId SourceValue);
public sealed record HirClosure(HirFunctionId Function, ImmutableArray<HirCapture> Captures);
public sealed record HirTemplateClosure(HirTemplateId Template, ImmutableArray<HirCapture> Captures);
public enum HirCallKind : byte { Function, Native, Dynamic, Path }
public sealed record HirDispatchTarget(HirCallableId Identity, HirFunctionId Target);
public sealed record HirCall(HirCallKind Kind, HirFunctionId Function,
    string? NativeName = null, ImmutableArray<HirDispatchTarget> Targets = default,
    int Scheduling = 0);
public sealed record HirScopeConstruction(int Layout, ImmutableArray<HirLocationId> Fields,
    int Singleton = -1, HirLocationId? Self = null, HirAllocationSiteId? Site = null);
public sealed record HirFor(HirLoopId Loop, HirLocationId Item);
public sealed record HirRange(ImmutableArray<bool> Deferred);
public sealed record HirFunctionAdaptation(ImmutableArray<HirDispatchTarget> Targets);
public enum HirPatternKind : byte { Any, Null, Literal, Type, Regex }
public sealed record HirPattern(HirPatternKind Kind, int Data = -1, string? Text = null,
    HirLocationId? Binding = null);
public sealed record HirMatchArm(HirPattern Pattern, HirNodeId Value);
public sealed record HirMatch(ImmutableArray<HirMatchArm> Arms);
public sealed record HirBarrier(bool Release, bool Acquire);

public enum HirLiteralKind : byte { Null, Bool, Number, String, Path, Connection, Type }
public readonly record struct HirLiteral(HirLiteralKind Kind, string? Text = null,
    double Number = 0, bool Boolean = false, TypeRef? Type = null,
    ConnectionKind ConnectionKind = ConnectionKind.Local);

public sealed record HirFunction(string Name, ImmutableArray<HirLocationId> Parameters,
    ImmutableArray<HirCapture> Captures, HirTypeId Result, HirNodeId Body,
    HirTemplateId? Template = null, bool IsOpen = false);
public sealed record HirTemplate(string Name, ImmutableArray<string> TypeParameters,
    ImmutableArray<HirLocationId> Parameters, HirNodeId Body, HirSourceRange Source,
    ImmutableArray<HirCapture> Captures = default, HirTypeId? ResultAnnotation = null,
    ImmutableArray<HirTypeId> TypeParameterTypes = default);
public sealed record HirConceptMember(string Name, HirTypeId Type, HirSourceRange Source,
    int Signature = -1, bool Nullable = false);
public sealed record HirConcept(string Name, HirSourceRange Source, ImmutableArray<int> Bases,
    ImmutableArray<HirTypeId> TypeParameters, ImmutableArray<HirConceptMember> Members);
public sealed record HirExport(string Name, HirLocationId Location);

public sealed record HirModule(
    ImmutableArray<HirSource> Sources,
    ImmutableArray<HirType> Types,
    ImmutableArray<HirLocation> Locations,
    ImmutableArray<HirFunction> Functions,
    ImmutableArray<HirTemplate> Templates,
    ImmutableArray<HirNode> Nodes,
    ImmutableArray<HirNodeId> Edges,
    ImmutableArray<HirLiteral> Literals,
    ImmutableArray<HirScopeLayout> ScopeLayouts,
    ImmutableArray<HirSignature> Signatures,
    ImmutableArray<HirCall> Calls,
    ImmutableArray<HirClosure> Closures,
    ImmutableArray<HirScopeConstruction> Scopes,
    ImmutableArray<HirFor> Fors,
    ImmutableArray<HirMatch> Matches,
    ImmutableArray<HirBarrier> Barriers,
    ImmutableArray<HirRange> Ranges,
    ImmutableArray<HirFunctionAdaptation> Adaptations,
    HirFunctionId Entry,
    ImmutableArray<HirExport> Exports,
    bool IsSession = false,
    ImmutableArray<HirTemplateClosure> TemplateClosures = default,
    ImmutableArray<HirConcept> Concepts = default)
{
    public ImmutableArray<HirLocationId> UnresolvedImports { get; init; } = [];
    public ReadOnlySpan<HirNodeId> Children(HirNode node) => Edges.AsSpan(node.Children.Start, node.Children.Count);
    public HirNode Node(HirNodeId id) => Nodes[id.Value];
    public TypeRef RuntimeType(HirTypeId id) => Types[id.Value].RuntimeType;
}
