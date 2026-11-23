using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<LlvmFunction, int> guestNumberImports = [];

        internal LlvmRangeCallbackWork ReductionWork(LlvmFunction callback) =>
            graph.RangeCallbackWork.GetValueOrDefault(callback);

        internal bool CooperativeNumberFront(LlvmCallInstruction call) => call.ArgumentCount == 1
            && call.Type.IsDoubleTy() && NativeRangeCall(call, "ranges.front")
            && TransformShape(call.GetArgument(0)) is { Indexed: null, Vector: null, Zip: null, Sorted: null, Empty: false } shape
            && shape.Callback.ReturnType.IsDoubleTy()
            && ReductionWork(shape.Callback) == LlvmRangeCallbackWork.Cooperative;

        internal int GuestNumberImport(LlvmFunction callback)
        {
            if (guestNumberImports.TryGetValue(callback, out int found)) return found;
            int index = Imports.Count;
            Imports.Add(HpirGuestImports.Number(entries[callback], callback.Arguments.Count));
            guestNumberImports.Add(callback, index);
            return index;
        }

        internal int GuestNumberLayout() => FutureLayout(-(int)HpirService.GuestNumber, HpirFutureImports.Completion(HpirWidth.U64));
    }

    private sealed partial class Body
    {
        private int guestNumberSites;

        private void FrontGuestNumber(LlvmCallInstruction call, TransformCursor cursor)
        {
            int available = Reserve(1, 1);
            int number = PullTransformInput(call, cursor, available);
            int layout = Reserve(8, 8);
            Add(new(HpirOp.ConstU64, layout, Immediate: geometry.GuestNumberLayout()), call.Source);
            RangeSourcePoint(call.Source);
            EmitCall(call, ~geometry.GuestNumberImport(cursor.Callback), Location(call),
                [.. cursor.Captures, number, available, layout]);
            ++guestNumberSites;
        }
    }
}
