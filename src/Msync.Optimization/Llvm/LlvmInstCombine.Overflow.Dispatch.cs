// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: InstCombineCompares.cpp
// computeOverflow.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmInstCombine
{
    internal sealed partial class Work
    {
        private LlvmOverflowResult ComputeOverflow(LlvmOpcode opcode, bool signed,
            LlvmValue left, LlvmValue right, LlvmInstruction context) => opcode switch
        {
            LlvmOpcode.Add => signed ? ComputeOverflowForSignedAdd(left, right, context) : ComputeOverflowForUnsignedAdd(left, right, context),
            LlvmOpcode.Sub => signed ? ComputeOverflowForSignedSub(left, right, context) : ComputeOverflowForUnsignedSub(left, right, context),
            LlvmOpcode.Mul => signed ? ComputeOverflowForSignedMul(left, right, context) : ComputeOverflowForUnsignedMul(left, right, context),
            _ => throw new InvalidOperationException("The source overflow query expects add, sub or mul.")
        };

    }
}
