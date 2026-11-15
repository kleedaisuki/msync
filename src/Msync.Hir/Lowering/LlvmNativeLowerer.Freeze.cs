using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private void Freeze(LlvmFreezeInstruction instruction)
        {
            LlvmValue operand = instruction.Operand;
            if (IsLeafAggregate(instruction.Type)
                && (operand is LlvmConstant || aggregateSlots.TryGetValue(operand, out var split) && split.BoundarySlot < 0))
            {
                FreezeAggregate(instruction);
                return;
            }
            int type = instruction.LogicalType >= 0 ? instruction.LogicalType : instruction.GetOperandUse(0).LogicalType;
            if (type < 0 && values.TryGetValue(operand, out int selected)) type = slots[selected].Type;
            if (type < 0 && operand.LogicalType >= 0) type = operand.LogicalType;
            if (type < 0) type = IsFunctionAggregate(instruction.Type)
                ? owner.Primitive(TypeKind.Function) : SelectedValueType(operand);
            SetSelectedValueType(instruction, type);
            FreezeCopy(values[instruction], OperandSlot(instruction, 0, type), instruction.Source);
            trace?.Metric("freeze.scalar_copies", 1);
            if (operand is LlvmUndefValue) trace?.Metric("freeze.undefined_constant_choices", 1);
        }

        private void FreezeAggregate(LlvmFreezeInstruction instruction)
        {
            AggregateSlots source = AggregateLeaves(instruction.Operand);
            int[] fields = new int[source.Slots.Length];
            for (int index = 0; index < fields.Length; ++index)
            {
                fields[index] = Register(slots[source.Slots[index]].Type);
                FreezeCopy(fields[index], source.Slots[index], instruction.Source);
                if (source.Shape.Leaves[index].FunctionComponent == 2)
                    CopyFunctionOrigin(fields[index], source.Slots[index]);
                ++aggregateLeafCopies;
            }
            AggregateSlots frozen = new(source.Shape, fields);
            aggregateSlots.Add(instruction, frozen);
            if (source.Shape.Function && values.TryGetValue(instruction, out int boundary))
            {
                RecordFunctionContext(frozen, inPrelude: false);
                Add(new(NativeOp.ComposeFunction, boundary, Inputs: fields, Source: instruction.Source));
                ++functionCompositions;
            }
            trace?.Metric("freeze.aggregate_instructions", 1);
            trace?.Metric("freeze.aggregate_leaf_copies", fields.Length);
            if (instruction.Operand is LlvmUndefValue) trace?.Metric("freeze.undefined_constant_choices", 1);
        }

        private void FreezeCopy(int destination, int source, Msync.Core.SourceSpan? origin)
        {
            int type = slots[destination].Type;
            if (!owner.Types[type].PhysicalType.Equals(owner.Types[slots[source].Type].PhysicalType)) type = -1;
            // Native constants already choose concrete undef/poison bits. Copy
            // snapshots that choice and preserves a pending datum's identity.
            Add(new(NativeOp.Copy, destination, source, Type: type, Source: origin));
        }
    }
}
