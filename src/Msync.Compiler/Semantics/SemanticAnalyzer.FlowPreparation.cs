using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private HirFlowAnalysis PrepareFlow(HirNodeId root, SemanticContext context)
    {
        var domains = ImmutableDictionary.CreateBuilder<HirLocationId, FlowLocationDomain>();
        var visited = new HashSet<HirNodeId>();
        HashSet<HirLocationId> capturedWrites = [];
        foreach (var declaration in program.Declarations)
            if (declaration.CapturedFrom is not null && program.WrittenLocations.Contains(program.Original(declaration.Location)))
                capturedWrites.Add(program.Original(declaration.Location));
        void Location(HirLocationId source)
        {
            HirLocationId location = context.Location(source);
            if (domains.ContainsKey(location)) return;
            HirTypeId storage = context.Storage.GetValueOrDefault(location,
                Resolve(hir.Locations[location.Value].StorageType, context));
            SemanticValue? value = context.Values.GetValueOrDefault(location);
            bool external = program.TryDeclaration(source, out var declaration)
                && (declaration.Kind is DeclarationKind.Field or DeclarationKind.Capture
                    || capturedWrites.Contains(program.Original(source))
                    || program.IsSession && program.Exports.Values.Contains(source));
            domains.Add(location, new(hir.Types[storage.Value].RuntimeType,
                hir.Types[storage.Value].RuntimeType, StableForLifetime: program.IsStable(source),
                ExternallyWritable: external, InitiallyAssigned: value is not null,
                InitialType: value is null ? new(TypeKind.Never) : Runtime(value),
                StorageSemanticType: storage, InitialSemanticType: value?.Type,
                DeferredSemanticType: storage));
        }
        void Visit(HirNodeId id)
        {
            if (!visited.Add(id)) return;
            HirNode node = program.Node(id);
            if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage or HirKind.Declare or HirKind.Assign)
                Location(new(node.Data));
            if (node.Kind == HirKind.For) Location(hir.Fors[node.Data].Item);
            if (node.Kind is HirKind.TemplateClosure or HirKind.Closure) return;
            foreach (HirNodeId child in program.Children(id)) Visit(child);
            if (node.Kind == HirKind.Match)
                foreach (var arm in hir.Matches[node.Data].Arms)
                {
                    if (arm.Pattern.Binding is { } binding) Location(binding);
                    Visit(arm.Value);
                }
        }
        Visit(root);
        var algebra = new HirFlowTypeAlgebra(hir);
        FlowFacts facts = FlowFacts.Create(domains.ToImmutable(), algebra);
        foreach (var pair in domains)
            if (context.Values.TryGetValue(pair.Key, out var value))
                facts = facts.Assign(pair.Key, new(Runtime(value), Boolean: value.KnownBool, SemanticType: value.Type,
                    Callables: value.Callables.IsDefaultOrEmpty ? null : new(value.Callables.Select(choice =>
                        new FlowCallableIdentity(choice.Template, choice.NativeName)))));
        var projection = new HirTypeProjection(program, hir, context, algebra,
            (callable, arguments) => SourceFlowReturn(callable, arguments, context));
        return new HirFlowGraphBuilder(program, projection.Project, context.Location).Analyze(root, facts);
    }

    private SemanticValue FlowRead(HirNodeId id, HirLocationId location, SemanticValue value,
        SemanticContext context)
    {
        if (context.FlowAnalysis is not { } analysis || !analysis.Inputs.TryGetValue(id, out var facts)
            || !facts.Reachable || !facts.Contains(location)) return value;
        FlowFact fact = facts.Read(location, context.DeferredReads ? FlowReadKind.Deferred : FlowReadKind.Immediate);
        if (!fact.Assigned || fact.Type.Kind == TypeKind.Never) return value;
        HirTypeId type = fact.SemanticType ?? hir.Type(fact.Type);
        SemanticValue result = value;
        if (hir.Types[type.Value].Shape != hir.Types[value.Type.Value].Shape
            || type != value.Type && !hir.Types[type.Value].Alternatives.IsDefaultOrEmpty)
            result = ValueForType(type) with { Location = value.Location, Origin = value.Origin };
        result = result with { Type = type, Literal = fact.Boolean is { } boolean
            ? new(HirLiteralKind.Bool, Boolean: boolean) : value.Literal is { Kind: HirLiteralKind.Bool } ? null : result.Literal };
        if (fact.Callables is { } callables)
            result = result with { Callables = callables.Choices.Select(choice =>
            {
                var existing = value.Callables.IsDefault ? null : value.Callables.FirstOrDefault(item =>
                    item.Template == choice.Template && item.NativeName == choice.NativeName);
                if (existing is not null) return existing;
                if (choice.Template is not { } template) return new SemanticCallable(NativeName: choice.NativeName);
                var captures = hir.Templates[template.Value].Captures.Select(capture =>
                {
                    HirLocationId source = context.Location(capture.SourceLocation);
                    SemanticValue captured = context.Values.GetValueOrDefault(source)
                        ?? ValueForType(Resolve(hir.Locations[source.Value].StorageType, context));
                    return new SemanticCapture(source, captured, capture.Mode);
                }).ToImmutableArray();
                return new SemanticCallable(template, Captures: captures);
            }).ToImmutableArray() };
        context.Values[location] = result;
        return result;
    }
}
