using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmValue, bool?> liveTextWindows = [];
        private int liveTextWindowAllocator = -1;

        internal bool? LiveTextWindow(LlvmValue value)
        {
            if (!IsGuestPointer(value.Type)) return null;
            if (liveTextWindows.TryGetValue(value, out bool? known)) return known;
            liveTextWindows.Add(value, null);
            bool? file = value is LlvmCallInstruction call && IsTransformTake(call)
                ? LiveTextPlan(call.GetArgument(0)) ?? LiveTextWindow(call.GetArgument(0)) : null;
            if (file is null && RangeInputs(value) is { Count: > 0 } incoming)
            {
                file = LiveTextWindow(incoming[0]);
                if (file is not null && incoming.Skip(1).Any(input => LiveTextWindow(input) != file)) file = null;
            }
            return liveTextWindows[value] = file;
        }

        internal int LiveTextWindowAllocator() => liveTextWindowAllocator >= 0 ? liveTextWindowAllocator
            : liveTextWindowAllocator = PhysicalAllocator(AddLayout(new(16, 8, [0])));
    }

    private sealed partial class Body
    {
        private readonly record struct TextWindowCursor(int Remaining, int One);
        private Dictionary<LlvmCallInstruction, TextWindowCursor>? textWindowCursors;
        private int textWindows;

        private void EmitLiveTextWindow(LlvmCallInstruction call)
        {
            var input = call.GetArgument(0);
            int cap = EmitRangeBound(call, call.GetArgument(1), HpirRangeBound.Take);
            int source = Location(input), plan = Location(call);
            if (geometry.LiveTextWindow(input) is not null)
            {
                ClampRangeLength(call, cap, Indirect(source, sizeof(ulong), call));
                int original = Reserve(8, 8);
                Copy(HpirWidth.U64, original, ~source, call.Source);
                source = original;
            }
            EmitCall(call, ~geometry.LiveTextWindowAllocator(), plan, []);
            Copy(HpirWidth.U64, ~plan, source, call.Source);
            Copy(HpirWidth.U64, Indirect(plan, sizeof(ulong), call), cap, call.Source);
            allocatedTransforms.Add(call);
            ++textWindows;
        }

        private void OpenLiveTextWindow(LlvmCallInstruction call, LlvmValue window, bool file)
        {
            int plan = Location(window), cursor = Location(call), remaining = Reserve(8, 8), one = Reserve(8, 8);
            Copy(HpirWidth.U64, remaining, Indirect(plan, sizeof(ulong), call), call.Source);
            Add(new(HpirOp.ConstU64, one, Immediate: 1), call.Source);
            Add(new(HpirOp.ConstU64, cursor), call.Source);
            int empty = BranchWhenZero(call, remaining);
            EmitImportCall(call, geometry.LiveTextImport(HpirTextRangeImports.Open(file)), [~plan], safepoint: false);
            LinkCursorBranch(empty, code.Count, empty + 1);
            (textWindowCursors ??= []).Add(call, new(remaining, one));
            (liveTextCursors ??= []).Add(call);
            ++liveTextOpens;
        }

        private void PullLiveTextWindow(LlvmCallInstruction call, LlvmCallInstruction open, LlvmAllocaInstruction item, TextWindowCursor cursor)
        {
            int available = Location(call);
            Add(new(HpirOp.ConstU8, available), call.Source);
            int empty = BranchWhenZero(call, cursor.Remaining);
            EmitImportCall(call, geometry.LiveTextImport(HpirTextRangeImports.Next()), [Location(open), Location(item)]);
            int hasItem = CursorBranch(available, call.Source);
            Add(new(HpirOp.SubU64, cursor.Remaining, cursor.Remaining, cursor.One), call.Source);
            LinkCursorBranch(hasItem, hasItem + 1, code.Count);
            LinkCursorBranch(empty, code.Count, empty + 1);
            ++liveTextNexts;
        }

        private void CloseLiveTextWindow(LlvmCallInstruction call, LlvmCallInstruction open)
        {
            int id = Location(open);
            int unopened = BranchWhenZero(call, id);
            EmitImportCall(call, geometry.LiveTextImport(HpirTextRangeImports.Close()), [id], safepoint: false);
            LinkCursorBranch(unopened, code.Count, unopened + 1);
            ++liveTextCloses;
        }
    }
}
