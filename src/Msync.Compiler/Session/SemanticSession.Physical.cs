using System.Collections.Immutable;
using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Hir;

namespace Msync.Compiler.Session;

internal sealed partial class SemanticSession
{
    private sealed class ScopeRecovery(BoundProgram program, SemanticContext facts, SemanticSessionSites sites,
        Func<SemanticValue, HpirSessionValue, SemanticValue> callable)
    {
        private readonly Dictionary<nint, SemanticValue> observed = [];
        private readonly HashSet<int> claimedRegions = [];

        internal int Count => observed.Count;
        internal int Fields { get; private set; }

        internal SemanticValue Read(SemanticValue hint, HpirSessionScope actual)
        {
            if (observed.TryGetValue(actual.Identity, out var known)) return known;
            if (actual.Cycle) return hint;
            SemanticScope? prior = hint.Scope is { } shape ? facts.Objects.GetValueOrDefault(shape.Region, shape) : null;
            if (prior is { Region: >= 0 } regionHint && claimedRegions.Contains(regionHint.Region)) prior = null;
            var construction = sites.Find(actual.Site);
            if (construction is null && prior is null) return hint;
            var members = prior?.Locations ?? ImmutableDictionary<string, HirLocationId>.Empty;
            int layout = prior?.Layout ?? -1;
            if (construction is not null)
            {
                var declared = program.Hir.ScopeLayouts[construction.Layout].Fields;
                var locations = Enumerable.Range(0, construction.Fields.Length).ToImmutableDictionary(
                    index => declared[index].Name, index => construction.Fields[index], StringComparer.Ordinal);
                if (!SameOrigins(members, locations))
                {
                    prior = null;
                    members = locations;
                    layout = construction.Layout;
                }
            }
            int region = prior is { Region: >= 0 } ? prior.Region : ++facts.NextRegion;
            claimedRegions.Add(region);
            var scope = new SemanticScope(region, layout, prior?.Fields ?? ImmutableDictionary<string, SemanticValue>.Empty,
                members, prior?.Named == true, FieldDefinitions: prior?.FieldDefinitions);
            var value = new SemanticValue(program.Hir.Type(new HirType(TypeRef.Scope, Shape: layout)), HirNodeId.None, Scope: scope);
            observed.Add(actual.Identity, value);
            var fields = scope.Fields.ToBuilder();
            Fields += actual.Members.Count;
            foreach (var member in actual.Members)
            {
                var previous = fields.GetValueOrDefault(member.Name) ?? new(program.Hir.Type(new TypeRef(member.Value.Kind)), HirNodeId.None);
                SemanticValue field = member.Value switch
                {
                    { Scope: { } nested } => Read(previous, nested),
                    { Kind: TypeKind.Function } => callable(previous, member.Value),
                    _ => ReadScalar(program, member.Value)
                };
                fields[member.Name] = field;
                if (members.TryGetValue(member.Name, out var location)) facts.Values[location] = field;
            }
            scope = scope with { Fields = fields.ToImmutable() };
            facts.Objects[region] = scope;
            observed[actual.Identity] = value = value with { Scope = scope };
            return value;
        }

        private bool SameOrigins(ImmutableDictionary<string, HirLocationId> prior,
            ImmutableDictionary<string, HirLocationId> actual) => prior.Count == actual.Count
            && actual.All(field => prior.TryGetValue(field.Key, out var location)
                && program.Hir.Locations[location.Value].Source == program.Hir.Locations[field.Value.Value].Source);
    }

    private static SemanticValue ReadScalar(BoundProgram program, HpirSessionValue value)
    {
        if (value.RequiresDemand) return new(program.Hir.Type(TypeRef.String), HirNodeId.None);
        if (value.Kind == TypeKind.Type)
            return new(program.Hir.Type(TypeRef.Type), HirNodeId.None, DenotedType: program.Hir.Type(value.Type!));
        if (value.Path is { } path)
            return new(program.Hir.Type(TypeRef.Path), HirNodeId.None, new(HirLiteralKind.Path, Text: path.Raw),
                Observation: new(Path: new(path.Raw, path.WorkingDirectory, path.HomeDirectory, path.LocalSpelling,
                    path.Connection is { } endpoint ? new(endpoint.Kind, endpoint.Address) : null)));
        var literal = value.Kind switch
        {
            TypeKind.Null => new HirLiteral(HirLiteralKind.Null),
            TypeKind.Bool => new HirLiteral(HirLiteralKind.Bool, Boolean: value.Boolean),
            TypeKind.Number => new HirLiteral(HirLiteralKind.Number, Number: value.Number),
            TypeKind.String => new HirLiteral(HirLiteralKind.String, Text: value.Text),
            TypeKind.Connection when value.Connection is { } endpoint => new HirLiteral(HirLiteralKind.Connection,
                Text: endpoint.Address, ConnectionKind: endpoint.Kind),
            _ => throw new InvalidOperationException("Expected a scalar observation.")
        };
        return new(program.Hir.Type(new TypeRef(value.Kind)), HirNodeId.None, literal);
    }
}
