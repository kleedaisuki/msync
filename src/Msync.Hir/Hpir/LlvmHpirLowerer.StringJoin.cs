using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        internal bool IsTextSnapshot(LlvmCallInstruction origin, int index,
            LlvmSnapshotCarrier shape) =>
            SnapshotKind(origin, index, shape.Element) == TypeKind.String
            && (shape.Empty || IsGuestPointer(shape.Element));
    }

    private sealed partial class Body
    {
        private int textJoinSnapshots, textJoinTraversals;

        private bool TryJoinTextRange(LlvmCallInstruction call)
        {
            if (call.ArgumentCount != 2 || !geometry.NativeRangeCall(call, "string.join")
                || !geometry.IsGuestPointer(call.Type)) return false;
            var delimiter = call.GetArgument(1);
            if (!geometry.StringValue(delimiter) || FutureRoot(delimiter)) return false;
            var input = call.GetArgument(0);
            if (TryJoinLiveText(call, input)) return true;
            if ((geometry.SelectedSnapshot(call) ?? geometry.VectorShape(input)) is { } shape
                && geometry.IsTextSnapshot(call, 0, shape))
            {
                EmitTextJoin(call, Location(input), shape.Empty, synthetic: false);
                ++textJoinSnapshots;
                return true;
            }
            if (geometry.TransformShape(input) is not { } transform || !geometry.IsStringResult(transform.Callback))
                return false;
            int snapshot = Reserve(8, 8);
            SnapshotTransform(call, OpenTransform(call, input, transform), snapshot);
            EmitTextJoin(call, snapshot, transform.Empty, synthetic: true);
            ++textJoinTraversals;
            return true;
        }

        private void EmitTextJoin(LlvmCallInstruction call, int owner, bool empty, bool synthetic)
        {
            int data = Reserve(8, 8), length = Reserve(8, 8);
            if (empty)
            {
                Add(new(HpirOp.ConstU64, length), call.Source);
                Add(new(HpirOp.ConstU64, data), call.Source);
            }
            else
            {
                Copy(HpirWidth.U64, length, ~owner, call.Source);
                Copy(HpirWidth.U64, data, Indirect(owner, HpirTransformImports.VectorData, call), call.Source);
            }
            if (synthetic) transformBufferRoots.Add(code.Count, (owner, -1));
            RangeSourcePoint(call.Source);
            EmitImportCall(call, geometry.StringImport(HpirStringOp.JoinSnapshot).Index,
                [data, length, Location(call.GetArgument(1))]);
        }

        private void ReportTextJoins(CompilationTrace? trace)
        {
            trace?.Metric("strings.join.snapshots", textJoinSnapshots);
            trace?.Metric("strings.chunk_appends", textChunkAppends);
            trace?.Metric("strings.join.compiled_traversals", textJoinTraversals);
        }
    }
}
