using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly List<HirNodeId> entryPrelude = [];
    private SemanticContext? entryState;

    private SemanticValue Standard(HirNode node, SemanticContext context)
    {
        SemanticValue rootValue = EnsureStandardRoot(node.Source, context);
        HirLocationId root = program.StandardLocations[""];
        var fields = ImmutableDictionary.CreateBuilder<string, SemanticValue>(StringComparer.Ordinal);
        foreach (string name in PublicStandardModules()) fields.Add(name, EnsureModule(name, node.Source, context));
        var scope = rootValue.Scope! with { Layout = Shape(fields.ToImmutable()), Fields = fields.ToImmutable() };
        var declared = hir.Types[rootValue.Type.Value];
        HirTypeId type = hir.Type(declared with { Shape = scope.Layout });
        var result = rootValue with { Type = type, Scope = scope };
        SemanticContext target = context.Publish ? entryState! : context;
        target.Objects[scope.Region] = scope;
        target.Values[root] = result;
        context.Objects[scope.Region] = scope;
        context.Values[root] = result;
        return result;
    }

    private static IEnumerable<string> PublicStandardModules() =>
        StandardSourceCatalog.Read().Keys.Where(name => !name.StartsWith('_')).Order(StringComparer.Ordinal);

    private void EnsureStandardCapture(HirLocationId location, HirSourceRange source, SemanticContext context)
    {
        if (sessionDispatch is null || !context.Publish
            || !program.StandardLocations.TryGetValue("", out var root) || entryState!.Values.ContainsKey(root))
            return;
        var definition = context.Definitions.GetValueOrDefault(location, location);
        var declaration = StandardDeclaration(definition);
        if (declaration is null || declaration.Kind != DeclarationKind.StandardRoot && declaration.Name.StartsWith('_'))
            return;
        // A retained closure must capture the canonical member address even
        // when no submission has needed the member's initializer yet.
        _ = EnsureStandardRoot(source, context);
        Trace?.Metric("session.std_capture_roots", 1);
    }

    private SemanticValue EnsureStandardRoot(HirSourceRange source, SemanticContext context)
    {
        HirLocationId root = program.StandardLocations[""];
        SemanticContext target = context.Publish ? entryState! : context;
        if (target.Values.TryGetValue(root, out var existing)) return existing;
        string[] names = PublicStandardModules().ToArray();
        HirTypeId storage = hir.Type(TypeRef.Scope);
        int layout = hir.InternScopeLayout(new(names.Select(name => new HirField(name, storage)).ToImmutableArray()));
        var fields = names.ToImmutableDictionary(name => name, _ => ValueForType(storage), StringComparer.Ordinal);
        var locations = names.ToImmutableDictionary(name => name, name => program.StandardLocations[name], StringComparer.Ordinal);
        var scope = new SemanticScope(++nextRegion, layout, fields, locations, Named: true);
        target.Objects[scope.Region] = scope;
        HirTypeId type = hir.Type(HirType.ConstructedScope(layout));
        HirNodeId expression = HirNodeId.None;
        if (context.Publish)
        {
            int construction = hir.Scopes.Count;
            context.Output!.AddScope(new(layout, names.Select(name => locations[name]).ToImmutableArray()));
            expression = context.Output.Add(HirKind.AllocateScope, type, source, data: construction,
                effects: HirEffects.WriteLocation | HirEffects.WriteMemory);
        }
        var value = new SemanticValue(type, expression, Scope: scope);
        target.Values[root] = value;
        context.Values[root] = value;
        if (context.Publish)
        {
            // Canonical module cells exist before any initializer can capture their addresses.
            entryPrelude.Insert(0, PublishModule(root, value, source, context));
        }
        return value;
    }

    private SemanticValue EnsureModule(string name, HirSourceRange source, SemanticContext context) =>
        Trace is null ? EnsureModuleCore(name, source, context)
            : Trace.MeasureDetailed("compiler.semantic.standard_module", (self: this, name, source, context),
                static state => state.self.EnsureModuleCore(state.name, state.source, state.context),
                attributes: new Dictionary<string, string> { ["module.name"] = name });

    private SemanticValue EnsureModuleCore(string name, HirSourceRange source, SemanticContext context)
    {
        if (!name.StartsWith('_')) _ = EnsureStandardRoot(source, context);
        HirLocationId shared = program.StandardLocations[name];
        if (entryState!.ActivatedModules.Contains(shared))
            return StorageValue(shared, context) ?? entryState.Values[shared];
        if (!context.Publish && context.Values.TryGetValue(shared, out var schema) && schema.Scope is not null)
            return schema;
        BoundModule bound = program.StandardModules[name];
        SemanticContext target = context.Publish ? entryState! : context;
        SemanticValue value = Analyze(bound.Body, target);
        if (value.Scope is { } module)
        {
            SemanticScope named = module with { Named = true };
            target.Objects[named.Region] = named;
            value = value with { Scope = named };
        }
        HirLocationId location = program.StandardLocations[name];
        if (!context.Publish)
        {
            context.Values[location] = value;
            context.Storage[location] = hir.Type(TypeRef.Scope);
            return value;
        }
        entryPrelude.Add(PublishModule(location, value, source, context));
        return value;
    }

    private HirNodeId PublishModule(HirLocationId location, SemanticValue value, HirSourceRange source,
        SemanticContext context)
    {
        SemanticContext target = entryState!;
        target.Values[location] = value;
        context.Values[location] = value;
        if (value.Scope is { } scope) target.Objects[scope.Region] = scope;
        HirTypeId storage = hir.Type(TypeRef.Scope);
        target.Storage[location] = storage;
        context.Storage[location] = storage;
        target.ActivatedModules = target.ActivatedModules.Add(location);
        context.ActivatedModules = context.ActivatedModules.Add(location);
        foreach (var pair in target.Objects) context.Objects[pair.Key] = pair.Value;
        SealStorage(location, storage, target);
        return context.Output!.Add(HirKind.Declare, hir.Type(TypeRef.Null), source, [value.Node], location.Value);
    }

    private BoundDeclaration? StandardDeclaration(HirLocationId source)
    {
        while (program.TryDeclaration(source, out var declaration))
        {
            if (declaration.Kind is DeclarationKind.StandardModule or DeclarationKind.StandardRoot) return declaration;
            if (declaration.CapturedFrom is not { } captured) return null;
            source = captured;
        }
        return null;
    }
}
