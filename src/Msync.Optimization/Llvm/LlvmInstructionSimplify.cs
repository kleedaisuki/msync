// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/InstructionSimplify.cpp valueDominatesPHI, simplifyPHINode,
// and simplifyInstruction's self-result handling; scalar dispatch is in the partials.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmInstructionSimplify
{
    public static LlvmValue? SimplifyInstruction(LlvmInstruction instruction, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null,
        bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null)
    {
        Debug.Assert(instruction.Parent?.Parent is not null);
        return SimplifyInstruction(instruction, new BinaryQuery(instruction.Parent!.Module!.DataLayout,
            instruction, metrics, tree, Assumptions: assumptions, AllowEphemerals: allowEphemerals, DC: conditions, CondContext: condContext));
    }

    public static LlvmValue? SimplifyPhi(LlvmPhiInstruction phi, LlvmDominatorTree? tree = null,
        Dictionary<string, ulong>? metrics = null, LlvmAssumptionCache? assumptions = null,
        bool allowEphemerals = false, LlvmDomConditionCache? conditions = null, LlvmCondContext? condContext = null) => SimplifyInstruction(phi, tree, metrics, assumptions, allowEphemerals, conditions, condContext);

    private static LlvmValue? SimplifyPhiCore(LlvmPhiInstruction phi, BinaryQuery query,
        IReadOnlyList<LlvmValue>? incomingValues = null)
    {
        LlvmDominatorTree? tree = query.Tree;
        Dictionary<string, ulong>? metrics = query.Metrics;
        Count(metrics, "simplify.phi.queries");
        LlvmValue? common = null;
        bool hasPoison = false, hasUndef = false;
        for (int index = 0; index < (incomingValues?.Count ?? phi.IncomingCount); ++index)
        {
            LlvmValue incoming = incomingValues is null ? phi.GetIncomingValue(index) : incomingValues[index];
            Count(metrics, "simplify.phi.incoming");
            if (ReferenceEquals(incoming, phi)) { Count(metrics, "simplify.phi.self"); continue; }
            if (incoming is LlvmPoisonValue) { hasPoison = true; Count(metrics, "simplify.phi.poison"); continue; }
            if (query.IsUndef(incoming)) { hasUndef = true; Count(metrics, "simplify.phi.undef"); continue; }
            if (common is not null && !ReferenceEquals(incoming, common))
            { Count(metrics, "simplify.phi.distinct"); return null; }
            common = incoming;
        }
        if (common is null)
        {
            Count(metrics, "simplify.phi.no_common");
            return hasUndef ? LlvmUndefValue.Get(phi.Type) : LlvmPoisonValue.Get(phi.Type);
        }
        if (hasPoison || hasUndef)
        {
            if (!ValueDominatesPhi(common, phi, tree))
            { Count(metrics, "simplify.phi.not_dominating"); return null; }
            if (hasUndef && !LlvmValueTracking.IsGuaranteedNotToBePoison(common, tree, query.Context, metrics,
                assumptions: query.Assumptions, allowEphemerals: query.AllowEphemerals, conditions: query.DC))
            { Count(metrics, "simplify.phi.common_may_be_poison"); return null; }
        }
        Count(metrics, "simplify.phi.common");
        return common;
    }

    private static bool ValueDominatesPhi(LlvmValue value, LlvmPhiInstruction phi, LlvmDominatorTree? tree)
    {
        if (value is not LlvmInstruction instruction) return true;
        if (tree is not null) return tree.Dominates(instruction, phi);
        return ReferenceEquals(instruction.Parent, instruction.Parent!.Parent!.BasicBlocks[0]) &&
            instruction.Opcode is not (LlvmOpcode.Invoke or LlvmOpcode.CallBr);
    }

    private static void Count(Dictionary<string, ulong>? metrics, string key)
    { if (metrics is not null) metrics[key] = metrics.GetValueOrDefault(key) + 1; }
}
