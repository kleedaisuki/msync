// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// getCaseResults/constantFold/initializeUniqueCases/foldSwitchToSelect.
using System.Numerics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private sealed record SwitchResultCases(LlvmConstant Result, List<LlvmConstantInt> Cases);

        private static IEnumerable<LlvmPhiInstruction> SwitchPhis(LlvmBasicBlock block)
        {
            for (LlvmInstruction? instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
                yield return phi;
        }

        private void RemoveSwitchPredecessor(LlvmBasicBlock block, LlvmBasicBlock predecessor, bool keepOneInputPhis = false) =>
            LlvmBasicBlockUtils.RemovePredecessor(block, predecessor, keepOneInputPhis, Trace, Metrics);

        private static void AddSwitchPredecessor(LlvmBasicBlock successor, LlvmBasicBlock newPredecessor, LlvmBasicBlock oldPredecessor)
        {
            foreach (LlvmPhiInstruction phi in SwitchPhis(successor))
                phi.AddIncoming(phi.GetIncomingValue(phi.GetBasicBlockIndex(oldPredecessor)), newPredecessor);
        }

        private static LlvmConstant? LookupSwitchConstant(LlvmValue value, Dictionary<LlvmValue, LlvmConstant?> pool) =>
            value as LlvmConstant ?? pool.GetValueOrDefault(value);

        private LlvmConstant? FoldSwitchInstruction(LlvmInstruction instruction, Dictionary<LlvmValue, LlvmConstant?> pool)
        {
            if (instruction is LlvmSelectInstruction select)
            {
                LlvmConstant? condition = LookupSwitchConstant(select.Condition, pool);
                if (condition is LlvmConstantInt { Value.IsAllOnes: true }) return LookupSwitchConstant(select.TrueValue, pool);
                if (condition is { IsNullValue: true }) return LookupSwitchConstant(select.FalseValue, pool);
                return null;
            }
            List<LlvmConstant> operands = [];
            foreach (LlvmValue? operand in instruction.Operands)
            {
                if (LookupSwitchConstant(operand!, pool) is not { } constant) return null;
                operands.Add(constant);
            }
            return LlvmConstantFolding.ConstantFoldInstOperands(instruction, operands, Layout, metrics: Metrics);
        }

        private bool GetSwitchCaseResults(LlvmSwitchInstruction selection, LlvmConstantInt? caseValue,
            LlvmBasicBlock caseDestination, ref LlvmBasicBlock? commonDestination,
            List<(LlvmPhiInstruction Phi, LlvmConstant Result)> results)
        {
            Count("switch.case_results.queries");
            LlvmBasicBlock predecessor = selection.Parent!;
            Dictionary<LlvmValue, LlvmConstant?> pool = new() { [selection.Condition] = caseValue };
            foreach (LlvmInstruction instruction in caseDestination.Instructions)
            {
                Count("switch.case_results.instructions");
                if (instruction.IsTerminator)
                {
                    if (instruction.SuccessorCount != 1 || instruction.Opcode is (LlvmOpcode.CatchSwitch or LlvmOpcode.CatchRet or LlvmOpcode.CleanupRet or LlvmOpcode.Invoke or LlvmOpcode.Resume or LlvmOpcode.CallBr)) return RetainSwitch("case_results.non_single_or_special_terminator");
                    predecessor = caseDestination;
                    caseDestination = instruction.GetSuccessor(0);
                }
                else if (FoldSwitchInstruction(instruction, pool) is { } folded)
                {
                    foreach (LlvmUse use in instruction.Uses)
                    {
                        if (use.User is LlvmInstruction user && user.Parent == caseDestination) continue;
                        if (use.User is LlvmPhiInstruction phi && phi.GetIncomingBlock(use.OperandNo) == caseDestination) continue;
                        Count("switch.case_results.external_use"); return false;
                    }
                    pool.Add(instruction, folded);
                }
                else break;
            }
            commonDestination ??= caseDestination;
            if (caseDestination != commonDestination) return RetainSwitch("case_results.no_common_destination");
            foreach (LlvmPhiInstruction phi in SwitchPhis(commonDestination))
            {
                int index = phi.GetBasicBlockIndex(predecessor);
                if (index == -1) continue;
                if (LookupSwitchConstant(phi.GetIncomingValue(index), pool) is not { } value) return RetainSwitch("case_results.nonconstant_phi_input");
                results.Add((phi, value));
            }
            return results.Count != 0;
        }

        private bool InitializeSwitchUniqueCases(LlvmSwitchInstruction selection, out LlvmPhiInstruction? phi,
            out LlvmBasicBlock? commonDestination, List<SwitchResultCases> uniqueResults, out LlvmConstant? defaultResult, int maximumResults)
        {
            phi = null; commonDestination = null; defaultResult = null;
            for (int i = 0; i < selection.CaseCount; ++i)
            {
                List<(LlvmPhiInstruction Phi, LlvmConstant Result)> results = [];
                LlvmConstantInt value = selection.GetCaseValue(i);
                if (!GetSwitchCaseResults(selection, value, selection.GetCaseDestination(i), ref commonDestination, results) || results.Count > 1) return false;
                SwitchResultCases? group = uniqueResults.FirstOrDefault(result => result.Result == results[0].Result);
                if (group is null) { group = new(results[0].Result, []); uniqueResults.Add(group); }
                group.Cases.Add(value);
                if (group.Cases.Count > Tuning.MaxSwitchCasesPerResult || uniqueResults.Count > maximumResults) return RetainSwitch("select.result_or_case_limit");
                if (phi is null) phi = results[0].Phi;
                else if (phi != results[0].Phi) return RetainSwitch("select.inconsistent_phi");
            }
            List<(LlvmPhiInstruction Phi, LlvmConstant Result)> defaults = [];
            GetSwitchCaseResults(selection, null, selection.DefaultDestination, ref commonDestination, defaults);
            defaultResult = defaults.Count == 1 ? defaults[0].Result : null;
            return defaultResult is not null || selection.DefaultDestUnreachable;
        }

        private LlvmValue? FoldSwitchToSelect(List<SwitchResultCases> results, LlvmConstant? defaultResult,
            LlvmValue condition, LlvmTransformBuilder builder)
        {
            if (results.Count == 2 && results[0].Cases.Count == 1 && results[1].Cases.Count == 1)
            {
                LlvmValue selected = results[1].Result;
                if (defaultResult is not null)
                    selected = builder.CreateSelect(builder.CreateICmp(LlvmCmpPredicate.ICmpEq, condition, results[1].Cases[0], "switch.selectcmp"),
                        results[1].Result, defaultResult, "switch.select");
                Count("switch.select.two_results");
                return builder.CreateSelect(builder.CreateICmp(LlvmCmpPredicate.ICmpEq, condition, results[0].Cases[0], "switch.selectcmp"),
                    results[0].Result, selected, "switch.select");
            }
            if (results.Count != 1 || defaultResult is null) return null;
            List<LlvmConstantInt> cases = results[0].Cases;
            int count = cases.Count;
            if (BitOperations.IsPow2((uint)count))
            {
                LlvmConstantInt min = cases[0];
                LlvmAPInt andMask = LlvmAPInt.GetAllOnes(min.Value.BitWidth);
                foreach (LlvmConstantInt value in cases)
                {
                    if (value.Value.Slt(min.Value)) min = value;
                    andMask &= value.Value;
                }
                LlvmKnownBits known = LlvmKnownBitsAnalysis.Compute(condition, metrics: Metrics);
                if (!andMask.IsZero && known.GetMaxValue().Uge(andMask) &&
                    known.CountMaxActiveBits() - andMask.PopCount() == (uint)BitOperations.Log2((uint)count))
                {
                    LlvmValue masked = builder.CreateBinOp(LlvmOpcode.And, condition, LlvmConstantInt.Get(condition.Context, andMask));
                    LlvmValue compare = builder.CreateICmp(LlvmCmpPredicate.ICmpEq, masked, LlvmConstantInt.Get(condition.Context, andMask));
                    Count("switch.select.known_and_mask");
                    return builder.CreateSelect(compare, results[0].Result, defaultResult);
                }
                LlvmAPInt bitMask = LlvmAPInt.GetZero(min.Value.BitWidth);
                foreach (LlvmConstantInt value in cases) bitMask |= value.Value - min.Value;
                if (bitMask.PopCount() == (uint)BitOperations.Log2((uint)count))
                {
                    if (!min.IsZero) condition = builder.CreateBinOp(LlvmOpcode.Sub, condition, min);
                    LlvmValue masked = builder.CreateBinOp(LlvmOpcode.And, condition, LlvmConstantInt.Get(condition.Context, ~bitMask), "switch.and");
                    LlvmValue compare = builder.CreateICmp(LlvmCmpPredicate.ICmpEq, masked, LlvmConstantInt.Get(condition.Context, LlvmAPInt.GetZero(bitMask.BitWidth)), "switch.selectcmp");
                    Count("switch.select.touched_bit_mask");
                    return builder.CreateSelect(compare, results[0].Result, defaultResult);
                }
            }
            if (cases.Count != 2) return null;
            LlvmValue cmp1 = builder.CreateICmp(LlvmCmpPredicate.ICmpEq, condition, cases[0], "switch.selectcmp.case1");
            LlvmValue cmp2 = builder.CreateICmp(LlvmCmpPredicate.ICmpEq, condition, cases[1], "switch.selectcmp.case2");
            LlvmValue either = builder.CreateBinOp(LlvmOpcode.Or, cmp1, cmp2, "switch.selectcmp");
            Count("switch.select.two_shared_cases");
            return builder.CreateSelect(either, results[0].Result, defaultResult);
        }

        private bool TrySwitchToSelect(LlvmSwitchInstruction selection, LlvmTransformBuilder builder)
        {
            Count("switch.select.queries");
            List<SwitchResultCases> results = [];
            if (!InitializeSwitchUniqueCases(selection, out LlvmPhiInstruction? phi, out _, results, out LlvmConstant? defaultResult, 2)) return false;
            builder.SetInsertPoint(selection);
            LlvmValue? selected = FoldSwitchToSelect(results, defaultResult, selection.Condition, builder);
            if (selected is null) return RetainSwitch("select.no_matching_representation");
            LlvmBasicBlock block = selection.Parent!, destination = phi!.Parent!;
            List<LlvmCfgUpdate> updates = [];
            if (!LlvmDominatorTree.Predecessors(destination).Contains(block)) updates.Add(new(LlvmCfgUpdateKind.Insert, block, destination));
            builder.CreateBr(destination);
            for (int i = phi.IncomingCount - 1; i >= 0; --i)
                if (phi.GetIncomingBlock(i) == block) phi.RemoveIncomingValue(i, false);
            phi.AddIncoming(selected, block);
            HashSet<LlvmBasicBlock> removed = [];
            foreach (LlvmBasicBlock successor in selection.Successors.ToArray())
            {
                if (successor == destination) continue;
                RemoveSwitchPredecessor(successor, block);
                if (removed.Add(successor)) updates.Add(new(LlvmCfgUpdateKind.Delete, block, successor));
            }
            selection.EraseFromParent();
            DominatorUpdater?.ApplyUpdates(updates);
            Count("switch.select.converted");
            return true;
        }
    }
}
