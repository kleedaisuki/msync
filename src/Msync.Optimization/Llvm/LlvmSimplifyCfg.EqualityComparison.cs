// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// value-equality case decoding and predecessor pruning/folding.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    private readonly record struct ValueEqualityComparisonCase(LlvmConstantInt Value, LlvmBasicBlock Dest);

    internal sealed partial class Work
    {
        private LlvmValue? IsValueEqualityComparison(LlvmInstruction terminator)
        {
            LlvmValue? value = null;
            if (terminator is LlvmSwitchInstruction selection) value = selection.Condition;
            else if (terminator is LlvmCondBrInstruction { Condition.HasOneUse: true } branch)
            {
                if (branch.Condition is LlvmICmpInstruction comparison &&
                    comparison.Predicate is LlvmCmpPredicate.ICmpEq or LlvmCmpPredicate.ICmpNe &&
                    GetComparisonConstantInt(comparison.Right, Layout) is not null)
                    value = comparison.Left;
                else if (branch.Condition is LlvmCastInstruction { Opcode: LlvmOpcode.Trunc, HasNoUnsignedWrap: true } trunc)
                    value = trunc.Operand;
            }
            if (value is LlvmCastInstruction { Opcode: LlvmOpcode.PtrToInt } ptrToInt &&
                ptrToInt.Type == Layout.GetIntPtrType(ptrToInt.Context, ((LlvmPointerType)ptrToInt.Operand.Type).AddressSpace))
                value = ptrToInt.Operand;
            return value;
        }

        private LlvmBasicBlock GetValueEqualityComparisonCases(LlvmInstruction terminator,
            List<ValueEqualityComparisonCase> cases)
        {
            if (terminator is LlvmSwitchInstruction selection)
            {
                for (int i = 0; i < selection.CaseCount; ++i)
                    cases.Add(new(selection.GetCaseValue(i), selection.GetCaseDestination(i)));
                return selection.DefaultDestination;
            }
            var branch = (LlvmCondBrInstruction)terminator;
            LlvmCmpPredicate predicate;
            LlvmConstantInt constant;
            if (branch.Condition is LlvmICmpInstruction comparison)
            { predicate = comparison.Predicate; constant = GetComparisonConstantInt(comparison.Right, Layout)!; }
            else
            {
                predicate = LlvmCmpPredicate.ICmpNe;
                var trunc = (LlvmCastInstruction)branch.Condition;
                constant = LlvmConstantInt.Get((LlvmIntegerType)trunc.Operand.Type, 0);
            }
            cases.Add(new(constant, branch.GetSuccessor(predicate == LlvmCmpPredicate.ICmpNe ? 1 : 0)));
            return branch.GetSuccessor(predicate == LlvmCmpPredicate.ICmpEq ? 1 : 0);
        }

        private static bool EqualityValuesOverlap(List<ValueEqualityComparisonCase> first,
            List<ValueEqualityComparisonCase> second)
        {
            if (first.Count > second.Count) (first, second) = (second, first);
            if (first.Count == 0) return false;
            if (first.Count == 1)
                foreach (var item in second)
                    if (first[0].Value == item.Value) return true;
            // C++ orders uniqued pointers. APInt ordering is an equivalent total
            // order on this same-width, context-uniqued constant domain.
            first.Sort(CompareEqualityCases);
            second.Sort(CompareEqualityCases);
            int left = 0, right = 0;
            while (left != first.Count && right != second.Count)
            {
                if (first[left].Value == second[right].Value) return true;
                if (first[left].Value.Value.Ult(second[right].Value.Value)) ++left;
                else ++right;
            }
            return false;
        }
        private static int CompareEqualityCases(ValueEqualityComparisonCase left, ValueEqualityComparisonCase right)
            => left.Value == right.Value ? 0 : left.Value.Value.Ult(right.Value.Value) ? -1 : 1;

        private bool SimplifyEqualityComparisonWithOnlyPredecessor(LlvmInstruction terminator,
            LlvmBasicBlock predecessor, LlvmTransformBuilder builder)
        {
            Count("equality.single_predecessor.queries");
            LlvmValue? priorValue = IsValueEqualityComparison(predecessor.GetTerminator());
            if (priorValue is null || IsValueEqualityComparison(terminator) != priorValue) return false;
            List<ValueEqualityComparisonCase> priorCases = [], cases = [];
            LlvmBasicBlock priorDefault = GetValueEqualityComparisonCases(predecessor.GetTerminator(), priorCases);
            LlvmBasicBlock defaultBlock = GetValueEqualityComparisonCases(terminator, cases);
            priorCases.RemoveAll(item => item.Dest == priorDefault);
            cases.RemoveAll(item => item.Dest == defaultBlock);
            LlvmBasicBlock block = terminator.Parent!;
            if (priorDefault == block)
            {
                if (!EqualityValuesOverlap(priorCases, cases)) return false;
                if (terminator is LlvmCondBrInstruction)
                {
                    builder.CreateBr(defaultBlock);
                    LlvmBasicBlockUtils.RemovePredecessor(cases[0].Dest, block);
                    EraseTerminatorAndDceCondition(terminator);
                    DominatorUpdater?.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, block, cases[0].Dest)]);
                    Count("equality.single_predecessor.branch");
                    return true;
                }
                var selection = (LlvmSwitchInstruction)terminator;
                HashSet<LlvmConstantInt> deadCases = priorCases.Select(item => item.Value).ToHashSet();
                Dictionary<LlvmBasicBlock, int> successorCounts = [];
                for (int i = selection.CaseCount - 1; i >= 0; --i)
                {
                    LlvmBasicBlock successor = selection.GetCaseDestination(i);
                    successorCounts[successor] = successorCounts.GetValueOrDefault(successor) + 1;
                    if (!deadCases.Contains(selection.GetCaseValue(i))) continue;
                    LlvmBasicBlockUtils.RemovePredecessor(successor, priorDefault);
                    selection.RemoveCase(i);
                    --successorCounts[successor];
                    Count("equality.single_predecessor.switch_cases_removed");
                }
                DominatorUpdater?.ApplyUpdates(successorCounts.Where(item => item.Value == 0)
                    .Select(item => new LlvmCfgUpdate(LlvmCfgUpdateKind.Delete, priorDefault, item.Key)).ToArray());
                return true;
            }
            LlvmConstantInt? incomingValue = null;
            foreach (var item in priorCases)
            {
                if (item.Dest != block) continue;
                if (incomingValue is not null) return false;
                incomingValue = item.Value;
            }
            Debug.Assert(incomingValue is not null);
            LlvmBasicBlock realDestination = cases.FirstOrDefault(item => item.Value == incomingValue).Dest ?? defaultBlock;
            HashSet<LlvmBasicBlock> removedSuccessors = [];
            LlvmBasicBlock? retainedEdge = realDestination;
            foreach (LlvmBasicBlock successor in terminator.Successors)
            {
                if (successor == retainedEdge) { retainedEdge = null; continue; }
                if (successor != realDestination) removedSuccessors.Add(successor);
                LlvmBasicBlockUtils.RemovePredecessor(successor, block);
            }
            builder.CreateBr(realDestination);
            EraseTerminatorAndDceCondition(terminator);
            DominatorUpdater?.ApplyUpdates(removedSuccessors.Select(successor =>
                new LlvmCfgUpdate(LlvmCfgUpdateKind.Delete, block, successor)).ToArray());
            Count("equality.single_predecessor.constant_destination");
            return true;
        }

        private bool PerformValueComparisonIntoPredecessorFolding(LlvmInstruction terminator,
            ref LlvmValue compared, LlvmInstruction predecessorTerminator, LlvmTransformBuilder builder)
        {
            LlvmBasicBlock block = terminator.Parent!, predecessor = predecessorTerminator.Parent!;
            List<LlvmCfgUpdate> updates = [];
            List<ValueEqualityComparisonCase> cases = [], priorCases = [];
            LlvmBasicBlock defaultBlock = GetValueEqualityComparisonCases(terminator, cases);
            LlvmBasicBlock priorDefault = GetValueEqualityComparisonCases(predecessorTerminator, priorCases);
            Dictionary<LlvmBasicBlock, int> newSuccessors = [];
            void AddSuccessor(LlvmBasicBlock destination)
                => newSuccessors[destination] = newSuccessors.GetValueOrDefault(destination) + 1;
            if (priorDefault == block)
            {
                HashSet<LlvmConstantInt> handled = [];
                for (int i = 0; i < priorCases.Count; ++i)
                {
                    if (priorCases[i].Dest != block) { handled.Add(priorCases[i].Value); continue; }
                    priorCases[i] = priorCases[^1]; priorCases.RemoveAt(priorCases.Count - 1); --i;
                }
                if (priorDefault != defaultBlock)
                {
                    LlvmBasicBlockUtils.RemovePredecessor(priorDefault, predecessor);
                    if (priorDefault != block) updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, priorDefault));
                    priorDefault = defaultBlock; AddSuccessor(defaultBlock);
                }
                foreach (var item in cases)
                {
                    if (handled.Contains(item.Value) || item.Dest == defaultBlock) continue;
                    priorCases.Add(item); AddSuccessor(item.Dest);
                }
            }
            else
            {
                HashSet<LlvmConstantInt> handled = [];
                for (int i = 0; i < priorCases.Count; ++i)
                {
                    if (priorCases[i].Dest != block) continue;
                    handled.Add(priorCases[i].Value);
                    priorCases[i] = priorCases[^1]; priorCases.RemoveAt(priorCases.Count - 1); --i;
                }
                foreach (var item in cases)
                {
                    if (!handled.Remove(item.Value)) continue;
                    priorCases.Add(item); AddSuccessor(item.Dest);
                }
                foreach (LlvmConstantInt value in handled.OrderBy(value => value.Value, EqualityAPIntComparer.Instance))
                { priorCases.Add(new(value, defaultBlock)); AddSuccessor(defaultBlock); }
            }
            HashSet<LlvmBasicBlock> priorSuccessors = predecessorTerminator.Successors.ToHashSet();
            foreach (var item in newSuccessors)
            {
                for (int i = 0; i < item.Value; ++i) AddPredecessorToBlock(item.Key, predecessor, block);
                if (!priorSuccessors.Contains(item.Key)) updates.Add(new(LlvmCfgUpdateKind.Insert, predecessor, item.Key));
            }
            builder.SetInsertPoint(predecessorTerminator);
            if (compared.Type.IsPointerTy())
                compared = builder.CreatePtrToInt(compared,
                    Layout.GetIntPtrType(compared.Context, ((LlvmPointerType)compared.Type).AddressSpace), "magicptr");
            LlvmSwitchInstruction merged = builder.CreateSwitch(compared, priorDefault, priorCases.Count);
            merged.Source = predecessorTerminator.Source;
            foreach (var item in priorCases) merged.AddCase(item.Value, item.Dest);
            EraseTerminatorAndDceCondition(predecessorTerminator);
            LlvmBasicBlock? infiniteLoop = null;
            for (int i = 0; i < merged.SuccessorCount; ++i)
            {
                if (merged.GetSuccessor(i) != block) continue;
                if (infiniteLoop is null)
                {
                    infiniteLoop = new LlvmBasicBlock(block.Context, "infloop", block.Parent);
                    infiniteLoop.Append(new LlvmUncondBrInstruction(infiniteLoop) { Source = terminator.Source });
                    updates.Add(new(LlvmCfgUpdateKind.Insert, infiniteLoop, infiniteLoop));
                }
                merged.SetSuccessor(i, infiniteLoop);
            }
            if (infiniteLoop is not null) updates.Add(new(LlvmCfgUpdateKind.Insert, predecessor, infiniteLoop));
            updates.Add(new(LlvmCfgUpdateKind.Delete, predecessor, block));
            DominatorUpdater?.ApplyUpdates(updates);
            Count("equality.fold_into_predecessor");
            return true;
        }

        private bool FoldValueComparisonIntoPredecessors(LlvmInstruction terminator, LlvmTransformBuilder builder)
        {
            Count("equality.fold_predecessors.queries");
            LlvmBasicBlock block = terminator.Parent!;
            LlvmValue compared = IsValueEqualityComparison(terminator)!;
            List<LlvmBasicBlock> predecessors = LlvmDominatorTree.Predecessors(block).Distinct().ToList();
            if (LlvmDominatorTree.Predecessors(block).Take(2).Count() != 1 &&
                LlvmDominatorTree.Predecessors(block).Take(128 / terminator.SuccessorCount).Count() >= 128 / terminator.SuccessorCount)
                return false;
            bool changed = false;
            while (predecessors.Count != 0)
            {
                LlvmBasicBlock predecessor = predecessors[^1]; predecessors.RemoveAt(predecessors.Count - 1);
                LlvmInstruction predecessorTerminator = predecessor.GetTerminator();
                if (predecessor == block || IsValueEqualityComparison(predecessorTerminator) != compared) continue;
                List<LlvmBasicBlock> failBlocks = [];
                if (!SafeToMergeTerminators(terminator, predecessorTerminator, failBlocks))
                    foreach (LlvmBasicBlock successor in failBlocks)
                        if (LlvmBasicBlockUtils.SplitBlockPredecessors(successor, [block], ".fold.split", DominatorUpdater) is null)
                            return false;
                PerformValueComparisonIntoPredecessorFolding(terminator, ref compared, predecessorTerminator, builder);
                changed = true;
            }
            return changed;
        }
    }

    private sealed class EqualityAPIntComparer : IComparer<LlvmAPInt>
    {
        internal static readonly EqualityAPIntComparer Instance = new();
        public int Compare(LlvmAPInt left, LlvmAPInt right) => left == right ? 0 : left.Ult(right) ? -1 : 1;
    }
}
