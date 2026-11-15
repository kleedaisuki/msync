// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// lib/IR/Dominators.cpp: instruction and BasicBlockEdge dominance queries.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmDominatorTree
{
    public bool Dominates(LlvmValue definition, LlvmInstruction user)
    {
        if (definition is not LlvmInstruction instruction)
        {
            Debug.Assert(definition is LlvmArgument or LlvmConstant);
            return true;
        }
        var useBlock = user.Parent!;
        var definitionBlock = instruction.Parent!;
        var useNode = GetNode(useBlock);
        if (useNode is null) return true;
        var definitionNode = GetNode(definitionBlock);
        if (definitionNode is null || ReferenceEquals(instruction, user)) return false;
        if (instruction.Opcode is LlvmOpcode.Invoke or LlvmOpcode.CallBr || user is LlvmPhiInstruction)
            return Dominates(instruction, useBlock);
        if (!ReferenceEquals(definitionBlock, useBlock)) return Dominates(definitionNode, useNode);
        return instruction.ComesBefore(user, trace);
    }
    public bool Dominates(LlvmInstruction definition, LlvmBasicBlock useBlock)
    {
        var definitionBlock = definition.Parent!;
        var useNode = GetNode(useBlock);
        if (useNode is null) return true;
        var definitionNode = GetNode(definitionBlock);
        if (definitionNode is null || ReferenceEquals(definitionBlock, useBlock)) return false;
        if (definition.Opcode == LlvmOpcode.Invoke)
            return DominatesEdge(definitionBlock, definition.GetSuccessor(0), useBlock);
        return Dominates(definitionNode, useNode);
    }
    public bool DominatesEdge(LlvmBasicBlock start, LlvmBasicBlock end, LlvmBasicBlock useBlock)
    {
        var endNode = GetNode(end);
        if (!Dominates(endNode, GetNode(useBlock))) return false;
        using (var predecessors = Predecessors(end).GetEnumerator())
        {
            if (predecessors.MoveNext() && !predecessors.MoveNext()) return true;
        }
        bool duplicateEdge = false;
        foreach (var predecessor in Predecessors(end))
        {
            if (ReferenceEquals(predecessor, start))
            {
                if (duplicateEdge) return false;
                duplicateEdge = true;
                continue;
            }
            if (!Dominates(endNode, GetNode(predecessor))) return false;
        }
        return true;
    }
}
