using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal readonly List<HpirGlobal> Globals = [];
        private readonly record struct GlobalOrigin(LlvmGlobalVariable Object, int Declaration, int Layout,
            bool Guard, HpirSessionCell? Cell = null);
        private Dictionary<LlvmGlobalVariable, GlobalOrigin>? globalOrigins;
        private readonly Dictionary<LlvmGlobalVariable, int> globalIds = [];
        private Dictionary<int, HpirSessionCell>? sessionCellRelocations;
        private Dictionary<int, LlvmGlobalVariable>? sessionAliasLinks;
        private bool HasGlobals => graph.StaticScopes.Count != 0 || graph.SessionCells.Count != 0;

        internal bool TryGlobal(LlvmGlobalVariable global, out int relocation)
        {
            relocation = -1;
            if (!HasGlobals || !GlobalOrigins().TryGetValue(global, out var origin)) return false;
            if (!globalIds.TryGetValue(origin.Object, out int id))
            {
                id = Globals.Count;
                int layout = origin.Cell is { Aliased: true } ? SessionAliasLayout() : LayoutId(origin.Layout, null);
                Globals.Add(new(origin.Declaration, layout));
                globalIds.Add(origin.Object, id);
                if (origin.Cell is { } cell) (sessionCellRelocations ??= []).Add(id, cell);
            }
            relocation = checked(id * 2 + (origin.Guard ? 1 : 0));
            return true;
        }

        // Only these two producer-owned operations used acquire/release. The
        // physical guest has one executor; host work never reads the guard.
        internal bool IsGlobalGuard(LlvmValue value) => value is LlvmGlobalVariable global
            && HasGlobals
            && GlobalOrigins().TryGetValue(global, out var origin) && origin.Guard;

        internal int GlobalLayout(LlvmGlobalVariable global) => HasGlobals
            && GlobalOrigins().TryGetValue(global, out var origin) && !origin.Guard ? origin.Layout : -1;

        internal LlvmGlobalVariable? SessionAliasLink(LlvmCallInstruction call)
        {
            if (graph.SessionGeneration is not { }
                || call.CalledFunction is not { } target || !graph.ValueOperations.TryGetValue(target, out var value)
                || value.Operation != LlvmRuntimeValueOperation.BindSessionCell) return null;
            _ = GlobalOrigins();
            return sessionAliasLinks?.GetValueOrDefault(value.Mode);
        }

        private Dictionary<LlvmGlobalVariable, GlobalOrigin> GlobalOrigins()
        {
            if (globalOrigins is not null) return globalOrigins;
            var origins = new Dictionary<LlvmGlobalVariable, GlobalOrigin>((graph.StaticScopes.Count + graph.SessionCells.Count) * 2);
            foreach (var origin in graph.StaticScopes)
            {
                GlobalOrigin binding = new(origin.Object, origin.Declaration, origin.Layout, false);
                origins.Add(origin.Object, binding);
                origins.Add(origin.Initialized, binding with { Guard = true });
            }
            foreach (var cell in graph.SessionCells)
            {
                if (cell.Initialized is not { } guard) continue;
                var union = !cell.Aliased && cell.Global.ValueType is LlvmStructType { Elements.Count: 1 } record
                    ? SessionUnion(record.Elements[0]) : null;
                GlobalOrigin binding = new(cell.Global, cell.Location.Value, cell.Layout, false,
                    new(cell.Slot, graph.Types[cell.Type].Kind, cell.Aliased) { Union = union });
                origins.Add(cell.Global, binding);
                origins.Add(guard, binding with { Guard = true });
                if (cell.Aliased) (sessionAliasLinks ??= []).Add(cell.Slot, cell.Global);
            }
            return globalOrigins = origins;
        }
    }

    private sealed partial class Body
    {
        private int globalAddresses;
        private int sessionAliasBindings;

        private bool TryBindSessionAlias(LlvmCallInstruction call)
        {
            if (geometry.SessionAliasLink(call) is not { } link) return false;
            Copy(HpirWidth.U64, ~Location(link), Location(call.GetArgument(0)), call.Source);
            ++sessionAliasBindings;
            return true;
        }

        private bool PrepareGlobal(LlvmGlobalVariable global)
        {
            if (!geometry.TryGlobal(global, out int relocation)) return false;
            int location = Reserve(8, 8);
            Add(new(HpirOp.AddressOfGlobal, location, Immediate: relocation));
            operands.Add(global, location);
            ++globalAddresses;
            return true;
        }
    }
}
