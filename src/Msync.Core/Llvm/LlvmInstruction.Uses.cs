// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Instruction.cpp isUsedOutsideOfBlock.
namespace Msync.Core.Llvm;

public abstract partial class LlvmInstruction
{
    public bool IsUsedOutsideOfBlock(LlvmBasicBlock block)
    {
        foreach (LlvmUse use in Uses)
        {
            var user = (LlvmInstruction)use.User;
            LlvmBasicBlock? useBlock = user is LlvmPhiInstruction phi
                ? phi.GetIncomingBlock(use.OperandNo) : user.Parent;
            if (!ReferenceEquals(useBlock, block)) return true;
        }
        return false;
    }
}
