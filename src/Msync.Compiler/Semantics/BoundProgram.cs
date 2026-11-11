using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal enum DeclarationKind : byte
{
    Local, Parameter, Field, Function, Native, Scope, Loop, Pattern, Capture,
    StandardModule, StandardRoot
}

internal sealed record BoundDeclaration(HirLocationId Location, string Name,
    DeclarationKind Kind, HirSourceRange Source, HirTypeId? Annotation = null,
    HirTemplateId? Template = null, HirLocationId? CapturedFrom = null);

internal sealed record BoundTypeUse(HirSourceRange Source, HirTypeId Type);
internal sealed record BoundReference(HirSourceRange Source, HirLocationId Location,
    bool Declaration = false, bool Parameter = false);
internal sealed record BoundVisibility(HirSourceRange Owner, int Position,
    ImmutableDictionary<string, HirLocationId> Bindings,
    ImmutableDictionary<string, int> Concepts, bool IncludesEnd = false,
    HirNodeId Anchor = default, bool After = false);
internal sealed record BoundMemberUse(HirNodeId Node, HirSourceRange ReceiverSpan,
    HirSourceRange MemberSpan, string? Name, HirSourceRange OperandSpan);
internal sealed record BoundPathUse(HirNodeId Node, HirSourceRange ReceiverSpan,
    HirSourceRange OperandSpan);
internal sealed record BoundGlobalImport(HirSourceRange Source,
    ImmutableDictionary<string, HirLocationId> Bindings);
internal sealed record BoundStandardUse(HirNodeId Node, HirSourceRange ReceiverSpan,
    HirSourceRange NameSpan, string Module, HirSourceRange? RootSpan = null);

internal sealed record BoundConcept(int Id, string Name, HirSourceRange Source,
    ImmutableArray<int> Bases, ImmutableArray<BoundRequirement> Requirements,
    ImmutableDictionary<string, HirTypeId> AssociatedTypes);
internal sealed record BoundRequirement(string Name, HirTypeId Type, bool Nullable,
    HirSourceRange Source, ImmutableArray<HirTypeId> Parameters = default,
    HirTypeId? Result = null, ImmutableArray<string> ParameterNames = default,
    HirSourceRange? DeclarationSource = null);
internal sealed record BoundNativeSignature(string Name, HirSourceRange Source,
    ImmutableArray<string> ParameterNames, ImmutableArray<HirTypeId> Parameters,
    HirTypeId Result, ImmutableArray<bool> NullableParameters = default);

internal sealed record BoundTemplate(HirTemplateId Id, string Name,
    ImmutableArray<HirLocationId> Parameters, ImmutableArray<HirLocationId> Captures,
    HirTypeId? ResultAnnotation, HirNodeId Body, HirSourceRange Source);
internal sealed record BoundModule(string Name, HirSourceId Source, HirNodeId Body,
    ImmutableDictionary<string, HirLocationId> Exports, ImmutableDictionary<string, int> Concepts);

internal sealed class BoundProgram(HirBuilder hir)
{
    public HirBuilder Hir { get; } = hir;
    public HirFunctionId Entry { get; set; }
    public HirNodeId Body { get; set; }
    public HirNodeId? DocumentPrefix { get; set; }
    public bool IsSession { get; set; }
    public int NextTypeParameter { get; set; }
    public int NextSingleton { get; set; }
    public int NextLoop { get; set; }
    public List<BoundDeclaration> Declarations { get; } = [];
    private readonly Dictionary<HirLocationId, BoundDeclaration> declarations = [];
    private readonly Dictionary<HirTemplateId, List<BoundDeclaration>> templateDeclarations = [];
    public List<BoundTemplate> Templates { get; } = [];
    public List<BoundConcept> Concepts { get; } = [];
    public List<BoundReference> References { get; } = [];
    public List<BoundVisibility> Visibility { get; } = [];
    public List<BoundTypeUse> TypeUses { get; } = [];
    public List<BoundMemberUse> MemberUses { get; } = [];
    public List<BoundPathUse> PathUses { get; } = [];
    public Dictionary<HirNodeId, HirSourceRange> QualificationPaths { get; } = [];
    public List<BoundGlobalImport> GlobalImports { get; } = [];
    public List<SourceDiagnostic> Diagnostics { get; } = [];
    public List<BoundStandardUse> StandardUses { get; } = [];
    public Dictionary<HirLocationId, HirTemplateId> FunctionLocations { get; } = [];
    public Dictionary<HirLocationId, string> NativeLocations { get; } = [];
    public Dictionary<string, BoundNativeSignature> NativeSignatures { get; } = new(StringComparer.Ordinal);
    public Dictionary<HirNodeId, HirTypeId> TypeAnnotations { get; } = [];
    public Dictionary<HirNodeId, int> ConceptUses { get; } = [];
    public Dictionary<int, string> TypeParameterNames { get; } = [];
    public HashSet<HirLocationId> WrittenLocations { get; } = [];
    public HashSet<string> WrittenMembers { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, BoundModule> StandardModules { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HirLocationId> StandardLocations { get; } = new(StringComparer.Ordinal);
    public ImmutableDictionary<string, HirLocationId> Exports { get; set; }
        = ImmutableDictionary<string, HirLocationId>.Empty;
    public ImmutableDictionary<string, int> RootConcepts { get; set; }
        = ImmutableDictionary<string, int>.Empty;

    public void Inherit(BoundProgram previous)
    {
        foreach (var declaration in previous.Declarations) Declare(declaration);
        Templates.AddRange(previous.Templates);
        Concepts.AddRange(previous.Concepts);
        foreach (var pair in previous.FunctionLocations) FunctionLocations.Add(pair.Key, pair.Value);
        foreach (var pair in previous.NativeLocations) NativeLocations.Add(pair.Key, pair.Value);
        foreach (var pair in previous.NativeSignatures) NativeSignatures.Add(pair.Key, pair.Value);
        foreach (var pair in previous.TypeAnnotations) TypeAnnotations.Add(pair.Key, pair.Value);
        foreach (var pair in previous.ConceptUses) ConceptUses.Add(pair.Key, pair.Value);
        foreach (var pair in previous.TypeParameterNames) TypeParameterNames.Add(pair.Key, pair.Value);
        foreach (var pair in previous.StandardModules) StandardModules.Add(pair.Key, pair.Value);
        foreach (var pair in previous.StandardLocations) StandardLocations.Add(pair.Key, pair.Value);
        WrittenLocations.UnionWith(previous.WrittenLocations);
        WrittenMembers.UnionWith(previous.WrittenMembers);
        Exports = previous.Exports;
        RootConcepts = previous.RootConcepts;
        Entry = previous.Entry;
        NextTypeParameter = previous.NextTypeParameter;
        NextSingleton = previous.NextSingleton;
        NextLoop = previous.NextLoop;
    }

    public HirNode Node(HirNodeId id) => Hir.Nodes[id.Value];
    public BoundDeclaration Declaration(HirLocationId location) => declarations[location];
    public IReadOnlyList<BoundDeclaration> DeclarationsOf(HirTemplateId template) =>
        templateDeclarations.TryGetValue(template, out var owned) ? owned : [];
    public bool TryDeclaration(HirLocationId location, out BoundDeclaration declaration) =>
        declarations.TryGetValue(location, out declaration!);
    public HirLocationId Original(HirLocationId location)
    {
        while (declarations[location].CapturedFrom is { } source) location = source;
        return location;
    }
    public bool IsStable(HirLocationId location)
    {
        if (!declarations.ContainsKey(location)) return false;
        location = Original(location);
        var declaration = declarations[location];
        if (declaration.Kind is not (DeclarationKind.Local or DeclarationKind.Parameter
                or DeclarationKind.Loop or DeclarationKind.Pattern) || WrittenLocations.Contains(location)) return false;
        if (declaration.Template is not null) return true;
        foreach (BoundModule module in StandardModules.Values)
            if (module.Source == declaration.Source.Source && module.Exports.Values.Contains(location)) return false;
        return !IsSession || !Exports.Values.Contains(location);
    }
    public void Declare(BoundDeclaration declaration)
    {
        Declarations.Add(declaration);
        declarations.Add(declaration.Location, declaration);
        if (declaration.Template is not { } template) return;
        if (!templateDeclarations.TryGetValue(template, out var owned))
            templateDeclarations.Add(template, owned = []);
        owned.Add(declaration);
    }
    public TypeRef RuntimeType(HirNodeId id) => Hir.Types[Node(id).Type.Value].RuntimeType;
    public ReadOnlySpan<HirNodeId> Children(HirNodeId id)
    {
        var range = Node(id).Children;
        return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(Hir.Edges)
            .Slice(range.Start, range.Count);
    }
}
