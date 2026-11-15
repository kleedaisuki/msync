using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private sealed record AggregateLeaf(LlvmType Type, int NativeType, uint[] Path, int FunctionComponent = -1);
        private sealed record AggregateShape(LlvmStructType Type, AggregateLeaf[] Leaves, bool Function);
        private sealed record AggregateSlots(AggregateShape Shape, int[] Slots, int BoundarySlot = -1);
        private readonly Dictionary<LlvmType, AggregateShape> aggregateShapes = [];
        private readonly Dictionary<LlvmValue, AggregateSlots> aggregateSlots = [];
        private readonly Dictionary<(NativeConstant Value, int Type), int> constantValueSlots = [];
        private int aggregateLeafCopies, aggregateLeafSelects, functionCompositions, functionProjections;
        private int constantValueSlotHits;

        private static bool IsFunctionAggregate(LlvmType type) => type is LlvmStructType { HasName: true } structure && structure.Name == "msync.function";
        private static bool IsLeafAggregate(LlvmType type) => type is LlvmStructType { IsOpaque: false, Elements.Count: > 0 } structure
            && (!structure.HasName || structure.Name != "msync.path" && !structure.Name.StartsWith("msync.union.", StringComparison.Ordinal));

        private AggregateShape AggregateShapeFor(LlvmType type)
        {
            if (aggregateShapes.TryGetValue(type, out var existing)) return existing;
            var structure = (LlvmStructType)type;
            List<AggregateLeaf> leaves = [];
            AddLeaves(structure, []);
            var shape = new AggregateShape(structure, leaves.ToArray(), IsFunctionAggregate(type));
            aggregateShapes.Add(type, shape);
            return shape;

            void AddLeaves(LlvmStructType current, uint[] prefix)
            {
                if (IsFunctionAggregate(current))
                {
                    int[] fields = [owner.Type(LlvmType.GetInt64Ty(current.Context)),
                        owner.Primitive(TypeKind.Scope | TypeKind.Null), owner.Primitive(TypeKind.String | TypeKind.Null)];
                    for (uint index = 0; index < 3; ++index)
                        leaves.Add(new(current.Elements[(int)index], fields[index], [.. prefix, index], (int)index));
                    return;
                }
                for (uint index = 0; index < current.Elements.Count; ++index)
                {
                    LlvmType member = current.Elements[(int)index];
                    uint[] path = [.. prefix, index];
                    if (member is LlvmStructType nested) AddLeaves(nested, path);
                    else leaves.Add(new(member, owner.Type(member), path));
                }
            }
        }

        private AggregateSlots AggregateLeaves(LlvmValue value)
        {
            if (aggregateSlots.TryGetValue(value, out var result)) return result;
            if (value is not LlvmConstant constant) throw Unsupported(value, "aggregate SSA definition was not emitted");
            var shape = AggregateShapeFor(value.Type);
            int[] leafSlots = new int[shape.Leaves.Length];
            for (int index = 0; index < leafSlots.Length; ++index)
            {
                LlvmConstant leaf = constant;
                foreach (uint field in shape.Leaves[index].Path) leaf = leaf.GetAggregateElement(field)!;
                leafSlots[index] = AggregateComponentSlot(shape, index, leaf);
            }
            result = new(shape, leafSlots);
            aggregateSlots.Add(value, result);
            return result;
        }

        private int AggregateComponentSlot(AggregateShape shape, int index, LlvmValue value)
        {
            int type = shape.Leaves[index].NativeType;
            int component = shape.Leaves[index].FunctionComponent;
            if (component < 0) return Slot(value, type);
            if (component == 0 && value is LlvmFunction target)
                return ConstantSlotValue(NativeConstant.FromMachineInteger(new LlvmAPInt(64, (ulong)owner.Functions[target] + 1),
                    owner.Types[type].Machine), type);
            if (component == 0 && value is LlvmConstant)
                return ConstantSlotValue(NativeConstant.FromMachineInteger(LlvmAPInt.GetZero(64), owner.Types[type].Machine), type);
            if (component == 2 && value is LlvmGlobalVariable global && owner.Input.Constants.TryGetValue(global, out var name))
                return ConstantSlotValue(name, type);
            if (component == 2 && value is LlvmLoadInstruction { PointerOperand: LlvmGlobalVariable literal } &&
                owner.Input.Constants.TryGetValue(literal, out var loadedName))
                return ConstantSlotValue(loadedName, type);
            if (value is LlvmConstantPointerNull or LlvmUndefValue)
                return ConstantSlotValue(new(NativeKind.Null), type);
            return Slot(value, type);
        }

        private int ConstantSlotValue(NativeConstant value, int type)
        {
            if (constantValueSlots.TryGetValue((value, type), out int existing))
            {
                ++constantValueSlotHits;
                return existing;
            }
            int slot = Register(type);
            constantValueSlots.Add((value, type), slot);
            prelude.Add(new(NativeOp.Constant, slot, owner.Constant(value), Source: instructionSource));
            ConstantFunctionOrigin(slot, value);
            return slot;
        }

        private bool TryAggregateSlot(LlvmValue value, int expectedType, out int slot)
        {
            slot = -1;
            if (!aggregateSlots.TryGetValue(value, out var leaves)) return false;
            if (!leaves.Shape.Function) throw Unsupported(value, "generic aggregate at a scalar ABI boundary");
            if (value is LlvmConstant constant)
            {
                slot = ConstantSlot(constant, owner.Primitive(TypeKind.Function));
                return true;
            }
            if (values.TryGetValue(value, out slot)) return true;
            if (leaves.BoundarySlot >= 0) { slot = leaves.BoundarySlot; return true; }
            int type = expectedType >= 0 ? expectedType : value.LogicalType >= 0 ? value.LogicalType : owner.Primitive(TypeKind.Function);
            slot = Register(type);
            RecordFunctionContext(leaves, inPrelude: false);
            Add(new(NativeOp.ComposeFunction, slot, Inputs: leaves.Slots, Source: instructionSource));
            ++functionCompositions;
            return true;
        }

        private void DefineAggregateBoundary(LlvmValue value, bool toPrelude = false)
        {
            if (!IsFunctionAggregate(value.Type) || aggregateSlots.ContainsKey(value)) return;
            bool[] needed = FunctionFieldsUsed(value);
            if (!needed.Any(static field => field) && !value.Uses.Any(static use => use.User is LlvmExtractValueInstruction)) return;
            var shape = AggregateShapeFor(value.Type);
            int whole = values[value];
            int[] fields = [-1, -1, -1];
            for (int index = 0; index < fields.Length; ++index)
            {
                if (!needed[index]) continue;
                fields[index] = Register(shape.Leaves[index].NativeType);
                var instruction = new NativeInstruction(NativeOp.FunctionComponent, fields[index], whole,
                    Immediate: index, Source: instructionSource);
                if (toPrelude) prelude.Add(instruction); else Add(instruction);
                RecordFunctionProjection(shape.Leaves[index].NativeType, index);
            }
            aggregateSlots.Add(value, new(shape, fields, whole));
        }

        private static bool[] FunctionFieldsUsed(LlvmValue value)
        {
            bool[] fields = new bool[3];
            foreach (var use in value.Uses)
            {
                if (use.User is LlvmInsertValueInstruction or LlvmSelectInstruction or LlvmPhiInstruction)
                    return [true, true, true];
            }
            return fields;
        }

        private void PrepareAggregateArguments()
        {
            foreach (var argument in function.Arguments.Skip(captureCount)) DefineAggregateBoundary(argument, toPrelude: true);
        }
    }
}
