using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

internal sealed partial class NativePreparationState
{
    private bool Machine(NativeInstruction instruction)
    {
        trace?.Metric("machine.queries", 1);
        NativeType type = image.Type(function.Slots[instruction.Result].Type);
        if (instruction.Op == NativeOp.MachineIntrinsic)
        {
            if (!Arguments(instruction.Inputs, out NativeValue[] inputs)) return Unknown();
            SetComputed(instruction, NativeMachineOperations.Intrinsic((NativeMachineIntrinsic)instruction.Immediate,
                inputs[0], inputs.Length > 1 ? inputs[1] : default, inputs.Length > 2 ? inputs[2] : default, type));
        }
        else
        {
            if (!Known(instruction.A, out NativeValue first)) return Unknown();
            if (instruction.Op == NativeOp.MachineUnary)
                SetComputed(instruction, NativeMachineOperations.Unary((LlvmOpcode)instruction.Immediate, first, type));
            else if (instruction.Op == NativeOp.MachineCast)
                SetComputed(instruction, NativeMachineOperations.Cast((LlvmOpcode)instruction.Immediate, first, type));
            else
            {
                if (!Known(instruction.B, out NativeValue second)) return Unknown();
                SetComputed(instruction, instruction.Op == NativeOp.MachineCompare
                    ? NativeMachineOperations.Compare((LlvmCmpPredicate)instruction.Immediate, first, second)
                    : NativeMachineOperations.Binary((LlvmOpcode)instruction.Immediate, first, second, type));
            }
        }
        trace?.Metric("machine.computed", 1);
        return true;

        bool Unknown()
        {
            frame.Drop(instruction.Result);
            trace?.Metric("machine.unknown_operand", 1);
            return true;
        }
    }
}
