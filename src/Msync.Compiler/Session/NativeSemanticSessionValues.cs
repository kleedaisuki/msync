using System.Collections.Immutable;
using Msync.Core;
using Msync.Core.Native;
using Msync.Compiler.Semantics;
using Msync.Hir;

namespace Msync.Compiler.Session;

// Acceptance borrows native roots; semantic facts never own runtime resources.
internal sealed class NativeSemanticSessionValues(BoundProgram program, HirLoweringLayout layout,
    SemanticContext facts, SemanticSessionSites sites, SemanticSessionDispatch? dispatch = null)
{
    private readonly Dictionary<NativeValue, SemanticValue> observed = [];
    private readonly Dictionary<NativeAddress, HashSet<HirLocationId>> locations = [];
    private int nextRegion = facts.NextRegion;

    public void Refresh(IEnumerable<HirLocationId> exports, Func<int, NativeValue?> resolve)
    {
        foreach (HirLocationId location in exports)
        {
            if (!layout.Locations.TryGetValue(new(program.Entry, location), out int slot)
                || resolve(slot) is not { } actual) continue;
            SemanticValue hint = facts.Values.GetValueOrDefault(location)
                ?? new(program.Hir.Locations[location.Value].StorageType, HirNodeId.None);
            SemanticValue value = Read(hint, actual);
            dispatch?.ObserveRoot(location, hint, value);
            facts.Values[location] = value;
        }
        foreach (var (address, sources) in locations)
        {
            if (!address.IsInitialized || !observed.TryGetValue(address.Load(), out SemanticValue? value)) continue;
            foreach (HirLocationId source in sources)
            {
                if (dispatch is not null && facts.Values.TryGetValue(source, out SemanticValue? previous))
                    dispatch.ObserveRoot(source, previous, value);
                facts.Values[source] = value;
            }
        }
        facts.NextRegion = Math.Max(facts.NextRegion, nextRegion);
    }

    private SemanticValue Read(SemanticValue hint, NativeValue actual)
    {
        if (observed.TryGetValue(actual, out SemanticValue? known)) return known;
        SemanticValue value = actual.Kind switch
        {
            NativeKind.Address => Scope(hint, actual.AsAddress()),
            NativeKind.Function => Function(hint, actual.AsFunction()),
            NativeKind.Bool => Scalar(TypeRef.Bool, new(HirLiteralKind.Bool, Boolean: actual.AsBool())),
            NativeKind.Number => Scalar(TypeRef.Number, new(HirLiteralKind.Number, Number: actual.AsNumber())),
            NativeKind.String => Scalar(TypeRef.String, new(HirLiteralKind.String, Text: actual.AsString())),
            NativeKind.Null => Scalar(TypeRef.Null, new(HirLiteralKind.Null)),
            NativeKind.Connection => Scalar(TypeRef.Connection, new(HirLiteralKind.Connection,
                Text: actual.AsConnection().Address, ConnectionKind: actual.AsConnection().Kind)),
            NativeKind.Path => Path(actual.AsPath()),
            NativeKind.Type => new(program.Hir.Type(TypeRef.Type), HirNodeId.None,
                DenotedType: program.Hir.Type(actual.AsType())),
            NativeKind.Range => hint with
            {
                Type = program.Hir.Type(actual.Type),
                Node = HirNodeId.None,
                IndexedRegion = null,
                IndexedAliases = [],
                Element = hint.Element is { } element ? element with { Type = program.Hir.Type(actual.AsRange().ItemType) }
                    : new(program.Hir.Type(actual.AsRange().ItemType), HirNodeId.None)
            },
            _ => new(program.Hir.Type(actual.Type), HirNodeId.None)
        };
        observed[actual] = value;
        return value;
    }

    private SemanticValue Scalar(TypeRef type, HirLiteral literal) => new(program.Hir.Type(type), HirNodeId.None, literal);

    private SemanticValue Path(NativePath path) => Scalar(TypeRef.Path, new(HirLiteralKind.Path, Text: path.Raw)) with
    {
        Observation = new(Path: new(path.Raw, path.WorkingDirectory, path.HomeDirectory, path.LocalSpelling,
            path.Connection is { } endpoint ? new(endpoint.Kind, endpoint.Address) : null))
    };

    private SemanticValue Scope(SemanticValue hint, NativeAddress address)
    {
        NativeValue actual = NativeValue.FromAddress(address);
        SemanticScope? previous = hint.Scope;
        if (previous is { Region: >= 0 }) previous = facts.Objects.GetValueOrDefault(previous.Region, previous);
        var members = previous?.Locations ?? ImmutableDictionary<string, HirLocationId>.Empty;
        if (address.Allocation.AllocationSite != 0 && sites.Find(address.Allocation.AllocationSite) is { } construction)
        {
            var declaredFields = program.Hir.ScopeLayouts[construction.Layout].Fields;
            var actualMembers = Enumerable.Range(0, construction.Fields.Length).ToImmutableDictionary(
                index => declaredFields[index].Name, index => construction.Fields[index], StringComparer.Ordinal);
            // A failure can leave an earlier alternative than the analyzer's final
            // assignment. Compare declaration origins, not copied location IDs:
            // constructor invocations own independently inferred field locations.
            bool sameConstruction = members.Count == actualMembers.Count && actualMembers.All(field =>
                members.TryGetValue(field.Key, out var location)
                && program.Hir.Locations[location.Value].Source == program.Hir.Locations[field.Value.Value].Source);
            if (!sameConstruction)
            {
                previous = null;
                members = actualMembers;
            }
        }
        int region = previous is { Region: >= 0 } ? previous.Region : ++nextRegion;
        SemanticScope scope = new(region, previous?.Layout ?? -1,
            ImmutableDictionary<string, SemanticValue>.Empty, members, previous?.Named == true);
        SemanticValue result = new(program.Hir.Type(TypeRef.Scope), HirNodeId.None, Scope: scope);
        observed[actual] = result;
        var fields = ImmutableDictionary.CreateBuilder<string, SemanticValue>(StringComparer.Ordinal);
        for (int i = 0; i < address.Allocation.Layout.Fields.Count; i++)
        {
            var field = address.Allocation.Layout[i];
            NativeAddress place = address.Field(i);
            if (!place.IsInitialized) continue;
            NativeValue member = place.Load();
            SemanticValue memberHint = previous?.Fields.GetValueOrDefault(field.Name)
                ?? new(program.Hir.Type(member.Type), HirNodeId.None);
            fields[field.Name] = Read(memberHint, member);
            if (members.TryGetValue(field.Name, out HirLocationId location)) facts.Values[location] = fields[field.Name];
        }
        scope = scope with { Fields = fields.ToImmutable() };
        int shape = previous?.Layout ?? -1;
        if (shape < 0)
        {
            shape = program.Hir.ScopeLayouts.Count;
            program.Hir.ScopeLayouts.Add(new(scope.Fields.Select(field => new HirField(field.Key,
                members.TryGetValue(field.Key, out HirLocationId location)
                    ? program.Hir.Locations[location.Value].StorageType : field.Value.Type,
                members.TryGetValue(field.Key, out location) && program.Hir.Locations[location.Value].AdmitsNull)).ToImmutableArray()));
        }
        scope = scope with { Layout = shape };
        facts.Objects[region] = scope;
        return result with { Type = program.Hir.Type(new HirType(TypeRef.Scope, Shape: shape)), Scope = scope };
    }

    private SemanticValue Function(SemanticValue hint, NativeFunction actual)
    {
        if (actual.NativeName is { } native)
            return new(program.Hir.Type(TypeRef.Function), HirNodeId.None, Callables: [new(NativeName: native)]);
        HirTemplateId? template = null;
        if (layout.Callables.TryGetValue(actual.Function, out HirCallableId identity))
            template = identity.IsTemplate ? new(identity.Value) : program.Hir.Functions[identity.Value].Template;
        SemanticCallable? prior = hint.Callables.IsDefaultOrEmpty ? null
            : hint.Callables.FirstOrDefault(callable => callable.Template == template);
        if (template is null) return hint with { Node = HirNodeId.None };
        var definition = program.Hir.Templates[template.Value.Value];
        SemanticValue provisional = new(program.Hir.Type(TypeRef.Function), HirNodeId.None,
            Callables: [new(template, RuntimeIdentity: identity)]);
        observed[NativeValue.FromFunction(actual)] = provisional;
        var captures = ImmutableArray.CreateBuilder<SemanticCapture>();
        for (int index = 0; index < definition.Captures.Length; index++)
        {
            var definitionCapture = definition.Captures[index];
            SemanticCapture? priorCapture = prior is not null && !prior.Captures.IsDefaultOrEmpty
                && index < prior.Captures.Length ? prior.Captures[index] : null;
            HirLocationId source = priorCapture?.Source ?? definitionCapture.SourceLocation;
            SemanticValue capturedHint = priorCapture?.Value ?? facts.Values.GetValueOrDefault(source)
                ?? new(program.Hir.Locations[source.Value].StorageType, HirNodeId.None);
            NativeValue? actualCapture = null;
            if (!PendingModule(source) && actual.Environment is { } environment && index < environment.Count
                && environment.TryGetCapture(index, out NativeCapture capture))
            {
                if (capture.IsLocation)
                {
                    if (!locations.TryGetValue(capture.Location, out var sources)) locations.Add(capture.Location, sources = []);
                    sources.Add(source);
                }
                if (capture.TryRead(out NativeValue captured)) actualCapture = captured;
            }
            SemanticValue value = actualCapture is { } runtime ? Read(capturedHint, runtime) : capturedHint;
            captures.Add(new(source, value, definitionCapture.Mode));
            if (definitionCapture.Mode == HirCaptureMode.Location) facts.Values[source] = value;
        }
        return provisional with { Callables = [new(template, Captures: captures.ToImmutable(), RuntimeIdentity: identity)] };
    }

    private bool PendingModule(HirLocationId location)
    {
        location = facts.Definitions.GetValueOrDefault(location, location);
        while (program.TryDeclaration(location, out var declaration))
        {
            if (declaration.Kind is DeclarationKind.StandardModule or DeclarationKind.StandardRoot)
                return !facts.ActivatedModules.Contains(declaration.Location);
            if (declaration.CapturedFrom is not { } captured) return false;
            location = captured;
        }
        return false;
    }
}
