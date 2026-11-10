using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly HashSet<HirLocationId> flowCapturedWrites = [];
    private int flowDeclarationCount;

    private HirFlowAnalysis PrepareFlow(HirNodeId root, SemanticContext context)
    {
        var domains = ImmutableDictionary.CreateBuilder<HirLocationId, FlowLocationDomain>();
        var visited = new HashSet<HirNodeId>();
        for (; flowDeclarationCount < program.Declarations.Count; flowDeclarationCount++)
        {
            var declaration = program.Declarations[flowDeclarationCount];
            if (declaration.CapturedFrom is not null && program.WrittenLocations.Contains(program.Original(declaration.Location)))
                flowCapturedWrites.Add(program.Original(declaration.Location));
        }
        void Location(HirLocationId source, FlowMemoryRegion? memory = null)
        {
            HirLocationId location = context.Location(source);
            if (domains.ContainsKey(location)) return;
            HirTypeId storage = context.Storage.GetValueOrDefault(location,
                Resolve(hir.Locations[location.Value].StorageType, context));
            SemanticValue? value = context.Values.GetValueOrDefault(location);
            bool module = StandardDeclaration(source) is not null;
            bool assigned = value is not null && (!module || value.Scope is not null && hir.Types[value.Type.Value].Shape >= 0);
            if (module && assigned) storage = value!.Type;
            bool external = program.TryDeclaration(source, out var declaration)
                && (declaration.Kind is DeclarationKind.Field or DeclarationKind.Capture
                    || flowCapturedWrites.Contains(program.Original(source))
                    || program.IsSession && program.Exports.Values.Contains(source));
            domains.Add(location, new(hir.Types[storage.Value].RuntimeType,
                hir.Types[storage.Value].RuntimeType, StableForLifetime: program.IsStable(source),
                ExternallyWritable: external, Memory: memory, InitiallyAssigned: assigned,
                InitialType: !assigned ? new(TypeKind.Never) : Runtime(value!),
                StorageSemanticType: storage, InitialSemanticType: assigned ? value!.Type : null,
                DeferredSemanticType: storage));
        }
        HirLocationId? Place(HirNodeId id)
        {
            HirNode node = program.Node(id);
            if (node.Kind is not (HirKind.MemberRead or HirKind.MemberAssign)) return null;
            if (Subject(program.Children(id)[0], context)?.Scope is not { } scope) return null;
            if (context.Objects.TryGetValue(scope.Region, out var current)) scope = current;
            if (!scope.Locations.TryGetValue(node.Text!, out var field)) return null;
            Location(field, new(scope.Region));
            return field;
        }
        void Visit(HirNodeId id)
        {
            if (!visited.Add(id)) return;
            HirNode node = program.Node(id);
            if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage or HirKind.Declare or HirKind.Assign)
                Location(new(node.Data));
            if (node.Kind == HirKind.For) Location(hir.Fors[node.Data].Item);
            _ = Place(id);
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
            if (pair.Value.InitiallyAssigned && context.Values.TryGetValue(pair.Key, out var value))
                facts = facts.Assign(pair.Key, new(Runtime(value), Boolean: value.KnownBool, SemanticType: value.Type,
                    Callables: value.Callables.IsDefaultOrEmpty ? null : new(value.Callables.Select(choice =>
                        new FlowCallableIdentity(choice.Template, choice.NativeName))),
                    Denotations: value.DenotedType is { } denoted ? new([denoted]) : null,
                    Eager: value.EagerRange));
        var projection = new HirTypeProjection(program, hir, context, algebra,
            (callable, arguments) => SourceFlowReturn(callable, arguments, context), Place,
            id => BoundCallables(id, context));
        return new HirFlowGraphBuilder(program, projection.Project, context.Location, Place).Analyze(root, facts);
    }

    private SemanticValue FlowRead(HirNodeId id, HirLocationId location, SemanticValue value,
        SemanticContext context)
    {
        if (context.FlowAnalysis is not { } analysis || !analysis.Inputs.TryGetValue(id, out var facts)
            || !facts.Reachable || !facts.Contains(location)) return value;
        FlowFact fact = facts.Read(location);
        if (!fact.Assigned || fact.Type.Kind == TypeKind.Never) return value;
        HirTypeId type = fact.SemanticType ?? hir.Type(fact.Type);
        SemanticValue result = value;
        if (hir.Types[type.Value].Shape != hir.Types[value.Type.Value].Shape
            || type != value.Type && !hir.Types[type.Value].Alternatives.IsDefaultOrEmpty)
            result = RetypeFlowValue(value, type, []);
        bool stable = facts.Domain(location).StableForLifetime;
        result = result with
        {
            Type = type,
            Literal = fact.Boolean is { } boolean
            ? new(HirLiteralKind.Bool, Boolean: boolean) : stable && Runtime(value) != TypeRef.Type ? value.Literal : null,
            DenotedType = fact.Denotations is { Types.Length: 1 } denotations ? denotations.Types[0] : null,
            Observation = stable ? value.Observation : null,
            EagerRange = fact.Eager
        };
        if (result.DenotedType is { } denoted)
            result = result with { Literal = new(HirLiteralKind.Type, Type: hir.Types[denoted.Value].RuntimeType) };
        if (result.Scope is { } scope)
        {
            var fields = scope.Fields;
            foreach (var (name, field) in scope.Locations)
            {
                if (!facts.Contains(field)) continue;
                FlowFact fieldFact = facts.Read(field);
                if (!fieldFact.Assigned || fieldFact.Type.Kind == TypeKind.Never) continue;
                SemanticValue fieldValue = context.Values.GetValueOrDefault(field, fields[name]);
                fieldValue = fieldValue with
                {
                    Type = fieldFact.SemanticType ?? hir.Type(fieldFact.Type),
                    Literal = fieldFact.Boolean is { } fieldBoolean ? new(HirLiteralKind.Bool, Boolean: fieldBoolean) : null,
                    DenotedType = fieldFact.Denotations is { Types.Length: 1 } fieldTypes ? fieldTypes.Types[0] : null,
                    Observation = null,
                    EagerRange = fieldFact.Eager
                };
                if (fieldValue.DenotedType is { } fieldType)
                    fieldValue = fieldValue with { Literal = new(HirLiteralKind.Type, Type: hir.Types[fieldType.Value].RuntimeType) };
                if (fieldFact.Callables is { } fieldCallables)
                    fieldValue = fieldValue with { Callables = ResolveFlowCallables(fieldValue, fieldCallables, context) };
                context.Values[field] = fieldValue;
                fields = fields.SetItem(name, fieldValue);
            }
            scope = scope with { Fields = fields };
            result = result with { Scope = scope };
            if (scope.Region >= 0) context.Objects[scope.Region] = scope;
        }
        if (fact.Callables is { } callables)
            result = result with { Callables = ResolveFlowCallables(value, callables, context) };
        context.Values[location] = result;
        return result;
    }

    private ImmutableArray<SemanticCallable> ResolveFlowCallables(SemanticValue value, FlowCallableDomain domain,
        SemanticContext context)
    {
        if (SameFlowCallables(value.Callables, domain)) return value.Callables;
        return domain.Choices.Select(choice => (value.Callables.IsDefault ? null : value.Callables.FirstOrDefault(item =>
            item.Template == choice.Template && item.NativeName == choice.NativeName))
            ?? RestoreFlowCallable(choice, context)).ToImmutableArray();
    }

    private static bool SameFlowCallables(ImmutableArray<SemanticCallable> values, FlowCallableDomain domain)
    {
        if (values.IsDefault || values.Length != domain.Choices.Length) return false;
        for (int index = 0; index < values.Length; index++)
            if (values[index].Template != domain.Choices[index].Template
                || values[index].NativeName != domain.Choices[index].NativeName) return false;
        return true;
    }

    private SemanticCallable RestoreFlowCallable(FlowCallableIdentity choice, SemanticContext context)
    {
        if (choice.Template is not { } template) return new(NativeName: choice.NativeName);
        var captures = hir.Templates[template.Value].Captures.Select(capture =>
        {
            HirLocationId source = context.Location(capture.SourceLocation);
            SemanticValue captured = context.Values.GetValueOrDefault(source)
                ?? ValueForType(Resolve(hir.Locations[source.Value].StorageType, context));
            return new SemanticCapture(source, captured, capture.Mode);
        }).ToImmutableArray();
        return new(template, Captures: captures);
    }

    private SemanticValue RetypeFlowValue(SemanticValue value, HirTypeId type, HashSet<int> active)
    {
        SemanticValue projected = ValueForType(type);
        if (projected.Scope is not { } shape)
            return value with { Type = type, Literal = null, DenotedType = null, Observation = null };
        if (value.Scope is not { } existing)
            return projected with { Location = value.Location, Origin = value.Origin };
        if (existing.Region >= 0 && !active.Add(existing.Region)) return value with { Type = type };
        var fields = shape.Fields;
        foreach (var (name, target) in shape.Fields)
            if (existing.Fields.TryGetValue(name, out var previous) && Runtime(target).Accepts(Runtime(previous)))
                fields = fields.SetItem(name, RetypeFlowValue(previous, target.Type, active));
        if (existing.Region >= 0) active.Remove(existing.Region);
        return value with
        {
            Type = type,
            Literal = null,
            DenotedType = null,
            Observation = null,
            Scope = existing with
            {
                Layout = shape.Layout,
                Fields = fields,
                Locations = existing.Locations.Where(pair => fields.ContainsKey(pair.Key))
                    .ToImmutableDictionary(StringComparer.Ordinal)
            }
        };
    }
}
