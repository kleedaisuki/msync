using System.Collections.Immutable;
using System.Text;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly record struct ReturnShape(FlowFact Fact, bool Eager = false, SemanticValue? Value = null);
    private readonly Dictionary<string, ReturnShape> returnSchemes = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeReturnSchemes = new(StringComparer.Ordinal);

    private ReturnShape? CallShape(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext caller)
    {
        // Eagerness is an expression policy, whereas future safety is a value
        // domain. Neither projection publishes or specializes executable code.
        ReturnShape? policy = ProjectCallShape(node, children, caller, future: false);
        if (policy is { } current && (current.Eager || (current.Fact.Type.Kind & TypeKind.Range) == 0)) return current;
        ReturnShape? domain = ProjectCallShape(node, children, caller, future: true);
        return domain is { } legal ? legal with { Eager = policy?.Eager ?? legal.Eager } : policy;
    }

    private ReturnShape? ProjectCallShape(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext caller,
        bool future)
    {
        SemanticContext context = caller.QueryFork();
        context.DeferredReads = future;
        context.FlowAnalysis = null;
        context.ReturnPlaces = null;
        HashSet<HirLocationId> read = [], declared = [];
        foreach (HirNodeId child in children) CollectLocations(child, read, declared);
        read.ExceptWith(declared);
        var domains = ImmutableDictionary.CreateBuilder<HirLocationId, FlowLocationDomain>();
        foreach (HirLocationId original in read)
        {
            HirLocationId location = context.Location(original);
            HirTypeId storage = context.Storage.GetValueOrDefault(location, hir.Locations[location.Value].StorageType);
            SemanticValue? current = context.Values.GetValueOrDefault(location);
            bool stable = Stable(original);
            HirTypeId initial = !future || stable ? current?.Type ?? storage : storage;
            if (future && current is not null && (!stable || current.Scope is not null))
                context.Values[location] = FutureRead(current, initial, context, location);
            domains[location] = new(hir.Types[storage.Value].RuntimeType, hir.Types[storage.Value].RuntimeType,
                stable, InitialType: hir.Types[initial.Value].RuntimeType,
                StorageSemanticType: storage, InitialSemanticType: initial, DeferredSemanticType: storage);
        }
        FlowFacts facts = FlowFacts.Create(domains.ToImmutable(), new HirFlowTypeAlgebra(hir));
        foreach (HirLocationId location in domains.Keys)
            if (context.Values.TryGetValue(location, out SemanticValue? value))
                facts = facts.Assign(location, new(hir.Types[value.Type.Value].RuntimeType, Boolean: value.KnownBool,
                    SemanticType: value.Type, Denotations: value.DenotedType is { } denoted ? new([denoted]) : null));
        Dictionary<HirLocationId, bool> eager = [];
        List<ReturnShape> returns = [];
        ReturnShape[] arguments = new ReturnShape[children.Length - 1];
        for (int index = 0; index < arguments.Length; index++)
            arguments[index] = ProjectReturn(children[index + 1], context, ref facts, eager, returns);
        SemanticCallable[] choices = BoundCallables(children[0], context).ToArray();
        string? native = hir.Calls[node.Data].NativeName ?? (choices.Length == 1 ? choices[0].NativeName : null);
        if (native is not null && NativeSignatures.TryGet(native, out var signature))
            return NativeShape(signature, arguments);
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
            HirTypeId storage = caller.Storage.GetValueOrDefault(source, value.Type);
            deferred.Values[target] = !Stable(original) || value.Scope is not null
                ? FutureRead(value, storage, deferred, source) : value;
            deferred.Storage[target] = storage;
            RegisterStorageAlias(target, source, deferred);
            captures.Add(new(target, HirCaptureMode.Location, source, HirNodeId.None));
            sources[target] = source;
        }
        foreach (HirLocationId local in declared) _ = AllocateThunkLocation(local, deferred);
        caller.Output?.ReserveFunction(new("<range-call>", [], captures.ToImmutable(),
            hir.Type(TypeRef.Any), HirNodeId.None, IsOpen: true));
        SemanticValue result = Call(node, children, deferred, allowDeferral: false);
        MergeSessionContracts(caller, [deferred]);
        result = PublishResultGraph(result, deferred, caller);
        var effects = result.DeferredEffects.IsDefault ? ImmutableArray<SemanticDeferredWrite>.Empty : result.DeferredEffects;
        effects = effects.Select(effect => effect.Kind == SemanticWriteKind.Location
            && sources.TryGetValue(effect.Location, out HirLocationId source)
                ? effect with { Location = source } : effect).ToImmutableArray();
        if (!caller.Publish) return result with { Node = HirNodeId.None, DeferredEffects = effects };
        caller.Output!.SealFunction(deferred.Function, new("<range-call>", [], captures.ToImmutable(), result.Type, result.Node));
        int descriptor = hir.Closures.Count;
        caller.Output.AddClosure(new(deferred.Function, captures.ToImmutable()));
        HirNodeId closure = caller.Output.Add(HirKind.Closure, hir.Type(TypeRef.Function), node.Source, data: descriptor);
        return result with
        {
            Node = caller.Output.Add(HirKind.DeferRange, result.Type, node.Source, [closure]),
            Location = null,
            DeferredEffects = effects
        };
    }

    private FlowFact? SourceFlowReturn(SemanticCallable callable, IReadOnlyList<FlowFact> arguments,
        SemanticContext context)
    {
        ReturnShape? result = SourceReturnShape(callable,
            arguments.Select(argument => new ReturnShape(argument, argument.Eager)).ToArray(), context);
        return result is { } returned ? returned.Fact with { Eager = returned.Eager } : null;
    }

    // This projection owns only type constraints. It cannot publish instructions,
    // instantiate executable functions, or apply a callee's effects to its caller.
    private ReturnShape? SourceReturnShape(SemanticCallable callable, IReadOnlyList<ReturnShape> arguments,
        SemanticContext caller)
    {
        if (callable.Template is not { } templateId) return null;
        HirTemplate template = hir.Templates[templateId.Value];
        if (template.Parameters.Length != arguments.Count) return null;
        var key = templateId.Value + (caller.DeferredReads ? "d:" : "i:") + string.Join(",", arguments.Select(argument =>
            (argument.Fact.SemanticType ?? hir.Type(argument.Fact.Type)).Value + (argument.Eager ? "!" : ".")
                + argument.Fact.Boolean + (argument.Value is { } value ? ValueKey(value) + PolicyKey(value, caller) : "")))
            + ":" + string.Join(",", callable.Captures.IsDefault ? [] : callable.Captures.Select(capture =>
                caller.Storage.GetValueOrDefault(capture.Source, capture.Value.Type).Value + ":"
                    + ValueKey(caller.Values.GetValueOrDefault(capture.Source, capture.Value))
                    + PolicyKey(caller.Values.GetValueOrDefault(capture.Source, capture.Value), caller)));
        if (returnSchemes.TryGetValue(key, out var cached)) return cached;
        SemanticContext context = caller.QueryFork();
        context.FlowAnalysis = null;
        context.ReturnPlaces = null;
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
            context.Values[parameter] = argument.Value is { } value
                ? value with { Type = type, Literal = null, Node = HirNodeId.None } : ValueForType(type);
            domains[parameter] = new(argument.Fact.Type, argument.Fact.Type,
                InitialType: argument.Fact.Type, StorageSemanticType: type, InitialSemanticType: type);
            eager[parameter] = argument.Eager;
        }
        if (!callable.Captures.IsDefault)
            for (int index = 0; index < Math.Min(template.Captures.Length, callable.Captures.Length); index++)
            {
                SemanticCapture capture = callable.Captures[index];
                HirLocationId target = template.Captures[index].Target;
                SemanticValue captured = capture.Mode == HirCaptureMode.Value ? capture.Value
                    : caller.Values.GetValueOrDefault(capture.Source, capture.Value);
                HirTypeId type = capture.Mode == HirCaptureMode.Location && caller.DeferredReads && !Stable(capture.Source)
                    ? caller.Storage.GetValueOrDefault(capture.Source, captured.Type) : captured.Type;
                if (capture.Mode == HirCaptureMode.Location && caller.DeferredReads
                    && (!Stable(capture.Source) || captured.Scope is not null))
                    captured = FutureRead(captured, type, context, capture.Source);
                context.Locations[target] = target;
                context.Values[target] = captured with { Type = type, Node = HirNodeId.None };
                domains[target] = new(hir.Types[type.Value].RuntimeType, hir.Types[type.Value].RuntimeType,
                    StorageSemanticType: type, InitialSemanticType: type);
                eager[target] = captured.EagerRange;
            }
        HirTypeId annotation = template.ResultAnnotation is { } annotated
            ? ResolveHirType(annotated, context) : hir.Type(TypeRef.Any);
        if (template.ResultAnnotation is not null && hir.Types[annotation.Value].RuntimeType.WithoutNull.Kind is
            TypeKind.Number or TypeKind.Bool or TypeKind.String or TypeKind.Path or TypeKind.Process
                or TypeKind.Connection or TypeKind.Type or TypeKind.Null)
        {
            ReturnShape fixedResult = Shape(annotation);
            returnSchemes[key] = fixedResult;
            return fixedResult;
        }
        if (!activeReturnSchemes.Add(key)) return Shape(annotation);
        try
        {
            var algebra = new HirFlowTypeAlgebra(hir);
            HashSet<HirLocationId> reads = [], locals = [];
            CollectLocations(template.Body, reads, locals);
            foreach (HirLocationId original in reads.Concat(locals))
            {
                HirLocationId location = context.Location(original);
                if (domains.ContainsKey(location)) continue;
                HirTypeId type = ResolveHirType(hir.Locations[original.Value].StorageType, context);
                domains[location] = new(hir.Types[type.Value].RuntimeType, hir.Types[type.Value].RuntimeType,
                    InitiallyAssigned: false, InitialType: new(TypeKind.Never),
                    StorageSemanticType: type, DeferredSemanticType: type);
            }
            FlowFacts facts = FlowFacts.Create(domains.ToImmutable(), algebra);
            for (int index = 0; index < template.Parameters.Length; index++)
                facts = facts.Assign(template.Parameters[index], arguments[index].Fact);
            foreach (HirCapture capture in template.Captures)
                if (context.Values.TryGetValue(capture.Target, out SemanticValue? captured))
                    facts = facts.Assign(capture.Target, new(hir.Types[captured.Type.Value].RuntimeType,
                        Boolean: captured.KnownBool, SemanticType: captured.Type,
                        Denotations: captured.DenotedType is { } denoted ? new([denoted]) : null));
            HashSet<int> described = [];
            foreach (HirLocationId input in template.Parameters.Concat(template.Captures.Select(capture => capture.Target)))
                if (context.Values.TryGetValue(input, out SemanticValue? value))
                    facts = SchemeScopeFacts(value, context, facts, described);
            facts = RegisterSchemePlaces(template.Body, context, facts);
            var projection = new HirTypeProjection(program, hir, context, algebra,
                (callee, inputs) => SourceFlowReturn(callee, inputs, context),
                places: place => SchemePlace(place, context),
                resolveCallable: callee => BoundCallables(callee, context));
            context.FlowAnalysis = new HirFlowGraphBuilder(program, projection.Project, context.Location,
                    place => SchemePlace(place, context))
                .Analyze(template.Body, facts);
            List<ReturnShape> exits = [];
            ReturnShape result = ProjectReturn(template.Body, context, ref facts, eager, exits);
            foreach (ReturnShape returned in exits) result = JoinShape(result, returned, algebra);
            if (template.ResultAnnotation is not null)
            {
                TypeRef declared = hir.Types[annotation.Value].RuntimeType;
                result = result with
                {
                    Fact = algebra.Constrain(result.Fact,
                    new(declared, declared, StorageSemanticType: annotation))
                };
            }
            returnSchemes[key] = result with
            {
                Value = result.Value is { } callableResult
                && !callableResult.Callables.IsDefaultOrEmpty
                    ? CallableShapeValue(callableResult, new(ReferenceEqualityComparer.Instance)) : null
            };
            return result;
        }
        finally { activeReturnSchemes.Remove(key); }
    }

    private ReturnShape ProjectReturn(HirNodeId id, SemanticContext context, ref FlowFacts facts,
        Dictionary<HirLocationId, bool> eager, List<ReturnShape> returns)
    {
        HirNode node = program.Node(id);
        ReadOnlySpan<HirNodeId> children = program.Children(id);
        if (context.FlowAnalysis is { } flow)
        {
            if (!flow.Inputs.TryGetValue(id, out FlowFacts? input) || !input.Reachable)
                return Shape(hir.Type(new TypeRef(TypeKind.Never)));
            facts = input;
        }
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
        if (node.Kind == HirKind.Discard)
        {
            ReturnShape discarded = ProjectReturn(children[0], context, ref facts, eager, returns);
            return SemanticControl.Terminates(discarded.Fact.Type) ? discarded
                : Shape(hir.Type(SemanticControl.Discard(discarded.Fact.Type)));
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
            context.Values[location] = value.Value ?? ValueForType(type);
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
            ReturnShape accepted = ProjectReturn(children[1], context.QueryFork(), ref yes, new(eager), returns);
            ReturnShape rejected = ProjectReturn(children[2], context.QueryFork(), ref no, new(eager), returns);
            return JoinShape(accepted, rejected, new(hir));
        }
        if (node.Kind == HirKind.For)
        {
            FlowFacts body = facts;
            _ = ProjectReturn(children[1], context.QueryFork(), ref body, new(eager), returns);
            return Shape(hir.Type(TypeRef.Null));
        }
        if (node.Kind == HirKind.Match)
        {
            ReturnShape result = Shape(hir.Type(new TypeRef(TypeKind.Never)));
            foreach (HirMatchArm arm in hir.Matches[node.Data].Arms)
            {
                FlowFacts branch = facts;
                ReturnShape value = ProjectReturn(arm.Value, context.QueryFork(), ref branch, new(eager), returns);
                result = JoinShape(result, value, new(hir));
            }
            return result;
        }
        if (node.Kind == HirKind.Loop)
        {
            FlowFacts body = facts;
            _ = ProjectReturn(children[0], context.QueryFork(), ref body, new(eager), returns);
            ReturnShape result = Shape(hir.Type(new TypeRef(TypeKind.Never)));
            foreach (HirNodeId exit in ReturnBreaks(children[0], node.Data))
            {
                FlowFacts branch = facts;
                var values = program.Children(exit);
                if (context.FlowAnalysis is { } analysis && !analysis.Inputs.ContainsKey(exit)) continue;
                if (context.FlowAnalysis is { } current) branch = current.Inputs[exit];
                ReturnShape value = values.Length == 0 ? Shape(hir.Type(TypeRef.Null))
                    : ProjectReturn(values[0], context.QueryFork(), ref branch, new(eager), returns);
                result = JoinShape(result, value, new(hir));
            }
            return result;
        }
        if (node.Kind == HirKind.Break) return Shape(hir.Type(new TypeRef(TypeKind.Never)));
        if (node.Kind == HirKind.MemberRead)
        {
            ReturnShape receiver = ProjectReturn(children[0], context, ref facts, eager, returns);
            if (receiver.Value?.Scope is { } row && node.Text is { } name)
            {
                row = context.Objects.GetValueOrDefault(row.Region, row);
                if (row.Fields.TryGetValue(name, out SemanticValue? member))
                {
                    FlowFact fact = Shape(member.Type).Fact;
                    if (row.Locations.TryGetValue(name, out HirLocationId location))
                        member = context.Values.GetValueOrDefault(location, member);
                    if (SchemePlace(id, context) is { } place && facts.Contains(place)) fact = facts.Read(place);
                    else fact = fact with { Boolean = member.KnownBool, Eager = member.EagerRange };
                    return new(fact, fact.Eager || member.EagerRange || member.IndexedRegion is not null
                        || !member.IndexedAliases.IsDefaultOrEmpty, member);
                }
            }
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
                return NativeShape(signature, arguments);
            ReturnShape? result = null;
            foreach (SemanticCallable choice in choices)
                if (SourceReturnShape(choice, arguments, context) is { } value)
                    result = result is { } previous ? JoinShape(previous, value, new(hir)) : value;
            if (result is { } resolved) return resolved;
        }
        var projector = new HirTypeProjection(program, hir, context, new(hir),
            (callable, arguments) => SourceFlowReturn(callable, arguments, context),
            resolveCallable: callee => BoundCallables(callee, context));
        FlowFact projected = projector.Project(id, facts);
        bool isEager = node.Kind is HirKind.ReadLocation or HirKind.ReadStorage
            && eager.GetValueOrDefault(context.Location(new(node.Data)));
        SemanticValue? metadata = BoundValue(id, context);
        if (metadata is not null && IndexedResourceCount(metadata) != 0)
        {
            metadata = RefreshIndexed(metadata, context);
            if (!context.DeferredReads) projected = projected with
            { Type = hir.Types[metadata.Type.Value].RuntimeType, SemanticType = metadata.Type };
        }
        if (node.Kind is HirKind.TemplateClosure or HirKind.NativeFunction)
            metadata = new(projected.SemanticType ?? hir.Type(projected.Type), HirNodeId.None,
                Callables: BoundCallables(id, context).ToImmutableArray());
        return new(projected, projected.Eager || isEager || metadata?.EagerRange == true,
            metadata is null ? null : metadata with
            { Type = projected.SemanticType ?? hir.Type(projected.Type), Node = HirNodeId.None, Literal = null });
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
        if (node.Kind == HirKind.Call)
        {
            SemanticContext projection = context.QueryFork();
            projection.FlowAnalysis = null;
            FlowFacts facts = FlowFacts.Create(ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty, new HirFlowTypeAlgebra(hir));
            ReturnShape returned = ProjectReturn(id, projection, ref facts, [], []);
            if (returned.Value is { } functionValue) return Choices(functionValue);
            if (returned.Fact.Callables is { } identities)
                return identities.Choices.Select(identity => new SemanticCallable(identity.Template, identity.NativeName));
            return [];
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

    private SemanticValue CallableShapeValue(SemanticValue value, HashSet<SemanticValue> active)
    {
        if (!active.Add(value)) return ValueForType(value.Type);
        SemanticScope? scope = value.Scope is { } row
            ? new(-1, row.Layout, row.Fields.ToImmutableDictionary(pair => pair.Key,
                pair => CallableShapeValue(pair.Value, active), StringComparer.Ordinal),
                ImmutableDictionary<string, HirLocationId>.Empty, row.Named) : null;
        var choices = Choices(value).Select(choice => choice with
        {
            Captures = choice.Captures.IsDefault ? [] : choice.Captures.Select((capture, index) =>
                new SemanticCapture(choice.Template is { } template && index < hir.Templates[template.Value].Captures.Length
                        ? hir.Templates[template.Value].Captures[index].Target : capture.Source,
                    CallableShapeValue(capture.Value, active), HirCaptureMode.Value)).ToImmutableArray()
        }).ToImmutableArray();
        SemanticValue result = new(value.Type, HirNodeId.None, Callables: choices, Scope: scope,
            Element: value.Element is { } element ? CallableShapeValue(element, active) : null,
            EagerRange: value.EagerRange, DenotedType: value.DenotedType);
        active.Remove(value);
        return result;
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

    private IEnumerable<HirNodeId> ReturnBreaks(HirNodeId id, int loop)
    {
        HirNode node = program.Node(id);
        if (node.Kind is HirKind.TemplateClosure or HirKind.Closure) yield break;
        if (node.Kind == HirKind.Break && node.Data == loop) { yield return id; yield break; }
        foreach (HirNodeId child in program.Children(id).ToArray())
            foreach (HirNodeId exit in ReturnBreaks(child, loop)) yield return exit;
        if (node.Kind == HirKind.Match)
            foreach (HirMatchArm arm in hir.Matches[node.Data].Arms)
                foreach (HirNodeId exit in ReturnBreaks(arm.Value, loop)) yield return exit;
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

    private ReturnShape NativeShape(NativeSignature signature, IReadOnlyList<ReturnShape> arguments)
    {
        HirTypeId? structural = null;
        if (arguments.Count != 0 && HirNativeResultTypes.IsStructuralRange(signature.ResultRule))
        {
            var first = arguments[0];
            structural = HirNativeResultTypes.Project(signature.ResultRule,
                first.Fact.SemanticType ?? hir.Type(first.Fact.Type),
                arguments.Count > 1 ? arguments[1].Fact.SemanticType ?? hir.Type(arguments[1].Fact.Type) : null,
                first.Value?.DenotedType,
                hir, new HirFlowTypeAlgebra(hir));
        }
        FlowFact fact = structural is { } dependent ? Shape(dependent).Fact : signature.ResultRule switch
        {
            NativeResultRule.FirstArgument when arguments.Count != 0 => arguments[0].Fact,
            NativeResultRule.RangeItem when arguments.Count != 0 => Shape(new HirFlowTypeAlgebra(hir)
                .Element(arguments[0].Fact.SemanticType ?? hir.Type(arguments[0].Fact.Type))).Fact,
            _ => Shape(hir.Type(signature.Result)).Fact
        };
        SemanticValue? metadata = signature.ResultRule switch
        {
            NativeResultRule.FirstArgument when arguments.Count != 0 && arguments[0].Value is { } first =>
                FirstNativeArgument(signature, first),
            NativeResultRule.RangeItem when arguments.Count != 0 && arguments[0].Value is { } range => Item(range),
            _ => null
        };
        metadata ??= structural is { } structured ? ValueForType(structured) : null;
        NativeBehavior behavior = signature.Behavior.GetValueOrDefault();
        bool eager = (behavior & NativeBehavior.EagerRange) != 0
            || signature.ResultRule == NativeResultRule.FirstArgument && arguments.Count != 0
                && (behavior & NativeBehavior.PreservesIdentity) != 0 && arguments[0].Eager;
        if (signature.ResultRule == NativeResultRule.RangeItem && metadata is not null)
            eager |= metadata.EagerRange || IndexedResourceCount(metadata) != 0;
        return new(fact, eager, metadata is null ? null : metadata with { EagerRange = eager });
    }

    private HirLocationId? SchemePlace(HirNodeId id, SemanticContext context)
    {
        if (context.ReturnPlaces?.TryGetValue(id, out HirLocationId registered) == true) return registered;
        HirNode node = program.Node(id);
        if (node.Kind is not (HirKind.MemberRead or HirKind.MemberAssign) || node.Text is null) return null;
        HirNodeId receiver = program.Children(id)[0];
        if (BoundValue(receiver, context)?.Scope is { } row)
        {
            row = context.Objects.GetValueOrDefault(row.Region, row);
            if (row.Locations.TryGetValue(node.Text, out HirLocationId location)) return location;
        }
        HirLocationId? field = (futureCallableWrites ??= new(program)).Field(receiver, node.Text);
        return field is { } definition ? context.Location(definition) : null;
    }

    private FlowFacts RegisterSchemePlaces(HirNodeId body, SemanticContext context, FlowFacts facts)
    {
        Dictionary<HirNodeId, HirLocationId> places = [];
        HashSet<HirNodeId> visited = [];
        Visit(body);
        context.ReturnPlaces = places;
        return facts;

        void Visit(HirNodeId id)
        {
            if (!visited.Add(id)) return;
            HirNode node = program.Node(id);
            if (node.Kind is HirKind.TemplateClosure or HirKind.Closure) return;
            if (SchemePlace(id, context) is { } place)
            {
                places[id] = place;
                if (!facts.Contains(place))
                {
                    HirLocation definition = hir.Locations[place.Value];
                    HirTypeId type = context.Storage.GetValueOrDefault(place, ResolveHirType(definition.StorageType, context));
                    if (definition.AdmitsNull) type = Nullable(type);
                    SemanticValue? value = BoundValue(id, context);
                    facts = facts.Extend([new(place, new(hir.Types[type.Value].RuntimeType,
                        hir.Types[type.Value].RuntimeType, ExternallyWritable: true,
                        InitialType: hir.Types[value?.Type.Value ?? type.Value].RuntimeType,
                        StorageSemanticType: type, InitialSemanticType: value?.Type ?? type))]);
                    if (value is not null) facts = facts.Assign(place, new(hir.Types[value.Type.Value].RuntimeType,
                        Boolean: value.KnownBool, SemanticType: value.Type, Eager: value.EagerRange));
                }
            }
            foreach (HirNodeId child in program.Children(id)) Visit(child);
            if (node.Kind == HirKind.Match)
                foreach (HirMatchArm arm in hir.Matches[node.Data].Arms) Visit(arm.Value);
        }
    }

    private FlowFacts SchemeScopeFacts(SemanticValue value, SemanticContext context, FlowFacts facts,
        HashSet<int> described)
    {
        if (value.Scope is not { } row || row.Region >= 0 && !described.Add(row.Region)) return facts;
        row = context.Objects.GetValueOrDefault(row.Region, row);
        foreach (var pair in row.Locations)
        {
            SemanticValue member = context.Values.GetValueOrDefault(pair.Value, row.Fields[pair.Key]);
            HirTypeId storage = context.Storage.GetValueOrDefault(pair.Value, member.Type);
            if (!facts.Contains(pair.Value))
                facts = facts.Extend([new(pair.Value, new(hir.Types[storage.Value].RuntimeType,
                    hir.Types[storage.Value].RuntimeType, ExternallyWritable: true,
                    StorageSemanticType: storage, InitialSemanticType: member.Type,
                    InitialType: hir.Types[member.Type.Value].RuntimeType))]);
            facts = facts.Assign(pair.Value, new(hir.Types[member.Type.Value].RuntimeType,
                Boolean: member.KnownBool, SemanticType: member.Type,
                Denotations: member.DenotedType is { } represented ? new([represented]) : null,
                Eager: member.EagerRange));
            facts = SchemeScopeFacts(member, context, facts, described);
        }
        return facts;
    }

    private string PolicyKey(SemanticValue value, SemanticContext context)
    {
        StringBuilder key = new();
        HashSet<SemanticValue> active = new(ReferenceEqualityComparer.Instance);
        Append(value);
        return key.ToString();

        void Append(SemanticValue current)
        {
            if (!active.Add(current)) { key.Append('^'); return; }
            key.Append(current.EagerRange ? '!' : '.').Append(current.KnownBool switch
            { true => 't', false => 'f', null => '?' }).Append(current.DenotedType?.Value ?? -1);
            if (current.Scope is { } row)
            {
                row = context.Objects.GetValueOrDefault(row.Region, row);
                foreach (var pair in row.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    key.Append(pair.Key).Append(':');
                    Append(row.Locations.TryGetValue(pair.Key, out HirLocationId location)
                        ? context.Values.GetValueOrDefault(location, pair.Value) : pair.Value);
                }
            }
            if (current.Element is { } element) Append(element);
            active.Remove(current);
        }
    }

    private static ReturnShape JoinShape(ReturnShape first, ReturnShape second, HirFlowTypeAlgebra types) =>
        first.Fact.Type.Kind == TypeKind.Never ? second : second.Fact.Type.Kind == TypeKind.Never ? first
            : new(types.Join(first.Fact, second.Fact), first.Eager || second.Eager);
}
