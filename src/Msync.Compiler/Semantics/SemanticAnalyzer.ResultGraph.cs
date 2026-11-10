using System.Collections.Immutable;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private SemanticValue PublishResultGraph(SemanticValue result, SemanticContext produced, SemanticContext caller)
    {
        var value = PublishResultGraph(result, produced, caller, out var admission);
        return value with { DeferredEffects = CombineDeferredEffects(value.DeferredEffects, admission) };
    }

    private SemanticValue PublishResultGraph(SemanticValue result, SemanticContext produced, SemanticContext caller,
        out ImmutableArray<SemanticDeferredWrite> admissionEffects)
    {
        ImmutableArray<SemanticDeferredWrite>.Builder pending = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
        foreach (var location in produced.CellWrites)
        {
            var owner = StorageOwner(location, produced);
            if ((caller.Values.ContainsKey(owner) || StandardCell(owner, caller) is not null)
                && StorageValue(location, produced) is { } value)
                pending.Add(new(SemanticWriteKind.Location, value, Location: owner));
        }
        foreach (var (region, field) in produced.FieldWrites)
            if (caller.Objects.ContainsKey(region) && produced.Objects.TryGetValue(region, out var scope)
                && scope.Fields.TryGetValue(field, out var value))
                pending.Add(new(SemanticWriteKind.Field, value, Region: region, Field: field));
        foreach (var region in produced.IndexedWrites)
            if (caller.Indexed.ContainsKey(region) && produced.Indexed.TryGetValue(region, out var storage))
                pending.Add(new(SemanticWriteKind.Indexed, storage.Element, Region: region, IndexedStorage: storage));
        Dictionary<SemanticValue, SemanticValue> values = new(ReferenceEqualityComparer.Instance);
        HashSet<int> objects = [], resources = [];
        var projectedResult = Visit(result);
        admissionEffects = pending.Select(VisitWrite).ToImmutableArray();
        return projectedResult;

        SemanticDeferredWrite VisitWrite(SemanticDeferredWrite write) => write with
        {
            After = Visit(write.After),
            Location = write.Kind == SemanticWriteKind.Location ? PublishCell(write.Location) : write.Location,
            IndexedStorage = write.IndexedStorage is { } storage ? storage with { Element = Visit(storage.Element) } : null
        };

        SemanticValue Visit(SemanticValue value)
        {
            if (values.TryGetValue(value, out var known)) return known;
            var projected = value;
            values[value] = projected;
            if (value.Scope is { Region: < 0 } structural)
            {
                structural = structural with
                {
                    Fields = structural.Fields.ToImmutableDictionary(pair => pair.Key,
                    pair => Visit(pair.Value), StringComparer.Ordinal)
                };
                projected = projected with { Scope = structural };
                if (!structural.Aliases.IsDefault)
                    foreach (int alias in structural.Aliases)
                        if (produced.Objects.TryGetValue(alias, out var candidate))
                            _ = Visit(new(hir.Type(new HirType(Msync.Core.TypeRef.Scope, Shape: candidate.Layout)),
                                HirNodeId.None, Scope: candidate));
            }
            else if (value.Scope is { } initial && objects.Add(initial.Region))
            {
                var scope = produced.Objects.GetValueOrDefault(initial.Region, initial);
                if (!caller.Objects.ContainsKey(scope.Region))
                {
                    caller.Objects[scope.Region] = scope;
                    foreach (var field in scope.Locations.Values) PublishCell(field);
                    scope = scope with
                    {
                        Fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                        pair => Visit(pair.Value), StringComparer.Ordinal)
                    };
                    caller.Objects[scope.Region] = scope;
                    foreach (var field in scope.Fields.Keys)
                        if (produced.MemberContracts.TryGetValue((scope.Region, field), out var contracts))
                            caller.MemberContracts[(scope.Region, field)] = contracts;
                    if (!scope.Aliases.IsDefault)
                        foreach (int alias in scope.Aliases)
                            caller.AliasViews[alias] = caller.AliasViews.GetValueOrDefault(alias,
                                ImmutableHashSet<int>.Empty).Add(scope.Region);
                }
                else scope = caller.Objects[scope.Region];
                projected = projected with { Scope = scope };
            }
            if (value.Element is { } element) projected = projected with { Element = Visit(element) };
            int count = IndexedResourceCount(value);
            for (int index = 0; index < count; index++) PublishResource(IndexedResourceAt(value, index));
            if (!value.Callables.IsDefaultOrEmpty)
                projected = projected with
                {
                    Callables = value.Callables.Select(callable => callable with
                    {
                        Captures = callable.Captures.IsDefault ? [] : callable.Captures.Select(capture => capture with
                        { Source = PublishCell(capture.Source), Value = Visit(capture.Value) }).ToImmutableArray()
                    }).ToImmutableArray()
                };
            if (value.FutureCallableDomain is { } future)
                projected = projected with
                {
                    FutureCallableDomain = new(PublishCell(future.Owner),
                    future.Choices.Select(callable => callable with
                    {
                        Captures = callable.Captures.IsDefault ? [] : callable.Captures.Select(capture => capture with
                        { Source = PublishCell(capture.Source), Value = Visit(capture.Value) }).ToImmutableArray()
                    }).ToImmutableArray(), future.Current.IsDefault ? default : future.Current.Select(callable => callable with
                    {
                        Captures = callable.Captures.IsDefault ? [] : callable.Captures.Select(capture => capture with
                        { Source = PublishCell(capture.Source), Value = Visit(capture.Value) }).ToImmutableArray()
                    }).ToImmutableArray())
                };
            if (!value.DeferredEffects.IsDefaultOrEmpty)
                projected = projected with { DeferredEffects = value.DeferredEffects.Select(VisitWrite).ToImmutableArray() };
            values[value] = projected;
            return projected;
        }

        void PublishResource(int identity)
        {
            if (!resources.Add(identity) || caller.Indexed.ContainsKey(identity)) return;
            // The descriptor belongs to this fresh result, not to a write applied
            // speculatively to a resource borrowed from the caller.
            var storage = produced.Indexed[identity];
            caller.Indexed[identity] = storage;
            caller.Indexed[identity] = storage with { Element = Visit(storage.Element) };
        }

        HirLocationId PublishCell(HirLocationId location)
        {
            var owner = StorageOwner(location, produced);
            if (caller.Values.ContainsKey(owner)) return owner;
            if (caller.Values.ContainsKey(location)) return location;
            var current = StorageValue(location, produced);
            if (current is null) return location;
            caller.Values[location] = current;
            caller.Storage[location] = StorageType(location, produced);
            if (produced.Definitions.TryGetValue(location, out var definition)) caller.Definitions[location] = definition;
            caller.Values[location] = Visit(current);
            return location;
        }
    }
}
