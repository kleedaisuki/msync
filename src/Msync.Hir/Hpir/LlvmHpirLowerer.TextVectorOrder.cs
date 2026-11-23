using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal HpirStringOp? TextVectorOrder(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 1 || !IsGuestPointer(call.Type)) return null;
            HpirStringOp? operation = NativeRangeCall(call, "shell.unique_lines") ? HpirStringOp.UniqueSnapshot
                : NativeRangeCall(call, "ranges.sort") ? HpirStringOp.SortSnapshot : null;
            if (operation is null || VectorShape(call.GetArgument(0)) is not { } shape || !IsTextSnapshot(call, 0, shape))
                return null;
            return operation;
        }
    }

    private sealed partial class Body
    {
        private int textVectorOrders;

        private bool TryOrderTextVector(LlvmCallInstruction call)
        {
            if (geometry.TextVectorOrder(call) is not { } operation) return false;
            int input = Location(call.GetArgument(0)), output = Location(call);
            int count = Reserve(8, 8);
            Copy(HpirWidth.U64, count, ~input, call.Source);
            EmitCall(call, ~geometry.TransformImport(HpirService.AllocateObject), output, []);
            int empty = BranchWhenZero(call, count);
            int data = Reserve(8, 8), zero = FormatImmediate(call, 0);
            transformBufferRoots.Add(code.Count, (output, -1));
            EmitCall(call, ~geometry.TransformImport(HpirService.RangeBufferGrow, reference: true), data, [zero, zero, count]);
            Copy(HpirWidth.U64, Indirect(output, HpirTransformImports.VectorData, call), data, call.Source);
            int source = Indirect(input, HpirTransformImports.VectorData, call);
            EmitCall(call, ~geometry.StringImport(operation).Index, ~output, [source, data, count], safepoint: false);
            LinkCursorBranch(empty, code.Count, empty + 1);
            ++textVectorOrders;
            return true;
        }
    }
}
