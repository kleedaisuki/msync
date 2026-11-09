using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

// A projection borrows runtime values only for the duration of acceptance. Its
// result contains semantic facts, never live Processes, ranges or credentials.
internal sealed class SemanticSessionValues(BoundProgram program, HirLoweringLayout layout,
    SemanticContext facts)
{
    private readonly Dictionary<Value, SemanticValue> observed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CellValue, HashSet<HirLocationId>> cells = new(ReferenceEqualityComparer.Instance);
    private int nextRegion = Math.Max(facts.Objects.Keys.DefaultIfEmpty(0).Max(),
        facts.Indexed.Keys.DefaultIfEmpty(0).Max()) + 1;

    public void Refresh(IEnumerable<HirLocationId> exports, Func<int, Value?> resolve)
    {
        foreach (var location in exports)
        {
            if (!layout.Locations.TryGetValue(new(program.Entry, location), out int slot)
                || resolve(slot) is not { } actual) continue;
            SemanticValue hint = facts.Values.GetValueOrDefault(location)
                ?? new(program.Hir.Locations[location.Value].StorageType, HirNodeId.None);
            facts.Values[location] = Read(hint, actual);
        }
        // A recursive capture can be visited before its value has been projected.
        // Resolve shared cells after every reachable value has its final descriptor.
        foreach (var (cell, locations) in cells)
            if (observed.TryGetValue(cell.Data, out var value))
                foreach (var location in locations) facts.Values[location] = value;
        facts.NextRegion = Math.Max(facts.NextRegion, nextRegion);
    }

    private SemanticValue Read(SemanticValue hint, Value actual)
    {
        if (actual is CellValue cell) actual = cell.Data;
        if (observed.TryGetValue(actual, out var known)) return known;
        SemanticValue value = actual switch
        {
            ScopeValue scope => Scope(hint, scope),
            FunctionValue function => Function(hint, function),
            BoolValue boolean => Scalar(TypeRef.Bool, new(HirLiteralKind.Bool, Boolean: boolean.Data)),
            NumberValue number => Scalar(TypeRef.Number, new(HirLiteralKind.Number, Number: number.Data)),
            StringValue text => Scalar(TypeRef.String, new(HirLiteralKind.String, Text: text.Data)),
            NullValue => Scalar(TypeRef.Null, new(HirLiteralKind.Null)),
            ConnectionValue connection => Scalar(TypeRef.Connection,
                new(HirLiteralKind.Connection, Text: connection.Address, ConnectionKind: connection.Kind)),
            PathValue path => Scalar(TypeRef.Path, new(HirLiteralKind.Path, Text: path.Raw)) with
            {
                Observation = new(Path: new(path.Raw, path.WorkingDirectory, path.HomeDirectory,
                    path.LocalSpelling, path.Connection is { } endpoint
                        ? new(endpoint.Kind, endpoint.Address) : null))
            },
            TypeValue type => new(program.Hir.Type(TypeRef.Type), HirNodeId.None,
                DenotedType: program.Hir.Type(type.Data)),
            RangeValue range => hint with { Type = program.Hir.Type(range.Type), Node = HirNodeId.None,
                Element = hint.Element is { } element ? element with { Type = program.Hir.Type(range.ItemType) }
                    : new(program.Hir.Type(range.ItemType), HirNodeId.None) },
            _ => new(program.Hir.Type(actual.Type), HirNodeId.None)
        };
        observed[actual] = value;
        return value;
    }

    private SemanticValue Scalar(TypeRef type, HirLiteral literal) =>
        new(program.Hir.Type(type), HirNodeId.None, literal);

    private SemanticValue Scope(SemanticValue hint, ScopeValue actual)
    {
        SemanticScope? previous = hint.Scope;
        if (previous is not null) previous = facts.Objects.GetValueOrDefault(previous.Region, previous);
        int region = previous?.Region ?? nextRegion++;
        var locations = previous?.Locations ?? ImmutableDictionary<string, HirLocationId>.Empty;
        if (locations.Count == 0 && actual.AllocationSite != 0)
            foreach (var (construction, site) in layout.AllocationSites)
                if (site == actual.AllocationSite)
                {
                    locations = program.Hir.Scopes[construction].Fields.ToImmutableDictionary(
                        field => program.Hir.Locations[field.Value].Name, field => field, StringComparer.Ordinal);
                    break;
                }
        SemanticScope scope = new(region, previous?.Layout ?? -1,
            ImmutableDictionary<string, SemanticValue>.Empty, locations, previous?.Named == true);
        SemanticValue result = new(program.Hir.Type(TypeRef.Scope), HirNodeId.None, Scope: scope);
        observed[actual] = result;
        var fields = ImmutableDictionary.CreateBuilder<string, SemanticValue>(StringComparer.Ordinal);
        foreach (var (name, member) in actual)
        {
            var memberHint = previous?.Fields.GetValueOrDefault(name)
                ?? new(program.Hir.Type(member.Type), HirNodeId.None);
            fields[name] = Read(memberHint, member);
            if (locations.TryGetValue(name, out var location)) facts.Values[location] = fields[name];
        }
        scope = scope with { Fields = fields.ToImmutable() };
        int shape = previous?.Layout ?? -1;
        if (shape < 0)
        {
            shape = program.Hir.ScopeLayouts.Count;
            program.Hir.ScopeLayouts.Add(new(scope.Fields.Select(field => new HirField(field.Key,
                locations.TryGetValue(field.Key, out var location)
                    ? program.Hir.Locations[location.Value].StorageType : field.Value.Type,
                locations.TryGetValue(field.Key, out location) && program.Hir.Locations[location.Value].AdmitsNull)).ToImmutableArray()));
        }
        scope = scope with { Layout = shape };
        facts.Objects[region] = scope;
        return result with { Type = program.Hir.Type(new HirType(TypeRef.Scope, Shape: shape)), Scope = scope };
    }

    private SemanticValue Function(SemanticValue hint, FunctionValue actual)
    {
        if (actual.NativeName is { } native)
            return new(program.Hir.Type(TypeRef.Function), HirNodeId.None, Callables: [new(NativeName: native)]);
        HirTemplateId? template = null;
        if (layout.Callables.TryGetValue(actual.Function, out var identity))
            template = identity.IsTemplate ? new(identity.Value) : program.Hir.Functions[identity.Value].Template;
        SemanticCallable? prior = hint.Callables.IsDefaultOrEmpty ? null
            : hint.Callables.FirstOrDefault(callable => callable.Template == template);
        if (template is null) return hint with { Node = HirNodeId.None };
        var definition = program.Hir.Templates[template.Value.Value];
        SemanticValue provisional = new(program.Hir.Type(TypeRef.Function), HirNodeId.None,
            Callables: [new(template)]);
        observed[actual] = provisional;
        var captures = ImmutableArray.CreateBuilder<SemanticCapture>();
        for (int index = 0; index < definition.Captures.Length; index++)
        {
            var definitionCapture = definition.Captures[index];
            SemanticCapture? priorCapture = prior is not null && !prior.Captures.IsDefaultOrEmpty
                && index < prior.Captures.Length ? prior.Captures[index] : null;
            HirLocationId source = priorCapture?.Source ?? definitionCapture.SourceLocation;
            SemanticValue capturedHint = priorCapture?.Value ?? facts.Values.GetValueOrDefault(source)
                ?? new(program.Hir.Locations[source.Value].StorageType, HirNodeId.None);
            Value? actualCapture = actual.Captures is { } values && index < values.Count ? values[index] : null;
            if (definitionCapture.Mode == HirCaptureMode.Location && actualCapture is CellValue cell)
            {
                if (!cells.TryGetValue(cell, out var locations)) cells.Add(cell, locations = []);
                locations.Add(source);
            }
            SemanticValue captured = actualCapture is not null ? Read(capturedHint, actualCapture) : capturedHint;
            captures.Add(new(source, captured, definitionCapture.Mode));
            if (definitionCapture.Mode == HirCaptureMode.Location) facts.Values[source] = captured;
        }
        return provisional with { Callables = [new(template, Captures: captures.ToImmutable())] };
    }
}
