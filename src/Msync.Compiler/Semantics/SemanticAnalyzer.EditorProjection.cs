using System.Collections.Immutable;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    // Literal payload and source provenance belong to this query, never the type cache.
    internal SemanticValue ProjectBoundResult(HirNodeId id, Func<HirLocationId, SemanticValue> bindings)
    {
        SemanticContext context = new(program.Entry) { Queries = queries, QueryBinding = location => ObservationBinding(location, bindings(location)) };
        HirFlowTypeAlgebra algebra = new(hir);
        FlowFacts facts = FlowFacts.Create(ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty, algebra);
        List<ReturnShape> returns = [];
        ReturnShape result = ProjectReturn(id, context, ref facts, returns);
        foreach (ReturnShape returned in returns) result = JoinShape(result, returned, algebra, observe: true);
        HirTypeId type = result.Fact.SemanticType ?? hir.Type(result.Fact.Type);
        return result.Value is { } value ? value with { Type = type } : new(type, id);
    }

    private SemanticValue ObservationBinding(HirLocationId location, SemanticValue value)
    {
        location = program.Original(location);
        bool dynamic = program.WrittenLocations.Contains(location)
            || program.TryDeclaration(location, out var declaration)
                && declaration.Kind == DeclarationKind.Field && program.WrittenMembers.Contains(declaration.Name)
            || program.IsSession && program.Exports.Values.Contains(location);
        return dynamic ? value with { RuntimeDependentMetadata = true } : value;
    }
}
