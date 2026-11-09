using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Msync.Core;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal readonly record struct SemanticValueId(int Value);

internal sealed record SemanticConnectionView(ConnectionKind Kind, string Address = "");

internal sealed record SemanticPathView(string Raw, string? WorkingDirectory = null,
    SemanticConnectionView? Connection = null, string? HomeDirectory = null,
    string? LocalSpelling = null);

// Values refer to the frozen HIR tables, not mutable inference objects or slots.
internal sealed record SemanticValueView(HirTypeId Type,
    HirFunctionId? Function = null, HirTemplateId? Template = null,
    HirTypeId? TypeValue = null,
    string? NativeName = null, bool IsNamedScope = false, bool IsUnknown = false,
    ImmutableArray<SemanticFieldView> Members = default,
    ImmutableArray<SemanticValueId> Alternatives = default,
    SemanticValueId? Element = null, SemanticConnectionView? Connection = null,
    SemanticPathView? Path = null, string? Text = null,
    string? Signature = null, string? Documentation = null, TypeRef? CurrentType = null);

internal sealed record SemanticFieldView(string Name, SemanticValueId Value,
    ImmutableArray<HirSourceRange> Definitions = default, bool AdmitsNull = false);

internal sealed record SemanticBindingView(string Name, HirLocationId Location,
    SemanticValueId Value, bool IsParameter = false);

internal sealed record SemanticConceptView(string Name, HirSourceRange Definition,
    string Signature, string Description, ImmutableArray<SemanticMember> Members);

internal readonly record struct SemanticObservation(HirSourceRange Span,
    SemanticValueId Value, string? Name = null, bool Concrete = true,
    bool IsParameter = false);

internal sealed record SemanticVisibility(SourceSpan Owner, bool IncludesEnd,
    int Point, IReadOnlyDictionary<string, SemanticBindingView> Bindings,
    ImmutableDictionary<string, SemanticConceptView> Concepts, bool Concrete = true);

internal readonly record struct SemanticReceiver(SourceSpan Span,
    SemanticValueId Value, EditorReceiverKind Kind = EditorReceiverKind.Member,
    bool Concrete = true);

internal readonly record struct SemanticPathOwner(SourceSpan Span,
    SemanticConnectionView? Endpoint, bool Concrete = true);

internal sealed record SemanticImport(SourceSpan Span,
    IReadOnlyDictionary<string, SemanticBindingView> Bindings, bool Concrete = true);

internal readonly record struct SemanticReference(SourceSpan Span,
    ImmutableArray<HirSourceRange> Definitions, bool Concrete = true);

internal sealed record SemanticFunctionSignature(string Name,
    ImmutableArray<HirLocationId> Parameters, HirTypeId Result);

internal sealed record SemanticTemplateSignature(string Name,
    ImmutableArray<HirLocationId> Parameters, HirTypeId? Result);

internal sealed record SemanticTypeTables(ImmutableArray<HirSource> Sources,
    ImmutableArray<HirType> Types, ImmutableArray<HirLocation> Locations,
    ImmutableArray<HirScopeLayout> ScopeLayouts, ImmutableArray<HirSignature> Signatures,
    ImmutableArray<SemanticFunctionSignature> Functions,
    ImmutableArray<SemanticTemplateSignature> Templates)
{
    public static SemanticTypeTables From(HirModule module) => new(module.Sources,
        module.Types, module.Locations, module.ScopeLayouts, module.Signatures,
        module.Functions.Select(function => new SemanticFunctionSignature(function.Name,
            function.Parameters, function.Result)).ToImmutableArray(),
        module.Templates.Select(template => new SemanticTemplateSignature(template.Name,
            template.Parameters, template.ResultAnnotation
                ?? (template.Body.IsNone ? null : module.Node(template.Body).Type))).ToImmutableArray());

    public TypeRef RuntimeType(HirTypeId type) => Types[type.Value].RuntimeType;
}

internal sealed record SemanticObservationView(SemanticTypeTables Module, HirSourceId Source,
    ImmutableArray<SemanticValueView> Values,
    ImmutableArray<SemanticObservation> Observations,
    ImmutableArray<SemanticVisibility> Contexts,
    ImmutableArray<SemanticReceiver> Receivers,
    ImmutableArray<SemanticPathOwner> PathOwners,
    ImmutableArray<SemanticImport> Imports,
    ImmutableArray<SemanticReference> References)
{
    public SemanticObservationView(HirModule module, HirSourceId source,
        ImmutableArray<SemanticValueView> values,
        ImmutableArray<SemanticObservation> observations,
        ImmutableArray<SemanticVisibility> contexts,
        ImmutableArray<SemanticReceiver> receivers,
        ImmutableArray<SemanticPathOwner> pathOwners,
        ImmutableArray<SemanticImport> imports,
        ImmutableArray<SemanticReference> references) : this(SemanticTypeTables.From(module),
            source, values, observations, contexts, receivers, pathOwners, imports, references) { }

    public SemanticValueView Value(SemanticValueId id) => Values[id.Value];
}

// Lexical membership and value facts share independently across source points.
// Only a requested completion/lookup turns their join into named descriptions.
internal sealed class SemanticBindingMap(ImmutableDictionary<string, HirLocationId> names,
    ImmutableDictionary<HirLocationId, SemanticValueId> values,
    ImmutableDictionary<HirLocationId, HirLocationId> locations,
    ImmutableDictionary<HirLocationId, SemanticBindingView> definitions)
    : IReadOnlyDictionary<string, SemanticBindingView>
{
    public int Count => names.Count;
    public IEnumerable<string> Keys => names.Keys;
    public IEnumerable<SemanticBindingView> Values => this.Select(pair => pair.Value);
    public SemanticBindingView this[string name] => TryGetValue(name, out var value)
        ? value : throw new KeyNotFoundException();
    public bool ContainsKey(string name) => names.ContainsKey(name);
    public bool TryGetValue(string name, [MaybeNullWhen(false)] out SemanticBindingView value)
    {
        if (!names.TryGetValue(name, out var original)) { value = null; return false; }
        HirLocationId location = locations.GetValueOrDefault(original, original);
        SemanticBindingView declaration = definitions[original];
        value = declaration with { Name = name, Location = location,
            Value = values.GetValueOrDefault(location, declaration.Value) };
        return true;
    }
    public IEnumerator<KeyValuePair<string, SemanticBindingView>> GetEnumerator()
    {
        foreach (string name in names.Keys) yield return new(name, this[name]);
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
