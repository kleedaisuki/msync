using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    internal sealed class StorageConstraintGraph
    {
        internal Dictionary<HirLocationId, HirLocationId> Owners { get; } = [];
        internal Dictionary<HirLocationId, List<HirLocationId>> Members { get; } = [];
        internal Dictionary<HirLocationId, HirTypeId> Types { get; } = [];
    }

    private StorageConstraintGraph? storageConstraints;

    private StorageConstraintGraph? StorageGraph(SemanticContext context, bool create)
    {
        if (context.Publish) return create ? storageConstraints ??= new() : storageConstraints;
        if (!create) return context.Queries?.StorageConstraints;
        return (context.Queries ??= new()).StorageConstraints ??= new();
    }

    private HirLocationId StorageOwner(HirLocationId location, SemanticContext context)
    {
        var graph = StorageGraph(context, false);
        if (graph?.Owners.TryGetValue(location, out var owner) == true) return owner;
        if (!context.Publish && storageConstraints?.Owners.TryGetValue(location, out owner) == true) return owner;
        return location;
    }

    private HirTypeId StorageType(HirLocationId location, SemanticContext context)
    {
        var owner = StorageOwner(location, context);
        var graph = StorageGraph(context, false);
        HirTypeId type = context.Storage.GetValueOrDefault(owner, hir.Locations[owner.Value].StorageType);
        if (graph?.Types.TryGetValue(owner, out var local) == true) type = local;
        else if (!context.Publish && storageConstraints?.Types.TryGetValue(owner, out var published) == true) type = published;
        type = ResolveHirType(type, context);
        return hir.Locations[owner.Value].AdmitsNull ? Nullable(type) : type;
    }

    private HirLocationId? StandardCell(HirLocationId location, SemanticContext context)
    {
        var definition = context.Definitions.GetValueOrDefault(location, location);
        return StandardDeclaration(definition) is { Kind: DeclarationKind.StandardModule } module ? module.Location : null;
    }

    private SemanticValue? StorageValue(HirLocationId location, SemanticContext context)
    {
        var owner = StorageOwner(location, context);
        var module = StandardCell(owner, context) ?? StandardCell(location, context);
        // Binding has already sealed namespace exports. Initialization completes
        // the program-owned cell, not a caller's provisional capture snapshot.
        if (module is { } canonical)
        {
            bool localWrite = context.ReplacedProgramCells.Contains(canonical);
            var value = localWrite ? context.Values.GetValueOrDefault(canonical) : null;
            value ??= entryState?.Values.GetValueOrDefault(canonical)
                ?? context.Values.GetValueOrDefault(canonical);
            if (value?.Scope is { } scope)
            {
                if (entryState?.Objects.TryGetValue(scope.Region, out var published) == true) scope = published;
                if (context.Objects.TryGetValue(scope.Region, out var current) && current.Layout == scope.Layout) scope = current;
                scope = scope with { Named = true };
                context.Objects[scope.Region] = scope;
                if (context.Publish) projectedSingletons[scope.Region] = scope;
                return value with { Scope = scope, Type = hir.Type(new HirType(TypeRef.Scope, Shape: scope.Layout)) };
            }
            if (value is not null) return value;
        }
        return context.Values.GetValueOrDefault(owner) ?? context.Values.GetValueOrDefault(location);
    }

    private void MarkProgramCell(HirLocationId location, SemanticContext context)
    {
        if (StandardCell(location, context) is { } canonical)
        {
            context.ReplacedProgramCells = context.ReplacedProgramCells.Add(canonical);
            if (location != canonical && context.Values.TryGetValue(location, out var value))
                context.Values[canonical] = value;
        }
    }

    private void RegisterStorageAlias(HirLocationId target, HirLocationId source, SemanticContext context)
    {
        var type = StorageType(source, context);
        var graph = StorageGraph(context, true)!;
        var owner = StorageOwner(source, context);
        if (!graph.Members.TryGetValue(owner, out var members))
        {
            members = [owner];
            graph.Members.Add(owner, members);
            graph.Owners[owner] = owner;
        }
        if (!graph.Owners.ContainsKey(target)) members.Add(target);
        graph.Owners[target] = owner;
        graph.Types[owner] = type;
        context.Storage[target] = type;
        context.Output?.SetLocation(target, hir.Locations[target.Value] with { StorageType = type });
    }

    // A capture imports a cell, not an independent storage constraint. Solving
    // its first non-Null carrier seals all same-cell aliases in this instance.
    private void SealStorage(HirLocationId location, HirTypeId type, SemanticContext context)
    {
        var owner = StorageOwner(location, context);
        if (hir.Locations[owner.Value].AdmitsNull) type = Nullable(type);
        context.Storage[location] = type;
        var graph = StorageGraph(context, false);
        if (graph is null || !graph.Members.TryGetValue(owner, out var members))
        {
            context.Output?.SetLocation(location, hir.Locations[location.Value] with { StorageType = type });
            return;
        }
        graph.Types[owner] = type;
        foreach (var member in members)
        {
            if (context.Storage.ContainsKey(member)) context.Storage[member] = type;
            context.Output?.SetLocation(member, hir.Locations[member.Value] with { StorageType = type });
        }
    }

    private void UpdateStorageValue(HirLocationId location, SemanticValue value, SemanticContext context)
    {
        var owner = StorageOwner(location, context);
        var graph = StorageGraph(context, false);
        if (graph is null || !graph.Members.TryGetValue(owner, out var members)) return;
        context.Values[owner] = value;
        foreach (var member in members)
            if (context.Values.ContainsKey(member)) context.Values[member] = value;
    }
}
