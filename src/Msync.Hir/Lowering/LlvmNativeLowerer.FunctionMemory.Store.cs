using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private readonly record struct FunctionMemoryLocation(LlvmValue Root, int Layout, int Field, int Type);
        private sealed record FunctionMemoryAssignment(FunctionMemoryLocation Location, LlvmStoreInstruction[] Stores);
        private readonly Dictionary<LlvmStoreInstruction, FunctionMemoryAssignment> functionMemoryAssignments = [];
        private int functionMemoryAssignmentsPacked, functionMemoryComponentStores;

        private bool FunctionMemoryComponent(LlvmValue pointer, LlvmType componentType,
            out FunctionMemoryLocation location, out int component)
        {
            var position = LlvmGepOperator.TryGet(pointer, out _) ? PointerPositionOf(pointer)
                : PointerRoot(pointer, owner.Input.Module.DataLayout.GetIndexTypeSizeInBits(pointer.Type));
            location = default;
            component = -1;
            ulong offset = position.Offset.GetZExtValue();
            int logical = position.Root.LogicalType >= 0 ? position.Root.LogicalType
                : values.TryGetValue(position.Root, out int slot) ? slots[slot].Type : -1;
            if (logical >= 0 && owner.Types[logical].Pointee is >= 0 and var pointee && owner.Types[pointee].Kind == TypeKind.Function)
            {
                location = new(position.Root, -1, -1, pointee);
                return FunctionComponentAt(offset, componentType, out component);
            }
            if (position.Structure is null || position.Layout < 0) return false;
            var structureLayout = owner.Input.Module.DataLayout.GetStructLayout(position.Structure);
            for (int field = 0; field < position.Structure.Elements.Count; ++field)
            {
                if (!IsFunctionAggregate(position.Structure.Elements[field])) continue;
                ulong begin = structureLayout.GetElementOffset((uint)field).GetFixedValue();
                if (offset < begin || !FunctionComponentAt(offset - begin, componentType, out component)) continue;
                int type = owner.Layouts[position.Layout].Fields[field].Type;
                location = new(position.Root, position.Layout, field, type);
                return true;
            }
            return false;
        }

        private bool FunctionComponentAt(ulong offset, LlvmType type, out int component)
        {
            var structure = LlvmStructType.GetTypeByName(function.Context, "msync.function")!;
            var layout = owner.Input.Module.DataLayout.GetStructLayout(structure);
            for (uint index = 0; index < 3; ++index)
                if (layout.GetElementOffset(index).GetFixedValue() == offset && structure.Elements[(int)index] == type)
                { component = (int)index; return true; }
            component = -1;
            return false;
        }

        private int FunctionMemoryAddress(FunctionMemoryLocation location)
        {
            if (location.Field < 0) return SlotForView(location.Root, owner.Pointer(location.Type));
            int address = Register(owner.Pointer(location.Type));
            Add(new(NativeOp.AddressOffset, address, Slot(location.Root), location.Layout, location.Field, Source: instructionSource));
            ++constantGepMaterializations;
            return address;
        }

        private bool FunctionComponentStore(LlvmStoreInstruction store)
        {
            if (!functionMemoryAssignments.TryGetValue(store, out var assignment))
            {
                if (!store.IsSimple || !FunctionMemoryComponent(store.PointerOperand, store.ValueOperand.Type,
                    out var location, out _)) return false;
                assignment = CollectFunctionMemoryAssignment(store, location);
                if (assignment is null) return false;
                foreach (var member in assignment.Stores) functionMemoryAssignments.Add(member, assignment);
            }
            ++functionMemoryComponentStores;
            if (!ReferenceEquals(store, assignment.Stores[^1])) return true;
            var structure = LlvmStructType.GetTypeByName(function.Context, "msync.function")!;
            var shape = AggregateShapeFor(structure);
            int[] components = new int[3];
            foreach (var member in assignment.Stores)
            {
                _ = FunctionMemoryComponent(member.PointerOperand, member.ValueOperand.Type, out _, out int component);
                components[component] = AggregateComponentSlot(shape, component, member.ValueOperand);
            }
            int whole = Register(assignment.Location.Type);
            RecordFunctionContext(new(shape, components), inPrelude: false);
            Add(new(NativeOp.ComposeFunction, whole, Inputs: components, Source: store.Source));
            Add(new(NativeOp.Store, A: FunctionMemoryAddress(assignment.Location), B: whole, C: 0,
                Type: assignment.Location.Type, Source: store.Source));
            ++functionCompositions;
            ++functionMemoryAssignmentsPacked;
            return true;
        }

        private FunctionMemoryAssignment? CollectFunctionMemoryAssignment(LlvmStoreInstruction first,
            FunctionMemoryLocation location)
        {
            List<LlvmStoreInstruction> stores = [];
            int written = 0;
            for (LlvmInstruction? instruction = first; instruction is not null; instruction = instruction.Next)
            {
                if (instruction is LlvmGetElementPtrInstruction or LlvmCastInstruction or LlvmExtractValueInstruction) continue;
                if (instruction is not LlvmStoreInstruction { IsSimple: true } store ||
                    !FunctionMemoryComponent(store.PointerOperand, store.ValueOperand.Type, out var target, out int component) ||
                    target != location || (written & (1 << component)) != 0) return null;
                stores.Add(store);
                written |= 1 << component;
                if (written == 7) return new(location, stores.ToArray());
            }
            return null;
        }
    }
}
