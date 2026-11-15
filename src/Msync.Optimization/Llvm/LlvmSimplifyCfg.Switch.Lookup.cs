// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// simplifySwitchLookup, shouldUseSwitchConditionAsTableIndex, reuseTableCompare.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private bool ShouldUseSwitchConditionAsTableIndex(LlvmConstantInt min, LlvmConstantInt max,
            bool haveDefault, IReadOnlyList<LlvmType> resultTypes)
        {
            if (min.IsZero) return true;
            if (min.Value.IsNegative || max.Value.GetLimitedValue() == ulong.MaxValue || !haveDefault) return false;
            return resultTypes.All(type => SwitchWouldFitInRegister(Layout, max.Value.GetLimitedValue() + 1, type));
        }

        private void ReuseSwitchTableCompare(LlvmUser user, LlvmBasicBlock phiBlock, LlvmCondBrInstruction rangeBranch,
            LlvmConstant defaultValue, IReadOnlyList<(LlvmConstantInt Case, LlvmConstant Result)> values)
        {
            if (user is not LlvmICmpInstruction compare || compare.Parent != phiBlock || compare.Right is not LlvmConstant right) return;
            LlvmValue rangeCondition = rangeBranch.Condition;
            LlvmConstantInt whenTrue = LlvmConstantInt.GetBool(rangeCondition.Context, true), whenFalse = LlvmConstantInt.GetBool(rangeCondition.Context, false);
            LlvmConstant? defaultResult = LlvmConstantFolding.ConstantFoldCompareInstOperands(compare.Predicate, defaultValue, right, Layout, metrics: Metrics);
            if (defaultResult != whenTrue && defaultResult != whenFalse) return;
            foreach ((_, LlvmConstant result) in values)
            {
                LlvmConstant? caseResult = LlvmConstantFolding.ConstantFoldCompareInstOperands(compare.Predicate, result, right, Layout, metrics: Metrics);
                if (caseResult is null || caseResult == defaultResult || caseResult != whenTrue && caseResult != whenFalse) return;
            }
            LlvmBasicBlock branchBlock = rangeBranch.Parent!;
            foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(phiBlock))
            {
                if (predecessor == branchBlock) continue;
                LlvmBasicBlock[] uniquePredecessors = LlvmDominatorTree.Predecessors(predecessor).Distinct().Take(2).ToArray();
                if (uniquePredecessors.Length != 1 || uniquePredecessors[0] != branchBlock) return;
            }
            if (defaultResult == whenFalse) compare.ReplaceAllUsesWith(rangeCondition);
            else
            {
                LlvmBinaryOperator inverse = new(LlvmOpcode.Xor, rangeCondition, LlvmConstantInt.Get((LlvmIntegerType)rangeCondition.Type, 1));
                inverse.SetName("inverted.cmp");
                rangeBranch.Parent!.InsertBefore(inverse, rangeBranch);
                compare.ReplaceAllUsesWith(inverse);
            }
            Count("switch.lookup.compare_reused");
        }

        private bool SimplifySwitchLookup(LlvmSwitchInstruction selection, LlvmTransformBuilder builder, bool allowLookupTable)
        {
            Count("switch.lookup.queries");
            if (selection.CaseCount < 3) return RetainSwitch("lookup.fewer_than_three_cases");
            LlvmBasicBlock block = selection.Parent!;
            LlvmFunction function = block.Parent!;
            LlvmConstantInt min = selection.GetCaseValue(0), max = min;
            LlvmBasicBlock? common = null;
            Dictionary<LlvmPhiInstruction, List<(LlvmConstantInt Case, LlvmConstant Result)>> resultLists = [];
            Dictionary<LlvmPhiInstruction, LlvmConstant> defaults = [];
            List<LlvmType> resultTypes = [];
            List<LlvmPhiInstruction> phis = [];
            for (int i = 0; i < selection.CaseCount; ++i)
            {
                Count("switch.lookup.cases");
                LlvmConstantInt value = selection.GetCaseValue(i);
                if (value.Value.Slt(min.Value)) min = value;
                if (value.Value.Sgt(max.Value)) max = value;
                List<(LlvmPhiInstruction Phi, LlvmConstant Result)> results = [];
                if (!GetSwitchCaseResults(selection, value, selection.GetCaseDestination(i), ref common, results)) return false;
                foreach ((LlvmPhiInstruction phi, LlvmConstant result) in results)
                {
                    if (!resultLists.TryGetValue(phi, out var list)) { resultLists.Add(phi, list = []); phis.Add(phi); }
                    list.Add((value, result)); resultTypes.Add(phi.Type);
                }
            }
            List<(LlvmPhiInstruction Phi, LlvmConstant Result)> defaultResults = [];
            bool haveDefaults = GetSwitchCaseResults(selection, null, selection.DefaultDestination, ref common, defaultResults);
            foreach ((LlvmPhiInstruction phi, LlvmConstant result) in defaultResults) defaults[phi] = result;
            bool directIndex = ShouldUseSwitchConditionAsTableIndex(min, max, haveDefaults, resultTypes);
            ulong tableSize = directIndex ? unchecked(max.Value.GetLimitedValue() + 1) : unchecked((max.Value - min.Value).GetLimitedValue() + 1);
            LlvmConstantInt indexOffset = directIndex ? LlvmConstantInt.Get((LlvmIntegerType)min.Type, 0) : min;
            ulong resultCount = (ulong)resultLists[phis[0]].Count;
            bool defaultReachable = !selection.DefaultDestUnreachable;
            bool holes = resultCount < tableSize;
            bool poisonHoles = holes && !haveDefaults;
            bool needMask = poisonHoles && defaultReachable;
            if (needMask && (selection.CaseCount < 4 || !Layout.FitsInLegalInteger((uint)tableSize)))
            { Count("switch.lookup.mask_not_profitable"); return false; }
            if (!ShouldBuildSwitchLookup(selection, tableSize, resultTypes))
            { Count("switch.lookup.cost_rejected"); return false; }
            LlvmValue tableIndex = selection.Condition;
            if (directIndex && haveDefaults)
            {
                LlvmConstantRange range = LlvmConstantRangeAnalysis.Compute(tableIndex, metrics: Metrics);
                ulong upper = range.Upper.GetLimitedValue();
                if (!range.IsUpperWrapped && resultTypes.All(type => SwitchWouldFitInRegister(Layout, upper, type)))
                {
                    tableSize = Math.Max(upper, tableSize);
                    defaultReachable = false;
                    Count("switch.lookup.table_enlarged_to_cover_range");
                }
            }
            Dictionary<LlvmPhiInstruction, SwitchReplacement> replacements = [];
            foreach (LlvmPhiInstruction phi in phis)
            {
                var list = resultLists[phi];
                LlvmConstant? defaultValue = poisonHoles ? LlvmPoisonValue.Get(list[0].Result.Type) : defaults.GetValueOrDefault(phi);
                SwitchReplacement replacement = new(this, tableSize, indexOffset, list, defaultValue);
                if (!replacement.IsValid) { Count("switch.lookup.invalid_replacement"); return false; }
                replacements.Add(phi, replacement);
            }
            bool anyLookup = replacements.Values.Any(replacement => replacement.IsLookupTable);
            bool anyBitmap = replacements.Values.Any(replacement => replacement.IsBitMap);
            if (anyLookup && (!TargetInfo.ShouldBuildLookupTables() || function.NoJumpTables)) return RetainSwitch("lookup.target_or_no_jump_tables_attribute");
            if (!allowLookupTable && (anyLookup || anyBitmap || needMask)) return RetainSwitch("lookup.option_disallows_table_bitmap_or_mask");
            builder.SetInsertPoint(selection);
            if (!directIndex)
            {
                bool mayWrap = true;
                if (!defaultReachable) max.Value.SSubOverflow(min.Value, out mayWrap);
                tableIndex = builder.CreateBinary(LlvmOpcode.Sub, selection.Condition, indexOffset, "switch.tableidx", nsw: !mayWrap);
            }
            List<LlvmCfgUpdate> updates = [];
            uint caseWidth = min.Type.GetIntegerBitWidth();
            ulong maximumTableSize = caseWidth > 63 ? ulong.MaxValue : 1UL << (int)caseWidth;
            LlvmBasicBlock lookup = new(block.Context, "switch.lookup", function, common);
            LlvmCondBrInstruction? rangeBranch = null, conditionalBranch = null;
            bool covered = maximumTableSize == tableSize;
            bool fullyCovered = !defaultReachable || covered;
            if (fullyCovered) builder.CreateBr(lookup);
            else
            {
                LlvmValue compare = builder.CreateICmp(LlvmCmpPredicate.ICmpUlt, tableIndex, LlvmConstantInt.Get((LlvmIntegerType)min.Type, tableSize));
                conditionalBranch = rangeBranch = builder.CreateCondBr(compare, lookup, selection.DefaultDestination);
            }
            updates.Add(new(LlvmCfgUpdateKind.Insert, block, lookup));
            builder.SetInsertPoint(lookup);
            if (needMask)
            {
                LlvmBasicBlock maskBlock = lookup;
                maskBlock.SetName("switch.hole_check");
                lookup = new(block.Context, "switch.lookup", function, common);
                ulong maskWidth = SwitchPowerOf2Ceil(Math.Max(7UL, tableSize - 1) + 1);
                LlvmAPInt mask = LlvmAPInt.GetZero((uint)maskWidth), one = new((uint)maskWidth, 1);
                foreach ((LlvmConstantInt caseValue, _) in resultLists[phis[0]])
                {
                    ulong index = (caseValue.Value - indexOffset.Value).GetLimitedValue();
                    mask |= one.Shl((uint)index);
                }
                LlvmConstantInt tableMask = LlvmConstantInt.Get(block.Context, mask);
                LlvmValue maskIndex = SwitchIntCast(tableIndex, (LlvmIntegerType)tableMask.Type, builder, "switch.maskindex");
                LlvmValue shifted = builder.CreateBinOp(LlvmOpcode.LShr, tableMask, maskIndex, "switch.shifted");
                LlvmValue lowBit = builder.CreateCast(LlvmOpcode.Trunc, shifted, LlvmType.GetInt1Ty(block.Context), "switch.lobit");
                conditionalBranch = builder.CreateCondBr(lowBit, lookup, selection.DefaultDestination);
                updates.Add(new(LlvmCfgUpdateKind.Insert, maskBlock, lookup));
                updates.Add(new(LlvmCfgUpdateKind.Insert, maskBlock, selection.DefaultDestination));
                builder.SetInsertPoint(lookup);
                AddSwitchPredecessor(selection.DefaultDestination, maskBlock, block);
            }
            if (!defaultReachable || covered)
            {
                RemoveSwitchPredecessor(selection.DefaultDestination, block, true);
                updates.Add(new(LlvmCfgUpdateKind.Delete, block, selection.DefaultDestination));
            }
            foreach (LlvmPhiInstruction phi in phis)
            {
                SwitchReplacement replacement = replacements[phi];
                LlvmValue result = replacement.ReplaceSwitch(tableIndex, builder, function);
                if (!holes && haveDefaults && rangeBranch is not null)
                    foreach (LlvmUser user in phi.Users) ReuseSwitchTableCompare(user, phi.Parent!, rangeBranch, replacement.DefaultValue!, resultLists[phi]);
                phi.AddIncoming(result, lookup);
            }
            builder.CreateBr(common!);
            updates.Add(new(LlvmCfgUpdateKind.Insert, lookup, common!));
            HashSet<LlvmBasicBlock> removed = [];
            foreach (LlvmBasicBlock successor in selection.Successors.ToArray())
            {
                if (successor == selection.DefaultDestination) continue;
                RemoveSwitchPredecessor(successor, block);
                if (removed.Add(successor)) updates.Add(new(LlvmCfgUpdateKind.Delete, block, successor));
            }
            selection.EraseFromParent();
            DominatorUpdater?.ApplyUpdates(updates);
            if (needMask) Count("switch.lookup.hole_masks");
            Count("switch.lookup.converted");
            return true;
        }
    }
}
