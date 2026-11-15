using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmNativeLowerer
{
    private sealed partial class Lowering
    {
        private readonly Dictionary<LlvmType, int> machineTypes = [];

        private int MachineScalarType(LlvmType type)
        {
            if (machineTypes.TryGetValue(type, out int index)) return index;
            int alignment = checked((int)Input.Module.DataLayout.GetABITypeAlign(type).Value);
            var descriptor = type is LlvmIntegerType integer
                ? NativeMachineType.Integer(integer.BitWidth, alignment)
                : NativeMachineType.Floating(type.GetFltSemantics(), alignment);
            index = Types.FindIndex(candidate => candidate.Machine == descriptor);
            if (index < 0) { index = Types.Count; Types.Add(NativeCodeType.ForMachine(descriptor)); }
            machineTypes.Add(type, index);
            return index;
        }
    }

    private sealed partial class Body
    {
        private void MachineBinary(LlvmBinaryOperator instruction)
        {
            Add(new(NativeOp.MachineBinary, Slot(instruction), OperandSlot(instruction, 0),
                OperandSlot(instruction, 1), Immediate: (int)instruction.Opcode, Source: instruction.Source));
            if (trace is not null) trace.Metric("machine.binary." + instruction.Opcode, 1);
        }

        private void MachineCompare(LlvmCmpInstruction instruction)
        {
            Add(new(NativeOp.MachineCompare, Slot(instruction), OperandSlot(instruction, 0),
                OperandSlot(instruction, 1), Immediate: (int)instruction.Predicate, Source: instruction.Source));
            if (trace is not null) trace.Metric("machine.compare." + instruction.Predicate, 1);
        }

        private void MachineCast(LlvmCastInstruction instruction)
        {
            int type = owner.Type(instruction);
            Add(new(NativeOp.MachineCast, Slot(instruction), OperandSlot(instruction, 0),
                Type: type, Immediate: (int)instruction.Opcode, Source: instruction.Source));
            if (trace is not null) trace.Metric("machine.cast." + instruction.Opcode, 1);
        }

        private void MachineUnary(LlvmUnaryOperator instruction)
        {
            Add(new(NativeOp.MachineUnary, Slot(instruction), OperandSlot(instruction, 0),
                Immediate: (int)instruction.Opcode, Source: instruction.Source));
            if (trace is not null) trace.Metric("machine.unary." + instruction.Opcode, 1);
        }

        private bool MachineIntegerIntrinsic(LlvmCallInstruction call, LlvmIntrinsicKind kind)
        {
            NativeMachineIntrinsic? operation = kind.Name switch
            {
                "llvm.smin" => NativeMachineIntrinsic.SMin, "llvm.smax" => NativeMachineIntrinsic.SMax,
                "llvm.umin" => NativeMachineIntrinsic.UMin, "llvm.umax" => NativeMachineIntrinsic.UMax,
                "llvm.scmp" => NativeMachineIntrinsic.SCmp, "llvm.ucmp" => NativeMachineIntrinsic.UCmp,
                "llvm.ctpop" => NativeMachineIntrinsic.CTPop, "llvm.ctlz" => NativeMachineIntrinsic.CTLZ,
                "llvm.cttz" => NativeMachineIntrinsic.CTTZ, "llvm.bswap" => NativeMachineIntrinsic.BSwap,
                "llvm.bitreverse" => NativeMachineIntrinsic.BitReverse, "llvm.abs" => NativeMachineIntrinsic.Abs,
                "llvm.fshl" => NativeMachineIntrinsic.FShl, "llvm.fshr" => NativeMachineIntrinsic.FShr,
                "llvm.uadd.sat" => NativeMachineIntrinsic.UAddSat, "llvm.sadd.sat" => NativeMachineIntrinsic.SAddSat,
                "llvm.usub.sat" => NativeMachineIntrinsic.USubSat, "llvm.ssub.sat" => NativeMachineIntrinsic.SSubSat,
                "llvm.ushl.sat" => NativeMachineIntrinsic.UShlSat, "llvm.sshl.sat" => NativeMachineIntrinsic.SShlSat,
                "llvm.umulh" => NativeMachineIntrinsic.UMulH, "llvm.smulh" => NativeMachineIntrinsic.SMulH,
                _ => null
            };
            if (operation is null) return false;
            Add(new(NativeOp.MachineIntrinsic, Slot(call), Immediate: (int)operation.Value,
                Inputs: ArgumentSlots(call), Source: call.Source));
            if (trace is not null) trace.Metric("machine.intrinsic." + kind.Name, 1);
            return true;
        }
    }
}
