using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private bool TryAggregateInsert(LlvmInsertValueInstruction instruction)
        {
            if (TryFunctionMemoryReassembly(instruction)) return true;
            if (!IsLeafAggregate(instruction.Type)) return false;
            var original = AggregateLeaves(instruction.AggregateOperand);
            int[] leaves = original.Slots.ToArray();
            int first = FindAggregateLeaf(original.Shape, instruction.Indices);
            if (IsLeafAggregate(instruction.InsertedValueOperand.Type))
            {
                var inserted = AggregateLeaves(instruction.InsertedValueOperand);
                Array.Copy(inserted.Slots, 0, leaves, first, inserted.Slots.Length);
            }
            else leaves[first] = AggregateComponentSlot(original.Shape, first, instruction.InsertedValueOperand);
            aggregateSlots.Add(instruction, new(original.Shape, leaves));
            return true;
        }

        private bool TryAggregateExtract(LlvmExtractValueInstruction instruction)
        {
            if (!IsLeafAggregate(instruction.AggregateOperand.Type)) return false;
            var source = AggregateLeaves(instruction.AggregateOperand);
            int first = FindAggregateLeaf(source.Shape, instruction.Indices);
            if (IsLeafAggregate(instruction.Type))
            {
                var shape = AggregateShapeFor(instruction.Type);
                aggregateSlots.Add(instruction, new(shape, source.Slots.AsSpan(first, shape.Leaves.Length).ToArray()));
            }
            else if (source.Shape.Function && source.BoundarySlot >= 0)
            {
                var leaf = source.Shape.Leaves[first];
                int type = FunctionProjectionType(instruction, leaf);
                int result = Slot(instruction, type);
                Add(new(NativeOp.FunctionComponent, result, source.BoundarySlot,
                    Immediate: leaf.FunctionComponent, Source: instruction.Source));
                RecordFunctionProjection(type, leaf.FunctionComponent);
            }
            else
            {
                int selected = FunctionProjectionType(instruction, source.Shape.Leaves[first]);
                int leaf = source.Slots[first];
                if (owner.Types[selected].PhysicalType.Equals(owner.Types[slots[leaf].Type].PhysicalType))
                    values.Add(instruction, leaf);
                else
                {
                    int result = Slot(instruction, selected);
                    Add(new(NativeOp.Copy, result, leaf, Source: instruction.Source));
                    ++functionProjectionCarrierCopies;
                }
            }
            return true;
        }

        private static int FindAggregateLeaf(AggregateShape shape, IReadOnlyList<uint> indices)
        {
            for (int leaf = 0; leaf < shape.Leaves.Length; ++leaf)
                if (StartsWith(shape.Leaves[leaf].Path, indices)) return leaf;
            throw new InvalidOperationException("An aggregate index does not select an SSA field");
        }

        private static bool StartsWith(uint[] path, IReadOnlyList<uint> prefix)
        {
            if (path.Length < prefix.Count) return false;
            for (int index = 0; index < prefix.Count; ++index)
                if (path[index] != prefix[index]) return false;
            return true;
        }

        private bool TryAggregateSelect(LlvmSelectInstruction instruction)
        {
            if (!IsLeafAggregate(instruction.Type)) return false;
            var left = AggregateLeaves(instruction.TrueValue);
            var right = AggregateLeaves(instruction.FalseValue);
            int[] result = new int[left.Slots.Length];
            int condition = OperandSlot(instruction, 0, owner.Primitive(TypeKind.Bool));
            for (int index = 0; index < result.Length; ++index)
            {
                result[index] = Register(left.Shape.Leaves[index].NativeType);
                Add(new(NativeOp.Select, result[index], condition, left.Slots[index], right.Slots[index], Source: instruction.Source));
                if (left.Shape.Leaves[index].FunctionComponent == 2)
                {
                    CopyFunctionOrigin(result[index], left.Slots[index]);
                    CopyFunctionOrigin(result[index], right.Slots[index]);
                }
                ++aggregateLeafSelects;
            }
            aggregateSlots.Add(instruction, new(left.Shape, result));
            return true;
        }

        private bool TryAggregateIntrinsic(LlvmCallInstruction instruction)
        {
            if (!IsLeafAggregate(instruction.Type) || instruction.CalledFunction?.IntrinsicDescriptor is not { } descriptor) return false;
            (NativeMachineIntrinsic Value, NativeMachineIntrinsic Flag)? operation = descriptor.Kind.Name switch
            {
                "llvm.uadd.with.overflow" => (NativeMachineIntrinsic.UAddOverflowValue, NativeMachineIntrinsic.UAddOverflowFlag),
                "llvm.sadd.with.overflow" => (NativeMachineIntrinsic.SAddOverflowValue, NativeMachineIntrinsic.SAddOverflowFlag),
                "llvm.usub.with.overflow" => (NativeMachineIntrinsic.USubOverflowValue, NativeMachineIntrinsic.USubOverflowFlag),
                "llvm.ssub.with.overflow" => (NativeMachineIntrinsic.SSubOverflowValue, NativeMachineIntrinsic.SSubOverflowFlag),
                "llvm.umul.with.overflow" => (NativeMachineIntrinsic.UMulOverflowValue, NativeMachineIntrinsic.UMulOverflowFlag),
                "llvm.smul.with.overflow" => (NativeMachineIntrinsic.SMulOverflowValue, NativeMachineIntrinsic.SMulOverflowFlag),
                _ => null
            };
            if (operation is not { } operations) return false;
            var shape = AggregateShapeFor(instruction.Type);
            int[] fields = [Register(shape.Leaves[0].NativeType), Register(shape.Leaves[1].NativeType)];
            int[] operands = ArgumentSlots(instruction);
            Add(new(NativeOp.MachineIntrinsic, fields[0], Immediate: (int)operations.Value, Inputs: operands, Source: instruction.Source));
            Add(new(NativeOp.MachineIntrinsic, fields[1], Immediate: (int)operations.Flag, Inputs: operands, Source: instruction.Source));
            aggregateSlots.Add(instruction, new(shape, fields));
            return true;
        }
    }
}
