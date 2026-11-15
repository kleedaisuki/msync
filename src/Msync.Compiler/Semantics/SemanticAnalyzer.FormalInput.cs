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
        SemanticContext context = new(new(-1)) { ReplacedProgramCells = caller.ReplacedProgramCells };
        foreach (var pair in caller.Definitions) context.Definitions.Add(pair.Key, pair.Value);
        FormalInput input = new(this, caller, context, keyOnly: true);
        SemanticValue[] parameters = new SemanticValue[arguments.Count];
        SemanticValue[] environment = new SemanticValue[captures.Count];
        for (int index = 0; index < captures.Count; index++) environment[index] = input.Abstract(captures[index]);
        for (int index = 0; index < arguments.Count; index++) parameters[index] = input.Abstract(arguments[index]);
        input.Complete();
        return new(parameters, environment, context);
    }

    private sealed class FormalInput(SemanticAnalyzer owner, SemanticContext caller, SemanticContext formal,
        bool keyOnly = false)
    {
        private readonly Dictionary<int, int> regions = [];
        private readonly List<int> admittedRegions = [];
        private readonly Dictionary<int, int> indexedRegions = [];
        private readonly Dictionary<SemanticValue, SemanticValue> active = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(SemanticValue Value, SemanticInputRole Role), SemanticValue> completed = new(ReferenceRoleComparer<SemanticValue>.Instance);
        private readonly Dictionary<(SemanticCallable Value, SemanticInputRole Role), SemanticCallable> callables = new(ReferenceRoleComparer<SemanticCallable>.Instance);
        private readonly Dictionary<SemanticCallable, SemanticCallable> locationCallables = new(SemanticCallableIdentity.Instance);
        private readonly Dictionary<SemanticInputRole, int> scopeRoles = [];
        private readonly Dictionary<SemanticInputRole, int> resourceRoles = [];
        private readonly SemanticInputRoles roles = new();
        private readonly Dictionary<int, HashSet<int>> scopeSources = [];
        private int nextRoot;
        public Dictionary<HirLocationId, HirLocationId> Locations { get; } = [];

        public SemanticValue Abstract(SemanticValue value) => Abstract(value, roles.Root(nextRoot++));

        private SemanticValue Abstract(SemanticValue value, SemanticInputRole role)
        {
            if (keyOnly && IsKeyLeaf(value)) return value;
            if (active.TryGetValue(value, out var known)) return known;
            if (completed.TryGetValue((value, role), out known)) return known;
            // Runtime constants are not specialization facts. In particular a first
            // Bool argument must not delete the other caller's branch from shared code.
            var result = value with
            {
                Type = owner.PlanType(value),
                Node = HirNodeId.None,
                Location = null,
                Literal = value.DenotedType is null ? null : value.Literal,
                Callables = [],
                Scope = null,
                Element = null,
                IndexedRegion = null,
                Origin = null,
                Observation = null,
                DeferredEffects = [],
                IndexedAliases = [],
                FutureCallableDomain = null
            };
            active[value] = result;
            if (value.Scope is { } scope)
            {
                scope = caller.Objects.GetValueOrDefault(scope.Region, scope);
                if (scope.Region >= 0 && regions.TryGetValue(scope.Region, out var existing)
                    && formal.Objects.TryGetValue(existing, out var admitted))
                    result = result with { Scope = admitted };
                else
                {
                    bool summary = scope.Region < 0;
                    int region = scope.Region >= 0 && regions.TryGetValue(scope.Region, out var family) ? family
                        : summary && scopeRoles.TryGetValue(role, out var prototype) ? prototype : ++owner.nextRegion;
                    if (summary) scopeRoles[role] = region;
                    if (scope.Region >= 0 && !regions.ContainsKey(scope.Region)) AddRegion(scope.Region, region);
                    var sources = scope.Aliases.IsDefaultOrEmpty ? (scope.Region >= 0 ? ImmutableHashSet.Create(scope.Region) : [])
                        : scope.Aliases.ToImmutableHashSet();
                    RegisterScopeSources(region, sources);
                    var symbolic = scope with
                    {
                        Region = region,
                        Fields = ImmutableDictionary<string, SemanticValue>.Empty,
                        Locations = ImmutableDictionary<string, HirLocationId>.Empty,
                        Aliases = [],
                        Dynamic = scope.Dynamic || summary
                    };
                    formal.Objects[region] = symbolic;
                    result = result with { Scope = symbolic };
                    active[value] = result;
                    var fields = ImmutableDictionary.CreateBuilder<string, SemanticValue>(StringComparer.Ordinal);
                    foreach (var (name, original) in scope.Fields)
                    {
                        bool located = scope.Locations.TryGetValue(name, out var location);
                        var field = located ? caller.Values.GetValueOrDefault(location, original) : original;
                        HirTypeId? carrier = located ? owner.StorageType(location, caller) : field.StorageConstraint;
                        if (carrier is { } storage && owner.hir.Types[storage.Value].TypeParameter < 0)
                        {
                            TypeRef stored = owner.hir.Types[storage.Value].RuntimeType;
                            if (owner.Runtime(field) == TypeRef.Null
                                || (stored.Kind & (TypeKind.Scope | TypeKind.Range | TypeKind.Function | TypeKind.Type)) == 0)
                                field = field with { Type = storage };
                        }
                        fields.Add(name, Abstract(field, roles.Field(role, name)));
                    }
                    symbolic = symbolic with { Fields = fields.ToImmutable() };
                    formal.Objects[region] = symbolic;
                    foreach (var field in scope.Fields.Keys)
                        foreach (int source in sources)
                            if (caller.MemberContracts.TryGetValue((source, field), out var contracts))
                                formal.MemberContracts[(region, field)] = formal.MemberContracts.GetValueOrDefault((region, field), [])
                                    .AddRange(contracts).Distinct().ToImmutableArray();
                    result = result with { Scope = symbolic };
                }
            }
            if (value.Element is { } element) result = result with
            { Element = Abstract(element with { StorageConstraint = null }, roles.Element(role)) };
            if (!value.Callables.IsDefaultOrEmpty)
                result = result with { Callables = AbstractCallables(value.Callables, role) };
            if (value.FutureCallableDomain is { } future)
                result = result with
                {
                    FutureCallableDomain = new(FormalLocation(future.Owner),
                    AbstractCallables(future.Choices, role), AbstractCallables(future.Current, role))
                };
            if (value.IndexedRegion is { } indexed)
            {
                if (!indexedRegions.TryGetValue(indexed, out var region))
                {
                    region = ++owner.nextRegion;
                    indexedRegions[indexed] = region;
                    if (caller.Indexed.TryGetValue(indexed, out var description))
                        formal.Indexed[region] = description with { Element = Abstract(description.Element, roles.Element(role)) };
                }
                result = result with { IndexedRegion = region };
            }
            if (!value.IndexedAliases.IsDefaultOrEmpty)
                result = result with { IndexedAliases = [MapResourceView(value.IndexedAliases, role)] };
            if (!value.DeferredEffects.IsDefaultOrEmpty)
                result = result with
                {
                    DeferredEffects = value.DeferredEffects.Select(write => write with
                    {
                        After = Abstract(write.After, roles.WriteValue(role)),
                        IndexedStorage = write.IndexedStorage is { } storage ? storage with { Element = Abstract(storage.Element, roles.WriteElement(role)) } : null,
                        Location = write.Kind == SemanticWriteKind.Location ? FormalLocation(write.Location) : write.Location,
                        Region = write.Kind == SemanticWriteKind.Field ? MapSourceRegion(write.Region)
                        : write.Kind == SemanticWriteKind.Indexed ? MapIndexedRegion(write.Region) : write.Region
                    }).ToImmutableArray()
                };
            if (!value.OpenSessionCalls.IsDefaultOrEmpty)
                result = result with
                {
                    OpenSessionCalls = [.. value.OpenSessionCalls.Select(binding => binding with
                {
                    Callee = binding.Callee with { Root = FormalLocation(binding.Callee.Root) },
                    Arguments = [.. binding.Arguments.Select(source => source is null ? null : source with { Root = FormalLocation(source.Root) })]
                })]
                };
            active.Remove(value);
            completed[(value, role)] = result;
            return result;
        }

        private static bool IsKeyLeaf(SemanticValue value)
        {
            if (value.Scope is not null || value.Element is not null || value.IndexedRegion is not null
                || !value.IndexedAliases.IsDefaultOrEmpty || !value.DeferredEffects.IsDefaultOrEmpty
                || value.FutureCallableDomain is not null || !value.OpenSessionCalls.IsDefaultOrEmpty) return false;
            foreach (var callable in value.Callables.IsDefault ? [] : value.Callables)
                if (!callable.Captures.IsDefaultOrEmpty) return false;
            // Key writing observes types and callable identities, never runtime
            // annotations. Executable-body abstraction still strips those facts.
            return true;
        }

        private ImmutableArray<SemanticCallable> AbstractCallables(ImmutableArray<SemanticCallable> values, SemanticInputRole role)
        {
            if (values.IsDefaultOrEmpty) return [];
            ImmutableArray<SemanticCallable>.Builder? rewritten = null;
            for (int index = 0; index < values.Length; index++)
            {
                var original = values[index];
                var item = AbstractCallable(original, role);
                if (rewritten is null && !ReferenceEquals(original, item))
                {
                    rewritten = ImmutableArray.CreateBuilder<SemanticCallable>(values.Length);
                    for (int previous = 0; previous < index; previous++) rewritten.Add(values[previous]);
                }
                rewritten?.Add(item);
            }
            return rewritten is null ? values : rewritten.MoveToImmutable();
        }

        private SemanticCallable AbstractCallable(SemanticCallable callable, SemanticInputRole role)
        {
            if (callable.Captures.IsDefaultOrEmpty) return callable;
            // Location captures name the same environment through every path in this
            // operation. Value captures retain their structural selection role.
            bool sharedEnvironment = SharesEnvironment(callable.Captures);
            if (sharedEnvironment && locationCallables.TryGetValue(callable, out var shared)) return shared;
            SemanticInputRole environment = sharedEnvironment ? SemanticInputRole.SharedEnvironment : role;
            if (callables.TryGetValue((callable, environment), out var known)) return known;
            var captures = ImmutableArray.CreateBuilder<SemanticCapture>(callable.Captures.Length);
            foreach (var capture in callable.Captures)
            {
                var source = FormalLocation(capture.Source);
                var captured = Abstract(capture.Mode == HirCaptureMode.Location
                    ? caller.Values.GetValueOrDefault(capture.Source, capture.Value) : capture.Value,
                    capture.Mode == HirCaptureMode.Location ? roles.Capture(source) : roles.ValueCapture(role, source));
                formal.Values[source] = captured;
                if (!keyOnly) formal.Storage[source] = owner.StorageType(capture.Source, caller);
                captures.Add(capture with { Source = source, Value = captured });
            }
            var result = callable with { Captures = captures.MoveToImmutable() };
            callables[(callable, environment)] = result;
            if (sharedEnvironment) locationCallables[callable] = result;
            return result;
        }

        private bool SharesEnvironment(ImmutableArray<SemanticCapture> captures)
        {
            foreach (var capture in captures)
                if (capture.Mode != HirCaptureMode.Location || !caller.Values.ContainsKey(capture.Source)) return false;
            return true;
        }

        private sealed class ReferenceRoleComparer<T> : IEqualityComparer<(T Value, SemanticInputRole Role)> where T : class
        {
            public static readonly ReferenceRoleComparer<T> Instance = new();
            public bool Equals((T Value, SemanticInputRole Role) first, (T Value, SemanticInputRole Role) second) =>
                ReferenceEquals(first.Value, second.Value) && first.Role == second.Role;
            public int GetHashCode((T Value, SemanticInputRole Role) key) => HashCode.Combine(
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(key.Value), key.Role.Value);
        }

        private int MapResourceView(ImmutableArray<int> sources, SemanticInputRole role)
        {
            if (resourceRoles.TryGetValue(role, out var existing)) return existing;
            int region = ++owner.nextRegion;
            resourceRoles[role] = region;
            var storage = caller.Indexed[sources[0]];
            for (int index = 1; index < sources.Length; index++) storage = owner.JoinIndexed(storage, caller.Indexed[sources[index]]);
            formal.Indexed[region] = storage with { Element = owner.ValueForType(storage.Element.Type) };
            formal.Indexed[region] = storage with { Element = Abstract(storage.Element, roles.Element(role)) };
            return region;
        }

        private int MapIndexedRegion(int source)
        {
            if (indexedRegions.TryGetValue(source, out var region)) return region;
            indexedRegions[source] = region = ++owner.nextRegion;
            if (caller.Indexed.TryGetValue(source, out var storage))
                formal.Indexed[region] = storage with { Element = Abstract(storage.Element, roles.ExternalResource(storage.Element.Type)) };
            return region;
        }

        private int MapSourceRegion(int source)
        {
            if (caller.Objects.TryGetValue(source, out var scope))
                return Abstract(new(owner.hir.Type(new HirType(TypeRef.Scope, Shape: scope.Layout)), HirNodeId.None, Scope: scope)).Scope!.Region;
            if (!regions.TryGetValue(source, out var region)) AddRegion(source, region = ++owner.nextRegion);
            return region;
        }

        public SemanticValue AttachOrigin(SemanticValue value, HirLocationId root) =>
            AttachOrigin(value, new(root), []);

        private SemanticValue AttachOrigin(SemanticValue value, SemanticOrigin origin, HashSet<int> active)
        {
            var result = value with { Origin = origin };
            if (value.Scope is not { } scope || !active.Add(scope.Region)) return result;
            scope = formal.Objects.GetValueOrDefault(scope.Region, scope);
            scope = scope with
            {
                Fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                pair => AttachOrigin(pair.Value, origin with
                {
                    Fields = origin.Fields.IsDefault
                    ? [pair.Key] : origin.Fields.Add(pair.Key)
                }, active), StringComparer.Ordinal)
            };
            formal.Objects[scope.Region] = scope;
            active.Remove(scope.Region);
            return result with { Scope = scope };
        }

        private void RegisterScopeSources(int region, IEnumerable<int> sources)
        {
            foreach (int source in sources)
            {
                if (!scopeSources.TryGetValue(source, out var views)) scopeSources[source] = views = [];
                foreach (int view in views)
                {
                    if (view == region) continue;
                    formal.AliasViews[region] = formal.AliasViews.GetValueOrDefault(region, ImmutableHashSet<int>.Empty).Add(view);
                    formal.AliasViews[view] = formal.AliasViews.GetValueOrDefault(view, ImmutableHashSet<int>.Empty).Add(region);
                }
                views.Add(region);
            }
        }

        private int AliasRegion(int source)
        {
            if (source < 0) return -1;
            if (regions.TryGetValue(source, out int target)) return target;
            // An external alias endpoint names a place family, not another
            // demanded value graph. Reads import facts through Abstract.
            AddRegion(source, target = ++owner.nextRegion);
            return target;
        }

        private void AddRegion(int source, int target)
        {
            regions.Add(source, target);
            admittedRegions.Add(source);
        }

        public void Complete()
        {
            foreach (var pair in Locations.ToArray())
                if (!formal.Values.ContainsKey(pair.Value) && caller.Values.TryGetValue(pair.Key, out var value))
                    formal.Values[pair.Value] = Abstract(value, roles.Capture(pair.Value));
            for (int index = 0; index < admittedRegions.Count; index++)
            {
                int source = admittedRegions[index];
                if (!caller.AliasViews.TryGetValue(source, out var views)) continue;
                int target = regions[source];
                foreach (int alias in views)
                {
                    int mapped = AliasRegion(alias);
                    formal.AliasViews[target] = formal.AliasViews.GetValueOrDefault(target, ImmutableHashSet<int>.Empty).Add(mapped);
                }
            }
        }

        private HirLocationId FormalLocation(HirLocationId source)
        {
            if (Locations.TryGetValue(source, out var target)) return target;
            target = source;
            if (formal.Publish)
            {
                target = new(owner.hir.Locations.Count);
                formal.Output!.ReserveLocation(owner.hir.Locations[source.Value] with
                {
                    Owner = formal.Function,
                    TemplateOwner = null,
                    StorageType = owner.ResolveHirType(caller.Storage.GetValueOrDefault(source,
                        owner.hir.Locations[source.Value].StorageType), caller)
                });
            }
            Locations[source] = target;
            // Key encoding uses the normalized location identity and current
            // value, not the facts needed to analyze a body in that location.
            if (keyOnly) return target;
            if (caller.CallableContracts.TryGetValue(source, out var contracts)) formal.CallableContracts[target] = contracts;
            if (caller.Definitions.TryGetValue(source, out var definition)) formal.Definitions[target] = definition;
            else if (owner.CanonicalDefinition(source) is { } canonical) formal.Definitions[target] = canonical;
            return target;
        }
    }

}
