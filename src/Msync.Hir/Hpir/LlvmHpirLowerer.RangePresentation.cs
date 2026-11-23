using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private Dictionary<HpirSessionField, int>? sessionRangeItemImports;
        internal readonly List<HpirSessionField> SessionRangeItems = [];

        internal int SessionRangeItem(LlvmCallInstruction call, LlvmAllocaInstruction item)
        {
            if (!graph.SessionStreamsRange || call.CalledFunction is not { } target
                || !graph.ValueOperations.TryGetValue(target, out var use)
                || use.Operation != LlvmRuntimeValueOperation.SessionRangeItem) return -1;
            var kind = (TypeKind)use.Mode;
            if ((kind & TypeKind.Range) != 0)
                throw new MsyncException("HPIR session Range presentation requires concrete non-Range items.", call.Source);
            var field = new HpirSessionField("<item>", 0, kind, SessionUnion(item.AllocatedType));
            var imports = sessionRangeItemImports ??= [];
            if (imports.TryGetValue(field, out int id)) return id;
            imports.Add(field, id = Imports.Count);
            Imports.Add(new(HpirService.SessionRangeItem, SessionRangeItems.Count, HpirWidth.Void, [HpirWidth.U64]));
            SessionRangeItems.Add(field);
            return id;
        }
    }

    private sealed partial class Body
    {
        private bool TrySessionRangeItem(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 1 || call.GetArgument(0) is not LlvmAllocaInstruction item
                || geometry.SessionRangeItem(call, item) is not (>= 0 and var import)) return false;
            EmitImportCall(call, import, [Location(item)], safepoint: false);
            return true;
        }
    }
}
