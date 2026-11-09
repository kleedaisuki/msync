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
            && instance.Result.DeferredEffects.IsDefaultOrEmpty && instance.WriteEvents.IsDefault)
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
        var events = instance.Complete && !instance.WriteEvents.IsDefault
            ? projection.ProjectEvents(instance.WriteEvents) : default;
        var effects = !instance.Complete ? [] : events.IsDefault
            ? projection.ProjectWrites() : projection.EventEffects(events);
        if (applyEffects)
        {
            if (events.IsDefault) projection.ApplyEffects(effects);
            else projection.ApplyEvents(events);
        }
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
            if (!formal.IndexedAliases.IsDefaultOrEmpty && !actual.IndexedAliases.IsDefaultOrEmpty)
                for (int index = 0; index < Math.Min(formal.IndexedAliases.Length, actual.IndexedAliases.Length); index++)
                    indexedRegions[formal.IndexedAliases[index]] = actual.IndexedAliases[index];
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
            if (!value.IndexedAliases.IsDefaultOrEmpty)
                result = result with { IndexedAliases = value.IndexedAliases.Select(MapIndexedRegion).Distinct().Order().ToImmutableArray() };
            if (!value.DeferredEffects.IsDefaultOrEmpty)
                result = result with { DeferredEffects = value.DeferredEffects.Select(write => write with
                {
                    After = Project(write.After),
                    IndexedStorage = write.IndexedStorage is { } storage ? storage with { Element = Project(storage.Element) } : null,
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
            if (instance.Indexed.TryGetValue(formal, out var storage))
            {
                caller.Indexed[region] = storage with { Element = owner.ValueForType(storage.Element.Type) };
                caller.Indexed[region] = storage with { Element = Project(storage.Element) };
            }
            return region;
        }

        public ImmutableArray<SemanticWriteEvent> ProjectEvents(ImmutableArray<SemanticWriteEvent> source)
        {
            ImmutableArray<SemanticWriteEvent>.Builder result = ImmutableArray.CreateBuilder<SemanticWriteEvent>();
            foreach (var item in source)
            {
                var guards = caller.ActiveSingletonGuards.IsDefaultOrEmpty ? item.Guards
                    : caller.ActiveSingletonGuards.AddRange(item.Guards).Distinct().ToImmutableArray();
                var branches = item.Branches.IsDefault ? default
                    : item.Branches.Select(ProjectEvents).ToImmutableArray();
                SemanticDeferredWrite? write = item.Write is { } original ? ProjectWrite(original) : null;
                if (write is not null || item.InitializeSingleton is not null || !branches.IsDefaultOrEmpty)
                    result.Add(new(write, guards, item.InitializeSingleton, branches));
            }
            return result.ToImmutable();
        }

        private SemanticDeferredWrite? ProjectWrite(SemanticDeferredWrite write)
        {
            if (write.Kind == SemanticWriteKind.Location)
                return Locations.TryGetValue(write.Location, out var location)
                    ? write with { Location = location, After = Project(write.After) } : null;
            if (write.Kind == SemanticWriteKind.Indexed)
                return indexedRegions.TryGetValue(write.Region, out var indexed)
                    ? write with { Region = indexed, After = Project(write.After),
                        IndexedStorage = write.IndexedStorage is { } storage ? storage with { Element = Project(storage.Element) } : null } : null;
            if (!regions.TryGetValue(write.Region, out var region))
            {
                if (!instance.Objects.TryGetValue(write.Region, out var shape) || !shape.Named
                    || !TrySingleton(write.Region, out var singleton)) return null;
                regions[write.Region] = region = singleton.Region;
                caller.Objects.TryAdd(region, singleton);
            }
            return write with { Region = region, After = Project(write.After) };
        }

        public ImmutableArray<SemanticDeferredWrite> EventEffects(ImmutableArray<SemanticWriteEvent> events)
        {
            ImmutableArray<SemanticDeferredWrite>.Builder writes = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
            Collect(events, caller.InitializedSingletons);
            return writes.ToImmutable();

            ImmutableHashSet<int> Collect(ImmutableArray<SemanticWriteEvent> sequence, ImmutableHashSet<int> initialized)
            {
                foreach (var item in sequence)
                {
                    if (item.Guards.Any(initialized.Contains)) continue;
                    if (item.Write is { } write) writes.Add(write);
                    if (!item.Branches.IsDefaultOrEmpty)
                    {
                        ImmutableHashSet<int>? common = null;
                        foreach (var branch in item.Branches)
                        {
                            var completed = Collect(branch, initialized);
                            common = common is null ? completed : common.Intersect(completed);
                        }
                        if (common is not null) initialized = common;
                    }
                    if (item.InitializeSingleton is { } singleton) initialized = initialized.Add(singleton);
                }
                return initialized;
            }
        }

        public void ApplyEvents(ImmutableArray<SemanticWriteEvent> events)
        {
            caller.ActivateWriteEvents();
            var log = caller.WriteEvents!;
            caller.WriteEvents = null;
            Replay(events, caller);
            caller.WriteEvents = log;
            log.AddRange(events);
        }

        private void Replay(ImmutableArray<SemanticWriteEvent> events, SemanticContext destination)
        {
            foreach (var item in events)
            {
                if (item.Guards.Any(destination.InitializedSingletons.Contains)) continue;
                if (!item.Branches.IsDefaultOrEmpty)
                {
                    SemanticContext[] branches = new SemanticContext[item.Branches.Length];
                    for (int index = 0; index < branches.Length; index++)
                    {
                        var branch = new SemanticContext(destination.Function, destination.Output, destination)
                        {
                            Queries = destination.Queries, DeferredReads = destination.DeferredReads,
                            InitializedSingletons = destination.InitializedSingletons,
                            MaybeInitializedSingletons = destination.MaybeInitializedSingletons,
                            ActiveSingletonGuards = destination.ActiveSingletonGuards
                        };
                        Replay(item.Branches[index], branch);
                        branches[index] = branch;
                    }
                    owner.MergeContexts(destination, branches);
                }
                if (item.Write is { } write)
                {
                    var after = write.After;
                    var before = destination.ReadWriteTarget(destination.WriteTarget(write));
                    if (before is not null)
                        foreach (int guard in item.Guards)
                            if (destination.MaybeInitializedSingletons.Contains(guard))
                                after = owner.Join(after, destination.FactWhenInitialized(guard, write, before));
                    var saved = destination.ActiveSingletonGuards;
                    destination.ActiveSingletonGuards = item.Guards;
                    ApplyEffects([write with { After = after }], destination);
                    destination.ActiveSingletonGuards = saved;
                }
                if (item.InitializeSingleton is { } singleton)
                {
                    destination.InitializedSingletons = destination.InitializedSingletons.Add(singleton);
                    destination.MaybeInitializedSingletons = destination.MaybeInitializedSingletons.Add(singleton);
                }
            }
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
                    writes.Add(new(SemanticWriteKind.Indexed, Project(storage.Element), Region: actual,
                        IndexedStorage: storage with { Element = Project(storage.Element) }));
            return writes.ToImmutable();
        }

        public void ApplyEffects(ImmutableArray<SemanticDeferredWrite> writes, SemanticContext? destination = null)
        {
            var context = destination ?? caller;
            foreach (var pair in instance.MemberContracts)
            {
                if (!regions.TryGetValue(pair.Key.Region, out var region)) continue;
                var key = (region, pair.Key.Field);
                context.MemberContracts[key] = context.MemberContracts.GetValueOrDefault(key, [])
                    .AddRange(pair.Value).Distinct().ToImmutableArray();
            }
            foreach (var pair in instance.AliasViews)
            {
                int source = MapRegion(pair.Key);
                var views = pair.Value.Select(MapRegion).ToImmutableHashSet();
                context.AliasViews[source] = context.AliasViews.GetValueOrDefault(source, ImmutableHashSet<int>.Empty).Union(views);
            }
            foreach (var write in writes)
            {
                if (write.Kind == SemanticWriteKind.Location)
                {
                    context.Values[write.Location] = write.After;
                    owner.MarkProgramCell(write.Location, context);
                    context.CellWrites.Add(write.Location);
                    context.RecordWrite(write);
                    continue;
                }
                if (write.Kind == SemanticWriteKind.Indexed)
                {
                    if (context.Indexed.TryGetValue(write.Region, out var storage))
                        context.Indexed[write.Region] = write.IndexedStorage is { } summarized
                            ? owner.JoinIndexed(storage, summarized) with { Element = write.After }
                            : storage with { Element = write.After,
                                FixedCarrier = storage.FixedCarrier ?? (storage.CarrierConstraints.IsDefaultOrEmpty
                                    && owner.Runtime(write.After).Kind != TypeKind.Never ? write.After.Type : null) };
                    context.IndexedWrites.Add(write.Region);
                    context.RecordWrite(write);
                    continue;
                }
                if (!context.Objects.TryGetValue(write.Region, out var target)) continue;
                owner.InvalidateScopeAliases(target, context);
                target = target with { Fields = target.Fields.SetItem(write.Field!, write.After) };
                context.Objects[write.Region] = target;
                if (target.Locations.TryGetValue(write.Field!, out var location))
                {
                    context.Values[location] = write.After;
                    owner.MarkProgramCell(location, context);
                }
                context.FieldWrites.Add((write.Region, write.Field!));
                context.RecordWrite(write);
                foreach (var pair in regions)
                    if (ReferenceEquals(context, caller) && pair.Value == write.Region && instance.Objects.GetValueOrDefault(pair.Key)?.Named == true)
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
            var ownerLocation = owner.StorageOwner(formal, caller);
            if (Locations.TryGetValue(formal, out var actual)) return actual;
            if (Locations.TryGetValue(ownerLocation, out actual))
            {
                Locations[formal] = actual;
                return actual;
            }
            actual = formal;
            if (caller.Publish)
            {
                actual = new(owner.hir.Locations.Count);
                caller.Output!.ReserveLocation(owner.hir.Locations[formal.Value] with { Owner = caller.Function, TemplateOwner = null });
            }
            Locations[formal] = actual;
            Locations[ownerLocation] = actual;
            caller.Storage[actual] = owner.StorageType(formal, caller);
            if (instance.Definitions.TryGetValue(formal, out var definition)
                || instance.Definitions.TryGetValue(ownerLocation, out definition)) caller.Definitions[actual] = definition;
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
