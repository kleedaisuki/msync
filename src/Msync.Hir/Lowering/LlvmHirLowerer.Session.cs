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
        internal Dictionary<int, LlvmSessionDispatchInfo>? physicalSessionTargets;
        private IReadOnlyList<LlvmSessionDispatchInfo> PrepareSession()
        {
            if (session is null) return [];
            foreach (var location in captured)
                if (session.Roots.ContainsKey(location)) session.CapturedRoots.Add(location);
            foreach (var location in session.Exports) SessionCell(location);
            var dispatches = hir.SessionDispatches.Select(dispatch => new LlvmSessionDispatchInfo(dispatch.Slot,
                Dispatch(dispatch.Targets)) { NativeTargets = dispatch.NativeTargets }).ToArray();
            physicalSessionTargets = dispatches.ToDictionary(dispatch => dispatch.Slot);
            trace?.Detail?.Metric("session.roots", session.Roots.Count);
            trace?.Detail?.Metric("session.dispatches", dispatches.Length);
            return dispatches;
        }

        internal LlvmConstant NativeCode(string native)
        {
            if (session is not { } physical) return LlvmConstantPointerNull.Get(CodePointer);
            ulong code = unchecked((ulong)physical.NativeIdentity(native));
            return LlvmConstantExpr.GetIntToPtr(Int(code), CodePointer);
        }

        internal (StorageRecord Storage, LlvmSessionCellInfo Binding) SessionCell(HirLocationId location)
        {
            if (sessionCells.TryGetValue(location, out var existing)) return existing;
            var root = session!.Roots[location];
            var declaration = hir.Locations[location.Value];
            var storage = Cell(declaration.StorageType, declaration.AdmitsNull);
            bool aliased = declaration.Kind == HirLocationKind.Import
                && (session.AliasedImports.Contains(location) || fields.Contains(location));
            // The canonical module's field is allocated once, even if the
            // module value is first initialized in a later submission.
            if (aliased) session.AliasedImports.Add(location);
            LlvmType type = aliased ? DataPointer : storage.Type;
            LlvmConstant initial = aliased ? LlvmConstantPointerNull.Get(DataPointer) : LlvmConstantAggregateZero.Get(storage.Type);
            var global = new LlvmGlobalVariable(module, type, false, LlvmLinkage.InternalLinkage,
                initial, "msync.session.cell." + location.Value)
                { LogicalType = metadata.Pointer(root.Type) };
            var binding = new LlvmSessionCellInfo(global, location, root.Slot, root.Type, storage.Layout)
            {
                Initialized = new LlvmGlobalVariable(module, LlvmIntegerType.Get(context, 1), false,
                    LlvmLinkage.InternalLinkage, LlvmConstantInt.GetFalse(context),
                    "msync.session.ready." + location.Value),
                Aliased = aliased,
                Captured = session.CapturedRoots.Contains(location)
            };
            var result = (storage, binding);
            sessionCells.Add(location, result);
            return result;
        }
    }

    private sealed partial class Body
    {
        private void PublishSessionDeclaration(HirLocationId location)
        {
            if (owner.session is not { } session || !session.Roots.ContainsKey(location)) return;
            var guard = owner.SessionCell(location).Binding.Initialized!;
            Add(new LlvmStoreInstruction(LlvmConstantInt.GetTrue(owner.context), guard, ordering: LlvmAtomicOrdering.Release));
        }

        private LlvmValue? SessionAddress(HirLocationId location)
        {
            if (owner.session is null || !owner.session.Roots.ContainsKey(location)) return null;
            var (storage, binding) = owner.SessionCell(location);
            // Inside the initializer BindScope already supplied the field address.
            if (binding.Aliased) return Add(new LlvmLoadInstruction(owner.DataPointer, binding.Global)
                { LogicalType = owner.metadata.Pointer(binding.Type) });
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
