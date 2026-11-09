using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private FunctionInvocation ProjectInvocation(FunctionInstance instance, IReadOnlyList<SemanticValue> arguments,
        IReadOnlyList<SemanticValue> captures, ImmutableArray<SemanticCapture> captureDefinitions, SemanticContext caller,
        bool applyEffects)
    {
        if (instance.Objects.IsEmpty && instance.Indexed.IsEmpty && instance.Cells.IsEmpty
            && instance.Result.Scope is null && instance.Result.Element is null
            && instance.Result.Callables.IsDefaultOrEmpty && instance.Result.Alternatives.IsDefaultOrEmpty
            && instance.Result.DeferredEffects.IsDefaultOrEmpty)
        {
            if (instance.Result.Origin is { } origin && origin.Fields.IsDefaultOrEmpty)
                for (int index = 0; index < instance.Arguments.Length; index++)
                    if (instance.Arguments[index].Origin?.Root == origin.Root)
                        return new(instance.Id, ProjectScalar(instance.Result, arguments[index]), []);
            return new(instance.Id, instance.Result with { Node = HirNodeId.None, Location = null, Origin = null }, []);
        }
        SemanticContext target = applyEffects ? caller : new(caller.Function, caller.Output, parent: caller)
        { Queries = caller.Queries, DeferredReads = caller.DeferredReads };
        InvocationProjection projection = new(this, instance, target, applyEffects);
        for (int index = 0; index < arguments.Count; index++) projection.Bind(instance.Arguments[index], arguments[index]);
        for (int index = 0; index < captures.Count; index++)
        {
            projection.Bind(instance.Captures[index], captures[index]);
            if (captureDefinitions[index].Mode == HirCaptureMode.Location)
                projection.Locations[instance.CaptureTargets[index]] = captureDefinitions[index].Source;
        }
        var effects = instance.Complete ? projection.ProjectWrites() : [];
        if (applyEffects) projection.ApplyEffects(effects);
        return new(instance.Id, projection.Project(instance.Result) with { Location = null, Node = HirNodeId.None }, effects);
    }

    private SemanticValue ProjectScalar(SemanticValue formal, SemanticValue actual)
    {
        var literal = actual.Literal;
        if (literal is { Kind: HirLiteralKind.Path } path && Runtime(formal).WithoutNull == TypeRef.String)
            literal = new(HirLiteralKind.String, Text: path.Text);
        else if (literal is { Kind: HirLiteralKind.String } text && Runtime(formal).WithoutNull == TypeRef.Path)
            literal = new(HirLiteralKind.Path, Text: text.Text);
        return formal with { Literal = literal, DenotedType = actual.DenotedType, Observation = actual.Observation,
            Node = HirNodeId.None, Location = null, Origin = null };
    }

    private sealed class InvocationProjection(SemanticAnalyzer owner, FunctionInstance instance, SemanticContext caller, bool commitEffects)
    {
        private readonly Dictionary<int, int> regions = [];
        private readonly Dictionary<int, int> indexedRegions = [];
        private readonly Dictionary<int, SemanticScope> singletonViews = caller.Publish && commitEffects ? owner.projectedSingletons
            : (caller.Queries ??= new()).Singletons;
        private readonly Dictionary<HirLocationId, SemanticValue> origins = [];
        private readonly Dictionary<SemanticValue, SemanticValue> values = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<(SemanticValue Formal, SemanticValue Actual)> bound = new(new ValuePairComparer());
        public Dictionary<HirLocationId, HirLocationId> Locations { get; } = [];

        public void Bind(SemanticValue formal, SemanticValue actual)
        {
            if (!bound.Add((formal, actual))) return;
            if (formal.Origin is { } origin && origin.Fields.IsDefaultOrEmpty) origins[origin.Root] = actual;
            if (formal.Scope is { } shape && actual.Scope is { } scope)
            {
                scope = caller.Objects.GetValueOrDefault(scope.Region, scope);
                regions[shape.Region] = scope.Region;
                if (!shape.Aliases.IsDefaultOrEmpty && !scope.Aliases.IsDefaultOrEmpty)
                    for (int alias = 0; alias < Math.Min(shape.Aliases.Length, scope.Aliases.Length); alias++)
                        regions[shape.Aliases[alias]] = scope.Aliases[alias];
                foreach (var pair in shape.Fields)
                    if (scope.Fields.TryGetValue(pair.Key, out var field))
                        Bind(pair.Value, scope.Locations.TryGetValue(pair.Key, out var location)
                            ? caller.Values.GetValueOrDefault(location, field) : field);
            }
            if (!formal.DeferredEffects.IsDefaultOrEmpty && !actual.DeferredEffects.IsDefaultOrEmpty)
                for (int index = 0; index < Math.Min(formal.DeferredEffects.Length, actual.DeferredEffects.Length); index++)
                {
                    var first = formal.DeferredEffects[index]; var second = actual.DeferredEffects[index];
                    if (first.Kind == SemanticWriteKind.Location) Locations[first.Location] = second.Location;
                    else if (first.Kind == SemanticWriteKind.Field) regions[first.Region] = second.Region;
                    else indexedRegions[first.Region] = second.Region;
                    Bind(first.After, second.After);
                }
            if (formal.Element is { } element && actual.Element is { } actualElement) Bind(element, actualElement);
            if (formal.IndexedRegion is { } indexed && actual.IndexedRegion is { } actualIndexed)
                indexedRegions[indexed] = actualIndexed;
            if (!formal.Callables.IsDefaultOrEmpty && !actual.Callables.IsDefaultOrEmpty)
                for (int choice = 0; choice < Math.Min(formal.Callables.Length, actual.Callables.Length); choice++)
                {
                    var first = formal.Callables[choice].Captures;
                    var second = actual.Callables[choice].Captures;
                    if (first.IsDefaultOrEmpty || second.IsDefaultOrEmpty) continue;
                    for (int index = 0; index < Math.Min(first.Length, second.Length); index++)
                    {
                        Locations[first[index].Source] = second[index].Source;
                        Bind(first[index].Value, second[index].Value);
                    }
                }
        }

        public SemanticValue Project(SemanticValue value)
        {
            if (values.TryGetValue(value, out var previous)) return previous;
            var result = value with { Node = HirNodeId.None, Location = null, Origin = null, DeferredEffects = [] };
            if (value.Origin is { } origin && origins.TryGetValue(origin.Root, out var actual))
            {
                bool found = true;
                if (!origin.Fields.IsDefault)
                    foreach (var name in origin.Fields)
                    {
                        if (actual.Scope is not { } scope) { found = false; break; }
                        scope = caller.Objects.GetValueOrDefault(scope.Region, scope);
                        if (!scope.Fields.TryGetValue(name, out var field)) { found = false; break; }
                        actual = scope.Locations.TryGetValue(name, out var location)
                            ? caller.Values.GetValueOrDefault(location, field) : field;
                    }
                if (found) result = owner.ProjectScalar(result, actual);
            }
            values[value] = result;
            if (value.Scope is { } shape)
            {
                shape = instance.Objects.GetValueOrDefault(shape.Region, shape);
                bool existing = regions.TryGetValue(shape.Region, out var region);
                if (!existing && shape.Named && TrySingleton(shape.Region, out var singleton))
                {
                    region = singleton.Region;
                    regions[shape.Region] = region;
                    caller.Objects.TryAdd(region, singleton);
                    existing = true;
                }
                if (!existing) regions[shape.Region] = region = ++owner.nextRegion;
                if (existing && caller.Objects.TryGetValue(region, out var live))
                {
                    result = result with { Scope = live };
                    values[value] = result;
                }
                else
                {
                    // Fresh fields and closures must project through one location map:
                    // a getter captures the same cell that member assignment mutates.
                    var symbolic = shape with { Region = region, Fields = ImmutableDictionary<string, SemanticValue>.Empty,
                        Locations = caller.Objects.GetValueOrDefault(region)?.Locations ?? shape.Locations.ToImmutableDictionary(
                            pair => pair.Key, pair => MapLocation(pair.Value), StringComparer.Ordinal),
                        Aliases = shape.Aliases.IsDefault ? [] : shape.Aliases.Select(MapRegion).ToImmutableArray() };
                    result = result with { Scope = symbolic };
                    values[value] = result;
                    symbolic = symbolic with { Fields = shape.Fields.ToImmutableDictionary(pair => pair.Key,
                        pair => Project(pair.Value), StringComparer.Ordinal) };
                    caller.Objects[region] = symbolic;
                    if (shape.Named) singletonViews[shape.Region] = symbolic;
                    foreach (var field in symbolic.Locations)
                        if (symbolic.Fields.TryGetValue(field.Key, out var fieldValue)) caller.Values[field.Value] = fieldValue;
                    result = result with { Scope = symbolic };
                }
            }
            if (value.Element is { } element) result = result with { Element = Project(element) };
            if (!value.Callables.IsDefaultOrEmpty)
                result = result with { Callables = value.Callables.Select(callable => callable with
                {
                    Captures = callable.Captures.IsDefault ? [] : callable.Captures.Select(capture => capture with
                    { Source = MapLocation(capture.Source), Value = Project(capture.Value) }).ToImmutableArray()
                }).ToImmutableArray() };
            if (!value.Alternatives.IsDefaultOrEmpty)
                result = result with { Alternatives = value.Alternatives.Select(Project).ToImmutableArray() };
            if (value.IndexedRegion is { } indexed)
            {
                if (!indexedRegions.TryGetValue(indexed, out var region)) indexedRegions[indexed] = region = ++owner.nextRegion;
                if (!caller.Indexed.ContainsKey(region) && instance.Indexed.TryGetValue(indexed, out var storage))
                    caller.Indexed[region] = storage with { Element = Project(storage.Element) };
                result = result with { IndexedRegion = region };
            }
            if (!value.DeferredEffects.IsDefaultOrEmpty)
                result = result with { DeferredEffects = value.DeferredEffects.Select(write => write with
                {
                    After = Project(write.After),
                    Location = write.Kind == SemanticWriteKind.Location ? MapLocation(write.Location) : write.Location,
                    Region = write.Kind == SemanticWriteKind.Field ? MapRegion(write.Region)
                        : write.Kind == SemanticWriteKind.Indexed ? MapIndexedRegion(write.Region) : write.Region
                }).ToImmutableArray() };
            values[value] = result;
            return result;
        }

        private int MapIndexedRegion(int formal)
        {
            if (indexedRegions.TryGetValue(formal, out var region)) return region;
            indexedRegions[formal] = region = ++owner.nextRegion;
            return region;
        }

        public ImmutableArray<SemanticDeferredWrite> ProjectWrites()
        {
            if (instance.CellWrites.IsEmpty && instance.FieldWrites.IsEmpty && instance.IndexedWrites.IsEmpty) return [];
            ImmutableArray<SemanticDeferredWrite>.Builder writes = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
            foreach (var formal in instance.CellWrites)
                if (Locations.TryGetValue(formal, out var actual) && instance.Cells.TryGetValue(formal, out var value))
                    writes.Add(new(SemanticWriteKind.Location, Project(value), Location: actual));
            foreach (var write in instance.FieldWrites)
            {
                if (!instance.Objects.TryGetValue(write.Region, out var shape)) continue;
                if (!regions.TryGetValue(write.Region, out var actual))
                {
                    if (!shape.Named || !TrySingleton(write.Region, out var singleton)) continue;
                    regions[write.Region] = actual = singleton.Region;
                    caller.Objects.TryAdd(actual, singleton);
                }
                if (shape.Fields.TryGetValue(write.Field, out var value))
                    writes.Add(new(SemanticWriteKind.Field, Project(value), Region: actual, Field: write.Field));
            }
            foreach (var region in instance.IndexedWrites)
                if (indexedRegions.TryGetValue(region, out var actual) && instance.Indexed.TryGetValue(region, out var storage))
                    writes.Add(new(SemanticWriteKind.Indexed, Project(storage.Element), Region: actual));
            return writes.ToImmutable();
        }

        public void ApplyEffects(ImmutableArray<SemanticDeferredWrite> writes)
        {
            foreach (var pair in instance.AliasViews)
            {
                int source = MapRegion(pair.Key);
                var views = pair.Value.Select(MapRegion).ToImmutableHashSet();
                caller.AliasViews[source] = caller.AliasViews.GetValueOrDefault(source, ImmutableHashSet<int>.Empty).Union(views);
            }
            foreach (var write in writes)
            {
                if (write.Kind == SemanticWriteKind.Location)
                {
                    caller.Values[write.Location] = write.After;
                    caller.CellWrites.Add(write.Location);
                    continue;
                }
                if (write.Kind == SemanticWriteKind.Indexed)
                {
                    if (caller.Indexed.TryGetValue(write.Region, out var storage))
                        caller.Indexed[write.Region] = storage with { Element = write.After };
                    caller.IndexedWrites.Add(write.Region);
                    continue;
                }
                if (!caller.Objects.TryGetValue(write.Region, out var target)) continue;
                owner.InvalidateScopeAliases(target, caller);
                target = target with { Fields = target.Fields.SetItem(write.Field!, write.After) };
                caller.Objects[write.Region] = target;
                if (target.Locations.TryGetValue(write.Field!, out var location)) caller.Values[location] = write.After;
                caller.FieldWrites.Add((write.Region, write.Field!));
                foreach (var pair in regions)
                    if (pair.Value == write.Region && instance.Objects.GetValueOrDefault(pair.Key)?.Named == true)
                        singletonViews[pair.Key] = target;
            }
        }

        private bool TrySingleton(int region, out SemanticScope singleton) =>
            singletonViews.TryGetValue(region, out singleton!) || caller.Publish && !commitEffects
            && owner.projectedSingletons.TryGetValue(region, out singleton!);

        private int MapRegion(int formal)
        {
            if (regions.TryGetValue(formal, out var actual)) return actual;
            regions[formal] = actual = ++owner.nextRegion;
            return actual;
        }

        private HirLocationId MapLocation(HirLocationId formal)
        {
            if (Locations.TryGetValue(formal, out var actual)) return actual;
            actual = formal;
            if (caller.Publish)
            {
                actual = new(owner.hir.Locations.Count);
                caller.Output!.ReserveLocation(owner.hir.Locations[formal.Value] with { Owner = caller.Function, TemplateOwner = null });
            }
            Locations[formal] = actual;
            if (instance.Cells.TryGetValue(formal, out var value)) caller.Values[actual] = Project(value);
            return actual;
        }
    }

    private sealed class ValuePairComparer : IEqualityComparer<(SemanticValue Formal, SemanticValue Actual)>
    {
        public bool Equals((SemanticValue Formal, SemanticValue Actual) x, (SemanticValue Formal, SemanticValue Actual) y) =>
            ReferenceEquals(x.Formal, y.Formal) && ReferenceEquals(x.Actual, y.Actual);
        public int GetHashCode((SemanticValue Formal, SemanticValue Actual) pair) => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Formal),
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Actual));
    }
}
