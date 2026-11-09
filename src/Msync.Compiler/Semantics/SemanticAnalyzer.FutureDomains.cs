using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue EnrichFutureDomains(SemanticValue value, SemanticContext context,
        HirLocationId? location = null, bool rootStorage = false)
    {
        if (value.Scope is null && value.Element is null && value.Alternatives.IsDefaultOrEmpty
            && (value.Callables.IsDefaultOrEmpty || value.Callables.All(callable => callable.Captures.IsDefaultOrEmpty))
            && (!rootStorage || Runtime(value).WithoutNull != TypeRef.Function))
            return value.FutureCallableDomain is null ? value : value with { FutureCallableDomain = null };
        Dictionary<SemanticValue, Dictionary<HirLocationId, SemanticValue>> values = new(ReferenceEqualityComparer.Instance);
        HashSet<int> scopes = [], resources = [];
        return Visit(value, location, rootStorage);

        SemanticValue Visit(SemanticValue current, HirLocationId? cell, bool borrowed, SemanticScope? receiver = null)
        {
            if (current.Scope is null && current.Element is null && current.Callables.IsDefaultOrEmpty
                && current.Alternatives.IsDefaultOrEmpty && current.FutureCallableDomain is null) return current;
            HirLocationId owner = borrowed && cell is { } source ? StorageOwner(source, context) : new(-1);
            if (!values.TryGetValue(current, out var versions)) values.Add(current, versions = []);
            if (versions.TryGetValue(owner, out SemanticValue? known)) return known;
            SemanticValue result = borrowed || current.FutureCallableDomain is null ? current
                : current with { FutureCallableDomain = null };
            versions[owner] = result;
            if (borrowed && cell is { } binding && Runtime(current).WithoutNull == TypeRef.Function)
            {
                SemanticValue selected = FutureCallables(current, binding, context, receiver);
                SemanticCallableDomain storageDomain = current.FutureCallableDomain is { } existing
                    && StorageOwner(existing.Owner, context) == owner ? existing : new(owner, selected.Callables);
                result = ReferenceEquals(storageDomain, current.FutureCallableDomain)
                    ? current : current with { FutureCallableDomain = storageDomain };
            }
            if (current.Scope is { } initial && (initial.Region < 0 || scopes.Add(initial.Region)))
            {
                SemanticScope row = context.Objects.GetValueOrDefault(initial.Region, initial);
                var fields = row.Fields;
                foreach (var pair in row.Fields)
                {
                    HirLocationId? field = row.Locations.TryGetValue(pair.Key, out HirLocationId actual)
                        ? actual : pair.Value.FutureCallableDomain?.Owner;
                    SemanticValue member = field is { } fieldLocation
                        ? context.Values.GetValueOrDefault(fieldLocation, pair.Value) : pair.Value;
                    SemanticValue enriched = Visit(member, field, true, row);
                    if (!ReferenceEquals(enriched, member)) fields = fields.SetItem(pair.Key, enriched);
                    if (field is { } target) context.Values[target] = enriched;
                }
                if (!ReferenceEquals(fields, row.Fields)) row = row with { Fields = fields };
                if (row.Region >= 0) context.Objects[row.Region] = row;
                if (!ReferenceEquals(result.Scope, row)) result = result with { Scope = row };
            }
            if (current.Element is { } element)
            {
                SemanticValue enriched = Visit(element, null, false);
                if (!ReferenceEquals(enriched, element)) result = result with { Element = enriched };
            }
            int count = IndexedResourceCount(current);
            for (int index = 0; index < count; index++)
            {
                int resource = IndexedResourceAt(current, index);
                if (resources.Add(resource) && context.Indexed.TryGetValue(resource, out var storage))
                {
                    SemanticValue enriched = Visit(storage.Element, null, false);
                    if (!ReferenceEquals(enriched, storage.Element)) context.Indexed[resource] = storage with { Element = enriched };
                }
            }
            if (!current.Callables.IsDefaultOrEmpty && current.Callables.Any(callable => !callable.Captures.IsDefaultOrEmpty))
                result = result with { Callables = current.Callables.Select(Callable).ToImmutableArray() };
            if (result.FutureCallableDomain is { } domain && domain.Choices.Any(callable => !callable.Captures.IsDefaultOrEmpty))
                result = result with { FutureCallableDomain = domain with
                { Choices = domain.Choices.Select(Callable).ToImmutableArray() } };
            if (!current.Alternatives.IsDefaultOrEmpty)
                result = result with { Alternatives = current.Alternatives.Select(item => Visit(item, cell, borrowed, receiver)).ToImmutableArray() };
            versions[owner] = result;
            return result;
        }

        SemanticCallable Callable(SemanticCallable callable) => callable.Captures.IsDefaultOrEmpty ? callable
            : callable with { Captures = callable.Captures.Select(capture => capture with
            {
                Value = Visit(context.Values.GetValueOrDefault(capture.Source, capture.Value), capture.Source,
                    capture.Mode == HirCaptureMode.Location)
            }).ToImmutableArray() };
    }
}
