using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private int functionComponentLoads;

        private int AllocaStorageType(LlvmAllocaInstruction allocation)
        {
            if (allocation.AllocatedLogicalType >= 0) return allocation.AllocatedLogicalType;
            if (allocation.StorageView is not { } view) return owner.Type(allocation.AllocatedType);
            if (view.ByteOffset == 0 && view.StorageType == allocation.AllocatedType && view.LogicalType >= 0)
                return view.LogicalType;
            if (!IsFunctionAggregate(view.StorageType)) return owner.Type(allocation.AllocatedType);
            var shape = AggregateShapeFor(view.StorageType);
            var layout = owner.Input.Module.DataLayout.GetStructLayout(shape.Type);
            for (int index = 0; index < shape.Leaves.Length; ++index)
                if (layout.GetElementOffset((uint)index).GetFixedValue() == view.ByteOffset &&
                    shape.Leaves[index].Type == allocation.AllocatedType) return shape.Leaves[index].NativeType;
            return owner.Type(allocation.AllocatedType);
        }

        private bool FunctionComponentLoad(LlvmLoadInstruction load)
        {
            if (load.Type is not LlvmPointerType) return false;
            if (!FunctionMemoryComponent(load.GetOperandUse(0), load.Type, out var location, out int index)) return false;
            if (TryFunctionMemoryLoadGroup(load, location, index)) return true;
            var structure = LlvmStructType.GetTypeByName(function.Context, "msync.function")!;
            var shape = AggregateShapeFor(structure);
            int type = FunctionProjectionType(load, shape.Leaves[index]);
            int storage = location.Type;
            int whole = Register(storage);
            Add(new(NativeOp.Load, whole, FunctionMemoryAddress(location),
                Type: storage, Source: load.Source));
            SetSelectedValueType(load, type);
            Add(new(NativeOp.FunctionComponent, values[load], whole, Immediate: index, Source: load.Source));
            RecordFunctionProjection(type, index);
            ++functionComponentLoads;
            return true;
        }
    }
}
