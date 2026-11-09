using System.Text;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private string InstanceKey(HirTemplateId id, IReadOnlyList<SemanticValue> arguments,
        IReadOnlyList<SemanticValue> captures, SemanticContext caller)
    {
        StringBuilder key = new();
        key.Append(id.Value).Append(caller.DeferredReads ? 'd' : 'i').Append(':');
        if (arguments.All(IsScalarSchema) && captures.All(IsScalarSchema))
        {
            foreach (var value in arguments) AppendScalarKey(value);
            key.Append('|');
            foreach (var value in captures) AppendScalarKey(value);
            return key.ToString();
        }
        SemanticInputSchema schema = CodeSchema(arguments, captures, caller);
        HashSet<SemanticValue> active = new(ReferenceEqualityComparer.Instance);
        KeyProvenance provenance = new();
        foreach (var value in schema.Arguments) { AppendValueKey(key, value, schema.Context, active, provenance); key.Append(';'); }
        key.Append('|');
        foreach (var value in schema.Captures) { AppendValueKey(key, value, schema.Context, active, provenance); key.Append(';'); }
        return key.ToString();

        void AppendScalarKey(SemanticValue value) => key.Append(value.Type.Value).Append('T')
            .Append(value.DenotedType?.Value ?? -1).Append(';');
    }

    private static bool IsScalarSchema(SemanticValue value) => value.Scope is null
        && value.Element is null && value.Callables.IsDefaultOrEmpty && value.IndexedRegion is null
        && value.Alternatives.IsDefaultOrEmpty && value.DeferredEffects.IsDefaultOrEmpty && value.IndexedAliases.IsDefaultOrEmpty
        && value.FutureCallableDomain is null;

    private string ValueKey(SemanticValue value)
    {
        StringBuilder key = new();
        AppendValueKey(key, value, null, new(ReferenceEqualityComparer.Instance), new());
        return key.ToString();
    }

    private void AppendValueKey(StringBuilder key, SemanticValue value, SemanticContext? context,
        HashSet<SemanticValue> active, KeyProvenance provenance)
    {
        key.Append(value.Type.Value).Append(value.EagerRange ? '!' : '.');
        if (value.DenotedType is { } denoted) key.Append('T').Append(denoted.Value);
        if (!active.Add(value)) { key.Append('^'); return; }
        AppendCallableKey(key, Choices(value), context, active, provenance);
        if (value.FutureCallableDomain is { } future)
        {
            key.Append("U(");
            AppendCallableKey(key, future.Choices, context, active, provenance);
            key.Append(')');
        }
        if (value.Scope is { } scope)
            AppendScopeKey(key, context?.Objects.GetValueOrDefault(scope.Region, scope) ?? scope, context, active, provenance);
        if (value.Element is { } element) { key.Append('['); AppendValueKey(key, element, context, active, provenance); key.Append(']'); }
        if (value.IndexedRegion is { } region && context?.Indexed.TryGetValue(region, out var indexed) == true)
        {
            key.Append('I').Append(provenance.Region(region)).Append(':').Append(indexed.FixedCarrier?.Value ?? -1);
            if (!indexed.CarrierConstraints.IsDefaultOrEmpty)
                foreach (var constraint in indexed.CarrierConstraints) key.Append('C').Append(constraint.Value);
            if (provenance.DefineIndexed(region)) AppendValueKey(key, indexed.Element, context, active, provenance);
            else key.Append('^');
        }
        if (!value.IndexedAliases.IsDefaultOrEmpty)
            foreach (int alias in value.IndexedAliases)
            {
                key.Append('J').Append(provenance.Region(alias));
                if (context?.Indexed.TryGetValue(alias, out var storage) == true)
                {
                    key.Append('C').Append(storage.FixedCarrier?.Value ?? -1);
                    if (!storage.CarrierConstraints.IsDefaultOrEmpty)
                        foreach (var constraint in storage.CarrierConstraints) key.Append('C').Append(constraint.Value);
                    if (provenance.DefineIndexed(alias)) AppendValueKey(key, storage.Element, context, active, provenance);
                    else key.Append('^');
                }
            }
        if (!value.DeferredEffects.IsDefaultOrEmpty)
            foreach (var write in value.DeferredEffects)
            {
                key.Append('W').Append((int)write.Kind).Append(write.Field);
                key.Append(write.Kind == SemanticWriteKind.Location
                    ? provenance.Location(write.Location) : provenance.Region(write.Region));
                AppendValueKey(key, write.After, context, active, provenance);
            }
        active.Remove(value);
    }

    private void AppendCallableKey(StringBuilder key, IEnumerable<SemanticCallable> choices,
        SemanticContext? context, HashSet<SemanticValue> active, KeyProvenance provenance)
    {
        foreach (var callable in choices)
        {
            key.Append('F').Append(callable.NativeName ?? callable.Template?.Value.ToString()).Append('(');
            if (!callable.Captures.IsDefault)
                foreach (var capture in callable.Captures)
                {
                    key.Append('C').Append(provenance.Location(capture.Source));
                    if (context is not null && context.Values.TryGetValue(capture.Source, out var captured))
                    {
                        if (provenance.DefineLocation(capture.Source)) AppendValueKey(key, captured, context, active, provenance);
                        else key.Append('^');
                    }
                    else AppendValueKey(key, capture.Value, context, active, provenance);
                }
            key.Append(')');
        }
    }

    private void AppendScopeKey(StringBuilder key, SemanticScope scope, SemanticContext? context,
        HashSet<SemanticValue> active, KeyProvenance provenance)
    {
        key.Append('{').Append(scope.Region >= 0 ? 'S' : 'V')
            .Append(scope.Region >= 0 ? provenance.Region(scope.Region) : provenance.Structural(scope));
        if (!provenance.DefineScope(scope))
        {
            key.Append("^}");
            return;
        }
        if (!scope.Aliases.IsDefaultOrEmpty)
            foreach (var alias in scope.Aliases) key.Append('A').Append(provenance.Region(alias));
        foreach (var field in scope.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            key.Append(field.Key).Append(':');
            var member = scope.Locations.TryGetValue(field.Key, out var location)
                ? context?.Values.GetValueOrDefault(location, field.Value) ?? field.Value : field.Value;
            AppendValueKey(key, member, context, active, provenance);
            if (context?.MemberContracts.TryGetValue((scope.Region, field.Key), out var contracts) == true)
                foreach (var contract in contracts)
                {
                    key.Append('K').Append(contract.Concept).Append(':').Append(contract.Requirement.Type.Value)
                        .Append(':').Append(contract.Requirement.Signature);
                    foreach (var associated in contract.Associated.OrderBy(pair => pair.Key.Value))
                        key.Append('A').Append(associated.Key.Value).Append(':').Append(associated.Value.Value);
                }
        }
        key.Append('}');
    }

    // Canonical alias partitions contain no allocation identities: two unrelated
    // calls with the same alias topology share code. Aliased and disjoint inputs
    // cannot share flow proofs after writes to nullable fields or captured cells.
    private sealed class KeyProvenance
    {
        private Dictionary<int, int>? regions;
        private Dictionary<HirLocationId, int>? locations;
        private HashSet<int>? definedScopes;
        private HashSet<int>? definedIndexed;
        private HashSet<HirLocationId>? definedLocations;
        private Dictionary<SemanticScope, int>? structural;
        private HashSet<SemanticScope>? definedStructural;
        public bool DefineScope(SemanticScope scope) => scope.Region >= 0
            ? (definedScopes ??= []).Add(scope.Region)
            : (definedStructural ??= new(ReferenceEqualityComparer.Instance)).Add(scope);
        public bool DefineIndexed(int region) => (definedIndexed ??= []).Add(region);
        public bool DefineLocation(HirLocationId location) => (definedLocations ??= []).Add(location);
        public int Structural(SemanticScope scope)
        {
            structural ??= new(ReferenceEqualityComparer.Instance);
            if (!structural.TryGetValue(scope, out var ordinal)) structural[scope] = ordinal = structural.Count;
            return ordinal;
        }
        public int Region(int region)
        {
            if (region < 0) return -1;
            regions ??= [];
            if (!regions.TryGetValue(region, out var ordinal)) regions[region] = ordinal = regions.Count;
            return ordinal;
        }
        public int Location(HirLocationId location)
        {
            locations ??= [];
            if (!locations.TryGetValue(location, out var ordinal)) locations[location] = ordinal = locations.Count;
            return ordinal;
        }
    }
}
