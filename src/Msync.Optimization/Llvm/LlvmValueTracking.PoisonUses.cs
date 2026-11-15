// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/ValueTracking.cpp programUndefinedIfUndefOrPoison (PoisonOnly),
// propagatesPoison, handleGuaranteedWellDefinedOps / NonPoisonOps,
// and isGuaranteedToTransferExecutionToSuccessor.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    internal static bool ProgramUndefinedIfPoison(LlvmValue value, Dictionary<string, ulong>? metrics)
    {
        LlvmBasicBlock block;
        LlvmInstruction? begin;
        if (value is LlvmInstruction instruction)
        {
            block = instruction.Parent!;
            begin = instruction.Next;
        }
        else if (value is LlvmArgument argument)
        {
            if (argument.Parent!.IsDeclaration) return false;
            block = argument.Parent.BasicBlocks[0];
            begin = block.FirstInstruction;
        }
        else return false;
        uint scanLimit = 32;
        HashSet<LlvmValue> yieldsPoison = [value];
        HashSet<LlvmBasicBlock> visited = [block];
        while (true)
        {
            for (LlvmInstruction? current = begin; current is not null; current = current.Next)
            {
                if (--scanLimit == 0)
                { Count(metrics, "value_tracking.poison.ub_scan_limit"); return false; }
                Count(metrics, "value_tracking.poison.ub_scan_instructions");
                if (MustTriggerUb(current, yieldsPoison)) return true;
                if (!IsGuaranteedToTransferExecutionToSuccessor(current))
                { Count(metrics, "value_tracking.poison.ub_scan_transfer_stop"); return false; }
                foreach (LlvmUse operand in current.OperandUses)
                {
                    if (!yieldsPoison.Contains(operand.Value!) || !PropagatesPoison(operand)) continue;
                    yieldsPoison.Add(current);
                    break;
                }
                if (current is LlvmSelectInstruction select && yieldsPoison.Contains(select.TrueValue) &&
                    yieldsPoison.Contains(select.FalseValue)) yieldsPoison.Add(current);
            }
            LlvmInstruction? terminator = block.GetTerminatorOrNull();
            if (terminator is null || terminator.SuccessorCount != 1) break;
            block = terminator.GetSuccessor(0);
            if (!visited.Add(block)) break;
            begin = block.FirstInstruction;
            while (begin is LlvmPhiInstruction) begin = begin.Next;
        }
        return false;
    }

    private static bool MustTriggerUb(LlvmInstruction instruction, HashSet<LlvmValue> knownPoison)
    {
        LlvmValue? wellDefined = instruction switch
        {
            LlvmStoreInstruction store => store.PointerOperand,
            LlvmLoadInstruction load => load.PointerOperand,
            LlvmCallBase indirectCall when indirectCall.CalledFunction is null => indirectCall.CalledOperand,
            LlvmSwitchInstruction dispatch => dispatch.Condition,
            LlvmCondBrInstruction branch => branch.Condition,
            _ => null
        };
        if (wellDefined is not null && knownPoison.Contains(wellDefined)) return true;
        if (instruction is LlvmCallBase call)
            for (int arg = 0; arg < call.ArgumentCount; arg++)
                if (call.IsPassingUndefUB(arg) && knownPoison.Contains(call.GetArgument(arg))) return true;
        return instruction.Opcode is LlvmOpcode.UDiv or LlvmOpcode.SDiv or LlvmOpcode.URem or LlvmOpcode.SRem &&
            knownPoison.Contains(instruction.GetOperand(1)!);
    }

    private static bool PropagatesPoison(LlvmUse operand)
    {
        LlvmInstruction instruction = (LlvmInstruction)operand.User;
        return instruction.Opcode switch
        {
            LlvmOpcode.Freeze or LlvmOpcode.PHI or LlvmOpcode.Invoke => false,
            LlvmOpcode.Select => operand.OperandNo == 0,
            LlvmOpcode.Call => instruction is LlvmCallBase { CalledFunction.IntrinsicDescriptor.PropagatesPoison: true },
            LlvmOpcode.ICmp or LlvmOpcode.FCmp or LlvmOpcode.GetElementPtr => true,
            _ => instruction is LlvmBinaryOperator or LlvmUnaryOperator or LlvmCastInstruction
        };
    }
}
