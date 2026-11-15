using Msync.Core;
using Msync.Core.Llvm;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private readonly Dictionary<LlvmValue, int> selectedCarriers = [];

        private int NativeCarrierType(LlvmValue value)
        {
            if (value.LogicalType >= 0) return value.LogicalType;
            if (values.TryGetValue(value, out int slot)) return slots[slot].Type;
            if (selectedCarriers.TryGetValue(value, out int cached))
            {
                trace?.Metric("carriers.cache_hits", 1);
                return cached;
            }
            trace?.Metric("carriers.queries", 1);
            int type = FindCarrier(value, []);
            if (type < 0) throw Unsupported(value, "native carrier for " + value.GetType().Name + " '" + value.Name + "'");
            selectedCarriers.Add(value, type);
            trace?.Metric("carriers.selected." + owner.Types[type].PhysicalType, 1);
            return type;
        }

        // SROA creates opaque pointer SSA values. Their physical carrier follows
        // existing operand/storage ABI facts; this query never changes source IR.
        private int FindCarrier(LlvmValue value, HashSet<LlvmValue> active)
        {
            if (value.LogicalType >= 0) return value.LogicalType;
            if (values.TryGetValue(value, out int slot)) return slots[slot].Type;
            if (selectedCarriers.TryGetValue(value, out int cached)) return cached;
            if (!value.Type.IsPointerTy()) return owner.Type(value);
            if (!active.Add(value)) return -1;
            int type = PointerCarrier(value, active);
            active.Remove(value);
            return type;
        }

        private int PointerCarrier(LlvmValue value, HashSet<LlvmValue> active)
        {
            switch (value)
            {
                case LlvmAllocaInstruction allocation: return owner.Pointer(AllocaStorageType(allocation));
                case LlvmLoadInstruction load: return LoadedCarrier(load, active);
                case LlvmExtractValueInstruction extract when IsLeafAggregate(extract.AggregateOperand.Type):
                    var shape = AggregateShapeFor(extract.AggregateOperand.Type);
                    return shape.Leaves[FindAggregateLeaf(shape, extract.Indices)].NativeType;
                case LlvmCastInstruction cast when cast.Operand.Type.IsPointerTy():
                    return FindCarrier(cast.Operand, active);
                case LlvmPhiInstruction phi:
                    return JoinedCarrier(phi.OperandUses, value, active);
                case LlvmSelectInstruction select:
                    return JoinedCarrier(select.OperandUses.Skip(1), value, active);
                case LlvmFunction:
                    return owner.Type(LlvmType.GetInt64Ty(value.Type.Context));
            }
            if (LlvmGepOperator.TryGet(value, out var address))
            {
                if (FunctionFieldCarrier(address) is int field) return owner.Pointer(field);
                var position = PointerPositionOf(value);
                if (position.Structure is not null) return owner.Pointer(PointerField(value, position).Type);
                return FindCarrier(position.Root, active);
            }
            return ConsumerCarrier(value);
        }

        private int JoinedCarrier(IEnumerable<LlvmUse> inputs, LlvmValue value, HashSet<LlvmValue> active)
        {
            int result = ConsumerCarrier(value);
            if (result >= 0) return result;
            bool nullable = false;
            foreach (var use in inputs)
            {
                if (use.Value is LlvmConstantPointerNull or LlvmUndefValue) { nullable = true; continue; }
                int type = use.LogicalType >= 0 ? use.LogicalType : FindCarrier(use.Value!, active);
                if (type < 0) continue;
                if (result < 0 || result == type) { result = type; continue; }
                var left = owner.Types[result];
                var right = owner.Types[type];
                if (!left.IsMachine && !right.IsMachine && left.Pointee < 0 && right.Pointee < 0)
                    result = owner.Primitive(left.Kind | right.Kind);
            }
            if (result < 0) return nullable ? owner.Primitive(TypeKind.Null) : -1;
            var carrier = owner.Types[result];
            return nullable && !carrier.IsMachine && carrier.Pointee < 0
                ? owner.Primitive(carrier.Kind | TypeKind.Null) : result;
        }

        private int ConsumerCarrier(LlvmValue value)
        {
            foreach (var use in value.Uses)
            {
                if (use.LogicalType >= 0) return use.LogicalType;
                if (use.User is LlvmInsertValueInstruction insert && use.OperandNo == 1 && IsLeafAggregate(insert.Type))
                {
                    var shape = AggregateShapeFor(insert.Type);
                    return shape.Leaves[FindAggregateLeaf(shape, insert.Indices)].NativeType;
                }
            }
            return -1;
        }

        private int LoadedCarrier(LlvmLoadInstruction load, HashSet<LlvmValue> active)
        {
            var use = load.GetOperandUse(0);
            if (use.LogicalType >= 0 && owner.Types[use.LogicalType].Pointee >= 0)
                return owner.Types[use.LogicalType].Pointee;
            if (LlvmGepOperator.TryGet(load.PointerOperand, out var address) && FunctionFieldCarrier(address) is int field)
                return field;
            int pointer = FindCarrier(load.PointerOperand, active);
            if (pointer >= 0 && owner.Types[pointer].Pointee >= 0) return owner.Types[pointer].Pointee;
            return ConsumerCarrier(load);
        }

        private int? FunctionFieldCarrier(LlvmGepOperator address)
        {
            if (!IsFunctionAggregate(address.SourceElementType) || address.IndexCount != 2 ||
                address.GetIndex(0) is not LlvmConstantInt { IsZero: true } ||
                address.GetIndex(1) is not LlvmConstantInt field) return null;
            return AggregateShapeFor(address.SourceElementType).Leaves[checked((int)field.GetZExtValue())].NativeType;
        }
    }
}
