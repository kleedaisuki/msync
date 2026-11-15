// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// InstructionSimplify.cpp simplifyAddInst public entry with RecursionLimit.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    internal static LlvmValue? SimplifyAddInst(LlvmValue left, LlvmValue right, bool nsw, bool nuw, BinaryQuery query) =>
        SimplifyIntegerAdd(left, right, nsw, nuw, query, 3);
}
