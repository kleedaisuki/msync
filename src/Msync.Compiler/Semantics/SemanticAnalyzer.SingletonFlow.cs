using System.Collections.Immutable;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private void MergeSingletonFlow(SemanticContext destination, SemanticContext[] branches)
    {
        if (branches.All(branch => branch.WriteEvents is null && branch.InitializedSingletons.IsEmpty
            && branch.MaybeInitializedSingletons.IsEmpty)) return;
        var definite = branches[0].InitializedSingletons;
        var possible = branches[0].MaybeInitializedSingletons.Union(definite);
        foreach (var branch in branches.AsSpan(1))
        {
            definite = definite.Intersect(branch.InitializedSingletons);
            possible = possible.Union(branch.MaybeInitializedSingletons).Union(branch.InitializedSingletons);
        }
        List<SemanticWriteEvent>? events = MergeSingletonEvents(branches);
        Dictionary<SemanticWriteTarget, HashSet<int>> targets = [];
        foreach (var branch in branches)
        {
            foreach (var pair in branch.SingletonWriteFacts)
            {
                if (!targets.TryGetValue(pair.Key, out var related)) targets.Add(pair.Key, related = []);
                related.UnionWith(pair.Value.Keys);
            }
            if (branch.WriteEvents is { } writes) CollectTargets(writes, branch, targets);
        }
        // Branches may borrow destination facts through an overlay. Capture all
        // conditional values before changing that shared ancestor.
        Dictionary<SemanticWriteTarget, ImmutableDictionary<int, SemanticConditionalFact>> facts = [];
        foreach (var (target, related) in targets)
        {
            var perSingleton = ImmutableDictionary.CreateBuilder<int, SemanticConditionalFact>();
            foreach (int singleton in related)
            {
                if (!possible.Contains(singleton)) continue;
                SemanticValue? initialized = null, uninitialized = null;
                foreach (var branch in branches)
                {
                    var current = branch.ReadWriteTarget(target);
                    if (current is null) continue;
                    var conditional = branch.SingletonWriteFacts.GetValueOrDefault(target)?.GetValueOrDefault(singleton);
                    if (branch.InitializedSingletons.Contains(singleton))
                        initialized = Merge(initialized, current);
                    else
                    {
                        uninitialized = Merge(uninitialized, conditional?.WhenNotInitialized ?? current);
                        if (branch.MaybeInitializedSingletons.Contains(singleton))
                            initialized = Merge(initialized, conditional?.WhenInitialized ?? current);
                    }
                }
                if (initialized is not null || uninitialized is not null)
                    perSingleton.Add(singleton, new(initialized, uninitialized));
            }
            facts.Add(target, perSingleton.ToImmutable());
        }
        destination.InitializedSingletons = definite;
        destination.MaybeInitializedSingletons = possible;
        destination.WriteEvents = events;
        foreach (var pair in facts) destination.SingletonWriteFacts[pair.Key] = pair.Value;

        SemanticValue Merge(SemanticValue? before, SemanticValue after) => before is null ? after : Join(before, after);
    }

    private static List<SemanticWriteEvent>? MergeSingletonEvents(SemanticContext[] branches)
    {
        if (branches.All(branch => branch.WriteEvents is null)) return null;
        var first = branches[0].WriteEvents;
        int prefix = first?.Count ?? 0;
        foreach (var branch in branches.AsSpan(1))
        {
            prefix = Math.Min(prefix, branch.WriteEvents?.Count ?? 0);
            for (int index = 0; index < prefix; index++)
                if (!ReferenceEquals(first![index], branch.WriteEvents![index])) { prefix = index; break; }
        }
        List<SemanticWriteEvent> result = first is null ? [] : first.GetRange(0, prefix);
        var alternatives = branches.Select(branch => branch.WriteEvents is { } events
            ? events.Skip(prefix).ToImmutableArray() : ImmutableArray<SemanticWriteEvent>.Empty).ToImmutableArray();
        if (alternatives.Any(alternative => !alternative.IsEmpty)) result.Add(new(null, [], Branches: alternatives));
        return result;
    }

    private static void CollectTargets(IEnumerable<SemanticWriteEvent> events, SemanticContext context,
        Dictionary<SemanticWriteTarget, HashSet<int>> targets)
    {
        foreach (var item in events)
        {
            if (item.Write is { } write && !item.Guards.IsDefaultOrEmpty)
            {
                var target = context.WriteTarget(write);
                if (!targets.TryGetValue(target, out var related)) targets.Add(target, related = []);
                related.UnionWith(item.Guards);
            }
            if (!item.Branches.IsDefault)
                foreach (var branch in item.Branches) CollectTargets(branch, context, targets);
        }
    }
}
