using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    // Closed source has all candidate definitions. Its type obligation therefore
    // needs no runtime dispatch slot and uses the same isolated signature checker.
    private HirTypeId? CheckClosedFutureCall(SemanticValue callee, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context)
    {
        if (program.IsSession || !context.Publish) return null;
        HirLocationId? location = callee.Location ?? callee.FutureCallableDomain?.Owner;
        if (location is not { } owner) return null;
        owner = StorageOwner(owner, context);
        foreach (SessionCallRequirement existing in context.ClosedCallRequirements.GetValueOrDefault(owner, []))
            if (SameCallArguments(existing, arguments)) return existing.Result;
        SemanticValue enriched = EnrichFutureDomains(callee, context, owner, rootStorage: true);
        if (enriched.FutureCallableDomain is not { } domain) return null;
        var current = domain.Current.IsDefault ? callee.Callables : domain.Current;
        if (current.IsDefaultOrEmpty || domain.Choices.All(candidate =>
            current.Contains(candidate, SemanticCallableIdentity.Instance))) return null;
        var query = QueryContext(context, callee, arguments);
        query.DeferredReads = true;
        SemanticValue initial = enriched with { Node = HirNodeId.None, Callables = current, FutureCallableDomain = null };
        SemanticValue promise = InvokeType(initial, arguments, source, query);
        var requirement = CreateCallRequirement(-1, new(StorageOwner(owner, context), []),
            arguments, promise.Type, source, context);
        context.ClosedCallRequirements[owner] = context.ClosedCallRequirements.GetValueOrDefault(owner, []).Add(requirement);
        foreach (SemanticCallable candidate in domain.Choices)
        {
            if (current.Contains(candidate, SemanticCallableIdentity.Instance)) continue;
            HirSourceRange definition = candidate.Template is { } template ? hir.Templates[template.Value].Source : source;
            SemanticValue target = new(hir.Type(TypeRef.Function), HirNodeId.None, Callables: [candidate]);
            _ = CheckSessionTarget(requirement, target, definition, context, publish: false, allowImplicitReturn: true);
        }
        return promise.Type;
    }

    private bool SameCallArguments(SessionCallRequirement requirement, IReadOnlyList<SemanticValue> arguments)
    {
        if (requirement.Arguments.Length != arguments.Count) return false;
        for (int index = 0; index < arguments.Count; index++)
            if (TypeSchemaKey(requirement.Arguments[index]) != TypeSchemaKey(arguments[index])) return false;
        return true;
    }

    private void CheckClosedAssignment(HirLocationId location, SemanticValue value, HirSourceRange source,
        SemanticContext context)
    {
        if (program.IsSession) return;
        foreach (SessionCallRequirement requirement in context.ClosedCallRequirements.GetValueOrDefault(StorageOwner(location, context), []))
            _ = CheckSessionTarget(requirement, value, source, context, publish: false, allowImplicitReturn: true);
    }

    private void CheckClosedMemberAssignment(SemanticScope scope, string field, SemanticValue value,
        HirSourceRange source, SemanticContext context)
    {
        if (scope.Locations.TryGetValue(field, out HirLocationId location)) CheckClosedAssignment(location, value, source, context);
    }
}
