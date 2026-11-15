using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Lowering
    {
        internal readonly LlvmHirLoweringSession.Generation? Session = input.SessionGeneration;
        internal readonly Dictionary<LlvmGlobalVariable, LlvmSessionCellInfo> SessionCells =
            input.SessionCells.ToDictionary(cell => cell.Global);

        private NativeStatic[] PrepareStatics()
        {
            List<NativeStatic> declarations = Session?.Statics ?? [];
            foreach (var scope in Input.StaticScopes)
            {
                int index = Session is null ? declarations.Count : Session.Static(scope.Declaration, scope.Layout);
                if (Session is null) declarations.Add(new(scope.Layout, scope.Declaration));
                StaticObjects.Add(scope.Object, index);
                StaticFlags.Add(scope.Initialized, index);
            }
            return declarations.ToArray();
        }
        private int CallableIndex(LlvmFunction function, int standaloneIndex)
        {
            if (Session is null) return standaloneIndex;
            if (Input.TemplateFunctions.TryGetValue(function, out var template))
                return Session.Template(template.SourceTemplate!.Value);
            return Session.Function(Input.SourceFunctions[function].SourceFunction!.Value);
        }
        private IReadOnlyList<SessionDispatchTable>? DispatchTables() => Input.SessionDispatches.Count == 0 ? null
            : Input.SessionDispatches.Select(dispatch => new SessionDispatchTable(dispatch.Slot,
                dispatch.Targets.ToDictionary(pair => Functions[pair.Key], pair => Functions[pair.Value]))).ToArray();
    }

    private sealed partial class Body
    {
        private int sessionCellAddresses;
        private bool IsSessionEntry => owner.Session is not null && ReferenceEquals(function, owner.Input.Entry);

        private void PrepareSessionRoots()
        {
            if (!IsSessionEntry) return;
            foreach (var root in owner.Session!.Roots.Values.OrderBy(root => root.Slot))
                Register(root.Type, NativeSlotRole.Local, root.Name);
        }
        private int SessionCellSlot(LlvmGlobalVariable global, LlvmSessionCellInfo cell)
        {
            if (!IsSessionEntry) throw Unsupported(global, "uncaptured lexical cell access in non-entry function");
            if (!values.TryGetValue(global, out int slot))
            {
                slot = Register(owner.Pointer(cell.Type));
                values.Add(global, slot);
            }
            Add(new(NativeOp.FrameAddress, slot, cell.Slot, Source: instructionSource));
            ++sessionCellAddresses;
            return slot;
        }
        private bool SessionFieldAddress(LlvmGetElementPtrInstruction address)
        {
            if (address.PointerOperand is not LlvmGlobalVariable global || !owner.SessionCells.TryGetValue(global, out var cell))
                return false;
            if (!IsSessionEntry) throw Unsupported(address, "uncaptured lexical cell GEP in non-entry function");
            Add(new(NativeOp.FrameAddress, Slot(address), cell.Slot, Source: address.Source));
            ++sessionCellAddresses;
            return true;
        }
        private int AllocationSite(LlvmHeapAllocInst allocation) => owner.Session is null
            ? owner.Allocations.Count + allocations.Count - 1
            : allocation.LogicalAllocationSite >= 0 ? owner.Session.Site(allocation.LogicalAllocationSite) : owner.Session.SyntheticSite();
    }
}
