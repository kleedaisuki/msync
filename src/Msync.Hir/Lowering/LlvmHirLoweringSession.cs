using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public sealed class LlvmHirLoweringSession
{
    private Generation? acceptedCode;

    public LlvmHirLoweringResult LowerPhysical(HirModule module, IReadOnlyList<HirExport> shadows, CompilationTrace? trace)
    {
        var generation = trace?.Detail is not { } detail ? new Generation(module, shadows, acceptedCode)
            : detail.MeasureDetailed("compiler.llvm.session.registry", (module, shadows, previous: acceptedCode),
                static state => new Generation(state.module, state.shadows, state.previous),
                metrics: static state => new Dictionary<string, ulong>
                {
                    ["generation"] = (ulong)state.Number,
                    ["roots"] = (ulong)state.Roots.Count,
                    ["shadow.paths"] = (ulong)(state.ShadowPaths?.Count ?? 0),
                    ["import.roots"] = (ulong)state.ImportSlots.Count,
                    ["import.aliases.inherited"] = (ulong)state.AliasedImports.Count,
                    ["types"] = (ulong)state.Metadata.Types.Count,
                    ["layouts"] = (ulong)state.Metadata.Layouts.Count,
                    ["retained.functions"] = (ulong)state.Map.Functions.Count
                });
        return LlvmHirLowerer.LowerSession(module, generation, trace);
    }

    public (HpirSessionSubmission Submission, HirLoweringLayout Layout) EmitPhysical(
        LlvmHirLoweringResult input, CompilationTrace? trace)
    {
        if (input.SessionGeneration is not { } generation)
            throw new ArgumentException("Expected a physical session graph.", nameof(input));
        HpirSessionSubmission submission = LlvmHpirLowerer.LowerSession(input, trace);
        // Keep code identities for statements that published storage even when
        // a subsequent statement in the same submission fails at execution.
        acceptedCode = generation;
        return (submission, generation.Map.Layout);
    }

    internal sealed record Root(HirLocationId Location, int Slot, int Type, string Name);

    internal sealed class Generation
    {
        internal readonly LlvmNativeMetadata Metadata;
        internal readonly HirLoweringMap Map = new();
        internal readonly Dictionary<HirLocationId, Root> Roots = [];
        internal readonly HashSet<HirLocationId> AliasedImports = [];
        internal readonly HashSet<HirLocationId> CapturedRoots = [];
        private readonly Dictionary<string, long> nativeIdentities = new(StringComparer.Ordinal);
        internal readonly HashSet<int> ScopeShapes = [];
        internal readonly object Owner;
        internal HpirSessionLayouts? HpirLayouts;
        internal HpirSessionTypes? HpirTypes;
        internal readonly HirFunctionId Entry;
        internal readonly int Number;
        internal readonly int SourceCount, InheritedSources;
        internal readonly HirLocationId[] Exports;
        internal readonly IReadOnlyList<int[]>? ShadowPaths;
        internal readonly List<int> ImportSlots = [];

        internal Generation(HirModule module, IReadOnlyList<HirExport> shadows, Generation? previous)
        {
            Owner = previous?.Owner ?? new object();
            Entry = module.Entry;
            Number = (previous?.Number ?? 0) + 1;
            SourceCount = module.Sources.Length;
            InheritedSources = previous?.SourceCount ?? 0;
            Exports = module.Exports.Concat(shadows).Select(export => export.Location).Distinct().ToArray();
            Metadata = previous is null ? new(module) : previous.Metadata.Fork(module);
            HpirLayouts = previous?.HpirLayouts;
            HpirTypes = previous?.HpirTypes;
            if (previous is not null) Copy(previous);
            // A module activation writes an import in this generation; later
            // submissions and existing closures still address the same cell.
            var imports = module.Locations.Select((declaration, index) => (declaration, index))
                .Where(item => item.declaration.Kind == HirLocationKind.Import
                    && item.declaration.TemplateOwner is null && item.declaration.Owner == module.Entry)
                .Select(item => new HirLocationId(item.index));
            foreach (var location in Exports.Concat(module.UnresolvedImports).Concat(imports).Distinct())
            {
                if (!Roots.TryGetValue(location, out var root))
                {
                    var declaration = module.Locations[location.Value];
                    root = new(location, Roots.Count, Metadata.Type(declaration.StorageType, declaration.AdmitsNull), declaration.Name);
                    Roots.Add(location, root);
                }
                if (module.Locations[location.Value].Kind == HirLocationKind.Import)
                    ImportSlots.Add(root.Slot);
                Map.Locations[new(module.Entry, location)] = root.Slot;
                Map.Locations[new(module.Locations[location.Value].Owner, location)] = root.Slot;
            }
            if (shadows.Count == 0) return;
            var visible = module.Exports.ToDictionary(export => export.Name, export => Roots[export.Location].Slot);
            ShadowPaths = [.. shadows.GroupBy(shadow => shadow.Name).Select(group =>
                new[] { visible[group.Key] }.Concat(group.Reverse().Select(shadow => Roots[shadow.Location].Slot)).ToArray())];
        }
        private void Copy(Generation previous)
        {
            foreach (var pair in previous.Map.Functions) Map.Functions.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Map.Templates) Map.Templates.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Map.Callables) Map.Callables.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Map.Locations) Map.Locations.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Map.AllocationSites) Map.AllocationSites.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Roots) Roots.Add(pair.Key, pair.Value);
            AliasedImports.UnionWith(previous.AliasedImports);
            CapturedRoots.UnionWith(previous.CapturedRoots);
            foreach (var pair in previous.nativeIdentities) nativeIdentities.Add(pair.Key, pair.Value);
            ScopeShapes.UnionWith(previous.ScopeShapes);
            Map.NextCallable = previous.Map.NextCallable;
            Map.NextAllocation = previous.Map.NextAllocation;
        }
        internal int Function(HirFunctionId function)
        {
            if (Map.Functions.TryGetValue(function, out int index)) return index;
            index = Map.NextCallable++;
            Map.Functions.Add(function, index);
            Map.Callables.Add(index, new(function.Value, false));
            return index;
        }

        internal long NativeIdentity(string native)
        {
            if (nativeIdentities.TryGetValue(native, out long code)) return code;
            // Physical guest identities use positive entries. Native tokens
            // also stay separate from nearby negative LLVM extern identities.
            code = long.MinValue + nativeIdentities.Count;
            nativeIdentities.Add(native, code);
            return code;
        }
        internal int Template(HirTemplateId template)
        {
            if (Map.Templates.TryGetValue(template, out int index)) return index;
            index = Map.NextCallable++;
            Map.Templates.Add(template, index);
            Map.Callables.Add(index, new(template.Value, true));
            return index;
        }
        internal int Site(int source)
        {
            var identity = new HirAllocationSiteId(source);
            if (Map.AllocationSites.TryGetValue(identity, out int site)) return site;
            site = Map.NextAllocation++;
            Map.AllocationSites.Add(identity, site);
            return site;
        }
    }
}

internal sealed record HpirSessionLayouts(IReadOnlyList<HpirLayout> Layouts, Dictionary<int, int> LogicalIds,
    int TextLayout, int AliasLayout, int PathLayout, Dictionary<int, HpirSessionField[]> ScopeLayouts,
    IReadOnlySet<int> TextSlots, IReadOnlyDictionary<int, HpirSessionSnapshot> SnapshotLayouts);
internal sealed record HpirSessionTypes(IReadOnlyList<HpirTypeLiteral> Literals,
    Dictionary<TypeRef, int> Ids, IReadOnlyList<TypeRef> Witnesses);

public sealed record LlvmSessionCellInfo(LlvmGlobalVariable Global, HirLocationId Location, int Slot, int Type, int Layout)
{
    internal LlvmGlobalVariable? Initialized { get; init; }
    internal bool Aliased { get; init; }
    internal bool Captured { get; init; }
}
public sealed record LlvmSessionDispatchInfo(int Slot, IReadOnlyDictionary<LlvmFunction, LlvmFunction> Targets)
{
    internal IReadOnlyList<HirSessionNativeTarget> NativeTargets { get; init; } = [];
}
