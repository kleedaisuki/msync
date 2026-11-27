using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly Dictionary<int, SemanticScope> singletonScopes = [];
    private readonly Dictionary<int, ImmutableDictionary<string, ImmutableArray<HirSourceRange>>> fieldDefinitions = [];

    private ImmutableDictionary<string, ImmutableArray<HirSourceRange>> ScopeDefinitions(int construction,
        ImmutableArray<HirLocationId> fields)
    {
        if (fieldDefinitions.TryGetValue(construction, out var known)) return known;
        var definitions = fields.ToImmutableDictionary(field => hir.Locations[field.Value].Name,
            field => ImmutableArray.Create(hir.Locations[field.Value].Source), StringComparer.Ordinal);
        fieldDefinitions.Add(construction, definitions);
        return definitions;
    }

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
            Queries = context.Queries,
            DeferredReads = context.DeferredReads,
            ExpectedReturn = context.ExpectedReturn,
            FlowAnalysis = context.FlowAnalysis,
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
        var definitions = ScopeDefinitions(node.Data, construction.Fields);
        SemanticScope provisional = new(region, construction.Layout, fields, locations, construction.Singleton >= 0,
            Initializing: construction.Self is not null, FieldDefinitions: definitions);
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
            foreach (var child in children)
            {
                HirNodeId initializer = Analyze(child, context).Node;
                if (context.Publish && hir.Nodes[initializer.Value] is { Kind: HirKind.Literal } literal
                    && hir.Literals[literal.Data].Kind == HirLiteralKind.Null) continue;
                initializers.Add(initializer);
            }
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
                    throw Error(node.Source, $"Named Object is missing its established field '{field.Name}'.", "E0203");
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
        SemanticScope result = currentObject ?? new(region, shape, populated, locations, construction.Singleton >= 0,
            FieldDefinitions: definitions);
        destination.Objects[region] = result;
        if (construction.Singleton >= 0)
        {
            destination.SingletonSchemas[construction.Singleton] = result;
            destination.InitializedSingletons = destination.InitializedSingletons.Add(construction.Singleton);
            destination.MaybeInitializedSingletons = destination.MaybeInitializedSingletons.Add(construction.Singleton);
        }
        if (context.Publish && construction.Singleton >= 0) singletonScopes.TryAdd(construction.Singleton, result);
        HirTypeId type = hir.Type(HirType.ConstructedScope(shape));
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
            var storage = StructuralStorageType(type);
            destination.Storage[completed] = storage;
            destination.Output?.SetLocation(completed, hir.Locations[completed.Value] with { StorageType = storage });
        }
        return valueResult;
    }

    private int InternShape(ImmutableArray<HirField> fields) => hir.InternScopeLayout(new(fields));

    private SemanticValue Range(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        if (Trace is null) return RangeCore(node, children, context);
        return Trace.MeasureDetailed("compiler.semantic.range_literal", (self: this, node, context),
            static state => state.self.RangeCore(state.node, state.self.hir.Children(state.node), state.context),
            attributes: HirTelemetry.Source(hir.Sources, node.Source));
    }

    private SemanticValue RangeCore(HirNode node, ReadOnlySpan<HirNodeId> children, SemanticContext context)
    {
        Trace?.Metric("elements.initialized", children.Length);
        HirNodeId[] values = new HirNodeId[children.Length];
        SemanticValue? element = null;
        for (int index = 0; index < children.Length; index++)
        {
            SemanticValue value = Analyze(children[index], context) with { Location = null };
            values[index] = value.Node;
            element = element is null ? value : Join(element, value);
        }
        element ??= ValueForType(hir.Type(new TypeRef(TypeKind.Never)));
        HirTypeId type = RangeType(element.Type);
        HirNodeId expression = context.Publish
            ? context.Output!.Add(HirKind.Range, type, node.Source, values, node.Data, effects: node.Effects)
            : HirNodeId.None;
        return new(type, expression, Element: element, EagerRange: true,
            RangeRecipe: sessionDispatch is null ? children.IsEmpty ? EmptyRange : InlineRange : null);
    }
}
