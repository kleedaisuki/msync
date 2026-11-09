using System.Collections.Immutable;
using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly Dictionary<string, SemanticValue> modules = new(StringComparer.Ordinal);
    private readonly List<HirNodeId> entryPrelude = [];
    private SemanticContext? entryState;

    private SemanticValue Standard(HirNode node, SemanticContext context)
    {
        ImmutableDictionary<string, SemanticValue>.Builder values = ImmutableDictionary.CreateBuilder<string, SemanticValue>(StringComparer.Ordinal);
        ImmutableDictionary<string, HirLocationId>.Builder locations = ImmutableDictionary.CreateBuilder<string, HirLocationId>(StringComparer.Ordinal);
        foreach (string module in StandardSourceCatalog.Read().Keys.Where(name => !name.StartsWith('_')).Order(StringComparer.Ordinal))
        {
            SemanticValue value = EnsureModule(module, node.Source, context);
            values.Add(module, value);
            locations.Add(module, program.StandardLocations[module]);
        }
        var fields = values.ToImmutable();
        int layout = Shape(fields);
        int region = nextRegion++;
        SemanticScope scope = new(region, layout, fields, locations.ToImmutable(), Named: true);
        context.Objects[region] = scope;
        HirTypeId type = hir.Type(new HirType(TypeRef.Scope, Shape: layout));
        if (!context.Publish) return new(type, HirNodeId.None, Scope: scope);
        int construction = hir.Scopes.Count;
        hir.Scopes.Add(new(layout, locations.Values.ToImmutableArray()));
        return new(type, hir.Add(HirKind.Scope, type, node.Source, data: construction), Scope: scope);
    }

    private SemanticValue EnsureModule(string name, HirSourceRange source, SemanticContext context)
    {
        if (context.Publish && modules.TryGetValue(name, out var existing)) return existing;
        BoundModule bound = program.BindModule!(name);
        SemanticContext target = context.Publish ? entryState! : context;
        SemanticValue value = Analyze(bound.Body, target);
        if (!program.StandardLocations.TryGetValue(name, out HirLocationId location))
        {
            location = new(hir.Locations.Count);
            hir.Locations.Add(new("$std::" + name, value.Type, source, program.Entry, HirLocationKind.Import));
            program.StandardLocations.Add(name, location);
            program.Declare(new(location, name, DeclarationKind.StandardModule, source));
        }
        target.Values[location] = value;
        target.Storage[location] = value.Type;
        context.Values[location] = value;
        context.Storage[location] = value.Type;
        foreach (var pair in target.Objects) context.Objects[pair.Key] = pair.Value;
        if (context.Publish)
        {
            modules.Add(name, value);
            hir.Locations[location.Value] = hir.Locations[location.Value] with { StorageType = value.Type };
            entryPrelude.Add(hir.Add(HirKind.Declare, hir.Type(TypeRef.Null), source, [value.Node], location.Value));
        }
        return value;
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
