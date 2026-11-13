using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed record SessionCallableSource(HirLocationId Root, ImmutableArray<string> Fields);
internal sealed record SessionCallBinding(int Slot, SessionCallableSource Callee,
    ImmutableArray<SessionCallableSource?> Arguments);

internal sealed record SessionCallRequirement(int Slot, SessionCallableSource Source,
    ImmutableArray<SemanticValue> Arguments, ImmutableArray<HirLocationId?> ArgumentSources,
    HirTypeId Result, HirSourceRange Origin,
    ImmutableDictionary<int, SemanticScope> Objects,
    ImmutableDictionary<int, SemanticIndexed> Indexed,
    ImmutableDictionary<HirLocationId, SemanticValue> Cells,
    ImmutableDictionary<HirLocationId, HirTypeId> Storage,
    ImmutableDictionary<HirLocationId, HirLocationId> Definitions,
    ImmutableArray<HirDispatchTarget> Targets);

// A generation owns its additions and target updates. These are typed binding
// obligations, not a saved caller context or a runtime compiler callback.
internal sealed class SemanticSessionDispatch
{
    internal List<SessionCallRequirement> Calls { get; } = [];
    private ImmutableDictionary<HirLocationId, ImmutableArray<int>> roots = ImmutableDictionary<HirLocationId, ImmutableArray<int>>.Empty;
    private ImmutableDictionary<string, ImmutableArray<int>> members = ImmutableDictionary<string, ImmutableArray<int>>.Empty;
    private ImmutableHashSet<int> refresh = ImmutableHashSet<int>.Empty;

    internal void Add(SessionCallRequirement requirement)
    {
        Calls.Add(requirement);
        refresh = refresh.Add(requirement.Slot);
        var source = requirement.Source;
        roots = roots.SetItem(source.Root, roots.GetValueOrDefault(source.Root, []).Add(requirement.Slot));
        if (!source.Fields.IsDefaultOrEmpty)
        {
            string name = source.Fields[^1];
            members = members.SetItem(name, members.GetValueOrDefault(name, []).Add(requirement.Slot));
        }
    }

    internal ImmutableArray<int> ForRoot(HirLocationId root) => roots.GetValueOrDefault(root, []);
    internal ImmutableArray<int> ForMember(string name) => members.GetValueOrDefault(name, []);
    internal IEnumerable<int> RefreshSlots => refresh;
    internal void RequireRefresh(int slot) => refresh = refresh.Add(slot);
    internal void CertifyDirectRoot(int slot) => refresh = refresh.Remove(slot);

    internal SemanticSessionDispatch Fork()
    {
        SemanticSessionDispatch result = new() { roots = roots, members = members, refresh = refresh };
        result.Calls.AddRange(Calls);
        return result;
    }

    internal void Reconcile() => refresh = [.. Enumerable.Range(0, Calls.Count)];

    internal void ObserveRoot(HirLocationId location, SemanticValue before, SemanticValue after)
    {
        var slots = ForRoot(location);
        if (slots.IsDefaultOrEmpty || SameCallable(before, after)) return;
        refresh = refresh.Union(slots);

        static bool SameCallable(SemanticValue before, SemanticValue after) =>
            before.Type == after.Type
            && !before.Callables.IsDefaultOrEmpty && !after.Callables.IsDefaultOrEmpty
            && before.Callables is [{ Template: { } a, Captures.IsDefaultOrEmpty: true } first]
            && after.Callables is [{ Template: { } b, Captures.IsDefaultOrEmpty: true } second]
            && a == b
            && (first.RuntimeIdentity ?? HirCallableId.From(a)) == (second.RuntimeIdentity ?? HirCallableId.From(b));
    }

    internal ImmutableArray<HirSessionDispatch> Tables => [.. Calls.Select(call =>
        new HirSessionDispatch(call.Slot, [.. call.Arguments.Select(argument => argument.Type)], call.Result, call.Targets))];
}
