using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    internal sealed class FormalInput(SemanticAnalyzer owner, SemanticContext caller, SemanticContext formal)
    {
        private readonly Dictionary<int, int> regions = [];
        private readonly Dictionary<int, (SemanticScope Scope, SemanticInputRole Role)> sources = [];
        private readonly Dictionary<int, int> indexedRegions = [];
        private readonly Dictionary<SemanticValue, SemanticValue> active = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(SemanticValue Value, SemanticInputRole Role), SemanticValue> completed = new(ReferenceRoleComparer<SemanticValue>.Instance);
        private readonly Dictionary<(SemanticCallable Value, SemanticInputRole Role), SemanticCallable> callables = new(ReferenceRoleComparer<SemanticCallable>.Instance);
        private readonly Dictionary<SemanticCallable, SemanticCallable> locationCallables = new(SemanticCallableIdentity.Instance);
        private readonly Dictionary<SemanticInputRole, int> scopeRoles = [];
        private readonly Dictionary<SemanticInputRole, int> resourceRoles = [];
        private readonly SemanticInputRoles roles = new();
        private int nextRoot;
        public Dictionary<HirLocationId, HirLocationId> Locations { get; } = [];
        public Dictionary<int, SemanticOrigin> ScopePaths { get; } = [];
        public List<(SemanticOrigin Path, SemanticValue Value)> NestedInputs { get; } = [];
        public int FieldLoads { get; private set; }
        public bool OwnsScope(int region) => sources.ContainsKey(region);

        public SemanticValue Abstract(SemanticValue value)
        {
            var role = roles.Root(nextRoot++);
            return owner.Trace?.Detail is { } trace
                ? trace.Measure("compiler.semantic.formal_input", (Input: this, value, role),
                    static state => state.Input.Abstract(state.value, state.role))
                : Abstract(value, role);
        }

        private SemanticValue Abstract(SemanticValue value, SemanticInputRole role)
        {
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
                BranchValue = null,
                TypeRead = null,
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
                int region = scopeRoles.TryGetValue(role, out var prototype) ? prototype : ++owner.nextRegion;
                scopeRoles[role] = region;
                if (scope.Region >= 0) regions.TryAdd(scope.Region, region);
                if (!sources.ContainsKey(region))
                {
                    sources.Add(region, (scope, role));
                    var symbolic = scope with
                    {
                        Region = region,
                        Fields = ImmutableDictionary<string, SemanticValue>.Empty,
                        Locations = ImmutableDictionary<string, HirLocationId>.Empty,
                        Aliases = [],
                        Dynamic = scope.Dynamic || scope.Region < 0
                    };
                    formal.Objects[region] = symbolic;
                }
                result = result with { Scope = formal.Objects[region] };
                active[value] = result;
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

        public bool TryField(SemanticScope scope, string name, SemanticContext context, out SemanticValue value)
        {
            if (!sources.TryGetValue(scope.Region, out var input))
            {
                value = null!;
                return false;
            }
            var loaded = owner.Trace?.Detail is { } trace
                ? trace.Measure("compiler.semantic.formal_field", (Input: this, input, scope, name, context),
                    static state => state.Input.LoadField(state.input.Scope, state.input.Role,
                        state.scope, state.name, state.context))
                : LoadField(input.Scope, input.Role, scope, name, context);
            value = loaded!;
            return loaded is not null;
        }

        private SemanticValue? LoadField(SemanticScope source, SemanticInputRole role, SemanticScope scope,
            string name, SemanticContext context)
        {
            if (!owner.TryScopeField(source, name, caller, out var field)) return null;
            FieldLoads++;
            HirTypeId? carrier = source.Locations.TryGetValue(name, out var location)
                ? owner.StorageType(location, caller) : field.StorageConstraint;
            if (carrier is { } storage && owner.hir.Types[storage.Value].TypeParameter < 0)
            {
                TypeRef stored = owner.hir.Types[storage.Value].RuntimeType;
                if (owner.Runtime(field) == TypeRef.Null
                    || (stored.Kind & (TypeKind.Scope | TypeKind.Range | TypeKind.Function | TypeKind.Type)) == 0)
                    field = field with { Type = storage };
            }
            var value = Abstract(field, roles.Field(role, name));
            if (ScopePaths.TryGetValue(scope.Region, out var origin))
                value = AttachOrigin(value, new SemanticOrigin(origin.Root, [.. origin.Fields.AsSpan(), name]), []);
            scope = context.Objects.GetValueOrDefault(scope.Region, scope);
            context.Objects[scope.Region] = scope with { Fields = scope.Fields.SetItem(name, value) };
            AdmitMemberContract(scope.Region, name, context);
            return value;
        }

        public void AdmitMemberContract(int region, string name, SemanticContext context)
        {
            if (!sources.TryGetValue(region, out var input)) return;
            foreach (int source in ScopeAliases(input.Scope))
                if (caller.MemberContracts.TryGetValue((source, name), out var contracts))
                    context.MemberContracts[(region, name)] = context.MemberContracts.GetValueOrDefault((region, name), [])
                        .AddRange(contracts).Distinct().ToImmutableArray();
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
                formal.Storage[source] = owner.StorageType(capture.Source, caller);
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
            if (!regions.TryGetValue(source, out var region)) regions.Add(source, region = ++owner.nextRegion);
            return region;
        }

        public SemanticValue AttachOrigin(SemanticValue value, HirLocationId root) =>
            owner.Trace?.Detail is { } trace
                ? trace.Measure("compiler.semantic.formal_origins", (Input: this, value, root),
                    static state => state.Input.AttachOrigin(state.value, new(state.root), []))
                : AttachOrigin(value, new(root), []);

        private SemanticValue AttachOrigin(SemanticValue value, SemanticOrigin origin, HashSet<int> active)
        {
            var result = value with { Origin = origin };
            if (!origin.Fields.IsDefaultOrEmpty && HasNestedBindings(value))
                NestedInputs.Add((origin, result));
            if (value.Scope is not { } scope || !active.Add(scope.Region)) return result;
            ScopePaths.TryAdd(scope.Region, origin);
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

        private static bool HasNestedBindings(SemanticValue value) =>
            value.IndexedRegion is not null || !value.IndexedAliases.IsDefaultOrEmpty
            || value.Element is not null || !value.DeferredEffects.IsDefaultOrEmpty
            || value.FutureCallableDomain is not null
            || !value.Callables.IsDefaultOrEmpty && value.Callables.Any(callable => !callable.Captures.IsDefaultOrEmpty);

        public void Complete()
        {
            foreach (var pair in Locations.ToArray())
                if (!formal.Values.ContainsKey(pair.Value) && caller.Values.TryGetValue(pair.Key, out var value))
                    formal.Values[pair.Value] = Abstract(value, roles.Capture(pair.Value));
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
            if (caller.CallableContracts.TryGetValue(source, out var contracts)) formal.CallableContracts[target] = contracts;
            if (caller.Definitions.TryGetValue(source, out var definition)) formal.Definitions[target] = definition;
            else if (owner.CanonicalDefinition(source) is { } canonical) formal.Definitions[target] = canonical;
            return target;
        }
    }

}
