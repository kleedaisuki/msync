// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// findContiguousCases, turnSwitchRangeIntoICmp, eliminateDeadSwitchCases,
// forwardSwitchConditionToPHI; ValueTracking.cpp collectPossibleValues.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private readonly record struct ContiguousSwitchCases(LlvmConstantInt Min, LlvmConstantInt Max,
            LlvmBasicBlock Destination, LlvmBasicBlock OtherDestination, List<LlvmConstantInt> Cases, List<LlvmConstantInt> OtherCases);

        private ContiguousSwitchCases? FindContiguousCases(LlvmValue condition, List<LlvmConstantInt> cases,
            List<LlvmConstantInt> otherCases, LlvmBasicBlock destination, LlvmBasicBlock otherDestination)
        {
            cases.Sort(static (left, right) => left.Value.Compare(right.Value) * -1);
            LlvmConstantInt min = cases[^1], max = cases[0];
            LlvmAPInt span = max.Value - min.Value;
            if (span == new LlvmAPInt(span.BitWidth, (ulong)cases.Count - 1, implicitTrunc: true))
                return new(min, max, destination, otherDestination, cases, otherCases);
            LlvmConstantRange range = LlvmConstantRangeAnalysis.Compute(condition, metrics: Metrics);
            if (max.Value != range.GetUnsignedMax() || min.Value != range.GetUnsignedMin()) return null;
            int gap = 0;
            for (; gap + 1 < cases.Count; ++gap)
                if (cases[gap].Value != cases[gap + 1].Value + new LlvmAPInt(span.BitWidth, 1)) break;
            if (gap + 1 == cases.Count) return null;
            LlvmAPInt otherMax = cases[gap].Value, otherMin = cases[gap + 1].Value;
            if ((max.Value - otherMax) + (otherMin - min.Value) != new LlvmAPInt(span.BitWidth, (ulong)cases.Count - 2, implicitTrunc: true)) return null;
            Count("switch.range.wrapping_complement");
            return new(LlvmConstantInt.Get(condition.Context, otherMin + new LlvmAPInt(span.BitWidth, 1)),
                LlvmConstantInt.Get(condition.Context, otherMax - new LlvmAPInt(span.BitWidth, 1)),
                otherDestination, destination, otherCases, cases);
        }

        private bool TurnSwitchRangeIntoICmp(LlvmSwitchInstruction selection, LlvmTransformBuilder builder)
        {
            Count("switch.range.queries");
            Debug.Assert(selection.CaseCount > 1);
            bool hasDefault = !selection.DefaultDestUnreachable;
            LlvmBasicBlock block = selection.Parent!;
            LlvmBasicBlock? destA = hasDefault ? selection.DefaultDestination : null, destB = null;
            List<LlvmConstantInt> casesA = [], casesB = [];
            for (int i = 0; i < selection.CaseCount; ++i)
            {
                LlvmBasicBlock destination = selection.GetCaseDestination(i);
                destA ??= destination;
                if (destination == destA) { casesA.Add(selection.GetCaseValue(i)); continue; }
                destB ??= destination;
                if (destination != destB) { Count("switch.range.more_than_two_destinations"); return false; }
                casesB.Add(selection.GetCaseValue(i));
            }
            if (destB is null) return RetainSwitch("range.single_destination");
            ContiguousSwitchCases? result = null;
            if (!hasDefault && casesA.Count == 1) result = new(casesA[0], casesA[0], destA!, destB, casesA, casesB);
            else if (casesB.Count == 1) result = new(casesB[0], casesB[0], destB, destA!, casesB, casesA);
            else if (!hasDefault) result = FindContiguousCases(selection.Condition, casesA, casesB, destA!, destB);
            result ??= FindContiguousCases(selection.Condition, casesB, casesA, destB, destA!);
            if (result is not { } contiguous) return RetainSwitch("range.noncontiguous");
            LlvmAPInt offset = -contiguous.Min.Value;
            LlvmAPInt count = contiguous.Max.Value - contiguous.Min.Value + new LlvmAPInt(offset.BitWidth, 1);
            LlvmInstruction branch;
            if (count.IsOne)
                branch = builder.CreateCondBr(builder.CreateICmp(LlvmCmpPredicate.ICmpEq, selection.Condition, contiguous.Min),
                    contiguous.Destination, contiguous.OtherDestination);
            else if (count.IsZero && contiguous.Cases.Count != 0) branch = builder.CreateBr(contiguous.Destination);
            else
            {
                LlvmValue index = selection.Condition;
                if (!offset.IsZero) index = builder.CreateBinOp(LlvmOpcode.Add, index, LlvmConstantInt.Get(index.Context, offset), index.Name + ".off");
                LlvmValue condition = builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, index, LlvmConstantInt.Get(index.Context, count), "switch");
                branch = builder.CreateCondBr(condition, contiguous.Destination, contiguous.OtherDestination);
            }
            // Branch-weight metadata is absent in the represented IR vocabulary.
            foreach (LlvmPhiInstruction phi in SwitchPhis(contiguous.Destination).ToArray())
            {
                int prior = contiguous.Cases.Count + (contiguous.Destination == selection.DefaultDestination ? 1 : 0);
                for (int i = 0; i < prior - 1; ++i) phi.RemoveIncomingValue(phi.GetBasicBlockIndex(block));
            }
            foreach (LlvmPhiInstruction phi in SwitchPhis(contiguous.OtherDestination).ToArray())
            {
                int prior = contiguous.OtherCases.Count + (contiguous.OtherDestination == selection.DefaultDestination ? 1 : 0);
                int remove = prior - 1 + (branch is LlvmUncondBrInstruction ? 1 : 0);
                for (int i = 0; i < remove; ++i) phi.RemoveIncomingValue(phi.GetBasicBlockIndex(block));
            }
            List<LlvmCfgUpdate> updates = [];
            if (!hasDefault)
            {
                LlvmBasicBlock oldDefault = selection.DefaultDestination;
                RemoveSwitchPredecessor(oldDefault, block);
                updates.Add(new(LlvmCfgUpdateKind.Delete, block, oldDefault));
            }
            selection.EraseFromParent();
            if (branch is LlvmUncondBrInstruction) updates.Add(new(LlvmCfgUpdateKind.Delete, block, contiguous.OtherDestination));
            DominatorUpdater?.ApplyUpdates(updates);
            Count("switch.range.converted");
            return true;
        }

        private void CreateUnreachableSwitchDefault(LlvmSwitchInstruction selection, bool removeOriginalDefault = true)
        {
            LlvmBasicBlock block = selection.Parent!, original = selection.DefaultDestination;
            if (removeOriginalDefault) RemoveSwitchPredecessor(original, block);
            LlvmBasicBlock unreachable = new(block.Context, block.Name + ".unreachabledefault", block.Parent, original);
            unreachable.Append(new LlvmUnreachableInstruction(block.Context));
            selection.DefaultDestination = unreachable;
            List<LlvmCfgUpdate> updates = [new(LlvmCfgUpdateKind.Insert, block, unreachable)];
            if (removeOriginalDefault && !LlvmDominatorTree.Successors(block).Contains(original)) updates.Add(new(LlvmCfgUpdateKind.Delete, block, original));
            DominatorUpdater?.ApplyUpdates(updates);
            Count("switch.default.made_unreachable");
        }

        private bool EliminateDeadSwitchCases(LlvmSwitchInstruction selection)
        {
            Count("switch.dead_cases.queries");
            LlvmKnownBits known = LlvmKnownBitsAnalysis.Compute(selection.Condition, context: selection, metrics: Metrics, assumptions: Options.AC);
            HashSet<LlvmConstant> possible = [];
            bool havePossible = CollectSwitchPossibleValues(selection.Condition, possible, 4);
            uint significant = selection.Condition.Type.GetIntegerBitWidth() -
                LlvmKnownBitsAnalysis.ComputeNumSignBits(selection.Condition, context: selection, metrics: Metrics, assumptions: Options.AC) + 1;
            List<LlvmConstantInt> dead = [];
            Dictionary<LlvmBasicBlock, int> successorCases = [];
            List<LlvmBasicBlock> successors = [];
            for (int i = 0; i < selection.CaseCount; ++i)
            {
                Count("switch.dead_cases.visited");
                LlvmBasicBlock successor = selection.GetCaseDestination(i);
                if (!successorCases.ContainsKey(successor)) { successorCases.Add(successor, 0); successors.Add(successor); }
                ++successorCases[successor];
                LlvmConstantInt constant = selection.GetCaseValue(i);
                LlvmAPInt value = constant.Value;
                if (!(known.Zero & value).IsZero || !(known.One & ~value).IsZero || value.GetSignificantBits() > significant ||
                    havePossible && !possible.Contains(constant))
                { dead.Add(constant); --successorCases[successor]; }
                else if (havePossible) possible.Remove(constant);
            }
            uint unknown = known.BitWidth - (known.Zero | known.One).PopCount();
            if (!selection.DefaultDestUnreachable && dead.Count == 0)
            {
                if (havePossible && possible.All(value => value is LlvmUndefValue)) { CreateUnreachableSwitchDefault(selection); return true; }
                if (unknown < 64)
                {
                    ulong allCases = 1UL << (int)unknown;
                    if ((ulong)selection.CaseCount == allCases) { CreateUnreachableSwitchDefault(selection); return true; }
                    if ((ulong)selection.CaseCount == allCases - 1)
                    {
                        uint width = selection.Condition.Type.GetIntegerBitWidth();
                        if (width > 64 || !Layout.FitsInLegalInteger(width)) return false;
                        ulong missing = 0;
                        for (int i = 0; i < selection.CaseCount; ++i) missing ^= selection.GetCaseValue(i).Value.GetLimitedValue();
                        selection.AddCase(LlvmConstantInt.Get((LlvmIntegerType)selection.Condition.Type, missing), selection.DefaultDestination);
                        CreateUnreachableSwitchDefault(selection, false);
                        Count("switch.dead_cases.missing_case_added");
                        return true;
                    }
                }
            }
            if (dead.Count == 0) return false;
            foreach (LlvmConstantInt constant in dead)
            {
                int index = selection.FindCaseValue(constant);
                RemoveSwitchPredecessor(selection.GetCaseDestination(index), selection.Parent!);
                selection.RemoveCase(index);
                Count("switch.dead_cases.removed");
            }
            DominatorUpdater?.ApplyUpdates(successors.Where(successor => successorCases[successor] == 0)
                .Select(successor => new LlvmCfgUpdate(LlvmCfgUpdateKind.Delete, selection.Parent!, successor)).ToArray());
            return true;
        }

        private bool CollectSwitchPossibleValues(LlvmValue value, HashSet<LlvmConstant> constants, uint maximum)
        {
            HashSet<LlvmInstruction> visited = [];
            Stack<LlvmInstruction> work = [];
            bool Push(LlvmValue candidate)
            {
                if (candidate is LlvmConstant constant && constant is not LlvmConstantExpr)
                {
                    if (constants.Contains(constant)) return true;
                    if (constants.Count == maximum) return false;
                    constants.Add(constant); return true;
                }
                if (candidate is not LlvmInstruction instruction) return false;
                if (visited.Add(instruction)) work.Push(instruction);
                return true;
            }
            if (!Push(value)) return false;
            while (work.TryPop(out LlvmInstruction? instruction))
            {
                Count("switch.possible_values.instructions");
                if (instruction is LlvmSelectInstruction select)
                { if (!Push(select.TrueValue) || !Push(select.FalseValue)) return false; }
                else if (instruction is LlvmPhiInstruction phi)
                {
                    foreach (LlvmValue? incoming in phi.Operands)
                        if (incoming != phi && !Push(incoming!)) return false;
                }
                else return false;
            }
            return true;
        }

        private bool ForwardSwitchConditionToPHI(LlvmSwitchInstruction selection)
        {
            Dictionary<LlvmPhiInstruction, List<int>> forwarding = [];
            LlvmBasicBlock block = selection.Parent!;
            bool changed = false;
            for (int i = 0; i < selection.CaseCount; ++i)
            {
                LlvmConstantInt value = selection.GetCaseValue(i);
                LlvmBasicBlock destination = selection.GetCaseDestination(i);
                foreach (LlvmPhiInstruction phi in SwitchPhis(destination))
                {
                    int index = phi.GetBasicBlockIndex(block);
                    if (phi.GetIncomingValue(index) != value || Enumerable.Range(0, phi.IncomingCount).Count(n => phi.GetIncomingBlock(n) == block) != 1) continue;
                    phi.SetIncomingValue(index, selection.Condition); changed = true;
                    Count("switch.forward_phi.direct");
                }
                if (FirstNonPhi(destination) != destination.GetTerminator() ||
                    LlvmDominatorTree.Predecessors(destination).Take(2).Count() != 1 ||
                    destination.GetTerminator() is not LlvmUncondBrInstruction branch) continue;
                foreach (LlvmPhiInstruction phi in SwitchPhis(branch.Destination))
                {
                    int index = phi.GetBasicBlockIndex(destination);
                    if (phi.GetIncomingValue(index) != value) continue;
                    if (!forwarding.TryGetValue(phi, out List<int>? indices)) forwarding.Add(phi, indices = []);
                    indices.Add(index); break;
                }
            }
            foreach ((LlvmPhiInstruction phi, List<int> indices) in forwarding)
            {
                if (indices.Count < 2 && !phi.Operands.Contains(selection.Condition)) continue;
                foreach (int index in indices) phi.SetIncomingValue(index, selection.Condition);
                changed = true; Count("switch.forward_phi.indirect", (ulong)indices.Count);
            }
            return changed;
        }
    }
}
