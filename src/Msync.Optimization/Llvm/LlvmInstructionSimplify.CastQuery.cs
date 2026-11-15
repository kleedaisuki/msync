// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
namespace Msync.Optimization.Llvm;
using Msync.Core.Llvm;
public static partial class LlvmInstructionSimplify
{
    internal static LlvmValue? SimplifyCast(LlvmOpcode opcode, LlvmValue operand, LlvmType destination, BinaryQuery query) =>
        SimplifyIntegerCast(opcode, operand, destination, query, 3);
}
