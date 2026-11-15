using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public sealed class LlvmHirLoweringSession
{
    private Generation? acceptedCode;

    public LlvmHirLoweringResult Lower(HirModule module, CompilationTrace? trace = null)
    {
        var generation = trace?.Detail is not { } detail ? new Generation(module, acceptedCode)
            : detail.MeasureDetailed("compiler.llvm.session.registry", (module, previous: acceptedCode),
                static state => new Generation(state.module, state.previous),
                metrics: static state => new Dictionary<string, ulong>
                {
                    ["generation"] = (ulong)state.Number,
                    ["roots"] = (ulong)state.Roots.Count,
                    ["types"] = (ulong)state.Metadata.Types.Count,
                    ["layouts"] = (ulong)state.Metadata.Layouts.Count,
                    ["retained.functions"] = (ulong)(state.Program?.Functions.Count ?? 0)
                });
        return LlvmHirLowerer.LowerSession(module, generation, trace);
    }

    public NativeHirLoweringResult Emit(LlvmHirLoweringResult input, CompilationTrace? trace = null)
    {
        var generation = input.SessionGeneration
            ?? throw new ArgumentException("The graph was not produced by a persistent lowering session.", nameof(input));
        NativeProgram program = LlvmNativeLowerer.Lower(input, trace);
        if (trace?.Detail is not { } detail) Commit(generation, program, null);
        else detail.Run("compiler.llvm.session.commit", (owner: this, generation, program, detail),
            static state => state.owner.Commit(state.generation, state.program, state.detail));
        return new(program, generation.Map.Layout);
    }

    private void Commit(Generation generation, NativeProgram program, CompilationTrace? trace)
    {
        int types = generation.Metadata.Types.Count, layouts = generation.Metadata.Layouts.Count;
        generation.Metadata.ImportTarget(program);
        generation.Program = program;
        acceptedCode = generation;
        trace?.Metric("generation", generation.Number);
        trace?.Metric("imported.types", generation.Metadata.Types.Count - types);
        trace?.Metric("imported.layouts", generation.Metadata.Layouts.Count - layouts);
        trace?.Metric("callables", generation.Map.NextCallable);
        trace?.Metric("roots", generation.Roots.Count);
        trace?.Metric("allocation.sites", generation.Map.AllocationSites.Count);
        trace?.Metric("statics", program.Statics.Count);
    }

    internal sealed record Root(HirLocationId Location, int Slot, int Type, string Name);

    internal sealed class Generation
    {
        internal readonly LlvmNativeMetadata Metadata;
        internal readonly HirLoweringMap Map = new();
        internal readonly Dictionary<HirLocationId, Root> Roots = [];
        internal readonly Dictionary<int, int> StaticIndices = [];
        internal readonly HashSet<int> ScopeShapes = [];
        internal NativeProgram? Program;
        internal readonly HirFunctionId Entry;
        internal readonly int Number;
        internal readonly HirLocationId[] Exports;
        internal readonly List<NativeStatic> Statics;

        internal Generation(HirModule module, Generation? previous)
        {
            Entry = module.Entry;
            Number = (previous?.Number ?? 0) + 1;
            Exports = module.Exports.Select(export => export.Location).Distinct().ToArray();
            Metadata = previous is null ? new(module) : previous.Metadata.Fork(module);
            Program = previous?.Program;
            Statics = Program?.Statics.ToList() ?? [];
            if (previous is not null) Copy(previous);
            foreach (var location in Exports.Concat(module.UnresolvedImports).Distinct())
            {
                if (!Roots.TryGetValue(location, out var root))
                {
                    var declaration = module.Locations[location.Value];
                    root = new(location, Roots.Count, Metadata.Type(declaration.StorageType, declaration.AdmitsNull), declaration.Name);
                    Roots.Add(location, root);
                }
                Map.Locations[new(module.Entry, location)] = root.Slot;
                Map.Locations[new(module.Locations[location.Value].Owner, location)] = root.Slot;
            }
        }
        private void Copy(Generation previous)
        {
            foreach (var pair in previous.Map.Functions) Map.Functions.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Map.Templates) Map.Templates.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Map.Callables) Map.Callables.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Map.Locations) Map.Locations.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Map.AllocationSites) Map.AllocationSites.Add(pair.Key, pair.Value);
            foreach (var pair in previous.Roots) Roots.Add(pair.Key, pair.Value);
            foreach (var pair in previous.StaticIndices) StaticIndices.Add(pair.Key, pair.Value);
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
        internal int SyntheticSite() => Map.NextAllocation++;
        internal int Static(int source, int layout)
        {
            if (StaticIndices.TryGetValue(source, out int index)) return index;
            index = Statics.Count;
            StaticIndices.Add(source, index);
            Statics.Add(new(layout, Site(source)));
            return index;
        }
    }
}

public sealed record LlvmSessionCellInfo(LlvmGlobalVariable Global, HirLocationId Location, int Slot, int Type, int Layout);
public sealed record LlvmSessionDispatchInfo(int Slot, IReadOnlyDictionary<LlvmFunction, LlvmFunction> Targets);
