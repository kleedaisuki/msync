using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal readonly record struct FlowBlockId(int Value);
internal enum FlowOperationKind : byte { Value, Copy, Expression, RangeElement, UnknownWrite, MemoryWrite }

internal readonly record struct FlowOperation(FlowOperationKind Kind, HirLocationId Target,
    FlowFact Value = default, HirLocationId Source = default, FlowMemoryRegion? Memory = null,
    FlowReadKind Read = FlowReadKind.Immediate, HirNodeId Expression = default)
{
    public static FlowOperation Assign(HirLocationId target, TypeRef type, bool? boolean = null) =>
        new(FlowOperationKind.Value, target, new(type, true, boolean));
    public static FlowOperation Copy(HirLocationId target, HirLocationId source,
        FlowReadKind read = FlowReadKind.Immediate) => new(FlowOperationKind.Copy, target, Source: source, Read: read);
    public static FlowOperation Write(HirLocationId target) => new(FlowOperationKind.UnknownWrite, target);
    public static FlowOperation Evaluate(HirLocationId target, HirNodeId expression) =>
        new(FlowOperationKind.Expression, target, Expression: expression);
    public static FlowOperation Element(HirLocationId target, HirNodeId source) =>
        new(FlowOperationKind.RangeElement, target, Expression: source);
    public static FlowOperation ElementOf(HirLocationId target, HirLocationId source) =>
        new(FlowOperationKind.RangeElement, target, Source: source, Expression: HirNodeId.None);
    public static FlowOperation WriteMemory(FlowMemoryRegion? region = null) =>
        new(FlowOperationKind.MemoryWrite, default, Memory: region);

    public FlowFacts Apply(FlowFacts facts, Func<HirNodeId, FlowFacts, FlowFact>? evaluate = null) => Kind switch
    {
        FlowOperationKind.Value => facts.Assign(Target, Value),
        FlowOperationKind.Copy => facts.Assign(Target, facts.Read(Source, Read)),
        FlowOperationKind.Expression => facts.Assign(Target, evaluate!(Expression, facts)),
        FlowOperationKind.RangeElement => facts.Assign(Target, new((Expression.IsNone
            ? facts.Read(Source).Type : evaluate!(Expression, facts).Type).Element ?? TypeRef.Any)),
        FlowOperationKind.UnknownWrite => facts.InvalidateLocation(Target),
        FlowOperationKind.MemoryWrite => facts.InvalidateMemory(Memory),
        _ => facts
    };
}

internal readonly record struct FlowPredicate(HirLocationId Location, TypeRef? Type,
    bool Positive = true, bool? Boolean = null, HirNodeId? Expression = null)
{
    public FlowFacts Apply(FlowFacts facts, Func<HirNodeId, FlowFacts, FlowFact>? evaluate = null)
    {
        if (Expression is { } expression)
        {
            bool? result = evaluate!(expression, facts).Boolean;
            return result is { } known && known != Positive ? facts.Unreachable() : facts;
        }
        return Boolean is { } value ? facts.RefineBoolean(Location, value)
            : Type is { } tested ? facts.Refine(Location, tested, Positive) : facts;
    }
}

internal readonly record struct FlowEdge(FlowBlockId Target,
    ImmutableArray<FlowPredicate> Predicates = default)
{
    public FlowFacts Apply(FlowFacts facts, Func<HirNodeId, FlowFacts, FlowFact>? evaluate = null)
    {
        if (!Predicates.IsDefault)
            foreach (FlowPredicate predicate in Predicates) facts = predicate.Apply(facts, evaluate);
        return facts;
    }
}

internal sealed record FlowBlock(ImmutableArray<FlowOperation> Operations,
    ImmutableArray<FlowEdge> Successors);
internal sealed record FlowGraph(ImmutableArray<FlowBlock> Blocks, FlowBlockId Entry);
internal sealed record FlowSolution(ImmutableArray<FlowFacts?> Inputs,
    ImmutableArray<FlowFacts?> Outputs, int Transfers);

// This graph is semantic analysis input, not an executable instruction stream.
// Revisit only blocks whose incoming lattice value changed; never rebind syntax.
internal static class FlowSolver
{
    public static FlowSolution Solve(FlowGraph graph, FlowFacts initial,
        Func<HirNodeId, FlowFacts, FlowFact>? evaluate = null)
    {
        var inputs = new FlowFacts?[graph.Blocks.Length];
        var outputs = new FlowFacts?[graph.Blocks.Length];
        var queued = new bool[graph.Blocks.Length];
        Queue<FlowBlockId> queue = [];
        inputs[graph.Entry.Value] = initial;
        queue.Enqueue(graph.Entry);
        queued[graph.Entry.Value] = true;
        int transfers = 0;
        while (queue.TryDequeue(out FlowBlockId current))
        {
            queued[current.Value] = false;
            FlowFacts facts = inputs[current.Value]!;
            foreach (FlowOperation operation in graph.Blocks[current.Value].Operations)
                facts = operation.Apply(facts, evaluate);
            transfers++;
            if (outputs[current.Value] is { } previous && previous.SameFacts(facts)) continue;
            outputs[current.Value] = facts;
            foreach (FlowEdge edge in graph.Blocks[current.Value].Successors)
            {
                FlowFacts candidate = edge.Apply(facts, evaluate);
                if (!candidate.Reachable) continue;
                FlowFacts merged = inputs[edge.Target.Value] is { } incoming
                    ? FlowFacts.Join(incoming, candidate) : candidate;
                if (inputs[edge.Target.Value] is { } before && before.SameFacts(merged)) continue;
                inputs[edge.Target.Value] = merged;
                if (queued[edge.Target.Value]) continue;
                queued[edge.Target.Value] = true;
                queue.Enqueue(edge.Target);
            }
        }
        return new(inputs.ToImmutableArray(), outputs.ToImmutableArray(), transfers);
    }
}
