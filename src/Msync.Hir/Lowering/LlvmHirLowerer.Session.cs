using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    internal static LlvmHirLoweringResult LowerSession(HirModule module,
        LlvmHirLoweringSession.Generation generation, Msync.Core.CompilationTrace? trace)
    {
        var lowering = new Lowering(module, trace, generation);
        return trace is null ? lowering.Run() : trace.Measure("compiler.llvm.lower", lowering, static state => state.Run());
    }

    private sealed partial class Lowering
    {
        private readonly Dictionary<HirLocationId, (StorageRecord Storage, LlvmSessionCellInfo Binding)> sessionCells = [];
        private IReadOnlyList<LlvmSessionDispatchInfo> PrepareSession()
        {
            if (session is null) return [];
            foreach (var location in session.Exports) SessionCell(location);
            var dispatches = hir.SessionDispatches.Select(dispatch => new LlvmSessionDispatchInfo(dispatch.Slot,
                dispatch.Targets.ToDictionary(target => target.Identity.IsTemplate
                    ? Template(new(target.Identity.Value)) : Reserve(new(target.Identity.Value)), target => Reserve(target.Target)))).ToArray();
            trace?.Detail?.Metric("session.roots", session.Roots.Count);
            trace?.Detail?.Metric("session.dispatches", dispatches.Length);
            return dispatches;
        }
        internal (StorageRecord Storage, LlvmSessionCellInfo Binding) SessionCell(HirLocationId location)
        {
            if (sessionCells.TryGetValue(location, out var existing)) return existing;
            var root = session!.Roots[location];
            var declaration = hir.Locations[location.Value];
            var storage = Cell(declaration.StorageType, declaration.AdmitsNull);
            var global = new LlvmGlobalVariable(module, storage.Type, false, LlvmLinkage.InternalLinkage,
                LlvmConstantAggregateZero.Get(storage.Type), "msync.session.cell." + location.Value)
                { LogicalType = metadata.Pointer(root.Type) };
            var binding = new LlvmSessionCellInfo(global, location, root.Slot, root.Type, storage.Layout);
            var result = (storage, binding);
            sessionCells.Add(location, result);
            return result;
        }
    }

    private sealed partial class Body
    {
        private LlvmValue? SessionAddress(HirLocationId location)
        {
            if (owner.session is null || !owner.session.Roots.ContainsKey(location)) return null;
            var (storage, binding) = owner.SessionCell(location);
            var address = new LlvmGetElementPtrInstruction(storage.Type, binding.Global,
                [owner.Int(0), LlvmConstantInt.Get(LlvmIntegerType.Get(owner.context, 32), 0)])
                { LogicalType = owner.metadata.Pointer(binding.Type) };
            entry.InsertBefore(address, entry.FirstInstruction);
            Record(address);
            locations.Add(location, address);
            return address;
        }
    }
}
