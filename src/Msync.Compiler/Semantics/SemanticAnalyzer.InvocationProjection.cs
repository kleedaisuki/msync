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
            && instance.Result.DeferredEffects.IsDefaultOrEmpty && instance.Result.OpenSessionCalls.IsDefaultOrEmpty
            && instance.CallableContracts.IsEmpty && instance.WriteEvents.IsDefault)
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
            projection.ApplyCallableContracts();
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
        private readonly Dictionary<int, ImmutableArray<int>> indexedGroups = [];
        private readonly Dictionary<int, SemanticScope> scopeBindings = [];
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
                scopeBindings[shape.Region] = scope;
                if (scope.Region >= 0) regions[shape.Region] = scope.Region;
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
            {
                indexedRegions[indexed] = actualIndexed;
                if (instance.Indexed.TryGetValue(indexed, out var prototype) && caller.Indexed.TryGetValue(actualIndexed, out var concrete))
                    Bind(prototype.Element, concrete.Element);
            }
            if (!formal.IndexedAliases.IsDefaultOrEmpty && IndexedResourceCount(actual) != 0)
            {
                var targets = actual.IndexedRegion is { } unique ? ImmutableArray.Create(unique) : actual.IndexedAliases;
                foreach (int view in formal.IndexedAliases)
                {
                    indexedGroups[view] = targets;
                    if (!instance.Indexed.TryGetValue(view, out var prototype)) continue;
                    var concrete = caller.Indexed[targets[0]];
                    for (int index = 1; index < targets.Length; index++) concrete = owner.JoinIndexed(concrete, caller.Indexed[targets[index]]);
                    Bind(prototype.Element, concrete.Element);
                }
            }
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
                if (shape.Region >= 0) shape = instance.Objects.GetValueOrDefault(shape.Region, shape);
                if (scopeBindings.TryGetValue(shape.Region, out var binding))
                {
                    var live = binding.Region >= 0 ? caller.Objects.GetValueOrDefault(binding.Region, binding) : binding;
                    result = result with { Scope = live };
                }
                else if (shape.Region < 0)
                {
                    result = result with { Scope = shape with
                    {
                        Fields = shape.Fields.ToImmutableDictionary(pair => pair.Key, pair => Project(pair.Value), StringComparer.Ordinal),
                        Aliases = ExpandScopeAliases(shape.Aliases)
                    } };
                }
                else
                {
                    bool existing = regions.TryGetValue(shape.Region, out var region);
                    if (!existing && shape.Named && TrySingleton(shape.Region, out var singleton))
                    {
                        region = singleton.Region;
                        regions[shape.Region] = region;
                        caller.Objects.TryAdd(region, singleton);
                        existing = true;
                    }
                    if (!existing) regions[shape.Region] = region = ++owner.nextRegion;
                    if (existing && caller.Objects.TryGetValue(region, out var live)) result = result with { Scope = live };
                    else
                    {
                        var symbolic = shape with { Region = region, Fields = ImmutableDictionary<string, SemanticValue>.Empty,
                            Locations = shape.Locations.ToImmutableDictionary(pair => pair.Key, pair => MapLocation(pair.Value), StringComparer.Ordinal),
                            Aliases = ExpandScopeAliases(shape.Aliases) };
                        result = result with { Scope = symbolic };
                        values[value] = result;
                        symbolic = symbolic with { Fields = shape.Fields.ToImmutableDictionary(pair => pair.Key,
                            pair => Project(pair.Value), StringComparer.Ordinal) };
                        caller.Objects[region] = symbolic;
                        foreach (int alias in symbolic.Aliases)
                            caller.AliasViews[alias] = caller.AliasViews.GetValueOrDefault(alias, ImmutableHashSet<int>.Empty).Add(region);
                        if (shape.Named) singletonViews[shape.Region] = symbolic;
                        foreach (var field in symbolic.Locations)
                            if (symbolic.Fields.TryGetValue(field.Key, out var fieldValue)) caller.Values[field.Value] = fieldValue;
                        result = result with { Scope = symbolic };
                    }
                }
                values[value] = result;
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
                result = result with { IndexedAliases = value.IndexedAliases.SelectMany(resource => ResourceTargets(resource)).Distinct().Order().ToImmutableArray() };
            if (!value.DeferredEffects.IsDefaultOrEmpty)
                result = result with { DeferredEffects = value.DeferredEffects.SelectMany(write => ProjectWrite(write)).ToImmutableArray() };
            if (!value.OpenSessionCalls.IsDefaultOrEmpty)
                result = result with { OpenSessionCalls = [.. value.OpenSessionCalls.Select(binding => binding with
                {
                    Callee = binding.Callee with { Root = MapLocation(binding.Callee.Root) },
                    Arguments = [.. binding.Arguments.Select(source => source is null ? null : source with { Root = MapLocation(source.Root) })]
                })] };
            values[value] = result;
            return result;
        }

        private ImmutableArray<int> ScopeTargets(int formal)
        {
            if (scopeBindings.TryGetValue(formal, out var scope))
                return scope.Aliases.IsDefaultOrEmpty ? (scope.Region >= 0 ? [scope.Region] : []) : scope.Aliases;
            return [MapRegion(formal)];
        }

        private ImmutableArray<int> ExpandScopeAliases(ImmutableArray<int> aliases) => aliases.IsDefaultOrEmpty ? []
            : aliases.SelectMany(scope => ScopeTargets(scope)).Distinct().Order().ToImmutableArray();

        private ImmutableArray<int> ResourceTargets(int formal)
        {
            if (indexedGroups.TryGetValue(formal, out var group)) return group;
            return [MapIndexedRegion(formal)];
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
                var branches = item.Branches.IsDefault ? default : item.Branches.Select(ProjectEvents).ToImmutableArray();
                var writes = item.Write is { } original ? ProjectWrite(original) : [];
                if (writes.IsEmpty)
                {
                    if (item.InitializeSingleton is not null || !branches.IsDefaultOrEmpty)
                        result.Add(new(null, guards, item.InitializeSingleton, branches));
                }
                else foreach (var write in writes) result.Add(new(write, guards, item.InitializeSingleton, branches));
            }
            return result.ToImmutable();
        }

        private ImmutableArray<SemanticDeferredWrite> ProjectWrite(SemanticDeferredWrite write)
        {
            if (write.Kind == SemanticWriteKind.Location)
                return Locations.TryGetValue(write.Location, out var location)
                    ? [write with { Location = location, After = Project(write.After) }] : [];
            if (write.Kind == SemanticWriteKind.Indexed)
            {
                if (!indexedGroups.ContainsKey(write.Region) && !indexedRegions.ContainsKey(write.Region)) return [];
                var after = Project(write.After);
                var storage = write.IndexedStorage is { } indexed ? indexed with { Element = Project(indexed.Element) } : null;
                var targets = ResourceTargets(write.Region);
                return targets.Select(target =>
                {
                    var element = after;
                    var summary = storage;
                    if (indexedGroups.ContainsKey(write.Region))
                    {
                        var before = caller.Indexed[target];
                        summary = summary is null ? before with { Element = owner.Join(before.Element, after) }
                            : owner.JoinIndexed(before, summary);
                        element = summary.Element;
                    }
                    return write with { Region = target, After = element, IndexedStorage = summary };
                }).ToImmutableArray();
            }
            if (!instance.Objects.TryGetValue(write.Region, out var shape)) return [];
            if (!regions.ContainsKey(write.Region) && !scopeBindings.ContainsKey(write.Region))
            {
                if (shape.Named && TrySingleton(write.Region, out var singleton))
                {
                    regions[write.Region] = singleton.Region;
                    caller.Objects.TryAdd(singleton.Region, singleton);
                }
                else if (shape.Dynamic && !shape.Aliases.IsDefaultOrEmpty)
                    _ = Project(new(owner.hir.Type(new HirType(TypeRef.Scope, Shape: shape.Layout)), HirNodeId.None, Scope: shape));
                else return [];
            }
            var projected = Project(write.After);
            var actualTargets = scopeBindings.TryGetValue(write.Region, out var binding) && binding.Region < 0
                ? binding.Aliases : ImmutableArray.Create(MapRegion(write.Region));
            return actualTargets.Select(target =>
            {
                var after = projected;
                if (binding is { Region: < 0 } && caller.Objects.TryGetValue(target, out var before)
                    && before.Fields.TryGetValue(write.Field!, out var old)) after = owner.Join(old, after);
                return write with { Region = target, After = after };
            }).ToImmutableArray();
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
                if (instance.Cells.TryGetValue(formal, out var value))
                    writes.AddRange(ProjectWrite(new(SemanticWriteKind.Location, value, Location: formal)));
            foreach (var write in instance.FieldWrites)
                if (instance.Objects.TryGetValue(write.Region, out var shape) && shape.Fields.TryGetValue(write.Field, out var value))
                    writes.AddRange(ProjectWrite(new(SemanticWriteKind.Field, value, Region: write.Region, Field: write.Field)));
            foreach (var region in instance.IndexedWrites)
                if (instance.Indexed.TryGetValue(region, out var storage))
                    writes.AddRange(ProjectWrite(new(SemanticWriteKind.Indexed, storage.Element, Region: region, IndexedStorage: storage)));
            return writes.ToImmutable();
        }

        public void ApplyEffects(ImmutableArray<SemanticDeferredWrite> writes, SemanticContext? destination = null)
        {
            var context = destination ?? caller;
            foreach (var pair in instance.MemberContracts)
            {
                foreach (int region in ScopeTargets(pair.Key.Region))
                {
                    var key = (region, pair.Key.Field);
                    context.MemberContracts[key] = context.MemberContracts.GetValueOrDefault(key, [])
                        .AddRange(pair.Value).Distinct().ToImmutableArray();
                }
            }
            foreach (var pair in instance.AliasViews)
            {
                var views = pair.Value.SelectMany(scope => ScopeTargets(scope)).ToImmutableHashSet();
                foreach (int source in ScopeTargets(pair.Key))
                    context.AliasViews[source] = context.AliasViews.GetValueOrDefault(source, ImmutableHashSet<int>.Empty).Union(views);
            }
            foreach (var write in writes)
            {
                if (write.Kind == SemanticWriteKind.Location)
                {
                    owner.CheckSessionAssignment(write.Location, write.After, owner.hir.Locations[write.Location.Value].Source, context);
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
                if (target.Locations.TryGetValue(write.Field!, out var fieldLocation))
                    owner.CheckSessionMemberAssignment(target, write.Field!, write.After, owner.hir.Locations[fieldLocation.Value].Source, context);
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

        public void ApplyCallableContracts()
        {
            foreach (var (formal, contracts) in instance.CallableContracts)
            {
                var actual = MapLocation(formal);
                foreach (int slot in contracts) EstablishSessionContract(actual, slot, caller);
            }
        }

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
