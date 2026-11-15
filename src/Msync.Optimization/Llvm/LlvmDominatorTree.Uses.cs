// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: IR/Dominators.h/.cpp.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public readonly record struct LlvmBasicBlockEdge(LlvmBasicBlock Start, LlvmBasicBlock End);

public sealed partial class LlvmDominatorTree
{
    public bool Dominates(LlvmBasicBlock block, LlvmUse use)
    {
        var user = (LlvmInstruction)use.User;
        return user is LlvmPhiInstruction phi ? Dominates(block, phi.GetIncomingBlock(use.OperandNo))
            : ProperlyDominates(block, user.Parent!);
    }

    public bool Dominates(LlvmBasicBlockEdge edge, LlvmBasicBlock block) => DominatesEdge(edge.Start, edge.End, block);

    public bool Dominates(LlvmBasicBlockEdge edge, LlvmUse use)
    {
        var user = (LlvmInstruction)use.User;
        if (user is LlvmPhiInstruction phi && ReferenceEquals(phi.Parent, edge.End) &&
            ReferenceEquals(phi.GetIncomingBlock(use.OperandNo), edge.Start)) return true;
        var useBlock = user is LlvmPhiInstruction incoming ? incoming.GetIncomingBlock(use.OperandNo) : user.Parent!;
        return Dominates(edge, useBlock);
    }

    public bool Dominates(LlvmBasicBlockEdge definition, LlvmBasicBlockEdge use) =>
        definition == use || Dominates(definition, use.Start);

    public bool Dominates(LlvmValue definition, LlvmUse use)
    {
        if (definition is not LlvmInstruction instruction)
        {
            Debug.Assert(definition is LlvmArgument or LlvmConstant);
            return true;
        }
        var user = (LlvmInstruction)use.User;
        var useBlock = user is LlvmPhiInstruction phi ? phi.GetIncomingBlock(use.OperandNo) : user.Parent!;
        var useNode = GetNode(useBlock);
        if (useNode is null) return true;
        var definitionBlock = instruction.Parent!;
        var definitionNode = GetNode(definitionBlock);
        if (definitionNode is null) return false;
        if (instruction.Opcode == LlvmOpcode.Invoke)
            return Dominates(new LlvmBasicBlockEdge(definitionBlock, instruction.GetSuccessor(0)), use);
        if (!ReferenceEquals(definitionBlock, useBlock)) return Dominates(definitionNode, useNode);
        if (user is LlvmPhiInstruction) return true;
        return instruction.ComesBefore(user, trace);
    }

    public bool IsReachableFromEntry(LlvmUse use)
    {
        if (use.User is not LlvmInstruction user) return true;
        return IsReachableFromEntry(user is LlvmPhiInstruction phi ? phi.GetIncomingBlock(use.OperandNo) : user.Parent!);
    }
}
