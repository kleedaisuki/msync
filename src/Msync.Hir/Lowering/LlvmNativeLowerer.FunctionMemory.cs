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
            var root = load.PointerOperand;
            ulong offset = 0;
            if (LlvmGepOperator.TryGet(root, out _))
            {
                var position = PointerPositionOf(root);
                root = position.Root;
                offset = position.Offset.GetZExtValue();
            }
            int pointer = root.LogicalType >= 0 ? root.LogicalType
                : values.TryGetValue(root, out int rootSlot) ? slots[rootSlot].Type : -1;
            if (pointer < 0 || owner.Types[pointer].Pointee is not (>= 0 and var pointee) ||
                owner.Types[pointee].Kind != TypeKind.Function) return false;
            var structure = LlvmStructType.GetTypeByName(function.Context, "msync.function")!;
            var layout = owner.Input.Module.DataLayout.GetStructLayout(structure);
            int index = -1;
            for (uint member = 0; member < 3; ++member)
                if (layout.GetElementOffset(member).GetFixedValue() == offset && structure.Elements[(int)member] == load.Type)
                { index = (int)member; break; }
            if (index < 0) return false;
            var shape = AggregateShapeFor(structure);
            int type = shape.Leaves[index].NativeType;
            int storage = owner.Primitive(TypeKind.Function);
            int whole = Register(storage);
            Add(new(NativeOp.Load, whole, SlotForView(root, owner.Pointer(storage)),
                Type: storage, Source: load.Source));
            SetSelectedValueType(load, type);
            Add(new(NativeOp.FunctionComponent, values[load], whole, Immediate: index, Source: load.Source));
            ++functionComponentLoads;
            return true;
        }
    }
}
