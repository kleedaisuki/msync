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
    ImmutableArray<HirDispatchTarget> Targets);

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

    private static void EstablishSessionContract(HirLocationId location, int slot, SemanticContext context)
    {
        var contracts = context.CallableContracts.GetValueOrDefault(location, []);
        if (!contracts.Contains(slot)) context.CallableContracts[location] = contracts.Add(slot);
        var established = context.EstablishedCallableContracts.GetValueOrDefault(location, []);
        if (!established.Contains(slot)) context.EstablishedCallableContracts[location] = established.Add(slot);
    }

    private static void MergeSessionContracts(SemanticContext destination, IEnumerable<SemanticContext> branches)
    {
        foreach (var branch in branches)
        {
            foreach (var (location, slots) in branch.CallableContracts.LocalEntries)
                destination.CallableContracts[location] = [.. destination.CallableContracts.GetValueOrDefault(location, [])
                    .Concat(slots).Distinct()];
            foreach (var (location, slots) in branch.EstablishedCallableContracts.LocalEntries)
                destination.EstablishedCallableContracts[location] = [.. destination.EstablishedCallableContracts.GetValueOrDefault(location, [])
                    .Concat(slots).Distinct()];
            foreach (var (location, requirements) in branch.ClosedCallRequirements.LocalEntries)
                destination.ClosedCallRequirements[location] = [.. destination.ClosedCallRequirements.GetValueOrDefault(location, [])
                    .Concat(requirements).Distinct()];
        }
    }

    private SessionCallBinding SessionBinding(int slot, SemanticValue callee,
        IReadOnlyList<SemanticValue> arguments, SemanticContext context) => new(slot,
        SessionSource(callee, context)!, [.. arguments.Select(argument => SessionSource(argument, context))]);

    private int OpenSessionCall(SemanticValue callee, IReadOnlyList<SemanticValue> arguments,
        HirTypeId result, HirSourceRange source, SemanticContext context)
    {
        if (sessionDispatch is null || !context.Publish || !context.DeferredReads
            || SessionSource(callee, context) is not { } address) return -1;
        if (address.Fields.IsDefaultOrEmpty && Stable(address.Root)) return -1;

        int slot = sessionDispatch.Calls.Count;
        sessionDispatch.Calls.Add(CreateCallRequirement(slot, address, arguments, result, source, context));
        if (callee.Location is { } location) EstablishSessionContract(StorageOwner(location, context), slot, context);
        RefreshSessionCall(slot, context, callee);
        return slot;
    }

    private SessionCallRequirement CreateCallRequirement(int slot, SessionCallableSource address,
        IReadOnlyList<SemanticValue> arguments, HirTypeId result, HirSourceRange source, SemanticContext context)
    {
        SemanticContext prototype = new(new(-1));
        FormalInput input = new(this, context, prototype);
        var values = arguments.Select(input.Abstract).ToImmutableArray();
        input.Complete();
        return new(slot, address, values,
            [.. arguments.Select(argument => argument.Location is { } location ? StorageOwner(location, context) : (HirLocationId?)null)],
            result, source, prototype.Objects.ToImmutableDictionary(), prototype.Indexed.ToImmutableDictionary(),
            prototype.Values.ToImmutableDictionary(), prototype.Storage.ToImmutableDictionary(), []);
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
        HashSet<int> checkedSlots = [];
        foreach (int slot in context.CallableContracts.GetValueOrDefault(owner, []))
        {
            var requirement = sessionDispatch.Calls[slot];
            var (targets, _) = CheckSessionTarget(requirement, value, source, context, publish: context.Publish);
            if (context.Publish) MergeSessionTargets(slot, targets);
            checkedSlots.Add(slot);
        }
        for (int slot = 0; slot < sessionDispatch.Calls.Count; slot++)
        {
            if (checkedSlots.Contains(slot)) continue;
            var requirement = sessionDispatch.Calls[slot];
            if (requirement.Source.Root != owner) continue;
            var target = NestedSessionValue(value, requirement.Source.Fields, context);
            if (target is not null)
            {
                var (targets, _) = CheckSessionTarget(requirement, target, source, context, publish: context.Publish);
                if (context.Publish) MergeSessionTargets(slot, targets);
            }
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
        if (scope.Locations.TryGetValue(field, out var location)) CheckSessionAssignment(location, value, source, context);
        foreach (var requirement in sessionDispatch.Calls)
        {
            var path = requirement.Source.Fields;
            if (path.IsDefaultOrEmpty || path[^1] != field) continue;
            var parent = NestedSessionValue(StorageValue(requirement.Source.Root, context), path.RemoveAt(path.Length - 1), context);
            if (parent?.Scope is not { } receiver || !ScopeAliases(receiver).Intersect(ScopeAliases(scope)).Any()) continue;
            var (targets, _) = CheckSessionTarget(requirement, value, source, context, publish: context.Publish);
            if (context.Publish) MergeSessionTargets(requirement.Slot, targets);
        }
    }

    private static ImmutableArray<SessionCallBinding> AddSessionCall(ImmutableArray<SessionCallBinding> calls, SessionCallBinding binding) =>
        calls.IsDefaultOrEmpty ? [binding] : calls.Contains(binding) ? calls : calls.Add(binding);

    private static ImmutableArray<SessionCallBinding> CombineSessionCalls(ImmutableArray<SessionCallBinding> first, ImmutableArray<SessionCallBinding> second) =>
        first.IsDefaultOrEmpty ? second.IsDefault ? [] : second
            : second.IsDefaultOrEmpty ? first : [.. first.Concat(second).Distinct()];

    private void ApplySessionCallEffects(ImmutableArray<SessionCallBinding> bindings, SemanticContext context)
    {
        if (sessionDispatch is null || bindings.IsDefaultOrEmpty) return;
        foreach (var binding in bindings)
        {
            var requirement = sessionDispatch.Calls[binding.Slot];
            if (SessionCallable(binding.Callee, context) is not { } callee) continue;
            var (_, effects) = CheckSessionTarget(requirement, callee, requirement.Origin, context,
                publish: context.Publish, argumentBindings: binding.Arguments);
            ApplyDeferredEffects(effects, context);
        }
    }

    private (ImmutableArray<HirDispatchTarget> Targets, ImmutableArray<SemanticDeferredWrite> Effects) CheckSessionTarget(SessionCallRequirement requirement,
        SemanticValue callee, HirSourceRange source, SemanticContext context, bool publish,
        ImmutableArray<SessionCallableSource?> argumentBindings = default, bool allowImplicitReturn = false)
    {
        if (Runtime(callee).Nullable)
            throw Error(source, "A deferred callable must remain non-null for its established signature.", "E0201");
        if (Choices(callee).Any() is false)
            throw Error(source, "The assigned value has no resolved callable definition for the established signature.", "E0213");
        SemanticContext target = new(context.Function, publish ? context.Output : null, context)
        { Queries = context.Queries, DeferredReads = true };
        foreach (var item in requirement.Objects) target.Objects[item.Key] = item.Value;
        foreach (var item in requirement.Indexed) target.Indexed[item.Key] = item.Value;
        foreach (var item in requirement.Cells) target.Values[item.Key] = item.Value;
        foreach (var item in requirement.Storage) target.Storage[item.Key] = item.Value;
        SemanticValue[] arguments = requirement.Arguments.ToArray();
        for (int index = 0; index < arguments.Length; index++)
        {
            var argumentSource = !argumentBindings.IsDefault ? argumentBindings[index]
                : requirement.ArgumentSources[index] is { } root ? new SessionCallableSource(root, []) : null;
            if (argumentSource is not null && SessionCallable(argumentSource, context) is { } current)
                arguments[index] = FutureRead(current, current.Type, target, current.Location);
        }
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
                if (publish) dispatch.Add(new(choice.RuntimeIdentity ?? HirCallableId.From(template), invocation.Id));
            }
            else result = InvokeType(new(hir.Type(TypeRef.Function), HirNodeId.None, Callables: [choice]), arguments, source, target);
            try { Require(result, requirement.Result, source, allowImplicit: allowImplicitReturn, context: target); }
            catch (CompileException error)
            {
                var document = hir.Sources[source.Source.Value];
                var previous = hir.Sources[requirement.Origin.Source.Value];
                var mismatch = new CompileException(
                    $"This function returns {Runtime(result)}, but the existing call requires {hir.Types[requirement.Result.Value].RuntimeType}.",
                    document.Text ?? "", document.Name, new(source.Start, source.Length),
                    label: "incompatible callable result", help: "Keep the existing parameter and return contract, or declare a new binding for a different callable protocol.",
                    note: error.Diagnostic.Message, code: "E0213");
                mismatch.Diagnostic.Related.Add(new(previous.Name, previous.Text ?? "",
                    new(requirement.Origin.Start, requirement.Origin.Length), "this call established the required signature"));
                throw mismatch;
            }
        }
        return (dispatch.ToImmutable(), effects.ToImmutable());
    }

    private void RefreshSessionCall(int slot, SemanticContext context, SemanticValue? initial = null)
    {
        var requirement = sessionDispatch!.Calls[slot];
        SemanticValue? callee = initial ?? SessionCallable(requirement.Source, context);
        if (callee is null) return;
        var (targets, _) = CheckSessionTarget(requirement, callee, requirement.Origin, context, publish: true);
        MergeSessionTargets(slot, targets);
    }

    private void MergeSessionTargets(int slot, ImmutableArray<HirDispatchTarget> targets)
    {
        var requirement = sessionDispatch!.Calls[slot];
        var combined = requirement.Targets.ToDictionary(target => target.Identity, target => target.Target);
        foreach (var target in targets) combined[target.Identity] = target.Target;
        sessionDispatch.Calls[slot] = requirement with
        { Targets = [.. combined.Select(pair => new HirDispatchTarget(pair.Key, pair.Value))] };
    }
}
