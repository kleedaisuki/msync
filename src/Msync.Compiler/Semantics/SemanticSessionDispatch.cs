using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed record SessionCallableSource(HirLocationId Root, ImmutableArray<string> Fields);

internal sealed record SessionCallRequirement(int Slot, SessionCallableSource Source,
    ImmutableArray<SemanticValue> Arguments, ImmutableArray<HirLocationId?> ArgumentSources,
    HirTypeId Result, HirSourceRange Origin,
    ImmutableDictionary<int, SemanticScope> Objects,
    ImmutableDictionary<int, SemanticIndexed> Indexed,
    ImmutableDictionary<HirLocationId, SemanticValue> Cells,
    ImmutableDictionary<HirLocationId, HirTypeId> Storage,
    ImmutableArray<HirDispatchTarget> Targets, ImmutableArray<SemanticDeferredWrite> Effects);

// A generation owns its additions and target updates. These are typed binding
// obligations, not a saved caller context or a runtime compiler callback.
internal sealed class SemanticSessionDispatch
{
    internal List<SessionCallRequirement> Calls { get; } = [];

    internal SemanticSessionDispatch Fork()
    {
        SemanticSessionDispatch result = new();
        result.Calls.AddRange(Calls);
        return result;
    }

    internal ImmutableArray<HirSessionDispatch> Tables => [.. Calls.Select(call =>
        new HirSessionDispatch(call.Slot, [.. call.Arguments.Select(argument => argument.Type)], call.Result, call.Targets))];
}

internal sealed partial class SemanticAnalyzer
{
    private bool refreshingSessionCalls;

    private int OpenSessionCall(SemanticValue callee, IReadOnlyList<SemanticValue> arguments,
        HirTypeId result, HirSourceRange source, SemanticContext context)
    {
        if (sessionDispatch is null || !context.Publish || !context.DeferredReads
            || SessionSource(callee, context) is not { } address) return -1;
        if (address.Fields.IsDefaultOrEmpty && Stable(address.Root)) return -1;

        SemanticContext prototype = new(new(-1));
        FormalInput input = new(this, context, prototype);
        var values = arguments.Select(input.Abstract).ToImmutableArray();
        input.Complete();
        int slot = sessionDispatch.Calls.Count;
        sessionDispatch.Calls.Add(new(slot, address, values,
            [.. arguments.Select(argument => argument.Location is { } location ? StorageOwner(location, context) : (HirLocationId?)null)],
            result, source, prototype.Objects.ToImmutableDictionary(), prototype.Indexed.ToImmutableDictionary(),
            prototype.Values.ToImmutableDictionary(), prototype.Storage.ToImmutableDictionary(), [], []));
        RefreshSessionCall(slot, context, callee);
        return slot;
    }

    private SessionCallableSource? SessionSource(SemanticValue callee, SemanticContext context)
    {
        if (!callee.Node.IsNone && Source(callee.Node) is { } expression) return expression;
        return callee.Location is { } location
            ? new(StorageOwner(location, context), []) : null;

        SessionCallableSource? Source(HirNodeId id)
        {
            HirNode node = hir.Nodes[id.Value];
            if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
                return new(StorageOwner(new(node.Data), context), []);
            if (node.Kind != HirKind.MemberRead || node.Text is null || node.Children.Count == 0) return null;
            var root = Source(hir.Children(node)[0]);
            return root is null ? null : root with { Fields = root.Fields.Add(node.Text) };
        }
    }

    private SemanticValue? SessionCallable(SessionCallableSource source, SemanticContext context)
    {
        SemanticValue? value = StorageValue(source.Root, context);
        foreach (string field in source.Fields)
        {
            if (value?.Scope is not { } scope) return null;
            scope = context.Objects.GetValueOrDefault(scope.Region, scope);
            value = scope.Fields.GetValueOrDefault(field);
            if (scope.Locations.TryGetValue(field, out var location)) value = StorageValue(location, context) ?? value;
        }
        return value;
    }

    private void RefreshSessionCalls(SemanticContext context)
    {
        if (sessionDispatch is null || !context.Publish || refreshingSessionCalls) return;
        refreshingSessionCalls = true;
        try
        {
            for (int slot = 0; slot < sessionDispatch.Calls.Count; slot++) RefreshSessionCall(slot, context);
        }
        finally { refreshingSessionCalls = false; }
    }

    private void CheckSessionAssignment(HirLocationId location, SemanticValue value,
        HirSourceRange source, SemanticContext context)
    {
        if (sessionDispatch is null || refreshingSessionCalls) return;
        var owner = StorageOwner(location, context);
        for (int slot = 0; slot < sessionDispatch.Calls.Count; slot++)
        {
            var requirement = sessionDispatch.Calls[slot];
            if (requirement.Source.Root != owner) continue;
            var target = NestedSessionValue(value, requirement.Source.Fields, context);
            if (target is not null) _ = CheckSessionTarget(requirement, target, source, context, publish: context.Publish);
        }
    }

    private SemanticValue? NestedSessionValue(SemanticValue? value, ImmutableArray<string> fields, SemanticContext context)
    {
        foreach (string field in fields)
        {
            if (value?.Scope is not { } scope) return null;
            scope = context.Objects.GetValueOrDefault(scope.Region, scope);
            value = scope.Fields.GetValueOrDefault(field);
            if (scope.Locations.TryGetValue(field, out var location)) value = StorageValue(location, context) ?? value;
        }
        return value;
    }

    private void CheckSessionMemberAssignment(SemanticScope scope, string field, SemanticValue value,
        HirSourceRange source, SemanticContext context)
    {
        if (sessionDispatch is null || refreshingSessionCalls) return;
        foreach (var requirement in sessionDispatch.Calls)
        {
            var path = requirement.Source.Fields;
            if (path.IsDefaultOrEmpty || path[^1] != field) continue;
            var parent = NestedSessionValue(StorageValue(requirement.Source.Root, context), path.RemoveAt(path.Length - 1), context);
            if (parent?.Scope is not { } receiver || !ScopeAliases(receiver).Intersect(ScopeAliases(scope)).Any()) continue;
            _ = CheckSessionTarget(requirement, value, source, context, publish: context.Publish);
        }
    }

    private static ImmutableArray<int> AddSessionCall(ImmutableArray<int> calls, int slot) =>
        calls.IsDefaultOrEmpty ? [slot] : calls.Contains(slot) ? calls : calls.Add(slot);

    private static ImmutableArray<int> CombineSessionCalls(ImmutableArray<int> first, ImmutableArray<int> second) =>
        first.IsDefaultOrEmpty ? second.IsDefault ? [] : second
            : second.IsDefaultOrEmpty ? first : [.. first.Concat(second).Distinct()];

    private void ApplySessionCallEffects(ImmutableArray<int> slots, SemanticContext context)
    {
        if (sessionDispatch is null || slots.IsDefaultOrEmpty) return;
        foreach (int slot in slots)
        {
            if (context.Publish) RefreshSessionCall(slot, context);
            ApplyDeferredEffects(sessionDispatch.Calls[slot].Effects, context);
        }
    }

    private (ImmutableArray<HirDispatchTarget> Targets, ImmutableArray<SemanticDeferredWrite> Effects) CheckSessionTarget(SessionCallRequirement requirement,
        SemanticValue callee, HirSourceRange source, SemanticContext context, bool publish)
    {
        SemanticContext target = new(context.Function, publish ? context.Output : null, context)
        { Queries = context.Queries, DeferredReads = true };
        foreach (var item in requirement.Objects) target.Objects[item.Key] = item.Value;
        foreach (var item in requirement.Indexed) target.Indexed[item.Key] = item.Value;
        foreach (var item in requirement.Cells) target.Values[item.Key] = item.Value;
        foreach (var item in requirement.Storage) target.Storage[item.Key] = item.Value;
        SemanticValue[] arguments = requirement.Arguments.ToArray();
        for (int index = 0; index < arguments.Length; index++)
            if (requirement.ArgumentSources[index] is { } location && StorageValue(location, context) is { } current)
                arguments[index] = FutureRead(current, StorageType(location, context), target, location);
        ImmutableArray<HirDispatchTarget>.Builder dispatch = ImmutableArray.CreateBuilder<HirDispatchTarget>();
        ImmutableArray<SemanticDeferredWrite>.Builder effects = ImmutableArray.CreateBuilder<SemanticDeferredWrite>();
        foreach (SemanticCallable choice in Choices(callee))
        {
            SemanticValue result;
            if (choice.Template is { } template)
            {
                var invocation = Instantiate(template, choice.Captures, arguments, source, target, applyEffects: false);
                result = invocation.Result;
                effects.AddRange(invocation.Effects);
                if (publish) dispatch.Add(new(HirCallableId.From(template), invocation.Id));
            }
            else result = InvokeType(new(hir.Type(TypeRef.Function), HirNodeId.None, Callables: [choice]), arguments, source, target);
            Require(result, requirement.Result, source, context: target);
        }
        return (dispatch.ToImmutable(), effects.ToImmutable());
    }

    private void RefreshSessionCall(int slot, SemanticContext context, SemanticValue? initial = null)
    {
        var requirement = sessionDispatch!.Calls[slot];
        SemanticValue? callee = initial ?? SessionCallable(requirement.Source, context);
        if (callee is null) return;
        var (targets, effects) = CheckSessionTarget(requirement, callee, requirement.Origin, context, publish: true);
        var combined = requirement.Targets.ToDictionary(target => target.Identity, target => target.Target);
        foreach (var target in targets) combined[target.Identity] = target.Target;
        sessionDispatch.Calls[slot] = requirement with
        { Targets = [.. combined.Select(pair => new HirDispatchTarget(pair.Key, pair.Value))], Effects = effects };
    }
}
