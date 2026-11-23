using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private Dictionary<int, HpirSessionSnapshot>? newSnapshotLayouts;
        private Dictionary<HpirSessionSnapshot, int>? sessionTransforms;
        private IReadOnlyDictionary<int, HpirSessionSnapshot>? InheritedSnapshotLayouts =>
            graph.SessionGeneration?.HpirLayouts?.SnapshotLayouts;

        internal void RememberSessionVector(LlvmCallInstruction call, LlvmSnapshotCarrier shape, int import)
        {
            if (shape.Completion || !IsPersistentSession) return;
            int result = call.LogicalType;
            int item = result >= 0 ? graph.Types[result].Element : -1;
            if (item < 0 && !shape.Empty) return;
            TypeKind kind = item >= 0 ? graph.Types[item].Kind : TypeKind.Never;
            if (kind == TypeKind.Never && !shape.Empty) return;
            int layout = Imports[import].Binding;
            RememberLayout(layout, new(kind, shape.Empty));
        }

        internal int RetainedVectorTransform(TransformRecipe shape)
        {
            if (!IsPersistentSession || shape.Vector is { Completion: true } || !shape.Callback.ReturnType.IsDoubleTy()
                || !shape.Callback.Arguments[Captures(shape.Callback)].Type.IsDoubleTy()
                || graph.SourceFunctions.GetValueOrDefault(shape.Callback)?.SourceFunction is not { } function)
                return VectorTransformAllocator();
            var retained = new HpirSessionSnapshot(TypeKind.Number, shape.Empty, function.Value);
            var imports = sessionTransforms ??= [];
            if (imports.TryGetValue(retained, out int id)) return id;
            int layout = AddLayout(new(VectorTransformPlan.Bytes, 8, [VectorTransformPlan.Owner])
            { BorrowOffsets = [VectorTransformPlan.Environment] });
            RememberLayout(layout, retained);
            int import = PhysicalAllocator(layout);
            imports.Add(retained, import);
            return import;
        }

        private void RememberLayout(int layout, HpirSessionSnapshot shape) =>
            (newSnapshotLayouts ??= InheritedSnapshotLayouts is { } prior ? new(prior) : []).TryAdd(layout, shape);

        private IReadOnlyDictionary<int, HpirSessionSnapshot> SessionSnapshots() =>
            newSnapshotLayouts ?? InheritedSnapshotLayouts ?? new Dictionary<int, HpirSessionSnapshot>();

        internal LlvmSessionRangeRead? SessionSnapshotRead(LlvmCallInstruction call) =>
            call.CalledFunction is { } function && graph.SessionSnapshotReads?.TryGetValue(function, out var shape) == true
                ? shape : null;
    }

    private sealed partial class Body
    {
        private bool TrySessionSnapshotRead(LlvmCallInstruction call)
        {
            if (geometry.SessionSnapshotRead(call) is null) return false;
            Copy(HpirWidth.U64, Location(call), Location(call.GetArgument(0)), call.Source);
            return true;
        }
    }
}
