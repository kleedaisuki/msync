using Msync.Core;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Body
    {
        private readonly Dictionary<(LlvmConstant Value, int Type), int> constantSlots = [];
        private int constantSlotHits, contextualConstantUses, constantFunctionMaterializations;

        private int OperandType(LlvmUse use, int expectedType = -1) =>
            use.LogicalType >= 0 ? use.LogicalType : expectedType >= 0 ? expectedType : SelectedValueType(use.Value!);

        private int SelectedValueType(LlvmValue value) => NativeCarrierType(value);

        private int OperandSlot(LlvmUser user, int index, int expectedType = -1) =>
            Slot(user.GetOperandUse(index), expectedType);

        private int ArgumentSlot(LlvmCallInstruction call, int index)
        {
            int expectedType = call.CalledFunction is { } target && index < target.Arguments.Count
                ? target.Arguments[index].LogicalType : -1;
            return OperandSlot(call, index, expectedType);
        }

        private int[] ArgumentSlots(LlvmCallInstruction call, int skip = 0) =>
            Enumerable.Range(skip, call.ArgumentCount - skip).Select(index => ArgumentSlot(call, index)).ToArray();

        private int Slot(LlvmUse use, int expectedType = -1)
        {
            int pointerType = use.LogicalType >= 0 ? use.LogicalType : expectedType;
            if (use.TargetStorageType is { } storage && pointerType >= 0 && owner.Types[pointerType].Pointee >= 0)
                return DeclaredStorageFieldView(use.Value!, storage, owner.Types[pointerType].Pointee);
            if (use.Value is LlvmConstant and not LlvmGlobalValue and not LlvmConstantExpr { Opcode: LlvmOpcode.GetElementPtr })
            {
                if (use.LogicalType >= 0 || expectedType >= 0) ++contextualConstantUses;
                return Slot(use.Value, OperandType(use, expectedType));
            }
            int type = use.LogicalType >= 0 ? use.LogicalType : expectedType;
            return SlotForView(use.Value!, type);
        }

        private int SlotForView(LlvmValue value, int type)
        {
            int slot = Slot(value, type);
            if (type >= 0 && owner.Types[type].Pointee >= 0 && slots[slot].Type != type
                && DeclaredZeroFieldView(value, slot, type, out int fieldView)) return fieldView;
            return type >= 0 && owner.Types[type].Pointee >= 0 && slots[slot].Type != type
                ? AddressView(slot, type) : slot;
        }

        private int Slot(LlvmValue value) => Slot(value, -1);

        private int Slot(LlvmValue value, int expectedType)
        {
            if (value is LlvmPhiInstruction phi && layoutPointerAliases?.TryGetValue(phi, out var address) == true)
                return Slot(address, expectedType);
            if (value is LlvmGlobalVariable lexical && owner.SessionCells.TryGetValue(lexical, out var cell))
                return SessionCellSlot(lexical, cell);
            if (value is LlvmGlobalVariable global && owner.StaticObjects.TryGetValue(global, out int declaration))
                return StaticObjectSlot(global, declaration);
            if (value is LlvmGlobalVariable { IsConstant: true, LogicalType: >= 0 } literal
                && owner.Input.Constants.TryGetValue(literal, out var contents))
                return LiteralAddress(literal, contents);
            if (value is LlvmFunction target)
            {
                if (values.TryGetValue(target, out int codeSlot)) return codeSlot;
                int type = expectedType >= 0 ? expectedType : owner.Type(LlvmType.GetInt64Ty(value.Context));
                codeSlot = ConstantSlotValue(NativeConstant.FromMachineInteger(
                    new LlvmAPInt(64, (ulong)owner.Functions[target] + 1), owner.Types[type].Machine), type);
                values.Add(target, codeSlot);
                return codeSlot;
            }
            if (TryAggregateSlot(value, expectedType, out int aggregateSlot)) return aggregateSlot;
            if (value is LlvmConstantExpr { Opcode: LlvmOpcode.GetElementPtr })
                return MaterializePointerRelocation(value, expectedType >= 0 ? expectedType : value.LogicalType);
            if (value is LlvmConstant constant and not LlvmGlobalValue)
                return ConstantSlot(constant, expectedType >= 0 ? expectedType : owner.Type(value));
            if (values.TryGetValue(value, out int slot)) return slot;
            if (value is LlvmGetElementPtrInstruction && pointerRelocations.ContainsKey(value))
                return MaterializePointerRelocation(value, expectedType >= 0 ? expectedType : value.LogicalType);
            slot = Register(SelectedValueType(value));
            values.Add(value, slot);
            return slot;
        }

        private int LiteralAddress(LlvmGlobalVariable global, NativeConstant literal)
        {
            if (values.TryGetValue(global, out int existing)) return existing;
            int pointer = global.LogicalType;
            int pointee = owner.Types[pointer].Pointee;
            int contents = ConstantSlotValue(literal, pointee);
            int local = Register(pointee, IsSessionEntry ? NativeSlotRole.Temporary : NativeSlotRole.Local);
            int address = Register(pointer);
            values.Add(global, address);
            prelude.Add(new(NativeOp.FrameAddress, address, local, Source: instructionSource));
            prelude.Add(new(NativeOp.Store, A: address, B: contents, C: 0, Type: pointee, Source: instructionSource));
            trace?.Metric("addresses.literal_cells_materialized", 1);
            return address;
        }

        private int ConstantSlot(LlvmConstant value, int type)
        {
            if (constantSlots.TryGetValue((value, type), out int slot))
            {
                ++constantSlotHits;
                return slot;
            }
            slot = Register(type);
            constantSlots.Add((value, type), slot);
            if (owner.Types[type].Kind == TypeKind.Function && value.Type.IsStructTy() && value is not LlvmUndefValue)
            {
                prelude.Add(ConstantFunction(value, slot));
                ++constantFunctionMaterializations;
                return slot;
            }
            prelude.Add(new(NativeOp.Constant, slot, owner.Constant(Constant(value, type)), Source: instructionSource));
            return slot;
        }

        private NativeConstant Constant(LlvmConstant value, int type) => value switch
        {
            LlvmConstantFP floating when owner.Types[type].Machine is { } machine =>
                NativeConstant.FromMachineFloat(floating.Value, machine),
            LlvmConstantFP floating => new(NativeKind.Number,
                Number: BitConverter.UInt64BitsToDouble(floating.Value.BitcastToAPInt().GetZExtValue())),
            LlvmConstantInt integer when integer.BitWidth == 1 => new(NativeKind.Bool, Boolean: !integer.IsZero),
            LlvmConstantInt integer => NativeConstant.FromMachineInteger(integer.Value, owner.Types[type].Machine),
            LlvmUndefValue when owner.Types[type].Machine is { IsInteger: true } machine =>
                NativeConstant.FromMachineInteger(LlvmAPInt.GetZero(machine.BitWidth), machine),
            LlvmUndefValue when owner.Types[type].Machine is { IsFloating: true } machine =>
                NativeConstant.FromMachineFloat(LlvmAPFloat.GetZero(machine.FloatFormat), machine),
            LlvmUndefValue when value.Type.IsPointerTy() => new(NativeKind.Null),
            LlvmUndefValue => owner.Types[type].Kind switch
            {
                TypeKind.Number => new(NativeKind.Number),
                TypeKind.Bool => new(NativeKind.Bool),
                _ => new(NativeKind.Null)
            },
            LlvmConstantPointerNull => new(NativeKind.Null),
            LlvmConstantAggregateZero zero when zero.Type is LlvmStructType { Elements.Count: 0 } => new(NativeKind.Null),
            _ => throw Unsupported(value, "constant " + value.GetType().Name)
        };
    }
}
