using System.Collections.Immutable;
using System.Text;
using Msync.Core;
using Msync.Hir;
using NativeConnection = Msync.Core.Native.NativeConnection;
using NativePath = Msync.Core.Native.NativePath;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly record struct ReturnShape(FlowFact Fact, bool Eager = false, SemanticValue? Value = null);
    private readonly Dictionary<string, ReturnShape> returnSchemes = new(StringComparer.Ordinal);
    private readonly HashSet<string> activeReturnSchemes = new(StringComparer.Ordinal);

    private readonly record struct CallInputLocations(HashSet<HirLocationId> Read, HashSet<HirLocationId> Declared);

    private CallInputLocations CallLocations(ReadOnlySpan<HirNodeId> children)
    {
        HashSet<HirLocationId> read = [], declared = [];
        foreach (HirNodeId child in children) CollectLocations(child, read, declared);
        read.ExceptWith(declared);
        return new(read, declared);
    }

    private ReturnShape? CallShape(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext caller,
        HashSet<HirLocationId> read)
    {
        // Eagerness is an expression policy, whereas future safety is a value
        // domain. Neither projection publishes or specializes executable code.
        ReturnShape? policy = ProjectCallShape(node, children, caller, read, future: false);
        if (policy is { } current && (current.Eager || (current.Fact.Type.Kind & TypeKind.Range) == 0)) return current;
        ReturnShape? domain = ProjectCallShape(node, children, caller, read, future: true);
        return domain is { } legal ? legal with { Eager = policy?.Eager ?? legal.Eager } : policy;
    }

    private ReturnShape? ProjectCallShape(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext caller,
        HashSet<HirLocationId> read, bool future)
    {
        SemanticContext context = caller.QueryFork();
        context.DeferredReads = future;
        context.FlowAnalysis = null;
        context.ReturnPlaces = null;
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
        FlowFacts facts = FlowFacts.CreateMutable(domains, new HirFlowTypeAlgebra(hir));
        foreach (HirLocationId location in domains.Keys)
            if (context.Values.TryGetValue(location, out SemanticValue? value))
                facts = facts.Assign(location, new(hir.Types[value.Type.Value].RuntimeType, Boolean: value.KnownBool,
                    SemanticType: value.Type, Denotations: value.DenotedType is { } denoted ? new([denoted]) : null, Eager: value.EagerRange));
        facts = facts.Freeze();
        List<ReturnShape> returns = [];
        ReturnShape[] arguments = new ReturnShape[children.Length - 1];
        for (int index = 0; index < arguments.Length; index++)
            arguments[index] = ProjectReturn(children[index + 1], context, ref facts, returns);
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

    private SemanticValue DeferredCall(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext caller,
        CallInputLocations locations)
    {
        SemanticContext deferred = new(new(caller.Publish ? hir.Functions.Count : -1), caller.Output, caller)
        { DeferredReads = true, NextRegion = caller.NextRegion };
        var captures = ImmutableArray.CreateBuilder<HirCapture>();
        Dictionary<HirLocationId, HirLocationId> sources = [];
        foreach (HirLocationId original in locations.Read.OrderBy(location => location.Value))
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
        foreach (HirLocationId local in locations.Declared) _ = AllocateThunkLocation(local, deferred);
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
        // A closed scalar annotation is already the return scheme. Do not
        // fingerprint a captured module/Scope merely to cache that answer.
        if (caller.QueryBinding is null && template.ResultAnnotation is { } fixedAnnotation && hir.Types[fixedAnnotation.Value].TypeParameter < 0)
        {
            HirTypeId resolved = ResolveHirType(fixedAnnotation, caller);
            if (ScalarReturn(hir.Types[resolved.Value].RuntimeType)) return Shape(resolved);
        }
        var key = caller.QueryBinding is not null ? "observation:" + templateId.Value : templateId.Value + (caller.DeferredReads ? "d:" : "i:") + string.Join(",", arguments.Select(argument =>
            (argument.Fact.SemanticType ?? hir.Type(argument.Fact.Type)).Value + (argument.Eager ? "!" : ".")
                + "T" + string.Join(".", argument.Fact.Denotations?.Types.Select(type => type.Value) ?? [])
                + argument.Fact.Boolean + (argument.Value is { } value ? ValueKey(value) + PolicyKey(value, caller) : "")))
            + ":" + string.Join(",", callable.Captures.IsDefault ? [] : callable.Captures.Select(capture =>
                caller.Storage.GetValueOrDefault(capture.Source, capture.Value.Type).Value + ":"
                    + ValueKey(caller.Values.GetValueOrDefault(capture.Source, capture.Value))
                    + PolicyKey(caller.Values.GetValueOrDefault(capture.Source, capture.Value), caller)));
        if (caller.QueryBinding is null && returnSchemes.TryGetValue(key, out var cached)) return cached;
        SemanticContext context = caller.QueryFork();
        context.FlowAnalysis = null;
        context.ReturnPlaces = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            HirLocationId parameter = template.Parameters[index];
            ReturnShape argument = arguments[index];
            HirTypeId type = argument.Fact.SemanticType ?? hir.Type(argument.Fact.Type);
            HirTypeId declared = hir.Locations[parameter.Value].StorageType;
            if (hir.Types[declared.Value].TypeParameter >= 0) context.Substitutions[declared] = type;
            context.Locations[parameter] = parameter;
            SemanticValue supplied = argument.Value is { } value
                ? value with { Type = type, Literal = caller.QueryBinding is null ? null : value.Literal, Node = HirNodeId.None } : ValueForType(type);
            if (argument.Fact.Denotations is { Types.Length: 1 } denotations)
                supplied = supplied with { DenotedType = denotations.Types[0] };
            context.Values[parameter] = supplied with { EagerRange = argument.Eager };
            context.Storage[parameter] = type;
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
                context.Locations[target] = target;
                context.Values[target] = captured with { Type = type, Node = HirNodeId.None };
                context.Storage[target] = type;
            }
        HirTypeId annotation = template.ResultAnnotation is { } annotated
            ? context.QueryBinding is null || TypeWitnessAvailable(annotated, context) ? ResolveHirType(annotated, context) : annotated : hir.Type(TypeRef.Any);
        if (caller.QueryBinding is null && template.ResultAnnotation is not null && ScalarReturn(hir.Types[annotation.Value].RuntimeType))
        {
            ReturnShape fixedResult = Shape(annotation);
            returnSchemes[key] = fixedResult;
            return fixedResult;
        }
        if (!activeReturnSchemes.Add(key))
            return Shape(template.ResultAnnotation is not null ? annotation : hir.Type(new TypeRef(TypeKind.Never)));
        try
        {
            // Future capture expansion can reenter this scheme before body projection starts.
            if (!callable.Captures.IsDefault && caller.DeferredReads)
                for (int index = 0; index < Math.Min(template.Captures.Length, callable.Captures.Length); index++)
                {
                    SemanticCapture capture = callable.Captures[index];
                    HirLocationId target = template.Captures[index].Target;
                    SemanticValue captured = context.Values[target];
                    if (capture.Mode == HirCaptureMode.Location && (!Stable(capture.Source) || captured.Scope is not null))
                        context.Values[target] = FutureRead(captured, captured.Type, context, capture.Source);
                }
            var algebra = new HirFlowTypeAlgebra(hir);
            if (caller.QueryBinding is null)
                context.FlowAnalysis = PrepareFlow(template.Body, context, initialType: static value => value.Type);
            FlowFacts facts = FlowFacts.Create(ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty, algebra);
            List<ReturnShape> exits = [];
            ReturnShape result = ProjectReturn(template.Body, context, ref facts, exits);
            foreach (ReturnShape returned in exits) result = JoinShape(result, returned, algebra, context.QueryBinding is not null);
            if (template.ResultAnnotation is not null)
            {
                TypeRef declared = hir.Types[annotation.Value].RuntimeType;
                result = result with
                {
                    Fact = algebra.Constrain(result.Fact,
                    new(declared, declared, StorageSemanticType: annotation))
                };
            }
            if (caller.QueryBinding is null) returnSchemes[key] = result with
            {
                Value = result.Value is { } callableResult
                && !callableResult.Callables.IsDefaultOrEmpty
                    ? CallableShapeValue(callableResult, new(ReferenceEqualityComparer.Instance)) : null
            };
            return result;
        }
        finally { activeReturnSchemes.Remove(key); }

        static bool ScalarReturn(TypeRef type) => (type.Kind & ~TypeKind.Null) is
            TypeKind.Number or TypeKind.Bool or TypeKind.String or TypeKind.Path or TypeKind.Process
                or TypeKind.Connection or TypeKind.Type;
    }

    private ReturnShape ProjectReturn(HirNodeId id, SemanticContext context, ref FlowFacts facts,
        List<ReturnShape> returns)
    {
        HirNode node = program.Node(id);
        ReadOnlySpan<HirNodeId> children = program.Children(id);
        if (context.FlowAnalysis is { } flow)
        {
            if (!flow.Inputs.TryGetValue(id, out FlowFacts? input) || !input.Reachable)
                return Shape(hir.Type(new TypeRef(TypeKind.Never)));
            facts = input;
        }
        if (node.Kind == HirKind.Literal)
        {
            HirTypeId type = TypeWitnessAvailable(node.Type, context) ? ResolveHirType(node.Type, context) : node.Type;
            return new(HirTypeProjection.LiteralFact(hir, node, type),
                Value: context.QueryBinding is null ? null : new(type, id, hir.Literals[node.Data]));
        }
        if (context.QueryBinding is { } binding && node.Kind is HirKind.ReadLocation or HirKind.ReadStorage)
        {
            HirLocationId location = context.Location(new(node.Data));
            SemanticValue value = context.Values.GetValueOrDefault(location) ?? binding(new(node.Data));
            context.Values[location] = value;
            FlowFact fact = facts.Contains(location) ? facts.Read(location) : HirTypeProjection.ValueFact(hir, value);
            return new(fact, fact.Eager, value with { Type = fact.SemanticType ?? value.Type, Node = id });
        }
        if (node.Kind == HirKind.Block)
        {
            ReturnShape result = Shape(hir.Type(TypeRef.Null));
            foreach (HirNodeId child in children)
            {
                result = ProjectReturn(child, context, ref facts, returns);
                if (result.Fact.Type.Kind == TypeKind.Never) break;
            }
            return result;
        }
        if (node.Kind == HirKind.Discard)
        {
            ReturnShape discarded = ProjectReturn(children[0], context, ref facts, returns);
            return SemanticControl.Terminates(discarded.Fact.Type) ? discarded
                : Shape(hir.Type(SemanticControl.Discard(discarded.Fact.Type)));
        }
        if (node.Kind is HirKind.Declare or HirKind.Assign)
        {
            ReturnShape value = ProjectReturn(children[0], context, ref facts, returns);
            HirLocationId location = context.Location(new(node.Data));
            HirTypeId type = value.Fact.SemanticType ?? hir.Type(value.Fact.Type);
            if (!facts.Contains(location)) facts = facts.Extend([new(location,
                new(value.Fact.Type, value.Fact.Type, StorageSemanticType: type, InitialSemanticType: type))]);
            facts = facts.Assign(location, value.Fact with { Eager = value.Eager });
            SemanticValue assigned = value.Value ?? ValueForType(type);
            HirTypeId declared = hir.Locations[location.Value].StorageType;
            HirTypeId storage = node.Kind == HirKind.Declare
                ? context.QueryBinding is null || TypeWitnessAvailable(declared, context)
                    ? StorageTypeFor(location, assigned, context) : declared
                : context.Storage.GetValueOrDefault(location, type);
            context.Storage[location] = storage;
            context.Values[location] = assigned with
            { StorageConstraint = storage,
                DenotedType = value.Fact.Denotations is { Types.Length: 1 } types ? types.Types[0] : assigned.DenotedType };
            return Shape(hir.Type(TypeRef.Null));
        }
        if (node.Kind == HirKind.Return)
        {
            returns.Add(children.Length == 0 ? Shape(hir.Type(TypeRef.Null))
                : ProjectReturn(children[0], context, ref facts, returns));
            return Shape(hir.Type(new TypeRef(TypeKind.Never)));
        }
        if (node.Kind == HirKind.If)
        {
            ReturnShape condition = ProjectReturn(children[0], context, ref facts, returns);
            if (condition.Fact.Boolean is { } known)
                return ProjectReturn(children[known ? 1 : 2], context, ref facts, returns);
            FlowFacts yes = RefineReturn(children[0], true, context, facts);
            FlowFacts no = RefineReturn(children[0], false, context, facts);
            ReturnShape accepted = ProjectReturn(children[1], context.QueryFork(), ref yes, returns);
            ReturnShape rejected = ProjectReturn(children[2], context.QueryFork(), ref no, returns);
            return JoinShape(accepted, rejected, new(hir), context.QueryBinding is not null);
        }
        if (node.Kind == HirKind.For)
        {
            FlowFacts body = facts;
            _ = ProjectReturn(children[1], context.QueryFork(), ref body, returns);
            return Shape(hir.Type(TypeRef.Null));
        }
        if (node.Kind == HirKind.Match)
        {
            ReturnShape result = Shape(hir.Type(new TypeRef(TypeKind.Never)));
            foreach (HirMatchArm arm in hir.Matches[node.Data].Arms)
            {
                FlowFacts branch = facts;
                ReturnShape value = ProjectReturn(arm.Value, context.QueryFork(), ref branch, returns);
                result = JoinShape(result, value, new(hir), context.QueryBinding is not null);
            }
            return result;
        }
        if (node.Kind == HirKind.Loop)
        {
            FlowFacts body = facts;
            _ = ProjectReturn(children[0], context.QueryFork(), ref body, returns);
            ReturnShape result = Shape(hir.Type(new TypeRef(TypeKind.Never)));
            foreach (HirNodeId exit in ReturnBreaks(children[0], node.Data))
            {
                FlowFacts branch = facts;
                var values = program.Children(exit);
                if (context.FlowAnalysis is { } analysis && !analysis.Inputs.ContainsKey(exit)) continue;
                if (context.FlowAnalysis is { } current) branch = current.Inputs[exit];
                ReturnShape value = values.Length == 0 ? Shape(hir.Type(TypeRef.Null))
                    : ProjectReturn(values[0], context.QueryFork(), ref branch, returns);
                result = JoinShape(result, value, new(hir), context.QueryBinding is not null);
            }
            return result;
        }
        if (node.Kind == HirKind.Break) return Shape(hir.Type(new TypeRef(TypeKind.Never)));
        if (node.Kind == HirKind.MemberRead)
        {
            ReturnShape receiver = ProjectReturn(children[0], context, ref facts, returns);
            if (receiver.Value?.Scope is { } row && node.Text is { } name)
            {
                row = context.Objects.GetValueOrDefault(row.Region, row);
                if (row.Fields.TryGetValue(name, out SemanticValue? member))
                {
                    HirFlowTypeAlgebra algebra = new(hir);
                    HirTypeId fieldType = algebra.Member(receiver.Fact.SemanticType ?? hir.Type(receiver.Fact.Type), name);
                    if (hir.Types[fieldType.Value].RuntimeType == TypeRef.Any) fieldType = member.Type;
                    FlowFact fact = Shape(fieldType).Fact;
                    if (row.Locations.TryGetValue(name, out HirLocationId location))
                        member = context.Values.GetValueOrDefault(location, member);
                    if (SchemePlace(id, context) is { } place && facts.Contains(place))
                    {
                        FlowFact selected = facts.Read(place);
                        HirTypeId narrowed = algebra.Narrow(fieldType, selected.Type, true);
                        fact = selected with { Type = hir.Types[narrowed.Value].RuntimeType, SemanticType = narrowed };
                    }
                    else fact = fact with { Boolean = member.KnownBool, Eager = member.EagerRange };
                    if (receiver.Value.RuntimeDependentMetadata) member = member with { RuntimeDependentMetadata = true };
                    return new(fact, fact.Eager || member.EagerRange || member.IndexedRegion is not null
                        || !member.IndexedAliases.IsDefaultOrEmpty, member);
                }
            }
        }
        if (node.Kind == HirKind.Call)
        {
            ReturnShape[] arguments = new ReturnShape[children.Length - 1];
            for (int index = 0; index < arguments.Length; index++)
                arguments[index] = ProjectReturn(children[index + 1], context, ref facts, returns);
            var call = hir.Calls[node.Data];
            IEnumerable<SemanticCallable> targets;
            if (context.QueryBinding is null) targets = BoundCallables(children[0], context);
            else
            {
                ReturnShape callee = ProjectReturn(children[0], context, ref facts, returns);
                targets = callee.Value is { } selected ? Choices(selected)
                    : callee.Fact.Callables?.Choices.Select(choice => new SemanticCallable(choice.Template, choice.NativeName)) ?? [];
            }
            var choices = targets.ToArray();
            string? native = call.NativeName ?? (choices.Length == 1 ? choices[0].NativeName : null);
            if (native is not null && NativeSignatures.TryGet(native, out var signature))
                return NativeShape(signature, arguments);
            ReturnShape? result = null;
            foreach (SemanticCallable choice in choices)
                if (SourceReturnShape(choice, arguments, context) is { } value)
                    result = result is { } previous ? JoinShape(previous, value, new(hir), context.QueryBinding is not null) : value;
            if (result is { } resolved) return resolved;
        }
        if (context.QueryBinding is not null && node.Kind is HirKind.Scope or HirKind.AllocateScope)
            foreach (HirNodeId child in children) _ = ProjectReturn(child, context, ref facts, returns);
        var projector = new HirTypeProjection(program, hir, context, new(hir),
            (callable, arguments) => SourceFlowReturn(callable, arguments, context),
            resolveCallable: callee => BoundCallables(callee, context),
                resolveType: type => TypeWitnessAvailable(type, context) ? ResolveHirType(type, context) : type);
        FlowFact projected = projector.Project(id, facts);
        SemanticValue? metadata = BoundValue(id, context);
        if (context.QueryBinding is not null)
        {
            if (node.Kind == HirKind.Builtin && node.Text != "std") metadata = Builtin(node, context);
            if (node.Kind is HirKind.QualifyOn or HirKind.PathJoin
                || node.Kind == HirKind.Binary && node.Data == (int)HirBinaryOperator.Divide)
            {
                SemanticValue? left = ProjectReturn(children[0], context, ref facts, returns).Value;
                SemanticValue? right = ProjectReturn(children[1], context, ref facts, returns).Value;
                NativePath? path = node.Kind == HirKind.QualifyOn
                    ? ProjectionPath(right) is { } raw && ProjectionEndpoint(left) is { } endpoint
                        && (endpoint.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
                            || endpoint is { Kind: ConnectionKind.Local, Address.Length: 0 })
                        ? PathSemantics.Qualify(raw, endpoint) : null
                    : ProjectionPath(left) is { } parent && ProjectionPath(right) is { } child
                        ? PathSemantics.JoinValue(parent, child) : null;
                if (path is { } selected) metadata = new(projected.SemanticType ?? node.Type, id,
                    Observation: new(Path: new(selected.Raw, selected.WorkingDirectory, selected.HomeDirectory,
                        selected.LocalSpelling, selected.Connection is { } owner ? new(owner.Kind, owner.Address) : null)));
            }
            if (node.Kind is HirKind.Scope or HirKind.AllocateScope)
            {
                HirScopeConstruction construction = hir.Scopes[node.Data];
                var locations = construction.Fields.ToImmutableDictionary(field => hir.Locations[field.Value].Name,
                    field => field, StringComparer.Ordinal);
                var fields = locations.ToImmutableDictionary(pair => pair.Key,
                    pair => ObservationBinding(pair.Value, context.Values.GetValueOrDefault(pair.Value)
                        ?? context.QueryBinding(pair.Value)), StringComparer.Ordinal);
                HirTypeId observedType = projected.SemanticType ?? node.Type;
                int layout = hir.Types[observedType.Value].Shape;
                metadata = new(observedType, id, Scope: new(-1, layout >= 0 ? layout : construction.Layout, fields,
                    locations, construction.Singleton >= 0, FieldDefinitions: ScopeDefinitions(node.Data, construction.Fields)));
            }
        }

        if (metadata is not null && IndexedResourceCount(metadata) != 0)
        {
            if (!context.DeferredReads) projected = projected with
            { Type = hir.Types[metadata.Type.Value].RuntimeType, SemanticType = metadata.Type };
        }
        if (node.Kind is HirKind.TemplateClosure or HirKind.NativeFunction)
            metadata = new(projected.SemanticType ?? hir.Type(projected.Type), HirNodeId.None,
                Callables: BoundCallables(id, context).ToImmutableArray());
        return new(projected, projected.Eager || metadata?.EagerRange == true,
            metadata is null ? null : metadata with
            { Type = projected.SemanticType ?? hir.Type(projected.Type), Node = context.QueryBinding is null ? HirNodeId.None : id, Literal = context.QueryBinding is null ? null : metadata.Literal });
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
                    ?? context.QueryBinding?.Invoke(location) ?? ValueForType(hir.Locations[location.Value].StorageType), capture.Mode);
            }).ToImmutableArray())];
        }
        if (node.Kind == HirKind.Call)
        {
            SemanticContext projection = context.QueryFork();
            projection.FlowAnalysis = null;
            FlowFacts facts = FlowFacts.Create(ImmutableDictionary<HirLocationId, FlowLocationDomain>.Empty, new HirFlowTypeAlgebra(hir));
            ReturnShape returned = ProjectReturn(id, projection, ref facts, []);
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
                        ?? context.QueryBinding?.Invoke(source) ?? ValueForType(hir.Locations[source.Value].StorageType), capture.Mode);
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
            return context.Values.GetValueOrDefault(context.Location(new(node.Data)))
                ?? context.QueryBinding?.Invoke(new(node.Data));
        if (node.Kind != HirKind.MemberRead || BoundValue(program.Children(id)[0], context)?.Scope is not { } scope) return null;
        scope = context.Objects.GetValueOrDefault(scope.Region, scope);
        return scope.Fields.GetValueOrDefault(node.Text!);
    }

    private ReturnShape Shape(HirTypeId type) => new(new(hir.Types[type.Value].RuntimeType, SemanticType: type));

    private ReturnShape NativeShape(NativeSignature signature, IReadOnlyList<ReturnShape> arguments)
    {
        if (signature.Name.StartsWith("meta.", StringComparison.Ordinal) && signature.Result == TypeRef.Bool
            && signature.Parameters.All(parameter => parameter == TypeRef.Type)
            && arguments.Count != 0 && arguments.All(argument => argument.Fact.Denotations is { Types.Length: 1 }
                || argument.Value?.DenotedType is not null))
        {
            TypeRef[] types = arguments.Select(argument => hir.Types[(argument.Fact.Denotations is { Types.Length: 1 } denotations
                ? denotations.Types[0] : argument.Value!.DenotedType!.Value).Value].RuntimeType).ToArray();
            var result = MetaTypes.Evaluate(signature.Name[5..], types);
            return new(new(TypeRef.Bool, Boolean: result.AsBool(), SemanticType: hir.Type(TypeRef.Bool)), false);
        }
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

    private static NativePath? ProjectionPath(SemanticValue? value) => value?.RuntimeDependentMetadata == true ? null
        : value?.Observation?.Path is { } path ? new(path.Raw, path.WorkingDirectory,
            path.Connection is { } owner ? new(owner.Kind, owner.Address) : null, path.HomeDirectory, path.LocalSpelling)
        : value?.Literal is { Kind: HirLiteralKind.Path or HirLiteralKind.String } literal ? new(literal.Text ?? "") : null;

    private static NativeConnection? ProjectionEndpoint(SemanticValue? value) => value?.RuntimeDependentMetadata == true ? null
        : value?.Observation?.Connection is { } endpoint ? new(endpoint.Kind, endpoint.Address)
        : value?.Literal is { Kind: HirLiteralKind.Connection } literal ? new(literal.ConnectionKind, literal.Text ?? "") : null;

    private ReturnShape JoinShape(ReturnShape first, ReturnShape second, HirFlowTypeAlgebra types, bool observe = false) =>
        first.Fact.Type.Kind == TypeKind.Never ? second : second.Fact.Type.Kind == TypeKind.Never ? first
            : new(types.Join(first.Fact, second.Fact), first.Eager || second.Eager,
                observe && first.Value is { } left && second.Value is { } right ? Join(left, right) : null);
}
