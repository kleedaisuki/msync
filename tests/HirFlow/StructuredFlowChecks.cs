using System.Collections.Immutable;
using Msync.Compiler.Semantics;
using Msync.Core;
using Msync.Hir;

internal static class StructuredFlowChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool result, string message)
        {
            checks++;
            if (!result) throw new InvalidOperationException(message);
        }

        HirBuilder hir = new();
        hir.Sources.Add(new("structured-flow.msync"));
        HirSourceRange source = new(new(0), 0, 1);
        HirTypeId nullType = hir.Type(TypeRef.Null), path = hir.Type(TypeRef.Path);
        HirTypeId maybe = hir.Type(TypeRef.Path.WithNull), boolean = hir.Type(TypeRef.Bool);
        HirLocationId value = new(0), condition = new(1);
        hir.Locations.Add(new("value", maybe, source, new(0), AdmitsNull: true));
        hir.Locations.Add(new("condition", boolean, source, new(0), HirLocationKind.Import));
        HirNodeId initial = hir.Literal(new(HirLiteralKind.Null), nullType, source);
        HirNodeId declare = hir.Add(HirKind.Declare, nullType, source, [initial], value.Value);
        HirNodeId file = hir.Literal(new(HirLiteralKind.Path, "./file"), path, source);
        HirNodeId write = hir.Add(HirKind.Assign, nullType, source, [file], value.Value);
        HirNodeId read = hir.Add(HirKind.ReadLocation, maybe, source, data: value.Value);
        HirNodeId guard = hir.Add(HirKind.ReadLocation, boolean, source, data: condition.Value);
        HirNodeId broken = hir.Add(HirKind.Break, hir.Type(new TypeRef(TypeKind.Never)), source, [read], 0);
        HirNodeId branch = hir.Add(HirKind.If, maybe, source, [guard, write, broken]);
        HirNodeId loop = hir.Add(HirKind.Loop, maybe, source, [branch], 0);
        HirNodeId body = hir.Add(HirKind.Block, maybe, source, [declare, loop]);
        hir.Functions.Add(new("main", [], [], maybe, body));
        HirModule module = hir.Freeze(new(0));
        FlowFacts facts = FlowFacts.Create(ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty
            .Add(value, new(TypeRef.Path.WithNull, TypeRef.Path.WithNull, InitiallyAssigned: false))
            .Add(condition, new(TypeRef.Bool, TypeRef.Bool, ExternallyWritable: true)));
        HirFlowAnalysis analysis = new HirFlowGraphBuilder(module).Analyze(body, facts);
        Check(analysis.Inputs[read].Read(value).Type == TypeRef.Path.WithNull,
            "Structured loop value joins the initial and back-edge assignments");
        Check(analysis.Solution.Transfers < 20, "Structured-loop analysis converges at stable identities");
        Check(analysis.Inputs[read].Read(value).Assigned, "Initialization precedes a structured-loop exit");

        HirBuilder sequenceBuilder = new();
        sequenceBuilder.Sources.Add(new("for-source-value.msync"));
        HirTypeId rangeNumber = sequenceBuilder.Type(new TypeRef(TypeKind.Range, TypeRef.Number));
        HirTypeId rangeString = sequenceBuilder.Type(new TypeRef(TypeKind.Range, TypeRef.String));
        HirTypeId rangeUnion = sequenceBuilder.Type(new TypeRef(TypeKind.Range, TypeRef.Union(TypeRef.Number, TypeRef.String)));
        HirTypeId any = sequenceBuilder.Type(TypeRef.Any), noValue = sequenceBuilder.Type(TypeRef.Null);
        HirLocationId sequence = new(0), item = new(1);
        sequenceBuilder.Locations.Add(new("sequence", rangeUnion, source, new(0)));
        sequenceBuilder.Locations.Add(new("item", any, source, new(0)));
        HirNodeId sourceRead = sequenceBuilder.Add(HirKind.ReadLocation, rangeUnion, source, data: sequence.Value);
        HirNodeId replacement = sequenceBuilder.Add(HirKind.Range, rangeString, source);
        HirNodeId reassignment = sequenceBuilder.Add(HirKind.Assign, noValue, source, [replacement], sequence.Value);
        HirNodeId itemRead = sequenceBuilder.Add(HirKind.ReadLocation, any, source, data: item.Value);
        HirNodeId stop = sequenceBuilder.Add(HirKind.Break, sequenceBuilder.Type(new TypeRef(TypeKind.Never)), source, data: 3);
        HirNodeId iterationBody = sequenceBuilder.Add(HirKind.Block, noValue, source, [reassignment, itemRead, stop]);
        sequenceBuilder.Fors.Add(new(new(3), item));
        HirNodeId iteration = sequenceBuilder.Add(HirKind.For, noValue, source, [sourceRead, iterationBody]);
        sequenceBuilder.Functions.Add(new("main", [], [], noValue, iteration));
        FlowFacts sequenceFacts = FlowFacts.Create(ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty
            .Add(sequence, new(sequenceBuilder.Types[rangeUnion.Value].RuntimeType,
                sequenceBuilder.Types[rangeUnion.Value].RuntimeType,
                InitialType: sequenceBuilder.Types[rangeNumber.Value].RuntimeType))
            .Add(item, new(TypeRef.Any, TypeRef.Any, InitiallyAssigned: false)));
        HirFlowAnalysis forAnalysis = new HirFlowGraphBuilder(sequenceBuilder.Freeze(new(0)))
            .Analyze(iteration, sequenceFacts);
        Check(forAnalysis.Inputs[itemRead].Read(item).Type == TypeRef.Number,
            "For item type follows the evaluated sequence, not a reassigned source cell");
        Check(forAnalysis.Inputs[itemRead].Read(sequence).Type == sequenceBuilder.Types[rangeString.Value].RuntimeType,
            "The source location can change independently of the iterator value");
        return checks;
    }
}
