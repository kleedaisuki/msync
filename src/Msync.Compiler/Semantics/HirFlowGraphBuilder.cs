using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed record HirFlowAnalysis(FlowGraph Graph, FlowSolution Solution,
    ImmutableDictionary<HirNodeId, FlowFacts> Inputs);

internal sealed class HirFlowGraphBuilder(HirModule module,
    Func<HirNodeId, FlowFacts, FlowFact>? project = null,
    Func<HirLocationId, HirLocationId>? locations = null,
    Func<HirNodeId, HirLocationId?>? places = null,
    Func<HirNodeId, HirEffects>? effects = null)
{
    private sealed class Block
    {
        public List<FlowOperation> Operations { get; } = [];
        public List<FlowEdge> Successors { get; } = [];
        public List<(HirNodeId Node, int Position)> Points { get; } = [];
    }

    private readonly List<Block> blocks = [new()];
    private readonly Dictionary<HirLoopId, FlowBlockId> exits = [];
    private readonly Dictionary<HirLocationId, FlowLocationDomain> temporaries = [];
    private int current;
    private int nextTemporary;

    public HirFlowAnalysis Analyze(HirNodeId body, FlowFacts initial)
    {
        nextTemporary = Math.Max(module.Locations.Length, initial.NextLocationIdentity);
        Visit(body);
        FlowGraph graph = new(blocks.Select(block => new FlowBlock(block.Operations.ToImmutableArray(),
            block.Successors.ToImmutableArray())).ToImmutableArray(), new(0));
        FlowSolution solution = FlowSolver.Solve(graph, initial.Extend(temporaries), Project);
        var inputs = ImmutableDictionary.CreateBuilder<HirNodeId, FlowFacts>();
        for (int index = 0; index < blocks.Count; index++)
        {
            if (solution.Inputs[index] is not { } facts) continue;
            Block block = blocks[index];
            int point = 0;
            for (int position = 0; position <= block.Operations.Count; position++)
            {
                while (point < block.Points.Count && block.Points[point].Position == position)
                {
                    HirNodeId node = block.Points[point++].Node;
                    inputs[node] = inputs.TryGetValue(node, out var before) ? FlowFacts.Join(before, facts) : facts;
                }
                if (position < block.Operations.Count) facts = block.Operations[position].Apply(facts, Project);
            }
        }
        return new(graph, solution, inputs.ToImmutable());
    }

    private FlowFact Project(HirNodeId id, FlowFacts facts)
    {
        if (project is not null) return project(id, facts);
        HirNode node = module.Node(id);
        if (Place(id) is { } place) return facts.Read(place);
        bool? boolean = node.Kind == HirKind.Literal && module.Literals[node.Data].Kind == HirLiteralKind.Bool
            ? module.Literals[node.Data].Boolean : null;
        return new(module.RuntimeType(node.Type), Boolean: boolean);
    }

    private HirLocationId Location(HirLocationId location) => locations?.Invoke(location) ?? location;
    private HirLocationId? Place(HirNodeId id)
    {
        if (places?.Invoke(id) is { } place) return place;
        HirNode node = module.Node(id);
        return node.Kind is HirKind.ReadLocation or HirKind.ReadStorage ? Location(new(node.Data)) : null;
    }

    private FlowBlockId NewBlock()
    {
        FlowBlockId id = new(blocks.Count);
        blocks.Add(new());
        return id;
    }

    private void Edge(FlowBlockId target, ImmutableArray<FlowPredicate> predicates = default)
    {
        if (current >= 0) blocks[current].Successors.Add(new(target, predicates));
    }

    private void Operation(FlowOperation operation)
    {
        if (current >= 0) blocks[current].Operations.Add(operation);
    }

    private void Point(HirNodeId id)
    {
        if (current >= 0) blocks[current].Points.Add((id, blocks[current].Operations.Count));
    }

    private void Visit(HirNodeId id)
    {
        if (current < 0 || id.IsNone) return;
        HirNode node = module.Node(id);
        Point(id);
        var children = module.Children(node);
        switch (node.Kind)
        {
            case HirKind.If: Conditional(children); return;
            case HirKind.Match: Match(node, children); return;
            case HirKind.For: For(node, children); return;
            case HirKind.Loop: Loop(node, children); return;
            case HirKind.Return:
                foreach (var child in children) Visit(child);
                current = -1;
                return;
            case HirKind.Break:
                foreach (var child in children) Visit(child);
                Edge(exits[new(node.Data)]);
                current = -1;
                return;
            case HirKind.Closure:
                foreach (var capture in module.Closures[node.Data].Captures)
                    if (capture.Mode == HirCaptureMode.Value) Visit(capture.SourceValue);
                return;
            case HirKind.TemplateClosure:
                foreach (var capture in module.TemplateClosures[node.Data].Captures)
                    if (capture.Mode == HirCaptureMode.Value) Visit(capture.SourceValue);
                return;
            case HirKind.Binary when (HirBinaryOperator)node.Data is HirBinaryOperator.And or HirBinaryOperator.Or:
                Logical(id);
                return;
        }
        foreach (var child in children) Visit(child);
        if (current < 0) return;
        if (node.Kind is HirKind.Declare or HirKind.Assign)
        {
            HirLocationId destination = Location(new(node.Data));
            if (Place(children[0]) is { } source) Operation(FlowOperation.Copy(destination, source));
            else Operation(FlowOperation.Evaluate(destination, children[0]));
        }
        else if (node.Kind == HirKind.MemberAssign)
        {
            Operation(FlowOperation.WriteMemory());
            if (places?.Invoke(id) is { } place) Operation(FlowOperation.Evaluate(place, children[^1]));
        }
        else if (node.Kind == HirKind.Call)
        {
            HirEffects applied = effects?.Invoke(id) ?? node.Effects;
            if ((applied & (HirEffects.Invoke | HirEffects.WriteLocation | HirEffects.WriteMemory)) != 0)
                Operation(FlowOperation.WriteMemory());
        }
        else if (node.Kind == HirKind.Scope)
        {
            HirScopeConstruction scope = module.Scopes[node.Data];
            if (scope.Self is { } self) Operation(FlowOperation.Assign(Location(self), module.RuntimeType(node.Type)));
            if (scope.Singleton >= 0)
                foreach (var field in scope.Fields) Operation(FlowOperation.Write(Location(field)));
        }
    }

    private void Conditional(ReadOnlySpan<HirNodeId> children)
    {
        FlowBlockId yes = NewBlock(), no = NewBlock(), join = NewBlock();
        Condition(children[0], yes, no);
        current = yes.Value;
        Visit(children[1]);
        Edge(join);
        current = no.Value;
        Visit(children[2]);
        Edge(join);
        current = join.Value;
    }

    private void Logical(HirNodeId id)
    {
        FlowBlockId yes = NewBlock(), no = NewBlock(), join = NewBlock();
        Condition(id, yes, no);
        current = yes.Value; Edge(join);
        current = no.Value; Edge(join);
        current = join.Value;
    }

    private void Condition(HirNodeId id, FlowBlockId yes, FlowBlockId no)
    {
        HirNode node = module.Node(id);
        var children = module.Children(node);
        if (node.Kind == HirKind.Unary && (HirUnaryOperator)node.Data == HirUnaryOperator.Not)
        {
            Point(id); Condition(children[0], no, yes); return;
        }
        if (node.Kind == HirKind.Binary && (HirBinaryOperator)node.Data is HirBinaryOperator.And or HirBinaryOperator.Or)
        {
            Point(id);
            FlowBlockId next = NewBlock();
            if ((HirBinaryOperator)node.Data == HirBinaryOperator.And) Condition(children[0], next, no);
            else Condition(children[0], yes, next);
            current = next.Value;
            Condition(children[1], yes, no);
            return;
        }
        Visit(id);
        Edge(yes, Predicates(id, true));
        Edge(no, Predicates(id, false));
        current = -1;
    }

    private ImmutableArray<FlowPredicate> Predicates(HirNodeId id, bool positive)
    {
        HirNode node = module.Node(id);
        var children = module.Children(node);
        if (node.Kind is HirKind.IsNull or HirKind.IsType && Place(children[0]) is { } subject)
        {
            TypeRef tested = node.Kind == HirKind.IsNull ? TypeRef.Null : module.RuntimeType(new(node.Data));
            return [new(subject, tested, positive)];
        }
        if (Place(id) is { } location) return [new(location, null, Boolean: positive)];
        return [new(default, null, positive, Expression: id)];
    }

    private void Loop(HirNode node, ReadOnlySpan<HirNodeId> children)
    {
        FlowBlockId head = NewBlock(), end = NewBlock();
        Edge(head);
        exits.Add(new(node.Data), end);
        current = head.Value;
        Visit(children[0]);
        Edge(head);
        exits.Remove(new(node.Data));
        current = end.Value;
    }

    private void For(HirNode node, ReadOnlySpan<HirNodeId> children)
    {
        Visit(children[0]);
        // Iteration reads the evaluated sequence value, not a later value of its source cell.
        HirLocationId sequence = new(nextTemporary++);
        temporaries.Add(sequence, new(TypeRef.Any, TypeRef.Any, StableForLifetime: true, InitiallyAssigned: false));
        Operation(FlowOperation.Evaluate(sequence, children[0]));
        HirFor iteration = module.Fors[node.Data];
        FlowBlockId head = NewBlock(), body = NewBlock(), end = NewBlock();
        Edge(head);
        current = head.Value;
        Edge(body); Edge(end);
        current = body.Value;
        Operation(FlowOperation.ElementOf(Location(iteration.Item), sequence));
        exits.Add(iteration.Loop, end);
        Visit(children[1]);
        Edge(head);
        exits.Remove(iteration.Loop);
        current = end.Value;
    }

    private void Match(HirNode node, ReadOnlySpan<HirNodeId> children)
    {
        Visit(children[0]);
        FlowBlockId end = NewBlock();
        foreach (HirMatchArm arm in module.Matches[node.Data].Arms)
        {
            FlowBlockId body = NewBlock(), next = NewBlock();
            HirPattern pattern = arm.Pattern;
            var positive = ImmutableArray<FlowPredicate>.Empty;
            var negative = ImmutableArray<FlowPredicate>.Empty;
            if (Place(children[0]) is { } place)
            {
                TypeRef? tested = pattern.Kind == HirPatternKind.Null ? TypeRef.Null
                    : pattern.Kind == HirPatternKind.Type ? module.RuntimeType(new(pattern.Data)) : null;
                if (tested is not null)
                {
                    positive = [new(place, tested)]; negative = [new(place, tested, false)];
                }
                else if (pattern.Kind == HirPatternKind.Literal && module.Literals[pattern.Data].Kind == HirLiteralKind.Bool)
                {
                    bool value = module.Literals[pattern.Data].Boolean;
                    positive = [new(place, null, Boolean: value)]; negative = [new(place, null, Boolean: !value)];
                }
            }
            Edge(body, positive);
            if (pattern.Kind != HirPatternKind.Any) Edge(next, negative);
            current = body.Value;
            if (pattern.Binding is { } binding)
                Operation(Place(children[0]) is { } original ? FlowOperation.Copy(Location(binding), original)
                    : FlowOperation.Evaluate(Location(binding), children[0]));
            Visit(arm.Value);
            Edge(end);
            current = next.Value;
            if (pattern.Kind == HirPatternKind.Any) break;
        }
        current = end.Value;
    }
}
