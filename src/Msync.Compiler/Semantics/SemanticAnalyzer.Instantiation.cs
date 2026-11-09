using System.Collections.Immutable;
using System.Text;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    internal sealed class InstantiationWorkspace
    {
        internal Dictionary<string, FunctionInstance> Instances { get; } = new(StringComparer.Ordinal);
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
        public bool Complete { get; set; }
    }

    private readonly record struct FunctionInvocation(HirFunctionId Id, SemanticValue Result);
    private readonly Dictionary<string, FunctionInstance> instances = new(StringComparer.Ordinal);

    private SemanticContext QueryContext(SemanticContext context, SemanticValue callee,
        IReadOnlyList<SemanticValue> arguments)
    {
        SemanticContext query = new(context.Function, publish: false)
        { Queries = context.Queries ?? new() };
        HashSet<SemanticValue> visited = new(ReferenceEqualityComparer.Instance);
        Import(callee);
        foreach (var argument in arguments) Import(argument);
        return query;

        void Import(SemanticValue value)
        {
            if (!visited.Add(value)) return;
            if (value.Scope is { } scope)
            {
                scope = context.Objects.GetValueOrDefault(scope.Region, scope);
                query.Objects[scope.Region] = scope;
                foreach (var field in scope.Fields.Values) Import(field);
                foreach (var location in scope.Locations.Values) ImportLocation(location);
            }
            if (value.IndexedRegion is { } region && context.Indexed.TryGetValue(region, out var indexed))
            {
                query.Indexed[region] = indexed;
                Import(indexed.Element);
            }
            if (value.Element is { } element) Import(element);
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
        if (context.Publish) hir.TemplateClosures.Add(new(closure.Template, emitted.ToImmutable()));
        var expression = context.Publish
            ? hir.Add(HirKind.TemplateClosure, hir.Type(TypeRef.Function), node.Source, data: index)
            : HirNodeId.None;
        return new(hir.Type(TypeRef.Function), expression,
            Callables: [new(closure.Template, Captures: captures.ToImmutable())]);
    }

    private FunctionInvocation Instantiate(HirTemplateId id, ImmutableArray<SemanticCapture> captured,
        IReadOnlyList<SemanticValue> arguments, HirSourceRange source, SemanticContext caller)
    {
        var template = hir.Templates[id.Value];
        if (arguments.Count != template.Parameters.Length)
            throw Error(source, $"Function expects {template.Parameters.Length} arguments, got {arguments.Count}.", "E0204");
        var actualCaptures = captured.IsDefault ? ImmutableArray<SemanticCapture>.Empty : captured;
        var captureValues = actualCaptures.Select(capture => capture.Mode == HirCaptureMode.Location
            ? caller.Values.GetValueOrDefault(capture.Source, capture.Value) : capture.Value).ToArray();
        string key = InstanceKey(id, arguments, captureValues, caller);
        var cache = caller.Publish ? instances : (caller.Queries ??= new()).Instances;
        if (!cache.TryGetValue(key, out var instance))
        {
            instance = BuildInstance(id, template, arguments, captureValues, actualCaptures, source, caller, cache, key);
        }
        return new(instance.Id, ProjectInvocation(instance, arguments, captureValues, actualCaptures, caller));
    }

    private FunctionInstance BuildInstance(HirTemplateId id, HirTemplate template,
        IReadOnlyList<SemanticValue> arguments, IReadOnlyList<SemanticValue> captureValues,
        ImmutableArray<SemanticCapture> captured, HirSourceRange source, SemanticContext caller,
        Dictionary<string, FunctionInstance> cache, string key)
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
        if (caller.Publish) hir.Functions.Add(new(template.Name, [], [],
            hir.Type(new TypeRef(TypeKind.Never)), HirNodeId.None, id, IsOpen: true));
        SemanticContext context = new(function, caller.Publish) { Queries = caller.Queries };
        foreach (var declaration in program.Declarations)
        {
            var location = hir.Locations[declaration.Location.Value];
            if (location.TemplateOwner != id) continue;
            var target = declaration.Location;
            if (caller.Publish)
            {
                target = new(hir.Locations.Count);
                hir.Locations.Add(location with { Owner = function, TemplateOwner = null });
            }
            context.Locations.Add(declaration.Location, target);
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
            context.Storage[target] = caller.Storage.GetValueOrDefault(captured[index].Source, value.Type);
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
                hir.Locations.Add(definition with { Name = "<input:" + definition.Name + ">", Owner = function,
                    TemplateOwner = null, StorageType = argument.Type });
                var read = hir.Add(HirKind.ReadLocation, argument.Type, definition.Source, data: incoming.Value);
                argument = argument with { Node = read };
            }
            // Conversion is a typed operation at the function boundary, including
            // callbacks and dynamic dispatch. The backend merely lays out these nodes.
            var converted = Convert(argument, parameterType, source, context);
            if (coercion && context.Publish)
                parameterInitializers.Add(hir.Add(HirKind.Declare, hir.Type(TypeRef.Null), definition.Source,
                    [converted.Node], data: target.Value));
            var value = converted with { Location = target, Node = HirNodeId.None };
            context.Values[target] = value;
            context.Storage[target] = definition.AdmitsNull ? Nullable(parameterType) : parameterType;
            if (caller.Publish) hir.Locations[target.Value] = hir.Locations[target.Value] with { StorageType = context.Storage[target] };
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
                body = body with { Node = hir.Add(HirKind.Block, body.Type, template.Source,
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(parameterInitializers)) };
            }
            instance.Result = result with { Location = null };
            FreezeSummary(instance, context);
            instance.Complete = true;
            if (caller.Publish) hir.Functions[function.Value] = new(template.Name,
                parameters.ToImmutable(), captures.ToImmutable(), result.Type, body.Node, id);
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

    private static void FreezeSummary(FunctionInstance instance, SemanticContext context)
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

        void VisitCell(HirLocationId location)
        {
            if (cells.ContainsKey(location) || !context.Values.TryGetValue(location, out var value)) return;
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
                    foreach (var field in scope.Fields.Values) Visit(field);
            }
            if (value.Element is { } element) Visit(element);
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

    private string InstanceKey(HirTemplateId id, IReadOnlyList<SemanticValue> arguments,
        IReadOnlyList<SemanticValue> captures, SemanticContext caller)
    {
        StringBuilder key = new();
        key.Append(id.Value).Append(':');
        HashSet<SemanticValue> active = new(ReferenceEqualityComparer.Instance);
        KeyProvenance provenance = new();
        foreach (var value in arguments) { AppendValueKey(key, value, caller, active, provenance); key.Append(';'); }
        key.Append('|');
        foreach (var value in captures) { AppendValueKey(key, value, caller, active, provenance); key.Append(';'); }
        return key.ToString();
    }

    private string ValueKey(SemanticValue value)
    {
        StringBuilder key = new();
        AppendValueKey(key, value, null, new(ReferenceEqualityComparer.Instance), new());
        return key.ToString();
    }

    private void AppendValueKey(StringBuilder key, SemanticValue value, SemanticContext? context,
        HashSet<SemanticValue> active, KeyProvenance provenance)
    {
        key.Append(value.Type.Value).Append(value.EagerRange ? '!' : '.');
        if (value.DenotedType is { } denoted) key.Append('T').Append(denoted.Value);
        if (!active.Add(value)) { key.Append('^'); return; }
        foreach (var callable in Choices(value))
        {
            key.Append('F').Append(callable.NativeName ?? callable.Template?.Value.ToString()).Append('(');
            if (!callable.Captures.IsDefault)
                foreach (var capture in callable.Captures)
                {
                    key.Append('C').Append(provenance.Location(capture.Source));
                    AppendValueKey(key, context?.Values.GetValueOrDefault(capture.Source, capture.Value) ?? capture.Value, context, active, provenance);
                }
            key.Append(')');
        }
        if (value.Scope is { } scope)
        {
            scope = context?.Objects.GetValueOrDefault(scope.Region, scope) ?? scope;
            key.Append('{').Append(provenance.Region(scope.Region));
            foreach (var field in scope.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                key.Append(field.Key).Append(':');
                var member = scope.Locations.TryGetValue(field.Key, out var location)
                    ? context?.Values.GetValueOrDefault(location, field.Value) ?? field.Value : field.Value;
                AppendValueKey(key, member, context, active, provenance);
            }
            key.Append('}');
        }
        if (value.Element is { } element) { key.Append('['); AppendValueKey(key, element, context, active, provenance); key.Append(']'); }
        if (value.IndexedRegion is { } region && context?.Indexed.TryGetValue(region, out var indexed) == true)
        {
            key.Append('I').Append(provenance.Region(region)).Append(':').Append(indexed.FixedCarrier?.Value ?? -1);
            AppendValueKey(key, indexed.Element, context, active, provenance);
        }
        active.Remove(value);
    }

    // Canonical alias partitions contain no allocation identities: two unrelated
    // calls with the same alias topology share code. Aliased and disjoint inputs
    // cannot share flow proofs after writes to nullable fields or captured cells.
    private sealed class KeyProvenance
    {
        private Dictionary<int, int>? regions;
        private Dictionary<HirLocationId, int>? locations;
        public int Region(int region)
        {
            if (region < 0) return -1;
            regions ??= [];
            if (!regions.TryGetValue(region, out var ordinal)) regions[region] = ordinal = regions.Count;
            return ordinal;
        }
        public int Location(HirLocationId location)
        {
            locations ??= [];
            if (!locations.TryGetValue(location, out var ordinal)) locations[location] = ordinal = locations.Count;
            return ordinal;
        }
    }

    private sealed class FormalInput(SemanticAnalyzer owner, SemanticContext caller, SemanticContext formal)
    {
        private readonly Dictionary<int, int> regions = [];
        private readonly Dictionary<int, int> indexedRegions = [];
        private readonly Dictionary<SemanticValue, SemanticValue> values = new(ReferenceEqualityComparer.Instance);
        public Dictionary<HirLocationId, HirLocationId> Locations { get; } = [];

        public SemanticValue Abstract(SemanticValue value)
        {
            if (values.TryGetValue(value, out var known)) return known;
            // Runtime constants are not specialization facts. In particular a first
            // Bool argument must not delete the other caller's branch from shared code.
            var result = value with { Node = HirNodeId.None, Location = null,
                Literal = value.DenotedType is null ? null : value.Literal,
                Callables = [], Scope = null, Element = null, Alternatives = [], IndexedRegion = null };
            values[value] = result;
            if (value.Scope is { } scope)
            {
                scope = caller.Objects.GetValueOrDefault(scope.Region, scope);
                if (scope.Region >= 0 && regions.TryGetValue(scope.Region, out var existing))
                    result = result with { Scope = formal.Objects[existing] };
                else
                {
                    int region = ++owner.nextRegion;
                    if (scope.Region >= 0) regions[scope.Region] = region;
                    var symbolic = scope with { Region = region, Fields = ImmutableDictionary<string, SemanticValue>.Empty,
                        Locations = ImmutableDictionary<string, HirLocationId>.Empty, Aliases = [] };
                    formal.Objects[region] = symbolic;
                    result = result with { Scope = symbolic };
                    values[value] = result;
                    var fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                        pair => Abstract(scope.Locations.TryGetValue(pair.Key, out var location)
                            ? caller.Values.GetValueOrDefault(location, pair.Value) : pair.Value), StringComparer.Ordinal);
                    symbolic = symbolic with { Fields = fields };
                    formal.Objects[region] = symbolic;
                    result = result with { Scope = symbolic };
                }
            }
            if (value.Element is { } element) result = result with { Element = Abstract(element) };
            if (!value.Callables.IsDefaultOrEmpty)
                result = result with { Callables = value.Callables.Select(callable => callable with
                {
                    Captures = callable.Captures.IsDefault ? [] : callable.Captures.Select(capture =>
                    {
                        var source = FormalLocation(capture.Source);
                        var captured = Abstract(capture.Mode == HirCaptureMode.Location
                            ? caller.Values.GetValueOrDefault(capture.Source, capture.Value) : capture.Value);
                        formal.Values[source] = captured;
                        formal.Storage[source] = caller.Storage.GetValueOrDefault(capture.Source, captured.Type);
                        return capture with { Source = source, Value = captured };
                    }).ToImmutableArray()
                }).ToImmutableArray() };
            if (!value.Alternatives.IsDefaultOrEmpty)
                result = result with { Alternatives = value.Alternatives.Select(Abstract).ToImmutableArray() };
            if (value.IndexedRegion is { } indexed)
            {
                if (!indexedRegions.TryGetValue(indexed, out var region))
                {
                    region = ++owner.nextRegion;
                    indexedRegions[indexed] = region;
                    if (caller.Indexed.TryGetValue(indexed, out var description))
                        formal.Indexed[region] = description with { Element = Abstract(description.Element) };
                }
                result = result with { IndexedRegion = region };
            }
            values[value] = result;
            return result;
        }

        public SemanticValue AttachOrigin(SemanticValue value, HirLocationId root) =>
            AttachOrigin(value, new(root), []);

        private SemanticValue AttachOrigin(SemanticValue value, SemanticOrigin origin, HashSet<int> active)
        {
            var result = value with { Origin = origin };
            if (value.Scope is not { } scope || !active.Add(scope.Region)) return result;
            scope = formal.Objects.GetValueOrDefault(scope.Region, scope);
            scope = scope with { Fields = scope.Fields.ToImmutableDictionary(pair => pair.Key,
                pair => AttachOrigin(pair.Value, origin with { Fields = origin.Fields.IsDefault
                    ? [pair.Key] : origin.Fields.Add(pair.Key) }, active), StringComparer.Ordinal) };
            formal.Objects[scope.Region] = scope;
            active.Remove(scope.Region);
            return result with { Scope = scope };
        }

        public void Complete()
        {
            foreach (var pair in Locations.ToArray())
                if (caller.Values.TryGetValue(pair.Key, out var value)) formal.Values[pair.Value] = Abstract(value);
        }

        private HirLocationId FormalLocation(HirLocationId source)
        {
            if (Locations.TryGetValue(source, out var target)) return target;
            target = source;
            if (formal.Publish)
            {
                target = new(owner.hir.Locations.Count);
                owner.hir.Locations.Add(owner.hir.Locations[source.Value] with { Owner = formal.Function, TemplateOwner = null });
            }
            Locations[source] = target;
            return target;
        }
    }

    private SemanticValue ProjectInvocation(FunctionInstance instance, IReadOnlyList<SemanticValue> arguments,
        IReadOnlyList<SemanticValue> captures, ImmutableArray<SemanticCapture> captureDefinitions, SemanticContext caller)
    {
        if (instance.Objects.IsEmpty && instance.Indexed.IsEmpty && instance.Cells.IsEmpty
            && instance.Result.Scope is null && instance.Result.Element is null
            && instance.Result.Callables.IsDefaultOrEmpty && instance.Result.Alternatives.IsDefaultOrEmpty)
        {
            if (instance.Result.Origin is { } origin && origin.Fields.IsDefaultOrEmpty)
                for (int index = 0; index < instance.Arguments.Length; index++)
                    if (instance.Arguments[index].Origin?.Root == origin.Root)
                        return ProjectScalar(instance.Result, arguments[index]);
            return instance.Result with { Node = HirNodeId.None, Location = null, Origin = null };
        }
        InvocationProjection projection = new(this, instance, caller);
        for (int index = 0; index < arguments.Count; index++) projection.Bind(instance.Arguments[index], arguments[index]);
        for (int index = 0; index < captures.Count; index++)
        {
            projection.Bind(instance.Captures[index], captures[index]);
            if (captureDefinitions[index].Mode == HirCaptureMode.Location)
                projection.Locations[instance.CaptureTargets[index]] = captureDefinitions[index].Source;
        }
        if (instance.Complete) projection.ApplyEffects();
        return projection.Project(instance.Result) with { Location = null, Node = HirNodeId.None };
    }

    private SemanticValue ProjectScalar(SemanticValue formal, SemanticValue actual)
    {
        var literal = actual.Literal;
        if (literal is { Kind: HirLiteralKind.Path } path && Runtime(formal).WithoutNull == TypeRef.String)
            literal = new(HirLiteralKind.String, Text: path.Text);
        else if (literal is { Kind: HirLiteralKind.String } text && Runtime(formal).WithoutNull == TypeRef.Path)
            literal = new(HirLiteralKind.Path, Text: text.Text);
        return formal with { Literal = literal, DenotedType = actual.DenotedType,
            Node = HirNodeId.None, Location = null, Origin = null };
    }

    private sealed class InvocationProjection(SemanticAnalyzer owner, FunctionInstance instance, SemanticContext caller)
    {
        private readonly Dictionary<int, int> regions = [];
        private readonly Dictionary<int, int> indexedRegions = [];
        private readonly Dictionary<HirLocationId, SemanticValue> origins = [];
        private readonly Dictionary<SemanticValue, SemanticValue> values = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<(SemanticValue Formal, SemanticValue Actual)> bound = new(new ValuePairComparer());
        public Dictionary<HirLocationId, HirLocationId> Locations { get; } = [];

        public void Bind(SemanticValue formal, SemanticValue actual)
        {
            if (!bound.Add((formal, actual))) return;
            if (formal.Origin is { } origin && origin.Fields.IsDefaultOrEmpty) origins[origin.Root] = actual;
            if (formal.Scope is { } shape && actual.Scope is { } scope)
            {
                scope = caller.Objects.GetValueOrDefault(scope.Region, scope);
                regions[shape.Region] = scope.Region;
                foreach (var pair in shape.Fields)
                    if (scope.Fields.TryGetValue(pair.Key, out var field))
                        Bind(pair.Value, scope.Locations.TryGetValue(pair.Key, out var location)
                            ? caller.Values.GetValueOrDefault(location, field) : field);
            }
            if (formal.Element is { } element && actual.Element is { } actualElement) Bind(element, actualElement);
            if (formal.IndexedRegion is { } indexed && actual.IndexedRegion is { } actualIndexed)
                indexedRegions[indexed] = actualIndexed;
            if (!formal.Callables.IsDefaultOrEmpty && !actual.Callables.IsDefaultOrEmpty)
                for (int choice = 0; choice < Math.Min(formal.Callables.Length, actual.Callables.Length); choice++)
                {
                    var first = formal.Callables[choice].Captures;
                    var second = actual.Callables[choice].Captures;
                    if (first.IsDefaultOrEmpty || second.IsDefaultOrEmpty) continue;
                    for (int index = 0; index < Math.Min(first.Length, second.Length); index++)
                    {
                        Locations[first[index].Source] = second[index].Source;
                        Bind(first[index].Value, second[index].Value);
                    }
                }
        }

        public SemanticValue Project(SemanticValue value)
        {
            if (values.TryGetValue(value, out var previous)) return previous;
            var result = value with { Node = HirNodeId.None, Location = null, Origin = null };
            if (value.Origin is { } origin && origins.TryGetValue(origin.Root, out var actual))
            {
                bool found = true;
                if (!origin.Fields.IsDefault)
                    foreach (var name in origin.Fields)
                    {
                        if (actual.Scope is not { } scope) { found = false; break; }
                        scope = caller.Objects.GetValueOrDefault(scope.Region, scope);
                        if (!scope.Fields.TryGetValue(name, out var field)) { found = false; break; }
                        actual = scope.Locations.TryGetValue(name, out var location)
                            ? caller.Values.GetValueOrDefault(location, field) : field;
                    }
                if (found) result = owner.ProjectScalar(result, actual);
            }
            values[value] = result;
            if (value.Scope is { } shape)
            {
                shape = instance.Objects.GetValueOrDefault(shape.Region, shape);
                if (!regions.TryGetValue(shape.Region, out var region)) regions[shape.Region] = region = ++owner.nextRegion;
                // Fresh fields and closures must project through one location map:
                // a getter captures the same cell that member assignment mutates.
                var symbolic = shape with { Region = region, Fields = ImmutableDictionary<string, SemanticValue>.Empty,
                    Locations = caller.Objects.GetValueOrDefault(region)?.Locations ?? shape.Locations.ToImmutableDictionary(
                        pair => pair.Key, pair => MapLocation(pair.Value), StringComparer.Ordinal),
                    Aliases = shape.Aliases.IsDefault ? [] : shape.Aliases.Select(MapRegion).ToImmutableArray() };
                result = result with { Scope = symbolic };
                values[value] = result;
                symbolic = symbolic with { Fields = shape.Fields.ToImmutableDictionary(pair => pair.Key,
                    pair => Project(pair.Value), StringComparer.Ordinal) };
                caller.Objects[region] = symbolic;
                foreach (var field in symbolic.Locations)
                    if (symbolic.Fields.TryGetValue(field.Key, out var fieldValue)) caller.Values[field.Value] = fieldValue;
                result = result with { Scope = symbolic };
            }
            if (value.Element is { } element) result = result with { Element = Project(element) };
            if (!value.Callables.IsDefaultOrEmpty)
                result = result with { Callables = value.Callables.Select(callable => callable with
                {
                    Captures = callable.Captures.IsDefault ? [] : callable.Captures.Select(capture => capture with
                    { Source = MapLocation(capture.Source), Value = Project(capture.Value) }).ToImmutableArray()
                }).ToImmutableArray() };
            if (!value.Alternatives.IsDefaultOrEmpty)
                result = result with { Alternatives = value.Alternatives.Select(Project).ToImmutableArray() };
            if (value.IndexedRegion is { } indexed)
            {
                if (!indexedRegions.TryGetValue(indexed, out var region)) indexedRegions[indexed] = region = ++owner.nextRegion;
                if (instance.Indexed.TryGetValue(indexed, out var storage))
                    caller.Indexed[region] = storage with { Element = Project(storage.Element) };
                result = result with { IndexedRegion = region };
            }
            values[value] = result;
            return result;
        }

        public void ApplyEffects()
        {
            foreach (var (formal, actual) in Locations.ToArray())
                if (instance.Cells.TryGetValue(formal, out var value)) caller.Values[actual] = Project(value);
            foreach (var region in regions.Keys.ToArray())
                if (instance.Objects.TryGetValue(region, out var shape))
                    _ = Project(new(owner.hir.Type(new HirType(TypeRef.Scope, Shape: shape.Layout)), HirNodeId.None, Scope: shape));
            foreach (var region in indexedRegions.Keys.ToArray())
                if (instance.Indexed.TryGetValue(region, out var storage))
                    caller.Indexed[indexedRegions[region]] = storage with { Element = Project(storage.Element) };
        }

        private int MapRegion(int formal)
        {
            if (regions.TryGetValue(formal, out var actual)) return actual;
            regions[formal] = actual = ++owner.nextRegion;
            return actual;
        }

        private HirLocationId MapLocation(HirLocationId formal)
        {
            if (Locations.TryGetValue(formal, out var actual)) return actual;
            actual = formal;
            if (caller.Publish)
            {
                actual = new(owner.hir.Locations.Count);
                owner.hir.Locations.Add(owner.hir.Locations[formal.Value] with { Owner = caller.Function, TemplateOwner = null });
            }
            Locations[formal] = actual;
            if (instance.Cells.TryGetValue(formal, out var value)) caller.Values[actual] = Project(value);
            return actual;
        }
    }

    private sealed class ValuePairComparer : IEqualityComparer<(SemanticValue Formal, SemanticValue Actual)>
    {
        public bool Equals((SemanticValue Formal, SemanticValue Actual) x, (SemanticValue Formal, SemanticValue Actual) y) =>
            ReferenceEquals(x.Formal, y.Formal) && ReferenceEquals(x.Actual, y.Actual);
        public int GetHashCode((SemanticValue Formal, SemanticValue Actual) pair) => HashCode.Combine(
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Formal),
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Actual));
    }
}
