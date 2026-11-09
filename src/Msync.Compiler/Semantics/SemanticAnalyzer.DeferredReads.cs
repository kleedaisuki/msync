using System.Collections.Immutable;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    // A deferred read preserves location/shape provenance, not an observation of
    // a writable cell made while the lazy operation was being constructed.
    private SemanticValue FutureRead(SemanticValue value, HirTypeId storage, SemanticContext context)
    {
        if (value.Scope is not null || value.Element is not null) return FutureRead(value, storage, context, []);
        return value.Type == storage && value.Literal is null && value.Observation is null && !value.EagerRange
            ? value : value with { Type = storage, Literal = null, Observation = null, EagerRange = false };
    }

    private SemanticValue FutureRead(SemanticValue value, HirTypeId storage,
        SemanticContext context, HashSet<int> active)
    {
        SemanticValue result = value with { Type = storage, Literal = null, Observation = null, EagerRange = false };
        if (value.Element is { } element)
        {
            HirType descriptor = hir.Types[storage.Value];
            HirTypeId carrier = descriptor.Element >= 0 ? new(descriptor.Element) : element.Type;
            if (value.IndexedRegion is { } indexed && context.Indexed.TryGetValue(indexed, out SemanticIndexed? resource))
            {
                if (!active.Add(indexed)) return result;
                carrier = resource.FixedCarrier ?? resource.Element.Type;
                element = FutureRead(resource.Element, carrier, context, active);
                context.Indexed[indexed] = resource with { Element = element };
                active.Remove(indexed);
            }
            else element = FutureRead(element, carrier, context, active);
            result = result with { Element = element };
        }
        if (value.Scope is not { } initial) return result;
        SemanticScope scope = context.Objects.GetValueOrDefault(initial.Region, initial);
        if (scope.Region >= 0 && !active.Add(scope.Region)) return result;
        var fields = ImmutableDictionary.CreateBuilder<string, SemanticValue>(StringComparer.Ordinal);
        foreach (HirField field in hir.ScopeLayouts[scope.Layout].Fields)
        {
            if (!scope.Fields.TryGetValue(field.Name, out SemanticValue? current)) continue;
            HirTypeId domain = field.AdmitsNull ? Nullable(field.StorageType) : field.StorageType;
            if (scope.Locations.TryGetValue(field.Name, out HirLocationId location))
            {
                current = context.Values.GetValueOrDefault(location, current);
                domain = context.Storage.GetValueOrDefault(location, domain);
            }
            SemanticValue future = FutureRead(current, domain, context, active);
            fields[field.Name] = future;
            if (scope.Locations.TryGetValue(field.Name, out location)) context.Values[location] = future;
        }
        scope = scope with { Fields = fields.ToImmutable() };
        if (scope.Region >= 0)
        {
            active.Remove(scope.Region);
            context.Objects[scope.Region] = scope;
        }
        return result with { Scope = scope };
    }
}
