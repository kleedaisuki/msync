// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/LoopInfo.cpp Loop::makeLoopInvariant, no MSSA/SE/metadata attributes.
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    private static bool Speculatable(LlvmInstruction instruction, CompilationTrace? trace)
    {
        if (trace is null) return LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction);
        var metrics = new Dictionary<string, ulong>();
        bool result = LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction, metrics: metrics);
        foreach (var pair in metrics) trace.Metric(pair.Key, (long)pair.Value);
        return result;
    }
    private static bool MakeLoopInvariant(LlvmLoop loop, LlvmValue value, ref bool changed,
        LlvmInstruction? insertBefore, CompilationTrace? trace)
    {
        if (value is not LlvmInstruction instruction || !loop.Contains(instruction.Parent!)) return true;
        if (!Speculatable(instruction, trace))
        { trace?.Metric("invariant.not_speculatable", 1); return false; }
        if (instruction.MayReadFromMemory())
        { trace?.Metric("invariant.reads_memory", 1); return false; }
        if (instruction.IsEHPad) return false;
        if (insertBefore is null)
        {
            var preheader = loop.GetLoopPreheader();
            if (preheader is null) return false;
            insertBefore = preheader.GetTerminator();
        }
        foreach (var operand in instruction.OperandUses)
            if (!MakeLoopInvariant(loop, operand.Value!, ref changed, insertBefore, trace)) return false;
        instruction.RemoveFromParent();
        insertBefore.Parent!.InsertBefore(instruction, insertBefore);
        // dropUBImplyingAttrsAndUnknownMetadata has no represented call attrs or
        // metadata to drop. SourceSpan and per-use ABI annotations are not LLVM metadata.
        changed = true;
        trace?.Metric("invariant.instructions_hoisted", 1);
        return true;
    }
}
