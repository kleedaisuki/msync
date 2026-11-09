using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private sealed record SemanticInputSchema(SemanticValue[] Arguments, SemanticValue[] Captures, SemanticContext Context);

    private SemanticInputSchema CodeSchema(IReadOnlyList<SemanticValue> arguments,
        IReadOnlyList<SemanticValue> captures, SemanticContext caller)
    {
        SemanticContext context = new(new(-1)) { Queries = new(), DeferredReads = caller.DeferredReads };
        FormalInput input = new(this, caller, context);
        SemanticValue[] parameters = new SemanticValue[arguments.Count];
        SemanticValue[] environment = new SemanticValue[captures.Count];
        for (int index = 0; index < captures.Count; index++) environment[index] = input.Abstract(captures[index]);
        for (int index = 0; index < arguments.Count; index++) parameters[index] = input.Abstract(arguments[index]);
        input.Complete();
        return new(parameters, environment, context);
    }

    private sealed class FormalInput(SemanticAnalyzer owner, SemanticContext caller, SemanticContext formal)
    {
        private readonly Dictionary<int, int> regions = [];
        private readonly Dictionary<int, int> indexedRegions = [];
        private readonly Dictionary<SemanticValue, SemanticValue> active = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> scopeRoles = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> resourceRoles = new(StringComparer.Ordinal);
        private readonly List<(int Formal, ImmutableHashSet<int> Sources)> scopeViews = [];
        private int nextRoot;
        public Dictionary<HirLocationId, HirLocationId> Locations { get; } = [];

        public SemanticValue Abstract(SemanticValue value) => Abstract(value, "r" + nextRoot++);

        private SemanticValue Abstract(SemanticValue value, string role)
        {
            if (active.TryGetValue(value, out var known)) return known;
            // Runtime constants are not specialization facts. In particular a first
            // Bool argument must not delete the other caller's branch from shared code.
            var result = value with { Node = HirNodeId.None, Location = null,
                Literal = value.DenotedType is null ? null : value.Literal,
                Callables = [], Scope = null, Element = null, Alternatives = [], IndexedRegion = null, Origin = null, Observation = null, DeferredEffects = [], IndexedAliases = [] };
            active[value] = result;
            if (value.Scope is { } scope)
            {
                scope = caller.Objects.GetValueOrDefault(scope.Region, scope);
                if (scope.Region >= 0 && regions.TryGetValue(scope.Region, out var existing))
                    result = result with { Scope = formal.Objects[existing] };
                else
                {
                    bool summary = scope.Region < 0;
                    int region = summary && scopeRoles.TryGetValue(role, out var prototype) ? prototype : ++owner.nextRegion;
                    if (summary) scopeRoles[role] = region;
                    if (scope.Region >= 0) regions[scope.Region] = region;
                    var sources = scope.Aliases.IsDefaultOrEmpty ? (scope.Region >= 0 ? ImmutableHashSet.Create(scope.Region) : [])
                        : scope.Aliases.ToImmutableHashSet();
                    if (summary || scope.Dynamic) scopeViews.Add((region, sources));
                    var symbolic = scope with { Region = region, Fields = ImmutableDictionary<string, SemanticValue>.Empty,
                        Locations = ImmutableDictionary<string, HirLocationId>.Empty, Aliases = [], Dynamic = scope.Dynamic || summary };
                    formal.Objects[region] = symbolic;
                    result = result with { Scope = symbolic };
                    active[value] = result;
                    var fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                        pair => Abstract(scope.Locations.TryGetValue(pair.Key, out var location)
                            ? caller.Values.GetValueOrDefault(location, pair.Value) : pair.Value, role + "." + pair.Key), StringComparer.Ordinal);
                    symbolic = symbolic with { Fields = fields };
                    formal.Objects[region] = symbolic;
                    foreach (var field in scope.Fields.Keys)
                        foreach (int source in sources)
                            if (caller.MemberContracts.TryGetValue((source, field), out var contracts))
                                formal.MemberContracts[(region, field)] = formal.MemberContracts.GetValueOrDefault((region, field), [])
                                    .AddRange(contracts).Distinct().ToImmutableArray();
                    result = result with { Scope = symbolic };
                }
            }
            if (value.Element is { } element) result = result with { Element = Abstract(element, role + "[]") };
            if (!value.Callables.IsDefaultOrEmpty)
                result = result with { Callables = value.Callables.Select(callable => callable with
                {
                    Captures = callable.Captures.IsDefault ? [] : callable.Captures.Select(capture =>
                    {
                        var source = FormalLocation(capture.Source);
                        var captured = Abstract(capture.Mode == HirCaptureMode.Location
                            ? caller.Values.GetValueOrDefault(capture.Source, capture.Value) : capture.Value, role + "@" + source.Value);
                        formal.Values[source] = captured;
                        formal.Storage[source] = caller.Storage.GetValueOrDefault(capture.Source, captured.Type);
                        return capture with { Source = source, Value = captured };
                    }).ToImmutableArray()
                }).ToImmutableArray() };
            if (!value.Alternatives.IsDefaultOrEmpty && value.Scope is null && value.Element is null)
                result = result with { Alternatives = value.Alternatives.Select(alternative => Abstract(alternative, role + "|" + alternative.Type.Value)).ToImmutableArray() };
            if (value.IndexedRegion is { } indexed)
            {
                if (!indexedRegions.TryGetValue(indexed, out var region))
                {
                    region = ++owner.nextRegion;
                    indexedRegions[indexed] = region;
                    if (caller.Indexed.TryGetValue(indexed, out var description))
                        formal.Indexed[region] = description with { Element = Abstract(description.Element, role + "[]") };
                }
                result = result with { IndexedRegion = region };
            }
            if (!value.IndexedAliases.IsDefaultOrEmpty)
                result = result with { IndexedAliases = [MapResourceView(value.IndexedAliases, role)] };
            if (!value.DeferredEffects.IsDefaultOrEmpty)
                result = result with { DeferredEffects = value.DeferredEffects.Select(write => write with
                {
                    After = Abstract(write.After, role + "!after"),
                    IndexedStorage = write.IndexedStorage is { } storage ? storage with { Element = Abstract(storage.Element, role + "!element") } : null,
                    Location = write.Kind == SemanticWriteKind.Location ? FormalLocation(write.Location) : write.Location,
                    Region = write.Kind == SemanticWriteKind.Field ? MapSourceRegion(write.Region)
                        : write.Kind == SemanticWriteKind.Indexed ? MapIndexedRegion(write.Region) : write.Region
                }).ToImmutableArray() };
            if (!value.OpenSessionCalls.IsDefaultOrEmpty)
                result = result with { OpenSessionCalls = [.. value.OpenSessionCalls.Select(binding => binding with
                {
                    Callee = binding.Callee with { Root = FormalLocation(binding.Callee.Root) },
                    Arguments = [.. binding.Arguments.Select(source => source is null ? null : source with { Root = FormalLocation(source.Root) })]
                })] };
            active.Remove(value);
            return result;
        }

        private int MapResourceView(ImmutableArray<int> sources, string role)
        {
            if (resourceRoles.TryGetValue(role, out var existing)) return existing;
            int region = ++owner.nextRegion;
            resourceRoles[role] = region;
            var storage = caller.Indexed[sources[0]];
            for (int index = 1; index < sources.Length; index++) storage = owner.JoinIndexed(storage, caller.Indexed[sources[index]]);
            formal.Indexed[region] = storage with { Element = owner.ValueForType(storage.Element.Type) };
            formal.Indexed[region] = storage with { Element = Abstract(storage.Element, role + "[]") };
            return region;
        }

        private int MapIndexedRegion(int source)
        {
            if (indexedRegions.TryGetValue(source, out var region)) return region;
            indexedRegions[source] = region = ++owner.nextRegion;
            if (caller.Indexed.TryGetValue(source, out var storage))
                formal.Indexed[region] = storage with { Element = Abstract(storage.Element, "external.resource." + storage.Element.Type.Value) };
            return region;
        }

        private int MapSourceRegion(int source)
        {
            if (regions.TryGetValue(source, out var region)) return region;
            if (caller.Objects.TryGetValue(source, out var scope))
                return Abstract(new(owner.hir.Type(new HirType(TypeRef.Scope, Shape: scope.Layout)), HirNodeId.None, Scope: scope)).Scope!.Region;
            regions[source] = region = ++owner.nextRegion;
            return region;
        }

        public SemanticValue AttachOrigin(SemanticValue value, HirLocationId root) =>
            AttachOrigin(value, new(root), []);

        private SemanticValue AttachOrigin(SemanticValue value, SemanticOrigin origin, HashSet<int> active)
        {
            var result = value with { Origin = origin };
            if (value.Scope is not { } scope || !active.Add(scope.Region)) return result;
            scope = formal.Objects.GetValueOrDefault(scope.Region, scope);
            scope = scope with { Fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                pair => AttachOrigin(pair.Value, origin with { Fields = origin.Fields.IsDefault
                    ? [pair.Key] : origin.Fields.Add(pair.Key) }, active), StringComparer.Ordinal) };
            formal.Objects[scope.Region] = scope;
            active.Remove(scope.Region);
            return result with { Scope = scope };
        }

        public void Complete()
        {
            for (int first = 0; first < scopeViews.Count; first++)
                for (int second = first + 1; second < scopeViews.Count; second++)
                {
                    var a = scopeViews[first]; var b = scopeViews[second];
                    if (a.Formal == b.Formal || !a.Sources.Overlaps(b.Sources)) continue;
                    formal.AliasViews[a.Formal] = formal.AliasViews.GetValueOrDefault(a.Formal, ImmutableHashSet<int>.Empty).Add(b.Formal);
                    formal.AliasViews[b.Formal] = formal.AliasViews.GetValueOrDefault(b.Formal, ImmutableHashSet<int>.Empty).Add(a.Formal);
                }
            foreach (var pair in Locations.ToArray())
                if (caller.Values.TryGetValue(pair.Key, out var value)) formal.Values[pair.Value] = Abstract(value);
        }

        private HirLocationId FormalLocation(HirLocationId source)
        {
            if (Locations.TryGetValue(source, out var target)) return target;
            target = source;
            if (formal.Publish)
            {
                target = new(owner.hir.Locations.Count);
                formal.Output!.ReserveLocation(owner.hir.Locations[source.Value] with { Owner = formal.Function, TemplateOwner = null,
                    StorageType = owner.ResolveHirType(caller.Storage.GetValueOrDefault(source,
                        owner.hir.Locations[source.Value].StorageType), caller) });
            }
            Locations[source] = target;
            if (caller.CallableContracts.TryGetValue(source, out var contracts)) formal.CallableContracts[target] = contracts;
            if (caller.Definitions.TryGetValue(source, out var definition)) formal.Definitions[target] = definition;
            else if (owner.CanonicalDefinition(source) is { } canonical) formal.Definitions[target] = canonical;
            return target;
        }
    }

}
