using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly record struct ReturnShape(FlowFact Fact, bool Eager = false);
    private readonly Dictionary<string, ReturnShape> returnSchemes = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeReturnSchemes = new(StringComparer.Ordinal);

    private ReturnShape? CallShape(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext caller)
    {
        SemanticContext context = caller.QueryFork();
        context.DeferredReads = true;
        HashSet<HirLocationId> read = [], declared = [];
        foreach (HirNodeId child in children) CollectLocations(child, read, declared);
        read.ExceptWith(declared);
        var domains = ImmutableDictionary.CreateBuilder<HirLocationId, FlowLocationDomain>();
        foreach (HirLocationId original in read)
        {
            HirLocationId location = context.Location(original);
            HirTypeId storage = context.Storage.GetValueOrDefault(location, hir.Locations[location.Value].StorageType);
            SemanticValue? current = context.Values.GetValueOrDefault(location);
            HirTypeId initial = current?.Type ?? storage;
            domains[location] = new(hir.Types[storage.Value].RuntimeType, hir.Types[storage.Value].RuntimeType,
                Stable(original), InitialType: hir.Types[initial.Value].RuntimeType,
                StorageSemanticType: storage, InitialSemanticType: initial, DeferredSemanticType: storage);
        }
        FlowFacts facts = FlowFacts.Create(domains.ToImmutable(), new HirFlowTypeAlgebra(hir));
        Dictionary<HirLocationId, bool> eager = [];
        List<ReturnShape> returns = [];
        ReturnShape[] arguments = new ReturnShape[children.Length - 1];
        for (int index = 0; index < arguments.Length; index++)
            arguments[index] = ProjectReturn(children[index + 1], context, ref facts, eager, returns);
        SemanticCallable[] choices = BoundCallables(children[0], context).ToArray();
        string? native = hir.Calls[node.Data].NativeName ?? (choices.Length == 1 ? choices[0].NativeName : null);
        if (native is not null && NativeSignatures.TryGet(native, out var signature))
        {
            bool eagerResult = (signature.Behavior.GetValueOrDefault() & NativeBehavior.EagerRange) != 0;
            if (signature.ResultRule == NativeResultRule.FirstArgument && arguments.Length != 0)
                return arguments[0] with { Eager = eagerResult || ((signature.Behavior.GetValueOrDefault()
                    & NativeBehavior.Deferred) == 0 && arguments[0].Eager) };
            return new(Shape(hir.Type(signature.Result)).Fact, eagerResult);
        }
        ReturnShape? result = null;
        foreach (SemanticCallable choice in choices)
            if (SourceReturnShape(choice, arguments, context) is { } shape)
                result = result is { } before ? JoinShape(before, shape, new(hir)) : shape;
        return result;
    }

    private SemanticValue DeferredCall(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext caller)
    {
        HashSet<HirLocationId> read = [], declared = [];
        foreach (HirNodeId child in children) CollectLocations(child, read, declared);
        read.ExceptWith(declared);
        SemanticContext deferred = new(new(caller.Publish ? hir.Functions.Count : -1), caller.Output, caller)
        { DeferredReads = true, NextRegion = caller.NextRegion };
        var captures = ImmutableArray.CreateBuilder<HirCapture>();
        Dictionary<HirLocationId, HirLocationId> sources = [];
        foreach (HirLocationId original in read.OrderBy(location => location.Value))
        {
            HirLocationId source = caller.Location(original);
            HirLocationId target = AllocateThunkLocation(original, deferred);
            SemanticValue value = caller.Values.GetValueOrDefault(source)
                ?? ValueForType(ResolveHirType(hir.Locations[source.Value].StorageType, caller));
            if (!Stable(original)) value = value with { Literal = null, Observation = null };
            deferred.Values[target] = value;
            deferred.Storage[target] = caller.Storage.GetValueOrDefault(source, value.Type);
            captures.Add(new(target, HirCaptureMode.Location, source, HirNodeId.None));
            sources[target] = source;
        }
        foreach (HirLocationId local in declared) _ = AllocateThunkLocation(local, deferred);
        caller.Output?.ReserveFunction(new("<range-call>", [], captures.ToImmutable(),
            hir.Type(TypeRef.Any), HirNodeId.None, IsOpen: true));
        SemanticValue result = Call(node, children, deferred, allowDeferral: false);
        var effects = result.DeferredEffects.IsDefault ? ImmutableArray<SemanticDeferredWrite>.Empty : result.DeferredEffects;
        effects = effects.Select(effect => effect.Kind == SemanticWriteKind.Location
            && sources.TryGetValue(effect.Location, out HirLocationId source)
                ? effect with { Location = source } : effect).ToImmutableArray();
        if (!caller.Publish) return result with { Node = HirNodeId.None, DeferredEffects = effects };
        caller.Output!.SealFunction(deferred.Function, new("<range-call>", [], captures.ToImmutable(), result.Type, result.Node));
        int descriptor = hir.Closures.Count;
        caller.Output.AddClosure(new(deferred.Function, captures.ToImmutable()));
        HirNodeId closure = caller.Output.Add(HirKind.Closure, hir.Type(TypeRef.Function), node.Source, data: descriptor);
        return result with { Node = caller.Output.Add(HirKind.DeferRange, result.Type, node.Source, [closure]),
            Location = null, DeferredEffects = effects };
    }

    private FlowFact? SourceFlowReturn(SemanticCallable callable, IReadOnlyList<FlowFact> arguments,
        SemanticContext context) => SourceReturnShape(callable,
            arguments.Select(argument => new ReturnShape(argument)).ToArray(), context)?.Fact;

    // This projection owns only type constraints. It cannot publish instructions,
    // instantiate executable functions, or apply a callee's effects to its caller.
    private ReturnShape? SourceReturnShape(SemanticCallable callable, IReadOnlyList<ReturnShape> arguments,
        SemanticContext caller)
    {
        if (callable.Template is not { } templateId) return null;
        HirTemplate template = hir.Templates[templateId.Value];
        if (template.Parameters.Length != arguments.Count) return null;
        var key = templateId.Value + ":" + string.Join(",", arguments.Select(argument =>
            (argument.Fact.SemanticType ?? hir.Type(argument.Fact.Type)).Value + (argument.Eager ? "!" : ".")
                + argument.Fact.Boolean))
            + ":" + string.Join(",", callable.Captures.IsDefault ? [] : callable.Captures.Select(capture =>
                caller.Storage.GetValueOrDefault(capture.Source, capture.Value.Type).Value));
        if (returnSchemes.TryGetValue(key, out var cached)) return cached;
        SemanticContext context = caller.QueryFork();
        context.DeferredReads = false;
        var domains = ImmutableDictionary.CreateBuilder<HirLocationId, FlowLocationDomain>();
        Dictionary<HirLocationId, bool> eager = [];
        for (int index = 0; index < arguments.Count; index++)
        {
            HirLocationId parameter = template.Parameters[index];
            ReturnShape argument = arguments[index];
            HirTypeId type = argument.Fact.SemanticType ?? hir.Type(argument.Fact.Type);
            HirTypeId declared = hir.Locations[parameter.Value].StorageType;
            if (hir.Types[declared.Value].TypeParameter >= 0) context.Substitutions[declared] = type;
            context.Locations[parameter] = parameter;
            context.Values[parameter] = ValueForType(type);
            domains[parameter] = new(argument.Fact.Type, argument.Fact.Type,
                InitialType: argument.Fact.Type, StorageSemanticType: type, InitialSemanticType: type);
            eager[parameter] = argument.Eager;
        }
        if (!callable.Captures.IsDefault)
            for (int index = 0; index < Math.Min(template.Captures.Length, callable.Captures.Length); index++)
            {
                SemanticCapture capture = callable.Captures[index];
                HirLocationId target = template.Captures[index].Target;
                HirTypeId type = Stable(capture.Source) ? capture.Value.Type
                    : caller.Storage.GetValueOrDefault(capture.Source, capture.Value.Type);
                context.Locations[target] = target;
                context.Values[target] = capture.Value with { Type = type, Node = HirNodeId.None, Literal = null };
                domains[target] = new(hir.Types[type.Value].RuntimeType, hir.Types[type.Value].RuntimeType,
                    StorageSemanticType: type, InitialSemanticType: type);
                eager[target] = capture.Value.EagerRange;
            }
        HirTypeId annotation = template.ResultAnnotation is { } annotated
            ? ResolveHirType(annotated, context) : hir.Type(TypeRef.Any);
        if (!activeReturnSchemes.Add(key)) return Shape(annotation);
        try
        {
            var algebra = new HirFlowTypeAlgebra(hir);
            FlowFacts facts = FlowFacts.Create(domains.ToImmutable(), algebra);
            List<ReturnShape> exits = [];
            ReturnShape result = ProjectReturn(template.Body, context, ref facts, eager, exits);
            foreach (ReturnShape returned in exits) result = JoinShape(result, returned, algebra);
            if (template.ResultAnnotation is not null) result = result with { Fact = Shape(annotation).Fact };
            returnSchemes[key] = result;
            return result;
        }
        finally { activeReturnSchemes.Remove(key); }
    }

    private ReturnShape ProjectReturn(HirNodeId id, SemanticContext context, ref FlowFacts facts,
        Dictionary<HirLocationId, bool> eager, List<ReturnShape> returns)
    {
        HirNode node = program.Node(id);
        ReadOnlySpan<HirNodeId> children = program.Children(id);
        if (node.Kind == HirKind.Block)
        {
            ReturnShape result = Shape(hir.Type(TypeRef.Null));
            foreach (HirNodeId child in children)
            {
                result = ProjectReturn(child, context, ref facts, eager, returns);
                if (result.Fact.Type.Kind == TypeKind.Never) break;
            }
            return result;
        }
        if (node.Kind is HirKind.Declare or HirKind.Assign)
        {
            ReturnShape value = ProjectReturn(children[0], context, ref facts, eager, returns);
            HirLocationId location = context.Location(new(node.Data));
            HirTypeId type = value.Fact.SemanticType ?? hir.Type(value.Fact.Type);
            if (!facts.Contains(location)) facts = facts.Extend([new(location,
                new(value.Fact.Type, value.Fact.Type, StorageSemanticType: type, InitialSemanticType: type))]);
            facts = facts.Assign(location, value.Fact);
            eager[location] = value.Eager;
            return Shape(hir.Type(TypeRef.Null));
        }
        if (node.Kind == HirKind.Return)
        {
            returns.Add(children.Length == 0 ? Shape(hir.Type(TypeRef.Null))
                : ProjectReturn(children[0], context, ref facts, eager, returns));
            return Shape(hir.Type(new TypeRef(TypeKind.Never)));
        }
        if (node.Kind == HirKind.If)
        {
            ReturnShape condition = ProjectReturn(children[0], context, ref facts, eager, returns);
            if (condition.Fact.Boolean is { } known)
                return ProjectReturn(children[known ? 1 : 2], context, ref facts, eager, returns);
            FlowFacts yes = RefineReturn(children[0], true, context, facts);
            FlowFacts no = RefineReturn(children[0], false, context, facts);
            ReturnShape accepted = ProjectReturn(children[1], context, ref yes, new(eager), returns);
            ReturnShape rejected = ProjectReturn(children[2], context, ref no, new(eager), returns);
            return JoinShape(accepted, rejected, new(hir));
        }
        if (node.Kind == HirKind.IsType && program.Node(children[1]).Kind == HirKind.TypeValue)
        {
            HirNodeId operand = children[0];
            if (program.Node(operand).Kind == HirKind.TypeOf) operand = program.Children(operand)[0];
            FlowFact actual = ProjectReturn(operand, context, ref facts, eager, returns).Fact;
            HirTypeId required = ResolveHirType(new(program.Node(children[1]).Data), context);
            TypeRef target = hir.Types[required.Value].RuntimeType;
            bool? known = target.Accepts(actual.Type) ? true : (target.Kind & actual.Type.Kind) == 0 ? false : null;
            return new(new(TypeRef.Bool, Boolean: known, SemanticType: hir.Type(TypeRef.Bool)));
        }
        if (node.Kind == HirKind.Call)
        {
            ReturnShape[] arguments = new ReturnShape[children.Length - 1];
            for (int index = 0; index < arguments.Length; index++)
                arguments[index] = ProjectReturn(children[index + 1], context, ref facts, eager, returns);
            var call = hir.Calls[node.Data];
            var choices = BoundCallables(children[0], context).ToArray();
            string? native = call.NativeName ?? (choices.Length == 1 ? choices[0].NativeName : null);
            if (native is not null && NativeSignatures.TryGet(native, out var signature))
            {
                FlowFact fact = signature.ResultRule switch
                {
                    NativeResultRule.FirstArgument when arguments.Length != 0 => arguments[0].Fact,
                    NativeResultRule.RangeItem when arguments.Length != 0 => Shape(new HirFlowTypeAlgebra(hir)
                        .Element(arguments[0].Fact.SemanticType ?? hir.Type(arguments[0].Fact.Type))).Fact,
                    _ => Shape(hir.Type(signature.Result)).Fact
                };
                bool eagerResult = (signature.Behavior.GetValueOrDefault() & NativeBehavior.EagerRange) != 0;
                if (signature.ResultRule == NativeResultRule.FirstArgument && arguments.Length != 0
                    && (signature.Behavior.GetValueOrDefault() & NativeBehavior.Deferred) == 0)
                    eagerResult |= arguments[0].Eager;
                return new(fact, eagerResult);
            }
            ReturnShape? result = null;
            foreach (SemanticCallable choice in choices)
                if (SourceReturnShape(choice, arguments, context) is { } value)
                    result = result is { } previous ? JoinShape(previous, value, new(hir)) : value;
            if (result is { } resolved) return resolved;
        }
        var projector = new HirTypeProjection(program, hir, context, new(hir),
            (callable, arguments) => SourceFlowReturn(callable, arguments, context));
        FlowFact projected = projector.Project(id, facts);
        bool isEager = node.Kind is HirKind.ReadLocation or HirKind.ReadStorage
            && eager.GetValueOrDefault(context.Location(new(node.Data)));
        return new(projected, isEager);
    }

    private IEnumerable<SemanticCallable> BoundCallables(HirNodeId id, SemanticContext context)
    {
        HirNode node = program.Node(id);
        if (node.Kind == HirKind.NativeFunction) return [new(NativeName: node.Text)];
        if (node.Kind == HirKind.TemplateClosure)
        {
            HirTemplateClosure closure = hir.TemplateClosures[node.Data];
            return [new(closure.Template, Captures: closure.Captures.Select(capture =>
            {
                HirLocationId location = context.Location(capture.SourceLocation);
                return new SemanticCapture(location, context.Values.GetValueOrDefault(location)
                    ?? ValueForType(hir.Locations[location.Value].StorageType), capture.Mode);
            }).ToImmutableArray())];
        }
        if (BoundValue(id, context) is { } value && !value.Callables.IsDefaultOrEmpty) return Choices(value);
        if (BoundLocation(id) is { } location)
        {
            if (program.NativeLocations.TryGetValue(location, out string? native)) return [new(NativeName: native)];
            if (program.FunctionLocations.TryGetValue(location, out HirTemplateId template))
                return [new(template, Captures: hir.Templates[template.Value].Captures.Select(capture =>
                {
                    HirLocationId source = context.Location(capture.SourceLocation);
                    return new SemanticCapture(source, context.Values.GetValueOrDefault(source)
                        ?? ValueForType(hir.Locations[source.Value].StorageType), capture.Mode);
                }).ToImmutableArray())];
        }
        return [];
    }

    private HirLocationId? BoundLocation(HirNodeId id)
    {
        HirNode node = program.Node(id);
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage) return program.Original(new(node.Data));
        if (node.Kind != HirKind.MemberRead || BoundLocation(program.Children(id)[0]) is not { } receiver)
            return null;
        if (program.TryDeclaration(receiver, out BoundDeclaration declaration)
            && declaration.Kind == DeclarationKind.StandardModule
            && program.StandardModules.TryGetValue(declaration.Name, out BoundModule? module)
            && module.Exports.TryGetValue(node.Text!, out HirLocationId member)) return member;
        return null;
    }

    private FlowFacts RefineReturn(HirNodeId condition, bool positive, SemanticContext context, FlowFacts facts)
    {
        HirNode test = program.Node(condition);
        var children = program.Children(condition);
        if (test.Kind == HirKind.Unary && (HirUnaryOperator)test.Data == HirUnaryOperator.Not)
            return RefineReturn(children[0], !positive, context, facts);
        if (test.Kind is not (HirKind.IsType or HirKind.IsNull)) return facts;
        HirNodeId operand = children[0];
        if (program.Node(operand).Kind == HirKind.TypeOf) operand = program.Children(operand)[0];
        HirNode read = program.Node(operand);
        if (read.Kind is not (HirKind.ReadLocation or HirKind.ReadStorage)) return facts;
        HirLocationId location = context.Location(new(read.Data));
        if (!facts.Contains(location)) return facts;
        TypeRef required = TypeRef.Null;
        if (test.Kind == HirKind.IsType)
        {
            HirNode target = program.Node(children[1]);
            if (target.Kind != HirKind.TypeValue) return facts;
            required = hir.Types[ResolveHirType(new(target.Data), context).Value].RuntimeType;
        }
        return facts.Refine(location, required, positive);
    }

    private SemanticValue? BoundValue(HirNodeId id, SemanticContext context)
    {
        HirNode node = program.Node(id);
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
            return context.Values.GetValueOrDefault(context.Location(new(node.Data)));
        if (node.Kind != HirKind.MemberRead || BoundValue(program.Children(id)[0], context)?.Scope is not { } scope) return null;
        scope = context.Objects.GetValueOrDefault(scope.Region, scope);
        return scope.Fields.GetValueOrDefault(node.Text!);
    }

    private ReturnShape Shape(HirTypeId type) => new(new(hir.Types[type.Value].RuntimeType, SemanticType: type));
    private static ReturnShape JoinShape(ReturnShape first, ReturnShape second, HirFlowTypeAlgebra types) =>
        first.Fact.Type.Kind == TypeKind.Never ? second : second.Fact.Type.Kind == TypeKind.Never ? first
            : new(types.Join(first.Fact, second.Fact), first.Eager || second.Eager);
}
