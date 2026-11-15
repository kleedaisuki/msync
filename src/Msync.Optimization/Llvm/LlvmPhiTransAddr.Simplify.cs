// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: PHITransAddr.cpp's
// InstructionSimplify calls, retaining the source's context-free DT query.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    internal static LlvmValue? PhiTransSimplifyCast(LlvmCastInstruction cast, LlvmValue operand,
        LlvmDataLayout layout, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics) =>
        SimplifyIntegerCast(cast.Opcode, operand, cast.Type, new(layout, null, metrics, tree), 3);

    internal static LlvmValue? PhiTransSimplifyGep(LlvmGetElementPtrInstruction gep, LlvmValue pointer,
        IReadOnlyList<LlvmValue> indices, LlvmDataLayout layout, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics) =>
        SimplifyScalarGep(gep, pointer, indices, new(layout, null, metrics, tree));

    internal static LlvmValue? PhiTransSimplifyAdd(LlvmValue left, LlvmValue right, bool nsw, bool nuw,
        LlvmDataLayout layout, LlvmDominatorTree? tree, Dictionary<string, ulong>? metrics) =>
        SimplifyIntegerAdd(left, right, nsw, nuw, new(layout, null, metrics, tree), 3);
}
