// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Analysis/CodeMetrics.h/.cpp.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public enum LlvmConvergenceKind { None, Controlled, ExtendedLoop, Uncontrolled }

public sealed partial class LlvmCodeMetrics
{
    private readonly List<LlvmInstructionCost> blockCosts = [];
    private readonly Dictionary<string, ulong>? workMetrics;

    public LlvmCodeMetrics(Dictionary<string, ulong>? metrics = null)
    {
        NumBBInsts = blockCosts.AsReadOnly();
        workMetrics = metrics;
    }

    public bool IsRecursive { get; private set; }
    public bool NotDuplicatable { get; private set; }
    public bool UsesDynamicAlloca { get; private set; }
    public LlvmConvergenceKind Convergence { get; private set; }
    public LlvmInstructionCost NumInsts { get; private set; }
    public IReadOnlyList<LlvmInstructionCost> NumBBInsts { get; }
    public uint NumInlineCandidates { get; private set; }
    public uint NumVectorInsts { get; private set; }
    public uint NumRets { get; private set; }

    public void AnalyzeBasicBlock(LlvmBasicBlock block, LlvmTargetTransformInfo target,
        IReadOnlySet<LlvmValue> ephemeralValues, bool prepareForLto = false, LlvmLoop? loop = null)
    {
        LlvmInstructionCost before = NumInsts;
        foreach (LlvmInstruction instruction in block.Instructions)
        {
            Count(workMetrics, "instructions.visited");
            if (ephemeralValues.Contains(instruction))
            {
                Count(workMetrics, "instructions.ephemeral");
                continue;
            }
            if (instruction is LlvmCallBase call) AnalyzeCall(call, block, target, prepareForLto);
            if (instruction is LlvmAllocaInstruction { IsStaticAlloca: false }) UsesDynamicAlloca = true;
            if (instruction.Opcode == LlvmOpcode.ExtractElement || instruction.Type.IsVectorTy()) ++NumVectorInsts;
            if (instruction.Type.IsTokenTy() && instruction.IsUsedOutsideOfBlock(block)) NotDuplicatable = true;
            if (instruction is LlvmCallBase convergent)
            {
                if (convergent.CannotDuplicate) NotDuplicatable = true;
                if ((Convergence is LlvmConvergenceKind.None or LlvmConvergenceKind.Controlled) && convergent.IsConvergent)
                    Convergence = convergent.GetConvergenceControlToken() is not null
                        ? LlvmConvergenceKind.Controlled : LlvmConvergenceKind.Uncontrolled;
            }
            LlvmInstructionCost cost = target.GetInstructionCost(instruction, LlvmTargetCostKind.CodeSize);
            NumInsts += cost;
            Count(workMetrics, cost.IsValid ? "costs.valid" : "costs.invalid");
        }
        if (block.GetTerminator().Opcode == LlvmOpcode.Ret) ++NumRets;
        NotDuplicatable |= block.GetTerminator().Opcode == LlvmOpcode.IndirectBr;
        int number = (int)block.Number;
        if (blockCosts.Count <= number)
        {
            int capacity = (int)block.Parent!.GetMaxBlockNumber();
            while (blockCosts.Count < capacity) blockCosts.Add(default);
        }
        blockCosts[number] = NumInsts - before;
        Count(workMetrics, "blocks.analyzed");
    }

    private void AnalyzeCall(LlvmCallBase call, LlvmBasicBlock block, LlvmTargetTransformInfo target, bool prepareForLto)
    {
        Count(workMetrics, "calls.visited");
        if (call.CalledFunction is { } function)
        {
            if (!call.IsNoInline && target.IsLoweredToCall(function) &&
                ((function.Linkage == LlvmLinkage.InternalLinkage && function.HasOneLiveUse) || prepareForLto))
            {
                ++NumInlineCandidates;
                Count(workMetrics, "calls.inline_candidates");
            }
            if (ReferenceEquals(function, block.Parent)) IsRecursive = true;
            return;
        }
        if (prepareForLto && !call.IsNoInline)
        {
            ++NumInlineCandidates;
            Count(workMetrics, "calls.inline_candidates");
        }
    }

    private static void Count(Dictionary<string, ulong>? metrics, string key)
    {
        if (metrics is null) return;
        key = "code_metrics." + key;
        metrics[key] = metrics.GetValueOrDefault(key) + 1;
    }
}
