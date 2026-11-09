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
        int region = construction.Singleton >= 0 && singletonScopes.TryGetValue(construction.Singleton, out var previous)
            ? previous.Region : ++nextRegion;
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

        List<HirNodeId> initializers = [];
        foreach (var child in children) initializers.Add(Analyze(child, context).Node);
        var populated = locations.ToImmutableDictionary(pair => pair.Key,
            pair => context.Values[pair.Value], StringComparer.Ordinal);
        var layout = declaredFields.Select(field =>
        {
            var location = locations[field.Name];
            var storage = context.Storage.GetValueOrDefault(location, populated[field.Name].Type);
            return new HirField(field.Name, storage, field.AdmitsNull);
        }).ToImmutableArray();

        if (construction.Singleton >= 0 && singletonScopes.TryGetValue(construction.Singleton, out previous))
            foreach (var field in hir.ScopeLayouts[previous.Layout].Fields)
            {
                if (!populated.TryGetValue(field.Name, out var value))
                    throw Error(node.Source, $"Named Scope is missing its established field '{field.Name}'.", "E0203");
                Require(value, field.StorageType, node.Source, allowImplicit: true, context: context);
            }

        int shape = InternShape(layout);
        SemanticScope result = new(region, shape, populated, locations, construction.Singleton >= 0);
        context.Objects[region] = result;
        if (context.Publish && construction.Singleton >= 0) singletonScopes.TryAdd(construction.Singleton, result);
        HirTypeId type = hir.Type(new HirType(TypeRef.Scope, Shape: shape));
        HirNodeId expression = HirNodeId.None;
        if (context.Publish)
        {
            int descriptor = hir.Scopes.Count;
            hir.Scopes.Add(new(shape, construction.Fields.Select(context.Location).ToImmutableArray(),
                construction.Singleton, self));
            expression = hir.Add(HirKind.Scope, type, node.Source,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(initializers), descriptor, effects: node.Effects);
        }
        SemanticValue valueResult = new(type, expression, Scope: result);
        if (self is { } completed) context.Values[completed] = valueResult;
        return valueResult;
    }

    private int InternShape(ImmutableArray<HirField> fields)
    {
        for (int index = 0; index < hir.ScopeLayouts.Count; index++)
            if (hir.ScopeLayouts[index].Fields.AsSpan().SequenceEqual(fields.AsSpan())) return index;
        int shape = hir.ScopeLayouts.Count;
        hir.ScopeLayouts.Add(new(fields));
        return shape;
    }

    private SemanticValue Range(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        var delayed = node.Kind == HirKind.LazyRange ? hir.Ranges[node.Data].Deferred : default;
        List<HirNodeId> values = [];
        SemanticValue? element = null;
        for (int index = 0; index < children.Length; index++)
        {
            SemanticValue value;
            if (!delayed.IsDefaultOrEmpty && delayed[index])
            {
                var thunk = RangeThunk(children[index], context);
                value = thunk.Value;
                values.Add(thunk.Closure);
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
                hir.Ranges.Add(new(delayed));
            }
            expression = hir.Add(node.Kind, type, node.Source,
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values), descriptor, effects: node.Effects);
        }
        return new(type, expression, Element: element);
    }

    private (SemanticValue Value, HirNodeId Closure) RangeThunk(HirNodeId expression, SemanticContext context)
    {
        HashSet<HirLocationId> read = [], declared = [];
        CollectLocations(expression, read, declared);
        read.ExceptWith(declared);
        SemanticContext deferred = new(new(context.Publish ? hir.Functions.Count : -1), context.Publish)
        { DeferredReads = true };
        foreach (var pair in context.Substitutions) deferred.Substitutions.Add(pair.Key, pair.Value);
        foreach (var pair in context.Objects) deferred.Objects.Add(pair.Key, pair.Value);
        foreach (var pair in context.Indexed) deferred.Indexed.Add(pair.Key, pair.Value);
        ImmutableArray<HirCapture>.Builder captures = ImmutableArray.CreateBuilder<HirCapture>();
        foreach (var original in read.OrderBy(location => location.Value))
        {
            var source = context.Location(original);
            var target = AllocateThunkLocation(original, deferred);
            var value = context.Values.GetValueOrDefault(source)
                ?? ValueForType(ResolveHirType(hir.Locations[source.Value].StorageType, context));
            deferred.Values[target] = value;
            deferred.Storage[target] = context.Storage.GetValueOrDefault(source, value.Type);
            captures.Add(new(target, HirCaptureMode.Location, source, HirNodeId.None));
        }
        foreach (var local in declared) _ = AllocateThunkLocation(local, deferred);
        if (context.Publish) hir.Functions.Add(new("<range-item>", [], captures.ToImmutable(),
            hir.Type(TypeRef.Any), HirNodeId.None, IsOpen: true));
        SemanticValue result = Analyze(expression, deferred);
        if (!context.Publish) return (result, HirNodeId.None);
        hir.Functions[deferred.Function.Value] = new("<range-item>", [], captures.ToImmutable(), result.Type, result.Node);
        int closure = hir.Closures.Count;
        hir.Closures.Add(new(deferred.Function, captures.ToImmutable()));
        return (result, hir.Add(HirKind.Closure, hir.Type(TypeRef.Function), program.Node(expression).Source, data: closure));
    }

    private HirLocationId AllocateThunkLocation(HirLocationId original, SemanticContext context)
    {
        HirLocationId target = original;
        if (context.Publish)
        {
            target = new(hir.Locations.Count);
            hir.Locations.Add(hir.Locations[original.Value] with { Owner = context.Function, TemplateOwner = null });
        }
        context.Locations.Add(original, target);
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
