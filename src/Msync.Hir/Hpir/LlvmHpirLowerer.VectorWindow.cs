using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private int vectorWindowAllocator = -1;

        internal HpirRangeBound VectorWindow(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || !call.GetArgument(1).Type.IsDoubleTy()) return HpirRangeBound.None;
            if (NativeRangeCall(call, "ranges.take")) return HpirRangeBound.Take;
            return NativeRangeCall(call, "ranges.drop") ? HpirRangeBound.Drop : HpirRangeBound.None;
        }

        internal int VectorWindowAllocator()
        {
            if (vectorWindowAllocator >= 0) return vectorWindowAllocator;
            int layout = AddLayout(new(16, 8, []) { BorrowOffsets = [HpirTransformImports.VectorData] });
            return vectorWindowAllocator = PhysicalAllocator(layout);
        }

        internal static bool ZeroVectorLimit(LlvmCallInstruction call) =>
            call.GetArgument(1) is LlvmConstantFP constant
            && (constant.Value.BitcastToAPInt().GetZExtValue() & long.MaxValue) == 0;

        internal LlvmSnapshotCarrier? ReversedSnapshot(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 1 || !NativeRangeCall(call, "ranges.reverse")
                || (SelectedSnapshot(call) ?? VectorShape(call.GetArgument(0))) is not { } input)
                return null;
            return input.Empty || input.Element.IsDoubleTy() || IsGuestPointer(input.Element) ? input : null;
        }
    }

    private sealed partial class Body
    {
        private int vectorWindows, reusedVectorSnapshots, reversedVectorSnapshots;

        private bool TryVectorWindow(LlvmCallInstruction call)
        {
            if (geometry.ReversedSnapshot(call) is { } reversed)
            {
                ReverseSnapshot(call, reversed);
                ++reversedVectorSnapshots;
                return true;
            }
            if (call.ArgumentCount == 1 && geometry.NativeRangeCall(call, "ranges.materialize")
                && AccessSnapshot(call) is not null)
            {
                Alias(call, Location(call.GetArgument(0)));
                ++reusedVectorSnapshots;
                return true;
            }
            HpirRangeBound operation = geometry.VectorWindow(call);
            if (operation == HpirRangeBound.None || AccessSnapshot(call) is not { } input) return false;
            int bound = EmitRangeBound(call, call.GetArgument(1), operation);
            int output = Location(call);
            EmitCall(call, ~geometry.VectorWindowAllocator(), output, []);
            allocatedTransforms.Add(call);
            ++vectorWindows;
            if (input.Empty || operation == HpirRangeBound.Take && Geometry.ZeroVectorLimit(call)) return true;
            SliceVector(call, Location(call.GetArgument(0)), output, bound, geometry.VectorStride(input), operation);
            return true;
        }

        private void ReverseSnapshot(LlvmCallInstruction call, LlvmSnapshotCarrier shape)
        {
            int source = Location(call.GetArgument(0)), output = Location(call);
            if (shape.Empty)
            {
                Alias(call, source);
                return;
            }
            var span = call.Source;
            int count = Reserve(8, 8), data = Reserve(8, 8);
            Copy(HpirWidth.U64, count, ~source, span);
            InitializeWordSnapshot(call, output);
            allocatedTransforms.Add(call);
            int empty = BranchWhenZero(call, count);
            Copy(HpirWidth.U64, ~output, count, span);
            int zero = FormatImmediate(call, 0);
            transformBufferRoots.Add(code.Count, (output, -1));
            EmitCall(call, ~geometry.TransformImport(HpirService.RangeBufferGrow, reference: geometry.IsGuestPointer(shape.Element)),
                data, [zero, zero, count]);
            Copy(HpirWidth.U64, Indirect(output, HpirTransformImports.VectorData, call), data, span);
            int end = Reserve(8, 8), cursor = Reserve(8, 8), item = Reserve(8, 8);
            int stride = FormatImmediate(call, sizeof(ulong)), one = FormatImmediate(call, 1);
            Copy(HpirWidth.U64, cursor, Indirect(source, HpirTransformImports.VectorData, call), span);
            Add(new(HpirOp.MulU64, end, count, stride), span);
            Add(new(HpirOp.AddPointer, cursor, cursor, end), span);
            int loop = code.Count;
            Add(new(HpirOp.OffsetPointer, cursor, cursor, Immediate: -sizeof(ulong)), span);
            Copy(HpirWidth.U64, item, ~cursor, span);
            Copy(HpirWidth.U64, ~data, item, span);
            Add(new(HpirOp.OffsetPointer, data, data, Immediate: sizeof(ulong)), span);
            Add(new(HpirOp.SubU64, count, count, one), span);
            int exhausted = BranchWhenZero(call, count);
            Add(new(HpirOp.Jump, A: loop), span);
            LinkCursorBranch(exhausted, code.Count, exhausted + 1);
            LinkCursorBranch(empty, code.Count, empty + 1);
        }

        private void SliceVector(LlvmInstruction site, int source, int output, int bound, int stride,
            HpirRangeBound operation)
        {
            int actual = Reserve(8, 8);
            Copy(HpirWidth.U64, actual, ~source, site.Source);
            ClampRangeLength(site, bound, actual);

            int length = bound;
            if (operation == HpirRangeBound.Drop)
            {
                length = actual;
                Add(new(HpirOp.SubU64, length, length, bound), site.Source);
            }
            Copy(HpirWidth.U64, ~output, length, site.Source);
            int empty = BranchWhenZero(site, length);
            int data = Reserve(8, 8);
            Copy(HpirWidth.U64, data, Indirect(source, HpirTransformImports.VectorData, site), site.Source);
            if (operation == HpirRangeBound.Drop && stride != 0)
            {
                int delta = Reserve(8, 8);
                Add(new(HpirOp.ConstU64, delta, Immediate: stride), site.Source);
                Add(new(HpirOp.MulU64, delta, bound, delta), site.Source);
                Add(new(HpirOp.AddPointer, data, data, delta), site.Source);
            }
            Copy(HpirWidth.U64, Indirect(output, HpirTransformImports.VectorData, site), data, site.Source);
            LinkCursorBranch(empty, code.Count, empty + 1);
        }

        private void ReportVectorWindows(CompilationTrace? trace)
        {
            trace?.Metric("vectors.physical_windows", vectorWindows);
            trace?.Metric("vectors.materialize_reuse", reusedVectorSnapshots);
            if (reversedVectorSnapshots != 0) trace?.Metric("vectors.reverse_snapshots", reversedVectorSnapshots);
        }
    }
}
