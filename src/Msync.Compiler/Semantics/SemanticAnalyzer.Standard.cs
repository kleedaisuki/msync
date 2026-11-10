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
        HirLocationId root = program.StandardLocations[""];
        if (entryState!.ActivatedModules.Contains(root))
            return context.Values.GetValueOrDefault(root) ?? entryState.Values[root];
        ImmutableDictionary<string, SemanticValue>.Builder values = ImmutableDictionary.CreateBuilder<string, SemanticValue>(StringComparer.Ordinal);
        ImmutableDictionary<string, HirLocationId>.Builder locations = ImmutableDictionary.CreateBuilder<string, HirLocationId>(StringComparer.Ordinal);
        foreach (string module in StandardSourceCatalog.Read().Keys.Where(name => !name.StartsWith('_')).Order(StringComparer.Ordinal))
        {
            SemanticValue value = EnsureModule(module, node.Source, context);
            values.Add(module, value);
            if (program.StandardLocations.TryGetValue(module, out var location)) locations.Add(module, location);
        }
        var fields = values.ToImmutable();
        int layout = Shape(fields);
        int region = nextRegion++;
        SemanticScope scope = new(region, layout, fields, locations.ToImmutable(), Named: true);
        context.Objects[region] = scope;
        HirTypeId type = hir.Type(new HirType(TypeRef.Scope, Shape: layout));
        if (!context.Publish) return new(type, HirNodeId.None, Scope: scope);
        int construction = hir.Scopes.Count;
        context.Output!.AddScope(new(layout, locations.Values.ToImmutableArray()));
        SemanticValue result = new(type, context.Output!.Add(HirKind.Scope, type, node.Source, data: construction), Scope: scope);
        PublishModule(root, result, node.Source, context);
        return result;
    }

    private SemanticValue EnsureModule(string name, HirSourceRange source, SemanticContext context)
    {
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
            context.Storage[location] = value.Type;
            return value;
        }
        PublishModule(location, value, source, context);
        return value;
    }

    private void PublishModule(HirLocationId location, SemanticValue value, HirSourceRange source,
        SemanticContext context)
    {
        SemanticContext target = entryState!;
        target.Values[location] = value;
        context.Values[location] = value;
        if (value.Scope is { } scope) target.Objects[scope.Region] = scope;
        target.Storage[location] = value.Type;
        context.Storage[location] = value.Type;
        target.ActivatedModules = target.ActivatedModules.Add(location);
        context.ActivatedModules = context.ActivatedModules.Add(location);
        foreach (var pair in target.Objects) context.Objects[pair.Key] = pair.Value;
        SealStorage(location, value.Type, target);
        entryPrelude.Add(context.Output!.Add(HirKind.Declare, hir.Type(TypeRef.Null), source, [value.Node], location.Value));
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
