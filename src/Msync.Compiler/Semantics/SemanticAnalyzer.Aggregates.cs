using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly Dictionary<int, SemanticScope> singletonScopes = [];

    private SemanticValue Scope(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        var construction = hir.Scopes[node.Data];
        SemanticScope? previous = construction.Singleton >= 0
            ? context.SingletonSchemas.GetValueOrDefault(construction.Singleton)
                ?? singletonScopes.GetValueOrDefault(construction.Singleton) : null;
        bool initialized = construction.Singleton >= 0 && context.InitializedSingletons.Contains(construction.Singleton);
        SemanticContext destination = context;
        // Keep the guarded initializer in generated code, but do not replay its
        // flow effects on a path where this singleton is already initialized.
        if (initialized) context = new(context.Function, context.Output, context)
        {
            Queries = context.Queries, DeferredReads = context.DeferredReads,
            ExpectedReturn = context.ExpectedReturn, FlowAnalysis = context.FlowAnalysis,
            InitializedSingletons = context.InitializedSingletons,
            MaybeInitializedSingletons = context.MaybeInitializedSingletons
        };
        int region = previous?.Region ?? ++nextRegion;
        SemanticScope? currentObject = initialized && previous is not null
            ? context.Objects.GetValueOrDefault(region, previous) : null;
        var locations = construction.Fields.ToImmutableDictionary(
            field => hir.Locations[field.Value].Name, context.Location, StringComparer.Ordinal);
        var declaredFields = hir.ScopeLayouts[construction.Layout].Fields;
        var fields = declaredFields.ToImmutableDictionary(field => field.Name,
            field => ValueForType(ResolveHirType(field.StorageType, context)), StringComparer.Ordinal);
        SemanticScope provisional = new(region, construction.Layout, fields, locations, construction.Singleton >= 0,
            Initializing: construction.Self is not null);
        context.Objects[region] = provisional;
        HirLocationId? self = construction.Self is { } original ? context.Location(original) : null;
        if (self is { } selfLocation)
        {
            context.Values[selfLocation] = new(hir.Type(new HirType(TypeRef.Scope, Shape: construction.Layout)),
                HirNodeId.None, Scope: provisional);
            context.Storage[selfLocation] = hir.Type(TypeRef.Scope);
        }

        var outerGuards = context.ActiveSingletonGuards;
        if (construction.Singleton >= 0)
        {
            context.ActivateWriteEvents();
            context.ActiveSingletonGuards = outerGuards.Add(construction.Singleton);
        }
        List<HirNodeId> initializers = [];
        try
        {
            foreach (var child in children) initializers.Add(Analyze(child, context).Node);
        }
        finally { context.ActiveSingletonGuards = outerGuards; }
        if (construction.Singleton >= 0 && !initialized)
            context.WriteEvents!.Add(new(null, outerGuards, construction.Singleton));
        var populated = locations.ToImmutableDictionary(pair => pair.Key,
            pair => context.Values[pair.Value], StringComparer.Ordinal);
        var layout = declaredFields.Select(field =>
        {
            var location = locations[field.Name];
            var storage = context.Storage.GetValueOrDefault(location, populated[field.Name].Type);
            return new HirField(field.Name, storage, field.AdmitsNull);
        }).ToImmutableArray();

        if (previous is not null)
        {
            var established = hir.ScopeLayouts[previous.Layout].Fields;
            for (int index = 0; index < established.Length; index++)
            {
                var field = established[index];
                if (!populated.TryGetValue(field.Name, out var value))
                    throw Error(node.Source, $"Named Scope is missing its established field '{field.Name}'.", "E0203");
                Require(value, field.StorageType, node.Source, allowImplicit: true, context: context);
                var converted = Convert(value, field.StorageType, node.Source, context);
                populated = populated.SetItem(field.Name, converted);
                var fieldLocation = locations[field.Name];
                context.Storage[fieldLocation] = field.StorageType;
                context.Values[fieldLocation] = converted;
                if (context.Publish)
                {
                    context.Output!.SetLocation(fieldLocation, hir.Locations[fieldLocation.Value] with
                        { StorageType = field.StorageType });
                    var declaration = hir.Nodes[initializers[index].Value];
                    initializers[index] = context.Output!.Add(HirKind.Declare, hir.Type(TypeRef.Null), declaration.Source,
                        [converted.Node], fieldLocation.Value);
                }
            }
            layout = established;
        }

        int shape = InternShape(layout);
        SemanticScope result = currentObject ?? new(region, shape, populated, locations, construction.Singleton >= 0);
        destination.Objects[region] = result;
        if (construction.Singleton >= 0)
        {
            destination.SingletonSchemas[construction.Singleton] = result;
            destination.InitializedSingletons = destination.InitializedSingletons.Add(construction.Singleton);
            destination.MaybeInitializedSingletons = destination.MaybeInitializedSingletons.Add(construction.Singleton);
        }
        if (context.Publish && construction.Singleton >= 0) singletonScopes.TryAdd(construction.Singleton, result);
        HirTypeId type = hir.Type(new HirType(TypeRef.Scope, Shape: shape));
        HirNodeId expression = HirNodeId.None;
        if (context.Publish)
        {
            int descriptor = hir.Scopes.Count;
            context.Output!.AddScope(new(shape, construction.Fields.Select(context.Location).ToImmutableArray(),
                construction.Singleton, self));
            expression = context.Output!.Add(HirKind.Scope, type, node.Source,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(initializers), descriptor, effects: node.Effects);
        }
        SemanticValue valueResult = new(type, expression, Scope: result);
        if (self is { } completed)
        {
            destination.Values[completed] = valueResult;
            destination.Storage[completed] = type;
            destination.Output?.SetLocation(completed, hir.Locations[completed.Value] with { StorageType = type });
        }
        return valueResult;
    }

    private int InternShape(ImmutableArray<HirField> fields) => hir.InternScopeLayout(new(fields));

    private SemanticValue Range(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        var delayed = node.Kind == HirKind.LazyRange ? hir.Ranges[node.Data].Deferred : default;
        List<HirNodeId> values = [];
        SemanticValue? element = null;
        ImmutableArray<SemanticDeferredWrite>.Builder? admission = null;
        for (int index = 0; index < children.Length; index++)
        {
            SemanticValue value;
            if (!delayed.IsDefaultOrEmpty && delayed[index])
            {
                var thunk = RangeThunk(children[index], context);
                value = thunk.Value;
                values.Add(thunk.Closure);
                if (!thunk.AdmissionEffects.IsDefaultOrEmpty)
                    (admission ??= ImmutableArray.CreateBuilder<SemanticDeferredWrite>()).AddRange(thunk.AdmissionEffects);
            }
            else
            {
                value = Analyze(children[index], context);
                values.Add(value.Node);
            }
            value = value with { Location = null };
            element = element is null ? value : Join(element, value);
        }
        element ??= ValueForType(hir.Type(new TypeRef(TypeKind.Never)));
        HirTypeId type = RangeType(element.Type);
        HirNodeId expression = HirNodeId.None;
        if (context.Publish)
        {
            int descriptor = node.Data;
            if (node.Kind == HirKind.LazyRange)
            {
                descriptor = hir.Ranges.Count;
                context.Output!.AddRange(new(delayed));
            }
            expression = context.Output!.Add(node.Kind, type, node.Source,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values), descriptor, effects: node.Effects);
        }
        return new(type, expression, Element: element, DeferredEffects: admission?.ToImmutable() ?? []);
    }

    private (SemanticValue Value, HirNodeId Closure, ImmutableArray<SemanticDeferredWrite> AdmissionEffects) RangeThunk(HirNodeId expression, SemanticContext context)
    {
        HashSet<HirLocationId> read = [], declared = [];
        CollectLocations(expression, read, declared);
        read.ExceptWith(declared);
        SemanticContext deferred = new(new(context.Publish ? hir.Functions.Count : -1), context.Output, context)
        { DeferredReads = true, NextRegion = context.NextRegion };
        ImmutableArray<HirCapture>.Builder captures = ImmutableArray.CreateBuilder<HirCapture>();
        foreach (var original in read.OrderBy(location => location.Value))
        {
            var source = context.Location(original);
            var target = AllocateThunkLocation(original, deferred);
            var value = context.Values.GetValueOrDefault(source)
                ?? ValueForType(ResolveHirType(hir.Locations[source.Value].StorageType, context));
            HirTypeId storage = context.Storage.GetValueOrDefault(source, value.Type);
            deferred.Values[target] = !Stable(original) || value.Scope is not null
                ? FutureRead(value, storage, deferred, source) : value;
            deferred.Storage[target] = storage;
            RegisterStorageAlias(target, source, deferred);
            captures.Add(new(target, HirCaptureMode.Location, source, HirNodeId.None));
        }
        foreach (var local in declared) _ = AllocateThunkLocation(local, deferred);
        context.Output?.ReserveFunction(new("<range-item>", [], captures.ToImmutable(),
            hir.Type(TypeRef.Any), HirNodeId.None, IsOpen: true));
        SemanticValue result = Analyze(expression, deferred);
        result = PublishResultGraph(result, deferred, context, out var admissionEffects);
        if (!context.Publish) return (result, HirNodeId.None, admissionEffects);
        context.Output!.SealFunction(deferred.Function, new("<range-item>", [], captures.ToImmutable(), result.Type, result.Node));
        int closure = hir.Closures.Count;
        context.Output!.AddClosure(new(deferred.Function, captures.ToImmutable()));
        return (result, context.Output!.Add(HirKind.Closure, hir.Type(TypeRef.Function), program.Node(expression).Source, data: closure), admissionEffects);
    }

    private HirLocationId AllocateThunkLocation(HirLocationId original, SemanticContext context)
    {
        HirLocationId definition = context.Definitions.GetValueOrDefault(context.Location(original), program.Original(original));
        HirLocationId target = original;
        if (context.Publish)
        {
            target = new(hir.Locations.Count);
            context.Output!.ReserveLocation(hir.Locations[original.Value] with { Owner = context.Function, TemplateOwner = null });
        }
        context.Locations.Add(original, target);
        context.Definitions[target] = definition;
        return target;
    }

    private void CollectLocations(HirNodeId id, HashSet<HirLocationId> read, HashSet<HirLocationId> declared)
    {
        var node = program.Node(id);
        if (node.Kind is HirKind.ReadLocation or HirKind.ReadStorage or HirKind.Assign) read.Add(new(node.Data));
        if (node.Kind == HirKind.Declare) declared.Add(new(node.Data));
        if (node.Kind == HirKind.TemplateClosure)
            foreach (var capture in hir.TemplateClosures[node.Data].Captures) read.Add(capture.SourceLocation);
        if (node.Kind == HirKind.For) declared.Add(hir.Fors[node.Data].Item);
        if (node.Kind == HirKind.Match)
            foreach (var arm in hir.Matches[node.Data].Arms)
            {
                if (arm.Pattern.Binding is { } binding) declared.Add(binding);
                CollectLocations(arm.Value, read, declared);
            }
        if (node.Kind == HirKind.Scope && hir.Scopes[node.Data].Self is { } self) declared.Add(self);
        foreach (var child in program.Children(id)) CollectLocations(child, read, declared);
    }
}
