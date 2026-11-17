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
        public IReadOnlyDictionary<int, SemanticOrigin> InputScopePaths { get; init; } = null!;
        public IReadOnlyList<(SemanticOrigin Path, SemanticValue Value)> NestedInputs { get; init; } = null!;
        public ImmutableDictionary<int, ImmutableHashSet<int>> AliasEffects { get; set; } = ImmutableDictionary<int, ImmutableHashSet<int>>.Empty;
        public ImmutableDictionary<int, SemanticScope> Objects { get; set; } = ImmutableDictionary<int, SemanticScope>.Empty;
        public ImmutableDictionary<int, SemanticIndexed> Indexed { get; set; } = ImmutableDictionary<int, SemanticIndexed>.Empty;
        public ImmutableDictionary<HirLocationId, SemanticValue> Cells { get; set; } = ImmutableDictionary<HirLocationId, SemanticValue>.Empty;
        public ImmutableDictionary<HirLocationId, HirTypeId> Storage { get; set; } = ImmutableDictionary<HirLocationId, HirTypeId>.Empty;
        public ImmutableHashSet<HirLocationId> NamedCells { get; set; } = [];
        public ImmutableDictionary<(int Region, string Field), ImmutableArray<SemanticMemberContract>> MemberContracts { get; set; }
            = ImmutableDictionary<(int Region, string Field), ImmutableArray<SemanticMemberContract>>.Empty;
        public ImmutableDictionary<HirLocationId, HirLocationId> Definitions { get; set; } = ImmutableDictionary<HirLocationId, HirLocationId>.Empty;
        public ImmutableDictionary<HirLocationId, ImmutableArray<int>> CallableContracts { get; set; }
            = ImmutableDictionary<HirLocationId, ImmutableArray<int>>.Empty;
        public ImmutableArray<SemanticWriteEvent> WriteEvents { get; set; } = [];
        public bool Complete { get; set; }
    }

    private readonly record struct FunctionInvocation(HirFunctionId Id, SemanticValue Result,
        ImmutableArray<SemanticDeferredWrite> Effects);
    private readonly SemanticInstanceCache instances = codeCache ?? new();
    private readonly Dictionary<int, SemanticScope> projectedSingletons = [];

    private SemanticContext QueryContext(SemanticContext context) => new(context.Function, parent: context)
    { Queries = context.Queries ?? queries, DeferredReads = context.DeferredReads };

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
        return Trace is null ? InstantiateCore(id, captured, arguments, source, caller, applyEffects)
            : Trace.MeasureDetailed("compiler.semantic.instantiate", (self: this, id, captured, arguments, source, caller, applyEffects),
                static state => state.self.InstantiateCore(state.id, state.captured, state.arguments,
                    state.source, state.caller, state.applyEffects),
                attributes: new Dictionary<string, string>(HirTelemetry.Source(hir.Sources, source))
                { ["template.name"] = hir.Templates[id.Value].Name, ["template.id"] = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                  ["semantic.mode"] = caller.Publish ? "publish" : "query" });
    }

    private FunctionInvocation InstantiateCore(HirTemplateId id, ImmutableArray<SemanticCapture> captured,
        IReadOnlyList<SemanticValue> arguments, HirSourceRange source, SemanticContext caller, bool applyEffects)
    {
        var template = hir.Templates[id.Value];
        Trace?.Metric("arguments", arguments.Count);
        Trace?.Metric("captures", captured.IsDefault ? 0 : captured.Length);
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
        SemanticValue[]? preparedArguments = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            SemanticValue enriched = EnrichFutureDomains(arguments[index], caller);
            if (ReferenceEquals(enriched, arguments[index])) continue;
            preparedArguments ??= arguments.ToArray();
            preparedArguments[index] = enriched;
        }
        if (preparedArguments is not null) arguments = preparedArguments;
        var actualCaptures = captured.IsDefault ? ImmutableArray<SemanticCapture>.Empty : captured;
        SemanticValue[] captureValues = new SemanticValue[actualCaptures.Length];
        for (int index = 0; index < actualCaptures.Length; index++)
        {
            var capture = actualCaptures[index];
            var value = capture.Mode == HirCaptureMode.Location
                ? StorageValue(capture.Source, caller) ?? capture.Value : capture.Value;
            var storage = capture.Mode == HirCaptureMode.Location ? StorageType(capture.Source, caller) : value.Type;
            if (capture.Mode == HirCaptureMode.Location)
                value = !ReadsCapture(id, index, out _) ? ValueForType(storage) with { StorageConstraint = storage }
                    : value with { StorageConstraint = storage };
            if (hir.Types[value.Type.Value].TypeParameter >= 0) value = value with { Type = storage };
            value = EnrichFutureDomains(value, caller, capture.Source, rootStorage: capture.Mode == HirCaptureMode.Location);
            if (caller.DeferredReads && capture.Mode == HirCaptureMode.Location
                && (!Stable(caller.Definitions.GetValueOrDefault(capture.Source, capture.Source)) || value.Scope is not null))
                value = FutureRead(value, storage, caller, capture.Source);
            captureValues[index] = value;
        }
        string key = InstanceKey(id, arguments, captureValues, caller);
        Trace?.Metric("key.characters", key.Length);
        var cache = caller.Publish ? instances : (caller.Queries ??= queries).Instances;
        if (!cache.TryGetValue(key, out var instance))
        {
            Trace?.Metric("cache.miss", 1);
            // A new specialization could add concrete facts to an earlier
            // function body. That edit must replace the whole projection.
            if (id.Value < reusedEditorTemplates) throw new EditorPrefixChanged();
            instance = BuildInstance(id, template, arguments, captureValues, actualCaptures, source, caller, cache, key);
        }
        else Trace?.Metric(instance.Complete ? "cache.hit" : "cache.recursive", 1);
        return ProjectInvocation(instance, arguments, captureValues, actualCaptures, caller, applyEffects, source);
    }

    private FunctionInstance BuildInstance(HirTemplateId id, HirTemplate template,
        IReadOnlyList<SemanticValue> arguments, IReadOnlyList<SemanticValue> captureValues,
        ImmutableArray<SemanticCapture> captured, HirSourceRange source, SemanticContext caller,
        SemanticInstanceCache cache, string key)
    {
        SemanticValue? recursiveSignature = null;
        if (caller.Publish && template.ResultAnnotation is null
            && (MayContainCallable(captureValues) || MayContainCallable(arguments))
            && (futureCallableWrites ??= new(program)).MayReenter(id)
            && ContainsCallableInput(captureValues, arguments, id, caller))
        {
            var query = QueryContext(caller);
            recursiveSignature = Instantiate(id, captured, arguments, source, query).Result;
        }
        HirFunctionId function = new(caller.Publish ? hir.Functions.Count : -1);
        if (Trace is not null)
            Trace.Report(new("compiler.semantic.specialization", CompilationTrace.UnixNow(), 0, 0, CompilationOutcome.Ok, Aggregated: true)
            {
                Attributes = new Dictionary<string, string>(HirTelemetry.Source(hir.Sources, source))
                {
                    ["template.id"] = id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["function.id"] = function.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["specialization.key"] = key
                }
            });
        caller.Output?.ReserveFunction(new(template.Name, [], [],
            hir.Type(new TypeRef(TypeKind.Never)), HirNodeId.None, id, IsOpen: true));
        SemanticContext context = new(function, caller.Output)
        { Queries = caller.Queries, DeferredReads = caller.DeferredReads, WriteEvents = [] };
        foreach (var declaration in program.DeclarationsOf(id))
        {
            var location = hir.Locations[declaration.Location.Value];
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
        context.InputFields = input;
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
            var storage = StorageType(captured[index].Source, caller);
            context.Storage[target] = captured[index].Mode == HirCaptureMode.Location ? StructuralStorageType(storage) : storage;
            context.Output?.SetLocation(target, hir.Locations[target.Value] with { StorageType = context.Storage[target] });
            if (caller.CallableContracts.TryGetValue(captured[index].Source, out var callableContracts))
                context.CallableContracts[target] = callableContracts;
            if (StandardCell(captured[index].Source, caller) is { } moduleCell)
                RegisterStorageAlias(target, moduleCell, context);
            input.Locations[captured[index].Source] = target;
            formalCaptures.Add(value);
            captureTargets.Add(target);
            // SourceLocation describes the formal import, not the first caller's cell.
            captures.Add(new(target, captured[index].Mode, target, HirNodeId.None));
        }
        ImmutableArray<HirLocationId>.Builder parameters = ImmutableArray.CreateBuilder<HirLocationId>();
        var parameterEntryTypes = caller.Publish ? ImmutableArray.CreateBuilder<HirTypeId>(template.Parameters.Length) : null;
        ImmutableArray<SemanticValue>.Builder formalArguments = ImmutableArray.CreateBuilder<SemanticValue>();
        List<HirNodeId> parameterInitializers = [];
        for (int index = 0; index < template.Parameters.Length; index++)
        {
            var original = template.Parameters[index];
            var target = context.Location(original);
            var definition = hir.Locations[original.Value];
            var argument = input.Abstract(arguments[index] with { StorageConstraint = null });
            parameterEntryTypes?.Add(argument.Type);
            var parameterType = ResolveHirType(definition.StorageType, context);
            if (hir.Types[parameterType.Value].TypeParameter >= 0)
            {
                context.Substitutions[parameterType] = argument.Type;
                parameterType = argument.Type;
            }
            else
            {
                if (definition.AdmitsNull) parameterType = Nullable(parameterType);
                Require(argument, parameterType, source, true, context);
            }
            if (!AdmitsNull(original, parameterType) && Runtime(argument).Nullable)
                throw Error(source, $"Nullable argument requires nullable parameter '{definition.Name}?'.", "E0201");
            HirLocationId incoming = target;
            bool coercion = !hir.Types[parameterType.Value].RuntimeType.Accepts(Runtime(argument))
                && MetaTypes.IsConvertible(Runtime(argument), hir.Types[parameterType.Value].RuntimeType);
            if (coercion && context.Publish)
            {
                incoming = new(hir.Locations.Count);
                context.Output!.ReserveLocation(definition with
                {
                    Name = "<input:" + definition.Name + ">",
                    Owner = function,
                    TemplateOwner = null,
                    StorageType = argument.Type
                });
                var read = context.Output!.Add(HirKind.ReadLocation, argument.Type, definition.Source, data: incoming.Value);
                argument = argument with { Node = read };
            }
            // Conversion is a typed operation at the function boundary, including
            // callbacks and dynamic dispatch. The backend merely lays out these nodes.
            var converted = Convert(argument, parameterType, definition.Source, context);
            if (coercion && context.Publish)
                parameterInitializers.Add(context.Output!.Add(HirKind.Declare, hir.Type(TypeRef.Null), definition.Source,
                    [converted.Node], data: target.Value));
            var value = converted with { Location = target, Node = HirNodeId.None,
                RuntimeDependentMetadata = converted.RuntimeDependentMetadata
                    || IsScalarSchema(converted) && Runtime(converted).WithoutNull != TypeRef.Type
                        && (Runtime(converted).Kind & TypeKind.Scope) == 0 };
            context.Values[target] = value;
            HirTypeId storageType = definition.AdmitsNull ? Nullable(parameterType) : parameterType;
            context.Storage[target] = program.Declaration(original).Annotation is null
                ? InferredStorageType(storageType) : StructuralStorageType(storageType);
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
            Arguments = formalArguments.ToImmutable(),
            Captures = formalCaptures.ToImmutable(),
            InputScopePaths = input.ScopePaths,
            NestedInputs = input.NestedInputs,
            CaptureTargets = captureTargets.ToImmutable()
        };
        // Both query and code generation reserve before examining a recursive body.
        var entryAliases = context.AliasViews.ToImmutableDictionary();
        cache.Add(key, instance);
        try
        {
            var body = Analyze(template.Body, context);
            if (context.ExpectedReturn is { } bodyType)
            {
                Require(body, bodyType, template.Source, true, context);
                body = Convert(body, bodyType, template.Source, context);
            }
            var result = body;
            foreach (var returned in context.Returns) result = Join(result, returned.Value);
            if (context.ExpectedReturn is { } expected)
                Require(result, expected, template.Source, true, context);
            if (context.Publish && parameterInitializers.Count != 0)
            {
                parameterInitializers.Add(body.Node);
                body = body with
                {
                    Node = context.Output!.Add(HirKind.Block, body.Type, template.Source,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(parameterInitializers))
                };
            }
            var observation = context.Publish && Runtime(result) == TypeRef.Path
                ? observations?.ReturnedPath(body, context) : null;
            result = Trace?.Detail is { } exportTrace
                ? exportTrace.Measure("compiler.semantic.export_type", (Self: this, result, context),
                    static state => state.Self.ResolveExportValue(state.result, state.context))
                : ResolveExportValue(result, context);
            instance.Result = result with { Node = HirNodeId.None, Location = null, Observation = observation ?? result.Observation };
            var exits = context.Returns.Select(exit => exit.Events).ToList();
            if (Runtime(body).Kind != TypeKind.Never) exits.Add(context.WriteEvents?.ToImmutableArray() ?? []);
            Trace?.Metric("plan.exits", exits.Count);
            context.WriteEvents = MergeEventSequences(exits);
            if (Trace?.Detail is { } summaryTrace)
                summaryTrace.Run("compiler.semantic.freeze_summary", (Self: this, instance, context, entryAliases),
                    static state => state.Self.FreezeSummary(state.instance, state.context, state.entryAliases));
            else FreezeSummary(instance, context, entryAliases);
            Trace?.Metric("input.fields.loaded", input.FieldLoads);
            instance.Complete = true;
            caller.Output?.SealFunction(function, new(template.Name,
                parameters.ToImmutable(), captures.ToImmutable(), result.Type, body.Node, id)
            {
                ParameterEntryTypes = parameterEntryTypes!.MoveToImmutable()
            });
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

    private void FreezeSummary(FunctionInstance instance, SemanticContext context,
        IReadOnlyDictionary<int, ImmutableHashSet<int>> entryAliases)
    {
        Dictionary<int, SemanticScope> objects = [];
        Dictionary<int, SemanticIndexed> indexed = [];
        Dictionary<HirLocationId, SemanticValue> cells = [];
        HashSet<HirLocationId>? namedCells = null;
        HashSet<SemanticValue> visited = new(ReferenceEqualityComparer.Instance);
        Visit(instance.Result);
        foreach (var argument in instance.Arguments) Visit(argument);
        foreach (var capture in instance.Captures) Visit(capture);
        foreach (var target in instance.CaptureTargets) VisitCell(target);
        HashSet<SemanticWriteEvent> exported = new(ReferenceEqualityComparer.Instance);
        int discovered;
        do
        {
            discovered = exported.Count;
            VisitEvents(context.WriteEvents ?? []);
        } while (exported.Count != discovered);
        instance.Objects = objects.ToImmutableDictionary();
        instance.Indexed = indexed.ToImmutableDictionary();
        instance.Cells = cells.ToImmutableDictionary();
        instance.NamedCells = namedCells?.ToImmutableHashSet() ?? [];
        instance.WriteEvents = ExportEvents(context.WriteEvents ?? []);
        Trace?.Metric("plan.writes.exported", exported.Count);
        instance.MemberContracts = context.MemberContracts.Where(pair => ExportedScope(pair.Key.Region))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
        HashSet<HirLocationId> reachedLocations = [.. cells.Keys, .. instance.CaptureTargets];
        foreach (var argument in instance.Arguments)
            if (argument.Location is { } location) reachedLocations.Add(location);
        foreach (var scope in objects.Values)
            reachedLocations.UnionWith(scope.Locations.Values);
        instance.Storage = reachedLocations.ToImmutableDictionary(location => location, location => StorageType(location, context));
        instance.Definitions = context.Definitions.Where(pair => reachedLocations.Contains(pair.Key))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
        instance.CallableContracts = context.EstablishedCallableContracts.Where(pair => reachedLocations.Contains(pair.Key))
            .ToImmutableDictionary(pair => pair.Key, pair => pair.Value);
        // Entry aliases are premises, not creations. Replaying them would give
        // borrowed external families fresh identities on every invocation.
        var aliasEffects = ImmutableDictionary.CreateBuilder<int, ImmutableHashSet<int>>();
        foreach (var pair in context.AliasViews)
        {
            if (!ExportedScope(pair.Key)) continue;
            var introduced = pair.Value.Except(entryAliases.GetValueOrDefault(pair.Key, ImmutableHashSet<int>.Empty));
            if (!introduced.IsEmpty) aliasEffects.Add(pair.Key, introduced);
        }
        instance.AliasEffects = aliasEffects.ToImmutable();

        void VisitEvents(IEnumerable<SemanticWriteEvent> events)
        {
            foreach (var item in events)
            {
                if (item.Write is { } write && Escapes(write) && exported.Add(item))
                {
                    Visit(write.After);
                    if (write.IndexedStorage is { } storage) Visit(storage.Element);
                }
                if (!item.Branches.IsDefaultOrEmpty)
                    foreach (var branch in item.Branches) VisitEvents(branch);
            }
        }

        bool Escapes(SemanticDeferredWrite write) => write.Kind switch
        {
            SemanticWriteKind.Location => cells.ContainsKey(write.Location),
            SemanticWriteKind.Field => write.Target is not null || ExportedScope(write.Region),
            _ => indexed.ContainsKey(write.Region)
        };

        bool ExportedScope(int region) => instance.InputScopePaths.ContainsKey(region) || objects.ContainsKey(region);

        ImmutableArray<SemanticWriteEvent> ExportEvents(IEnumerable<SemanticWriteEvent> events)
        {
            var result = ImmutableArray.CreateBuilder<SemanticWriteEvent>();
            foreach (var item in events)
            {
                var branches = item.Branches.IsDefaultOrEmpty ? default
                    : item.Branches.Select(branch => ExportEvents(branch)).ToImmutableArray();
                bool hasBranches = !branches.IsDefaultOrEmpty && branches.Any(branch => !branch.IsEmpty);
                var write = exported.Contains(item) && item.Write is { } original
                    ? original with { After = ResolveExportValue(original.After, context) } : null;
                var read = item.TypeRead is { } observed
                    && (observed.RequiredAt is not null || ReferenceEquals(observed, instance.Result.TypeRead))
                    && (observed.Target is not null || (observed.Alternatives.IsDefault
                        ? ExportedScope(observed.Region) : observed.Alternatives.All(ExportedScope)))
                    ? observed : null;
                if (write is null && read is null && item.InitializeSingleton is null && !hasBranches) continue;
                result.Add(item with { Write = write, Branches = hasBranches ? branches : default, TypeRead = read });
            }
            return result.ToImmutable();
        }

        void VisitRegion(int region)
        {
            if (context.Objects.TryGetValue(region, out var scope) && !objects.ContainsKey(region))
                Visit(new(new(-1), HirNodeId.None, Scope: scope));
        }
        void VisitCell(HirLocationId location)
        {
            if (cells.ContainsKey(location) || StorageValue(location, context) is not { } value) return;
            cells[location] = ResolveExportValue(value, context);
            var owner = StorageOwner(location, context);
            if (hir.Types[hir.Locations[owner.Value].StorageType.Value].TypeParameter >= 0)
                foreach (var scope in context.Objects.Values)
                    if (scope.Named && scope.Locations.Values.Contains(owner))
                    {
                        (namedCells ??= []).Add(owner);
                        break;
                    }
            Visit(value);
        }
        void Visit(SemanticValue value)
        {
            if (!visited.Add(value)) return;
            if (value.Scope is { } scope)
            {
                scope = context.Objects.GetValueOrDefault(scope.Region, scope);
                if (scope.Region < 0 || !instance.InputScopePaths.ContainsKey(scope.Region) && objects.TryAdd(scope.Region, scope))
                {
                    var fields = scope.Fields;
                    foreach (var (name, field) in scope.Fields)
                    {
                        var exportedField = ResolveExportValue(field, context);
                        if (!ReferenceEquals(exportedField, field)) fields = fields.SetItem(name, exportedField);
                        Visit(field);
                    }
                    if (scope.Region >= 0 && !ReferenceEquals(fields, scope.Fields))
                        objects[scope.Region] = scope with { Fields = fields };
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
            if (!value.OpenSessionCalls.IsDefaultOrEmpty)
                foreach (var binding in value.OpenSessionCalls)
                {
                    VisitCell(binding.Callee.Root);
                    foreach (var argumentSource in binding.Arguments)
                        if (argumentSource is not null) VisitCell(argumentSource.Root);
                }
            if (!value.DeferredEffects.IsDefault)
                foreach (var write in value.DeferredEffects)
                {
                    Visit(write.After);
                    if (write.Kind == SemanticWriteKind.Location) VisitCell(write.Location);
                    else if (write.Kind == SemanticWriteKind.Field) VisitRegion(write.Region);
                    else if (write.Kind == SemanticWriteKind.Indexed && context.Indexed.TryGetValue(write.Region, out var deferredStorage))
                        indexed.TryAdd(write.Region, deferredStorage);
                }
            if (value.FutureCallableDomain is { } future)
            {
                VisitCell(future.Owner);
                foreach (var callable in future.Current.IsDefault ? future.Choices : future.Choices.AddRange(future.Current))
                    if (!callable.Captures.IsDefault)
                        foreach (var capture in callable.Captures) { VisitCell(capture.Source); Visit(capture.Value); }
            }
            foreach (var callable in Choices(value))
                if (!callable.Captures.IsDefault)
                    foreach (var capture in callable.Captures) { VisitCell(capture.Source); Visit(capture.Value); }
            if (value.IndexedRegion is { } region && context.Indexed.TryGetValue(region, out var storage)
                && indexed.TryAdd(region, storage)) Visit(storage.Element);
        }
    }

    private static bool MayContainCallable(IReadOnlyList<SemanticValue> values)
    {
        for (int index = 0; index < values.Count; index++)
        {
            SemanticValue value = values[index];
            if (value.Scope is not null || value.Element is not null || !value.Callables.IsDefaultOrEmpty) return true;
        }
        return false;
    }

    private static bool ContainsCallableInput(IReadOnlyList<SemanticValue> captures,
        IReadOnlyList<SemanticValue> arguments, HirTemplateId template, SemanticContext context)
    {
        HashSet<SemanticValue> visited = new(ReferenceEqualityComparer.Instance);
        for (int index = 0; index < captures.Count; index++)
            if (ContainsCallable(captures[index], template, context, visited)) return true;
        for (int index = 0; index < arguments.Count; index++)
            if (ContainsCallable(arguments[index], template, context, visited)) return true;
        return false;
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
