// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// SimplifyCFG.cpp foldBranchToCommonDest and common-destination recipe.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    public static bool FoldBranchToCommonDest(LlvmCondBrInstruction branch, CompilationTrace? trace = null,
        uint bonusInstThreshold = 1)
    {
        trace = trace?.Detail;
        var folder = new CommonDestinationFolder(branch, trace, bonusInstThreshold);
        return trace is null ? folder.Run() : trace.MeasureDetailed("optimizer.llvm-simplifycfg.fold-common-destination",
            folder, static state => state.Run(), attributes: Attributes(branch), metrics: _ => folder.Metrics!);
    }

    private static bool FoldBranchToCommonDest(LlvmCondBrInstruction branch, LlvmDomTreeUpdater? updater,
        LlvmTargetTransformInfo targetInfo, LlvmSimplifyCfgOptions options, LlvmSimplifyCfgTuning tuning,
        CompilationTrace? trace)
    {
        var folder = new CommonDestinationFolder(branch, trace, (uint)options.BonusInstThreshold,
            updater, targetInfo, tuning, options.AC);
        return trace is null ? folder.Run() : trace.MeasureDetailed("optimizer.llvm-simplifycfg.fold-common-destination",
            folder, static state => state.Run(), attributes: Attributes(branch), metrics: _ => folder.Metrics!);
    }

    private static Dictionary<string, string> Attributes(LlvmInstruction instruction)
    {
        var attributes = new Dictionary<string, string>
        {
            ["function.name"] = instruction.Parent!.Parent!.Name,
            ["block.name"] = instruction.Parent.Name
        };
        if (instruction.Source is { } source)
        {
            attributes["source.file"] = source.Source;
            attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return attributes;
    }

    private readonly record struct CommonDestinationRecipe(LlvmBasicBlock Common, LlvmOpcode Opcode, bool InvertPredecessor);

    private static CommonDestinationRecipe? CommonDestination(LlvmCondBrInstruction branch, LlvmCondBrInstruction predecessor)
    {
        // No branch profile metadata is represented. The source probability
        // remains unknown even with TTI, retaining all four recipe orders.
        if (predecessor.GetSuccessor(0) == branch.GetSuccessor(0))
            return new(branch.GetSuccessor(0), LlvmOpcode.Or, false);
        if (predecessor.GetSuccessor(1) == branch.GetSuccessor(1))
            return new(branch.GetSuccessor(1), LlvmOpcode.And, false);
        if (predecessor.GetSuccessor(0) == branch.GetSuccessor(1))
            return new(branch.GetSuccessor(1), LlvmOpcode.And, true);
        if (predecessor.GetSuccessor(1) == branch.GetSuccessor(0))
            return new(branch.GetSuccessor(0), LlvmOpcode.Or, true);
        return null;
    }

    private sealed partial class CommonDestinationFolder(LlvmCondBrInstruction branch, CompilationTrace? trace,
        uint bonusInstThreshold, LlvmDomTreeUpdater? updater = null,
        LlvmTargetTransformInfo? targetInfo = null, LlvmSimplifyCfgTuning? tuning = null,
        LlvmAssumptionCache? assumptions = null)
    {
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        private LlvmBasicBlock Block => branch.Parent!;
        private uint VectorMultiplier => tuning?.BranchFoldToCommonDestVectorMultiplier ?? 2;
        private LlvmTargetCostKind CostKind => Block.Parent!.HasMinSize ? LlvmTargetCostKind.CodeSize : LlvmTargetCostKind.SizeAndLatency;

        internal bool Run()
        {
            if (branch.Condition is not LlvmInstruction condition || condition.Parent != Block || !condition.HasOneUse ||
                condition is not (LlvmCmpInstruction or LlvmBinaryOperator or LlvmSelectInstruction) &&
                condition is not LlvmCastInstruction { Opcode: LlvmOpcode.Trunc }) return Reject("condition");
            if (branch.GetSuccessor(0) == Block || branch.GetSuccessor(1) == Block) return Reject("self-successor");
            List<LlvmBasicBlock> predecessors = [];
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(Block))
            {
                Count("predecessors.visited");
                if (predecessor.GetTerminatorOrNull() is not LlvmCondBrInstruction prior || !SafeToMerge(branch, prior))
                { Count("predecessors.incompatible"); continue; }
                if (CommonDestination(branch, prior) is null) { Count("predecessors.no_common_destination"); continue; }
                if (targetInfo is not null)
                {
                    CommonDestinationRecipe recipe = CommonDestination(branch, prior)!.Value;
                    LlvmInstructionCost cost = targetInfo.GetArithmeticInstrCost(recipe.Opcode, branch.Condition.Type, CostKind);
                    if (recipe.InvertPredecessor && (!prior.Condition.HasOneUse || prior.Condition is not LlvmCmpInstruction))
                        cost += targetInfo.GetArithmeticInstrCost(LlvmOpcode.Xor, branch.Condition.Type, CostKind);
                    Count("predecessors.target_cost_queries");
                    if (cost > (tuning?.BranchFoldThreshold ?? 2)) { Count("predecessors.logic_cost"); continue; }
                }
                predecessors.Add(predecessor);
            }
            if (predecessors.Count == 0) return Reject("no-predecessors");
            LlvmInstruction? context = predecessors.Count == 1 ? predecessors[0].GetTerminator() : null;
            uint bonus = 0;
            bool sawVector = false;
            foreach (LlvmInstruction instruction in Block.Instructions)
            {
                if (instruction == condition || instruction is LlvmUncondBrInstruction or LlvmCondBrInstruction) continue;
                Count("bonus.speculation_queries");
                if (!LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction, context, metrics: Metrics, assumptions: assumptions))
                    return Reject("not-speculatable");
                sawVector |= instruction.Type.IsVectorTy() || instruction.Operands.Any(value => value!.Type.IsVectorTy());
                if (targetInfo is null || !targetInfo.GetInstructionCost(instruction, CostKind).IsFree)
                {
                    bonus += (uint)predecessors.Count;
                    if (bonus > bonusInstThreshold * VectorMultiplier) return Reject("early-budget");
                }
                foreach (LlvmUse use in instruction.Uses)
                {
                    Count("bonus.uses");
                    var user = (LlvmInstruction)use.User;
                    if (user is LlvmPhiInstruction phi)
                    {
                        if (phi.GetIncomingBlock(use.OperandNo) != Block) return Reject("not-block-closed-ssa");
                    }
                    else if (user.Parent != Block || !instruction.ComesBefore(user)) return Reject("not-block-closed-ssa");
                }
            }
            if (bonus > bonusInstThreshold * (sawVector ? VectorMultiplier : 1U)) return Reject("budget");
            foreach (LlvmBasicBlock predecessor in predecessors)
                return Perform((LlvmCondBrInstruction)predecessor.GetTerminator());
            return false;
        }

        private bool SafeToMerge(LlvmCondBrInstruction first, LlvmCondBrInstruction second)
        {
            if (first == second) return false;
            HashSet<LlvmBasicBlock> successors = [first.GetSuccessor(0), first.GetSuccessor(1)];
            foreach (LlvmBasicBlock successor in second.Successors)
            {
                if (!successors.Contains(successor)) continue;
                for (var instruction = successor.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
                {
                    Count("merge.phi_checks");
                    if (phi.GetIncomingValue(phi.GetBasicBlockIndex(first.Parent!)) !=
                        phi.GetIncomingValue(phi.GetBasicBlockIndex(second.Parent!))) return false;
                }
            }
            return true;
        }

        private bool Reject(string cause)
        { if (Metrics is not null) Count("retained." + cause); return false; }
        private void Count(string name)
        { if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + 1; }
    }
}
