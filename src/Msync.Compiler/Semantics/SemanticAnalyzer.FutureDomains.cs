using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue EnrichFutureDomains(SemanticValue value, SemanticContext context,
        HirLocationId? location = null, bool rootStorage = false)
    {
        if (value.Scope is null && value.Element is null
            && (value.Callables.IsDefaultOrEmpty || value.Callables.All(callable => callable.Captures.IsDefaultOrEmpty))
            && (!rootStorage || Runtime(value).WithoutNull != TypeRef.Function))
            return value.FutureCallableDomain is null ? value : value with { FutureCallableDomain = null };
        Dictionary<SemanticValue, Dictionary<HirLocationId, SemanticValue>> values = new(ReferenceEqualityComparer.Instance);
        HashSet<int> scopes = [], resources = [];
        return Visit(value, location, rootStorage);

        SemanticValue Visit(SemanticValue current, HirLocationId? cell, bool borrowed, SemanticScope? receiver = null)
        {
            if (current.Scope is null && current.Element is null && current.Callables.IsDefaultOrEmpty
                && current.FutureCallableDomain is null) return current;
            HirLocationId owner = borrowed && cell is { } source ? StorageOwner(source, context) : new(-1);
            if (!values.TryGetValue(current, out var versions)) values.Add(current, versions = []);
            if (versions.TryGetValue(owner, out SemanticValue? known)) return known;
            SemanticValue result = borrowed || current.FutureCallableDomain is null ? current
                : current with { FutureCallableDomain = null };
            versions[owner] = result;
            if (borrowed && cell is { } binding && Runtime(current).WithoutNull == TypeRef.Function)
            {
                SemanticCallableDomain storageDomain = current.FutureCallableDomain is { } existing
                    && StorageOwner(existing.Owner, context) == owner ? existing
                    : new(owner, FutureCallables(current, binding, context, receiver).Callables, current.Callables);
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
            ImmutableArray<SemanticCallable> callables = Callables(current.Callables);
            if (callables != current.Callables) result = result with { Callables = callables };
            if (result.FutureCallableDomain is { } domain)
            {
                ImmutableArray<SemanticCallable> choices = Callables(domain.Choices);
                ImmutableArray<SemanticCallable> admitted = Callables(domain.Current);
                if (choices != domain.Choices || admitted != domain.Current)
                    result = result with { FutureCallableDomain = domain with { Choices = choices, Current = admitted } };
            }
            versions[owner] = result;
            return result;
        }

        ImmutableArray<SemanticCallable> Callables(ImmutableArray<SemanticCallable> source)
        {
            if (source.IsDefaultOrEmpty) return source;
            ImmutableArray<SemanticCallable>.Builder? changed = null;
            for (int index = 0; index < source.Length; index++)
            {
                SemanticCallable original = source[index];
                SemanticCallable updated = Callable(original);
                if (ReferenceEquals(original, updated)) continue;
                changed ??= source.ToBuilder();
                changed[index] = updated;
            }
            return changed is null ? source : changed.ToImmutable();
        }

        SemanticCallable Callable(SemanticCallable callable)
        {
            if (callable.Captures.IsDefaultOrEmpty) return callable;
            ImmutableArray<SemanticCapture>.Builder? changed = null;
            for (int index = 0; index < callable.Captures.Length; index++)
            {
                SemanticCapture capture = callable.Captures[index];
                SemanticValue updated = Visit(context.Values.GetValueOrDefault(capture.Source, capture.Value), capture.Source,
                    capture.Mode == HirCaptureMode.Location);
                if (ReferenceEquals(updated, capture.Value)) continue;
                changed ??= callable.Captures.ToBuilder();
                changed[index] = capture with { Value = updated };
            }
            return changed is null ? callable : callable with { Captures = changed.ToImmutable() };
        }
    }
}
