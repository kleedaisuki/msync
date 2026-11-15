// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/ConstantFolding.cpp ConstantFoldScalarCall1 launder_invariant_group.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmConstantFolding
{
    public static LlvmConstant? ConstantFoldMemoryIntrinsic(LlvmIntrinsicKind kind,
        IReadOnlyList<LlvmConstant> operands, LlvmCallBase? call = null, Dictionary<string, ulong>? metrics = null)
    {
        if (kind != LlvmIntrinsicKind.LaunderInvariantGroup) return null;
        Count(metrics, "constant_fold.memory.launder.queries");
        var operand = operands[0];
        // PoisonValue is an UndefValue subclass upstream.
        if (operand is LlvmUndefValue or LlvmPoisonValue)
        { Count(metrics, "constant_fold.memory.launder.undef"); return operand; }
        if (operand is LlvmConstantPointerNull pointer && call?.Parent?.Parent is { } caller &&
            !LlvmFunction.NullPointerIsDefined(caller, pointer.PointerType.AddressSpace))
        { Count(metrics, "constant_fold.memory.launder.null"); return operand; }
        Count(metrics, "constant_fold.memory.launder.no_fold");
        return null;
    }
}
