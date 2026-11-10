using System.Collections.Immutable;
using Msync.Compiler.Semantics;
using Msync.Hir;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class SemanticObservationBuilder
{
    private sealed record ProjectedFacts(
        ImmutableDictionary<HirLocationId, SemanticValueId> Values,
        ImmutableDictionary<HirLocationId, HirLocationId> Locations)
    {
        public SemanticBindingMap Bindings(ImmutableDictionary<string, HirLocationId> names,
            ImmutableDictionary<HirLocationId, SemanticBindingView> declarations) =>
            new(names, Values, Locations, declarations);
    }

    private sealed record PendingVisibility(BoundVisibility Visibility,
        ProjectedFacts Facts, bool Concrete);
    private sealed record PendingImport(SourceSpan Span,
        ImmutableDictionary<string, HirLocationId> Bindings, ProjectedFacts Facts, bool Concrete);

    private ProjectedFacts Snapshot(SemanticContext context)
    {
        if (!states.TryGetValue(context, out var state))
        {
            state = new(this, context);
            states.Add(context, state);
        }
        return state.Capture();
    }

    private readonly record struct ResourceDependency(int Region, bool Indexed = false);

    private sealed class ObservationState
    {
        private readonly SemanticObservationBuilder owner;
        private readonly SemanticContext context;
        private readonly HashSet<HirLocationId> dirty = [];
        private readonly HashSet<ResourceDependency> dirtyRegions = [];
        private readonly Dictionary<ResourceDependency, HashSet<HirLocationId>> dependents = [];
        private readonly Dictionary<HirLocationId, HashSet<ResourceDependency>> dependencies = [];
        private ImmutableDictionary<HirLocationId, SemanticValueId> values
            = ImmutableDictionary<HirLocationId, SemanticValueId>.Empty;
        private ImmutableDictionary<HirLocationId, HirLocationId> locations;

        public ObservationState(SemanticObservationBuilder owner, SemanticContext context)
        {
            this.owner = owner;
            this.context = context;
            locations = context.Locations.ToImmutableDictionary();
            // Branches can share an outer row while viewing different nested regions.
            owner.valueIds.Clear();
            foreach (var pair in context.Values) Project(pair.Key, pair.Value);
            context.Values.ObserveChanges((location, _) => dirty.Add(location));
            context.Objects.ObserveChanges((region, _) => ChangedRegion(new(region)));
            context.Indexed.ObserveChanges((region, _) => ChangedRegion(new(region, Indexed: true)));
            context.Locations.ObserveChanges((source, target) => locations = locations.SetItem(source, target));
        }

        private void ChangedRegion(ResourceDependency region)
        {
            dirtyRegions.Add(region);
            // A nested object can change while its enclosing immutable row stays
            // identical. Reusing that row's previous projection would be stale.
            owner.valueIds.Clear();
        }

        public ProjectedFacts Capture()
        {
            foreach (var region in dirtyRegions)
                if (dependents.TryGetValue(region, out var affected)) dirty.UnionWith(affected);
            foreach (var location in dirty)
                if (context.Values.TryGetValue(location, out var value)) Project(location, value);
            dirty.Clear();
            dirtyRegions.Clear();
            return new(values, locations);
        }

        private void Project(HirLocationId location, SemanticValue value)
        {
            values = values.SetItem(location, owner.Value(value, context));
            if (dependencies.TryGetValue(location, out var previous))
                foreach (var region in previous) dependents[region].Remove(location);
            if (IndexedResourceCount(value) == 0 && value.Scope is null && value.Element is null
                && (value.Callables.IsDefaultOrEmpty || value.Callables.All(callable => callable.Captures.IsDefaultOrEmpty)))
            {
                dependencies.Remove(location);
                return;
            }
            HashSet<ResourceDependency> regions = [];
            HashSet<SemanticValue> visited = new(ReferenceEqualityComparer.Instance);
            Collect(value, regions, visited);
            if (regions.Count == 0) { dependencies.Remove(location); return; }
            dependencies[location] = regions;
            foreach (var region in regions)
            {
                if (!dependents.TryGetValue(region, out var affected)) dependents.Add(region, affected = []);
                affected.Add(location);
            }
        }

        private void Collect(SemanticValue value, HashSet<ResourceDependency> regions, HashSet<SemanticValue> visited)
        {
            if (!visited.Add(value)) return;
            if (value.Scope is { } scope)
            {
                if (scope.Region >= 0 && !regions.Add(new(scope.Region))) return;
                scope = context.Objects.GetValueOrDefault(scope.Region, scope);
                foreach (var member in scope.Fields.Values) Collect(member, regions, visited);
            }
            int indexed = IndexedResourceCount(value);
            for (int index = 0; index < indexed; index++)
            {
                int region = IndexedResourceAt(value, index);
                if (regions.Add(new(region, Indexed: true))) Collect(context.Indexed[region].Element, regions, visited);
            }
            if (indexed == 0 && value.Element is { } element) Collect(element, regions, visited);
            CollectCaptures(value.Callables, regions, visited);
        }

        private void CollectCaptures(ImmutableArray<SemanticCallable> callables,
            HashSet<ResourceDependency> regions, HashSet<SemanticValue> visited)
        {
            if (callables.IsDefaultOrEmpty) return;
            foreach (var callable in callables)
            {
                if (callable.Captures.IsDefaultOrEmpty) continue;
                foreach (var capture in callable.Captures) Collect(capture.Value, regions, visited);
            }
        }

        public void Detach()
        {
            context.Values.ObserveChanges(null);
            context.Objects.ObserveChanges(null);
            context.Indexed.ObserveChanges(null);
            context.Locations.ObserveChanges(null);
        }
    }
}
