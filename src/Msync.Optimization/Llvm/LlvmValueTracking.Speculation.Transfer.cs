// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/ValueTracking.cpp
// isGuaranteedToTransferExecutionToSuccessor, isValidAssumeForContext,
// isEphemeralValueOf. EH personality attributes are not represented.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmValueTracking
{
    public static bool IsGuaranteedToTransferExecutionToSuccessor(LlvmInstruction instruction,
        Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.transfer.instructions");
        if (instruction is LlvmReturnInstruction or LlvmUnreachableInstruction)
        { Count(metrics, "value_tracking.transfer.no_successor"); return false; }
        if (instruction.Opcode == LlvmOpcode.CatchPad)
        { Count(metrics, "value_tracking.transfer.no_coreclr_personality"); return false; }
        bool result = !instruction.MayThrow() && instruction.WillReturn();
        Count(metrics, result ? "value_tracking.transfer.guaranteed" : "value_tracking.transfer.may_interrupt");
        return result;
    }

    public static bool IsGuaranteedToTransferExecutionToSuccessor(LlvmBasicBlock block,
        Dictionary<string, ulong>? metrics = null)
    {
        foreach (var instruction in block.Instructions)
            if (!IsGuaranteedToTransferExecutionToSuccessor(instruction, metrics)) return false;
        return true;
    }

    public static bool IsGuaranteedToTransferExecutionToSuccessor(LlvmInstruction? begin, LlvmInstruction? end,
        uint scanLimit, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(scanLimit != 0);
        for (var current = begin; !ReferenceEquals(current, end); current = current!.Next)
        {
            if (--scanLimit == 0) { Count(metrics, "value_tracking.transfer.scan_limit"); return false; }
            if (!IsGuaranteedToTransferExecutionToSuccessor(current!, metrics)) return false;
        }
        return true;
    }

    public static bool IsValidAssumeForContext(LlvmInstruction assumption, LlvmInstruction context,
        LlvmDominatorTree? tree = null, bool allowEphemerals = false, Dictionary<string, ulong>? metrics = null)
    {
        Count(metrics, "value_tracking.assume_context.queries");
        if (ReferenceEquals(assumption.Parent, context.Parent))
        {
            if (assumption.ComesBefore(context)) return true;
            if (!allowEphemerals && ReferenceEquals(assumption, context)) return false;
            if (!IsGuaranteedToTransferExecutionToSuccessor(context, assumption, 15, metrics)) return false;
            return allowEphemerals || !IsEphemeralValueOf(assumption, context, metrics);
        }
        if (tree is not null) return tree.Dominates(assumption, context);
        if (ReferenceEquals(assumption.Parent, context.Parent!.Parent!.BasicBlocks[0])) return true;
        using var predecessors = LlvmDominatorTree.Predecessors(context.Parent).GetEnumerator();
        if (!predecessors.MoveNext()) return false;
        var predecessor = predecessors.Current;
        return !predecessors.MoveNext() && ReferenceEquals(assumption.Parent, predecessor);
    }

    private static bool IsEphemeralValueOf(LlvmInstruction assumption, LlvmValue value, Dictionary<string, ulong>? metrics)
    {
        foreach (var operand in assumption.OperandUses) if (ReferenceEquals(operand.Value, value)) return true;
        if (value is not LlvmInstruction instruction) return false;
        if (ReferenceEquals(instruction, assumption)) return true;
        var visited = new HashSet<LlvmInstruction>(ReferenceEqualityComparer.Instance) { instruction };
        var work = new Stack<LlvmInstruction>(); work.Push(instruction);
        bool reachesAssumption = false;
        while (work.TryPop(out var current))
            foreach (var user in current.Users)
            {
                Count(metrics, "value_tracking.assume_context.ephemeral_users");
                var next = (LlvmInstruction)user;
                if (ReferenceEquals(next, assumption)) { reachesAssumption = true; continue; }
                if (next.MayHaveSideEffects() || next.IsTerminator) return false;
                if (visited.Add(next)) work.Push(next);
            }
        return reachesAssumption;
    }
}
