using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    internal sealed class InstantiationWorkspace
    {
        internal SemanticInstanceCache Instances { get; } = new();
        internal Dictionary<int, SemanticScope> Singletons { get; } = [];
        internal StorageConstraintGraph? StorageConstraints { get; set; }
    }

    // An instance is a code/signature promise in formal provenance space. Caller
    // locations and regions belong only to the short-lived projection below.
    internal sealed class FunctionInstance(HirFunctionId id)
    {
        public HirFunctionId Id { get; } = id;
        public SemanticValue Result { get; set; } = null!;
        public ImmutableArray<SemanticValue> Arguments { get; set; } = [];
        public ImmutableArray<SemanticValue> Captures { get; set; } = [];
        public ImmutableArray<HirLocationId> CaptureTargets { get; set; } = [];
        public ImmutableDictionary<int, SemanticScope> Objects { get; set; } = ImmutableDictionary<int, SemanticScope>.Empty;
        public ImmutableDictionary<int, SemanticIndexed> Indexed { get; set; } = ImmutableDictionary<int, SemanticIndexed>.Empty;
        public ImmutableDictionary<HirLocationId, SemanticValue> Cells { get; set; } = ImmutableDictionary<HirLocationId, SemanticValue>.Empty;
        public ImmutableDictionary<int, ImmutableHashSet<int>> AliasViews { get; set; } = ImmutableDictionary<int, ImmutableHashSet<int>>.Empty;
        public ImmutableDictionary<(int Region, string Field), ImmutableArray<SemanticMemberContract>> MemberContracts { get; set; }
            = ImmutableDictionary<(int Region, string Field), ImmutableArray<SemanticMemberContract>>.Empty;
        public ImmutableDictionary<HirLocationId, HirLocationId> Definitions { get; set; } = ImmutableDictionary<HirLocationId, HirLocationId>.Empty;
        public ImmutableArray<SemanticWriteEvent> WriteEvents { get; set; } = default;
        public ImmutableHashSet<int> IndexedWrites { get; set; } = [];
        public ImmutableHashSet<HirLocationId> CellWrites { get; set; } = [];
        public ImmutableHashSet<(int Region, string Field)> FieldWrites { get; set; } = [];
        public bool Complete { get; set; }
    }

    private readonly record struct FunctionInvocation(HirFunctionId Id, SemanticValue Result,
        ImmutableArray<SemanticDeferredWrite> Effects);
    private readonly SemanticInstanceCache instances = codeCache ?? new();
    private readonly Dictionary<int, SemanticScope> projectedSingletons = [];

    private SemanticContext QueryContext(SemanticContext context, SemanticValue callee,
        IReadOnlyList<SemanticValue> arguments)
    {
        SemanticContext query = new(context.Function)
        { Queries = context.Queries ?? new(), DeferredReads = context.DeferredReads };
        HashSet<SemanticValue> visited = new(ReferenceEqualityComparer.Instance);
        HashSet<int> importedRegions = [];
        Import(callee);
        foreach (var argument in arguments) Import(argument);
        return query;

        void Import(SemanticValue value)
        {
            if (!visited.Add(value)) return;
            if (value.Scope is { } scope)
            {
                scope = context.Objects.GetValueOrDefault(scope.Region, scope);
                if (scope.Region >= 0 && !importedRegions.Add(scope.Region)) return;
                query.Objects[scope.Region] = scope;
                foreach (var field in scope.Fields.Keys)
                    if (context.MemberContracts.TryGetValue((scope.Region, field), out var contracts))
                        query.MemberContracts[(scope.Region, field)] = contracts;
                if (context.AliasViews.TryGetValue(scope.Region, out var views))
                {
                    query.AliasViews[scope.Region] = views;
                    foreach (var view in views)
                        if (context.Objects.TryGetValue(view, out var viewScope))
                            Import(new(hir.Type(new HirType(TypeRef.Scope, Shape: viewScope.Layout)), HirNodeId.None, Scope: viewScope));
                }
                if (!scope.Aliases.IsDefault)
                    foreach (var alias in scope.Aliases)
                        if (context.Objects.TryGetValue(alias, out var aliasScope))
                            Import(new(hir.Type(new HirType(TypeRef.Scope, Shape: aliasScope.Layout)), HirNodeId.None, Scope: aliasScope));
                foreach (var field in scope.Fields.Values) Import(field);
                foreach (var location in scope.Locations.Values) ImportLocation(location);
            }
            if (value.IndexedRegion is { } region && context.Indexed.TryGetValue(region, out var indexed))
            {
                query.Indexed[region] = indexed;
                Import(indexed.Element);
            }
            if (!value.IndexedAliases.IsDefaultOrEmpty)
                foreach (int alias in value.IndexedAliases)
                    if (context.Indexed.TryGetValue(alias, out var storage))
                    {
                        query.Indexed[alias] = storage;
                        Import(storage.Element);
                    }
            if (value.Element is { } element) Import(element);
            if (!value.DeferredEffects.IsDefault)
                foreach (var write in value.DeferredEffects)
                {
                    Import(write.After);
                    if (write.Kind == SemanticWriteKind.Location) ImportLocation(write.Location);
                    else if (write.Kind == SemanticWriteKind.Field && context.Objects.TryGetValue(write.Region, out var target))
                        Import(new(hir.Type(new HirType(TypeRef.Scope, Shape: target.Layout)), HirNodeId.None, Scope: target));
                    else if (write.Kind == SemanticWriteKind.Indexed && context.Indexed.TryGetValue(write.Region, out var storage))
                        query.Indexed[write.Region] = storage;
                }
            foreach (var callable in Choices(value))
                if (!callable.Captures.IsDefault)
                    foreach (var capture in callable.Captures)
                    {
                        Import(capture.Value);
                        ImportLocation(capture.Source);
                    }
        }

        void ImportLocation(HirLocationId location)
        {
            if (context.Definitions.TryGetValue(location, out var definition)) query.Definitions[location] = definition;
            if (context.Storage.TryGetValue(location, out var type)) query.Storage[location] = type;
            if (!context.Values.TryGetValue(location, out var value)) return;
            query.Values[location] = value;
            Import(value);
        }
    }
    private SemanticValue TemplateClosure(HirNode node, SemanticContext context)
    {
        var closure = hir.TemplateClosures[node.Data];
        ImmutableArray<SemanticCapture>.Builder captures = ImmutableArray.CreateBuilder<SemanticCapture>();
        ImmutableArray<HirCapture>.Builder emitted = ImmutableArray.CreateBuilder<HirCapture>();
        foreach (var capture in closure.Captures)
        {
            HirLocationId source = context.Location(capture.SourceLocation);
            var value = context.Values.GetValueOrDefault(source)
                ?? ValueForType(ResolveHirType(hir.Locations[source.Value].StorageType, context));
            captures.Add(new(source, value, capture.Mode));
            emitted.Add(capture with { SourceLocation = source });
        }
        int index = hir.TemplateClosures.Count;
        if (context.Publish) context.Output!.AddTemplateClosure(new(closure.Template, emitted.ToImmutable()));
        var expression = context.Publish
            ? context.Output!.Add(HirKind.TemplateClosure, hir.Type(TypeRef.Function), node.Source, data: index)
            : HirNodeId.None;
        return new(hir.Type(TypeRef.Function), expression,
            Callables: [new(closure.Template, Captures: captures.ToImmutable())]);
    }

    private HirLocationId? CanonicalDefinition(HirLocationId location) =>
        program.TryDeclaration(location, out _) ? program.Original(location) : null;

    private FunctionInvocation Instantiate(HirTemplateId id, ImmutableArray<SemanticCapture> captured,
        IReadOnlyList<SemanticValue> arguments, HirSourceRange source, SemanticContext caller, bool applyEffects = true)
    {
        var template = hir.Templates[id.Value];
        if (arguments.Count != template.Parameters.Length)
        {
            var declaration = hir.Sources[template.Source.Source.Value];
            throw SemanticDiagnosticFactory.Arity(hir.Sources[source.Source.Value], new(source.Start, source.Length),
                $"Function expects {template.Parameters.Length} arguments, got {arguments.Count}.",
                template.Parameters.Select(parameter => hir.Locations[parameter.Value].Name),
                new(declaration.Name, declaration.Text ?? "", new(template.Source.Start, template.Source.Length),
                    "function signature declared here"));
        }
        RequireArgumentConcepts(template, arguments, source, caller);
        var actualCaptures = captured.IsDefault ? ImmutableArray<SemanticCapture>.Empty : captured;
        SemanticValue[] captureValues = new SemanticValue[actualCaptures.Length];
        for (int index = 0; index < actualCaptures.Length; index++)
        {
            var capture = actualCaptures[index];
            var value = capture.Mode == HirCaptureMode.Location
                ? StorageValue(capture.Source, caller) ?? capture.Value : capture.Value;
            var storage = capture.Mode == HirCaptureMode.Location ? StorageType(capture.Source, caller) : value.Type;
            if (hir.Types[value.Type.Value].TypeParameter >= 0) value = value with { Type = storage };
            if (caller.DeferredReads && capture.Mode == HirCaptureMode.Location
                && (!Stable(capture.Source) || value.Scope is not null))
                value = FutureRead(value, storage, caller, capture.Source);
            captureValues[index] = value;
        }
        string key = InstanceKey(id, arguments, captureValues, caller);
        var cache = caller.Publish ? instances : (caller.Queries ??= new()).Instances;
        if (!cache.TryGetValue(key, out var instance))
        {
            instance = BuildInstance(id, template, arguments, captureValues, actualCaptures, source, caller, cache, key);
        }
        return ProjectInvocation(instance, arguments, captureValues, actualCaptures, caller, applyEffects);
    }

    private FunctionInstance BuildInstance(HirTemplateId id, HirTemplate template,
        IReadOnlyList<SemanticValue> arguments, IReadOnlyList<SemanticValue> captureValues,
        ImmutableArray<SemanticCapture> captured, HirSourceRange source, SemanticContext caller,
        SemanticInstanceCache cache, string key)
    {
        SemanticValue? recursiveSignature = null;
        if (caller.Publish && template.ResultAnnotation is null
            && captureValues.Concat(arguments).Any(value => ContainsCallable(value, id, caller, new(ReferenceEqualityComparer.Instance))))
        {
            SemanticValue callable = new(hir.Type(TypeRef.Function), HirNodeId.None,
                Callables: [new(id, Captures: captured)]);
            var query = QueryContext(caller, callable, arguments);
            recursiveSignature = Instantiate(id, captured, arguments, source, query).Result;
        }
        HirFunctionId function = new(caller.Publish ? hir.Functions.Count : -1);
        caller.Output?.ReserveFunction(new(template.Name, [], [],
            hir.Type(new TypeRef(TypeKind.Never)), HirNodeId.None, id, IsOpen: true));
        SemanticContext context = new(function, caller.Output) { Queries = caller.Queries, DeferredReads = caller.DeferredReads };
        foreach (var declaration in program.Declarations)
        {
            var location = hir.Locations[declaration.Location.Value];
            if (location.TemplateOwner != id) continue;
            var target = declaration.Location;
            if (caller.Publish)
            {
                target = new(hir.Locations.Count);
                caller.Output!.ReserveLocation(location with { Owner = function, TemplateOwner = null });
            }
            context.Locations.Add(declaration.Location, target);
            context.Definitions[target] = program.Original(declaration.Location);
        }
        FormalInput input = new(this, caller, context);
        foreach (var pair in context.Locations) input.Locations[pair.Key] = pair.Value;
        for (int index = 0; index < template.Captures.Length; index++)
            input.Locations[captured[index].Source] = context.Location(template.Captures[index].Target);
        ImmutableArray<HirCapture>.Builder captures = ImmutableArray.CreateBuilder<HirCapture>();
        ImmutableArray<SemanticValue>.Builder formalCaptures = ImmutableArray.CreateBuilder<SemanticValue>();
        ImmutableArray<HirLocationId>.Builder captureTargets = ImmutableArray.CreateBuilder<HirLocationId>();
        for (int index = 0; index < template.Captures.Length; index++)
        {
            var definition = template.Captures[index];
            var target = context.Location(definition.Target);
            var value = input.Abstract(captureValues[index]);
            context.Values[target] = value;
            context.Storage[target] = StorageType(captured[index].Source, caller);
            if (StandardCell(captured[index].Source, caller) is { } moduleCell)
                RegisterStorageAlias(target, moduleCell, context);
            input.Locations[captured[index].Source] = target;
            formalCaptures.Add(value);
            captureTargets.Add(target);
            // SourceLocation describes the formal import, not the first caller's cell.
            captures.Add(new(target, captured[index].Mode, target, HirNodeId.None));
        }
        ImmutableArray<HirLocationId>.Builder parameters = ImmutableArray.CreateBuilder<HirLocationId>();
        ImmutableArray<SemanticValue>.Builder formalArguments = ImmutableArray.CreateBuilder<SemanticValue>();
        List<HirNodeId> parameterInitializers = [];
        for (int index = 0; index < template.Parameters.Length; index++)
        {
            var original = template.Parameters[index];
            var target = context.Location(original);
            var definition = hir.Locations[original.Value];
            var argument = input.Abstract(arguments[index]);
            var parameterType = ResolveHirType(definition.StorageType, context);
            if (hir.Types[parameterType.Value].TypeParameter >= 0)
            {
                context.Substitutions[parameterType] = argument.Type;
                parameterType = argument.Type;
            }
            else Require(argument, parameterType, source, true, context);
            if (!definition.AdmitsNull && Runtime(argument).Nullable)
                throw Error(source, $"Nullable argument requires nullable parameter '{definition.Name}?'.", "E0201");
            HirLocationId incoming = target;
            bool coercion = !hir.Types[parameterType.Value].RuntimeType.Accepts(Runtime(argument))
                && MetaTypes.IsConvertible(Runtime(argument), hir.Types[parameterType.Value].RuntimeType);
            if (coercion && context.Publish)
            {
                incoming = new(hir.Locations.Count);
                context.Output!.ReserveLocation(definition with { Name = "<input:" + definition.Name + ">", Owner = function,
                    TemplateOwner = null, StorageType = argument.Type });
                var read = context.Output!.Add(HirKind.ReadLocation, argument.Type, definition.Source, data: incoming.Value);
                argument = argument with { Node = read };
            }
            // Conversion is a typed operation at the function boundary, including
            // callbacks and dynamic dispatch. The backend merely lays out these nodes.
            var converted = Convert(argument, parameterType, definition.Source, context);
            if (coercion && context.Publish)
                parameterInitializers.Add(context.Output!.Add(HirKind.Declare, hir.Type(TypeRef.Null), definition.Source,
                    [converted.Node], data: target.Value));
            var value = converted with { Location = target, Node = HirNodeId.None };
            context.Values[target] = value;
            context.Storage[target] = definition.AdmitsNull ? Nullable(parameterType) : parameterType;
            caller.Output?.SetLocation(target, hir.Locations[target.Value] with { StorageType = context.Storage[target] });
            parameters.Add(incoming);
            formalArguments.Add(value);
        }
        input.Complete();
        for (int index = 0; index < formalArguments.Count; index++)
        {
            var target = context.Location(template.Parameters[index]);
            formalArguments[index] = input.AttachOrigin(formalArguments[index], target);
            context.Values[target] = formalArguments[index];
        }
        for (int index = 0; index < formalCaptures.Count; index++)
        {
            var target = captureTargets[index];
            formalCaptures[index] = input.AttachOrigin(context.Values[target], target);
            context.Values[target] = formalCaptures[index];
        }
        context.ExpectedReturn = template.ResultAnnotation is { } annotation ? ResolveHirType(annotation, context) : null;
        FunctionInstance instance = new(function)
        {
            Result = recursiveSignature is null
                ? ValueForType(context.ExpectedReturn ?? hir.Type(new TypeRef(TypeKind.Never)))
                : input.Abstract(recursiveSignature),
            Arguments = formalArguments.ToImmutable(), Captures = formalCaptures.ToImmutable(),
            CaptureTargets = captureTargets.ToImmutable()
        };
        // Both query and code generation reserve before examining a recursive body.
        cache.Add(key, instance);
        try
        {
            var body = Analyze(template.Body, context);
            var result = body;
            foreach (var returned in context.Returns) result = Join(result, returned);
            if (context.ExpectedReturn is { } expected)
            {
                Require(result, expected, template.Source, true, context);
                result = Convert(result, expected, template.Source, context);
                if (result.Node != body.Node) body = result;
            }
            if (context.Publish && parameterInitializers.Count != 0)
            {
                parameterInitializers.Add(body.Node);
                body = body with { Node = context.Output!.Add(HirKind.Block, body.Type, template.Source,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(parameterInitializers)) };
            }
            instance.Result = result with { Location = null };
            FreezeSummary(instance, context);
            instance.Complete = true;
            caller.Output?.SealFunction(function, new(template.Name,
                parameters.ToImmutable(), captures.ToImmutable(), result.Type, body.Node, id));
            return instance;
        }
        catch (CompileException error)
        {
            cache.Remove(key);
            var document = hir.Sources[source.Source.Value];
            error.Diagnostic.Related.Add(new(document.Name, document.Text ?? "",
                new(source.Start, source.Length), "while checking this call"));
            throw;
        }
    }

    private void FreezeSummary(FunctionInstance instance, SemanticContext context)
    {
        Dictionary<int, SemanticScope> objects = [];
        Dictionary<int, SemanticIndexed> indexed = [];
        Dictionary<HirLocationId, SemanticValue> cells = [];
        HashSet<SemanticValue> visited = new(ReferenceEqualityComparer.Instance);
        Visit(instance.Result);
        foreach (var argument in instance.Arguments) Visit(argument);
        foreach (var capture in instance.Captures) Visit(capture);
        foreach (var target in instance.CaptureTargets) VisitCell(target);
        instance.Objects = objects.ToImmutableDictionary();
        instance.Indexed = indexed.ToImmutableDictionary();
        instance.Cells = cells.ToImmutableDictionary();
        instance.FieldWrites = context.FieldWrites.ToImmutableHashSet();
        instance.CellWrites = context.CellWrites.ToImmutableHashSet();
        instance.IndexedWrites = context.IndexedWrites.ToImmutableHashSet();
        instance.WriteEvents = context.WriteEvents is null ? default : context.WriteEvents.ToImmutableArray();
        instance.MemberContracts = context.MemberContracts.Where(pair => objects.ContainsKey(pair.Key.Region))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
        HashSet<HirLocationId> reachedLocations = [.. cells.Keys, .. instance.CaptureTargets];
        foreach (var argument in instance.Arguments)
            if (argument.Location is { } location) reachedLocations.Add(location);
        foreach (var scope in objects.Values)
            reachedLocations.UnionWith(scope.Locations.Values);
        instance.Definitions = context.Definitions.Where(pair => reachedLocations.Contains(pair.Key))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
        instance.AliasViews = context.AliasViews.Where(pair => objects.ContainsKey(pair.Key))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value);

        void VisitRegion(int region)
        {
            if (context.Objects.TryGetValue(region, out var scope) && !objects.ContainsKey(region))
                Visit(new(new(-1), HirNodeId.None, Scope: scope));
        }
        void VisitCell(HirLocationId location)
        {
            if (cells.ContainsKey(location) || StorageValue(location, context) is not { } value) return;
            cells[location] = value;
            Visit(value);
        }
        void Visit(SemanticValue value)
        {
            if (!visited.Add(value)) return;
            if (value.Scope is { } scope)
            {
                scope = context.Objects.GetValueOrDefault(scope.Region, scope);
                if (objects.TryAdd(scope.Region, scope))
                {
                    foreach (var field in scope.Fields.Values) Visit(field);
                    if (!scope.Aliases.IsDefault)
                        foreach (var alias in scope.Aliases) VisitRegion(alias);
                    if (context.AliasViews.TryGetValue(scope.Region, out var views))
                        foreach (var view in views) VisitRegion(view);
                }
            }
            if (!value.IndexedAliases.IsDefaultOrEmpty)
                foreach (int alias in value.IndexedAliases)
                    if (context.Indexed.TryGetValue(alias, out var aliasedStorage) && indexed.TryAdd(alias, aliasedStorage))
                        Visit(aliasedStorage.Element);
            if (value.Element is { } element) Visit(element);
            if (!value.DeferredEffects.IsDefault)
                foreach (var write in value.DeferredEffects)
                {
                    Visit(write.After);
                    if (write.Kind == SemanticWriteKind.Location) VisitCell(write.Location);
                    else if (write.Kind == SemanticWriteKind.Field) VisitRegion(write.Region);
                    else if (write.Kind == SemanticWriteKind.Indexed && context.Indexed.TryGetValue(write.Region, out var deferredStorage))
                        indexed.TryAdd(write.Region, deferredStorage);
                }
            foreach (var callable in Choices(value))
                if (!callable.Captures.IsDefault)
                    foreach (var capture in callable.Captures) { VisitCell(capture.Source); Visit(capture.Value); }
            if (!value.Alternatives.IsDefault)
                foreach (var alternative in value.Alternatives) Visit(alternative);
            if (value.IndexedRegion is { } region && context.Indexed.TryGetValue(region, out var storage)
                && indexed.TryAdd(region, storage)) Visit(storage.Element);
        }
    }

    private static bool ContainsCallable(SemanticValue value, HirTemplateId template, SemanticContext context,
        HashSet<SemanticValue> visited)
    {
        if (!visited.Add(value)) return false;
        foreach (var callable in Choices(value))
        {
            if (callable.Template == template) return true;
            if (!callable.Captures.IsDefault)
                foreach (var capture in callable.Captures)
                    if (ContainsCallable(context.Values.GetValueOrDefault(capture.Source, capture.Value), template, context, visited)) return true;
        }
        if (value.Scope is { } scope)
        {
            scope = context.Objects.GetValueOrDefault(scope.Region, scope);
            foreach (var field in scope.Fields.Values)
                if (ContainsCallable(field, template, context, visited)) return true;
        }
        return value.Element is { } element && ContainsCallable(element, template, context, visited);
    }

}
