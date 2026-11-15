// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SimplifyCFG.cpp
// reduceSwitchRange, simplifySwitchWhenUMin/defaultBranch/powersOfTwo/cmpIntrinsic.
using System.Numerics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        private static bool IsSwitchDense(IReadOnlyList<long> values, bool optimizeForSize)
        {
            ulong difference = unchecked((ulong)values[^1] - (ulong)values[0]);
            ulong range = unchecked(difference + 1);
            return range >= difference && IsSwitchDense((ulong)values.Count, range, optimizeForSize);
        }

        private static uint? GetDenseSwitchReductionShift(IReadOnlyList<long> values, long basis, bool optimizeForSize)
        {
            if (values.Any(value => value < basis)) return null;
            List<long> reduced = new(values);
            ulong combined = 0;
            for (int i = 0; i < reduced.Count; ++i)
            {
                ulong value = unchecked((ulong)reduced[i] - (ulong)basis);
                combined |= value; reduced[i] = unchecked((long)value);
            }
            uint shift = (uint)BitOperations.TrailingZeroCount(combined);
            if (shift != 0)
                for (int i = 0; i < reduced.Count; ++i) reduced[i] = unchecked((long)((ulong)reduced[i] >> (int)shift));
            return IsSwitchDense(reduced, optimizeForSize) ? shift : null;
        }

        private bool ReduceSwitchRange(LlvmSwitchInstruction selection, LlvmTransformBuilder builder)
        {
            Count("switch.reduce_range.queries");
            LlvmIntegerType type = (LlvmIntegerType)selection.Condition.Type;
            if (type.BitWidth > 64 || !Layout.FitsInLegalInteger(type.BitWidth) || selection.CaseCount < 4) return false;
            List<long> values = Enumerable.Range(0, selection.CaseCount).Select(i => selection.GetCaseValue(i).Value.GetSExtValue()).ToList();
            values.Sort();
            bool optimizeForSize = selection.Parent!.Parent!.HasOptSize;
            if (IsSwitchDense(values, optimizeForSize)) return RetainSwitch("reduce_range.already_dense");
            long basis = values[0];
            uint? shift = GetDenseSwitchReductionShift(values, 0, true);
            if (shift.HasValue) basis = 0;
            else if (basis != 0) shift = GetDenseSwitchReductionShift(values, basis, optimizeForSize);
            if (!shift.HasValue) return RetainSwitch("reduce_range.no_dense_shift");
            builder.SetInsertPoint(selection);
            LlvmValue sub = selection.Condition;
            if (basis != 0) sub = builder.CreateBinOp(LlvmOpcode.Sub, sub, LlvmConstantInt.Get(type, unchecked((ulong)basis), isSigned: true));
            LlvmValue rotation = builder.Insert(LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.FShl,
                [sub, sub, LlvmConstantInt.Get(type, type.BitWidth - shift.Value)], selection.Parent.Module!, Metrics));
            selection.Condition = rotation;
            for (int i = 0; i < selection.CaseCount; ++i)
            {
                LlvmAPInt difference = selection.GetCaseValue(i).Value - new LlvmAPInt(type.BitWidth, unchecked((ulong)basis), isSigned: true);
                selection.SetCaseValue(i, LlvmConstantInt.Get(type.Context, difference.LShr(shift.Value)));
            }
            Count("switch.reduce_range.converted");
            return true;
        }

        private bool SimplifySwitchWhenUMin(LlvmSwitchInstruction selection)
        {
            if (selection.Condition is not LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } call ||
                descriptor.Kind != LlvmIntrinsicKind.UMin || call.GetArgument(1) is not LlvmConstantInt limit) return false;
            LlvmBasicBlock block = selection.Parent!;
            List<LlvmCfgUpdate> updates = [];
            for (int i = 0; i < selection.CaseCount;)
            {
                if (!selection.GetCaseValue(i).Value.Ugt(limit.Value)) { ++i; continue; }
                LlvmBasicBlock dead = selection.GetCaseDestination(i);
                RemoveSwitchPredecessor(dead, block);
                selection.RemoveCase(i);
                if (!LlvmDominatorTree.Successors(block).Contains(dead)) updates.Add(new(LlvmCfgUpdateKind.Delete, block, dead));
                Count("switch.umin.dead_cases_removed");
            }
            int index = selection.FindCaseValue(limit);
            if (!selection.DefaultDestUnreachable || index == -1)
            {
                DominatorUpdater?.ApplyUpdates(updates);
                return updates.Count != 0;
            }
            LlvmBasicBlock unreachable = selection.DefaultDestination;
            selection.DefaultDestination = selection.GetCaseDestination(index);
            selection.RemoveCase(index);
            selection.Condition = call.GetArgument(0);
            updates.Add(new(LlvmCfgUpdateKind.Delete, block, unreachable));
            DominatorUpdater?.ApplyUpdates(updates);
            Count("switch.umin.default_replaced");
            return true;
        }

        private bool SimplifySwitchDefaultBranch(LlvmSwitchInstruction selection)
        {
            if (selection.DefaultDestUnreachable) return false;
            LlvmKnownBits known = LlvmKnownBitsAnalysis.Compute(selection.Condition, context: FirstNonPhi(selection.DefaultDestination), metrics: Metrics, assumptions: Options.AC, allowEphemerals: true);
            if (!known.IsConstant) return false;
            LlvmConstantInt constant = LlvmConstantInt.Get(selection.Context, known.GetConstant());
            int index = selection.FindCaseValue(constant);
            if (index == -1) selection.AddCase(constant, selection.DefaultDestination);
            CreateUnreachableSwitchDefault(selection, index != -1);
            Count("switch.default.concrete_value_forwarded");
            return true;
        }

        private bool SimplifySwitchOfPowersOfTwo(LlvmSwitchInstruction selection, LlvmTransformBuilder builder)
        {
            Count("switch.powers_of_two.queries");
            LlvmValue condition = selection.Condition;
            LlvmIntegerType type = (LlvmIntegerType)condition.Type;
            if (type.BitWidth > 64 || !Layout.FitsInLegalInteger(type.BitWidth)) return RetainSwitch("powers_of_two.illegal_condition_type");
            LlvmConstantInt poisonZero = LlvmConstantInt.GetBool(condition.Context, true);
            if (TargetInfo.GetIntrinsicInstrCost(LlvmIntrinsicKind.CTTZ, type, [condition, poisonZero], LlvmTargetCostKind.SizeAndLatency).Value > 2) return RetainSwitch("powers_of_two.expensive_cttz");
            if (selection.CaseCount < 4) return RetainSwitch("powers_of_two.fewer_than_four_cases");
            List<ulong> values = [];
            for (int i = 0; i < selection.CaseCount; ++i)
            {
                ulong value = selection.GetCaseValue(i).Value.GetZExtValue();
                if (!BitOperations.IsPow2(value)) return RetainSwitch("powers_of_two.non_power_case");
                values.Add(value);
            }
            values.Sort();
            if (!IsSwitchDense((ulong)values.Count, (ulong)(BitOperations.TrailingZeroCount(values[^1]) - BitOperations.TrailingZeroCount(values[0]) + 1),
                selection.Parent!.Parent!.HasOptSize)) return RetainSwitch("powers_of_two.sparse_log2_range");
            builder.SetInsertPoint(selection);
            if (!selection.DefaultDestUnreachable)
            {
                LlvmValue population = builder.Insert(LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CTPop, [condition], selection.Parent.Module!, Metrics));
                LlvmValue isPower = builder.CreateICmp(LlvmCmpPredicate.ICmpEq, population, LlvmConstantInt.Get(type, 1));
                LlvmBasicBlock original = selection.Parent!, defaultDestination = selection.DefaultDestination;
                LlvmBasicBlock split = LlvmBasicBlockUtils.SplitBlock(original, selection, DominatorUpdater, trace: Trace, metrics: Metrics);
                LlvmInstruction oldTerminator = original.GetTerminator();
                LlvmCondBrInstruction branch = new(isPower, split, defaultDestination) { Source = selection.Source };
                original.InsertBefore(branch, oldTerminator);
                oldTerminator.EraseFromParent();
                AddSwitchPredecessor(defaultDestination, original, split);
                DominatorUpdater?.ApplyUpdates([new(LlvmCfgUpdateKind.Insert, original, defaultDestination)]);
                Count("switch.powers_of_two.guard_inserted");
            }
            for (int i = 0; i < selection.CaseCount; ++i)
            {
                LlvmConstantInt old = selection.GetCaseValue(i);
                selection.SetCaseValue(i, LlvmConstantInt.Get((LlvmIntegerType)old.Type, old.Value.CountTrailingZeros()));
            }
            LlvmValue trailingZeros = builder.Insert(LlvmIntrinsic.CreateIntrinsic(LlvmIntrinsicKind.CTTZ, [condition, poisonZero], selection.Parent!.Module!, Metrics));
            selection.Condition = trailingZeros;
            Count("switch.powers_of_two.converted");
            return true;
        }

        private bool SimplifySwitchOfCmpIntrinsic(LlvmSwitchInstruction selection, LlvmTransformBuilder builder)
        {
            if (selection.Condition is not LlvmCallBase { CalledFunction.IntrinsicDescriptor: { } descriptor } compare || !compare.HasOneUse ||
                descriptor.Kind != LlvmIntrinsicKind.SCmp && descriptor.Kind != LlvmIntrinsicKind.UCmp) return false;
            LlvmBasicBlock? successor = null, other = null, unreachable = null;
            long result = 0;
            if (selection.CaseCount == 2)
            {
                HashSet<long> missing = [-1, 0, 1];
                successor = selection.DefaultDestination;
                for (int i = 0; i < selection.CaseCount; ++i)
                {
                    long? value = selection.GetCaseValue(i).Value.TrySExtValue();
                    if (!value.HasValue || !missing.Remove(value.Value)) return false;
                    if (other is not null && other != selection.GetCaseDestination(i)) return false;
                    other = selection.GetCaseDestination(i);
                }
                result = missing.Single();
            }
            else if (selection.CaseCount == 3 && selection.DefaultDestUnreachable)
            {
                unreachable = selection.DefaultDestination;
                for (int i = 0; i < selection.CaseCount; ++i)
                {
                    LlvmBasicBlock destination = selection.GetCaseDestination(i);
                    if (other is null || other == destination) other = destination;
                    else if (successor is null) successor = destination;
                    else if (successor == destination) (successor, other) = (other, successor);
                    else return false;
                }
                for (int i = 0; i < selection.CaseCount; ++i)
                {
                    long? value = selection.GetCaseValue(i).Value.TrySExtValue();
                    if (!value.HasValue || value != -1 && value != 0 && value != 1) return false;
                    if (selection.GetCaseDestination(i) == successor) { result = value.Value; break; }
                }
            }
            else return false;
            LlvmCmpPredicate predicate = result == 1 ? LlvmCmpPredicate.ICmpUgt : result == -1 ? LlvmCmpPredicate.ICmpUlt : LlvmCmpPredicate.ICmpEq;
            if (descriptor.Kind == LlvmIntrinsicKind.SCmp)
                predicate = predicate == LlvmCmpPredicate.ICmpUgt ? LlvmCmpPredicate.ICmpSgt : predicate == LlvmCmpPredicate.ICmpUlt ? LlvmCmpPredicate.ICmpSlt : predicate;
            LlvmBasicBlock block = selection.Parent!;
            builder.SetInsertPoint(selection);
            LlvmValue condition = builder.CreateICmp(predicate, compare.GetArgument(0), compare.GetArgument(1));
            builder.CreateCondBr(condition, successor!, other!);
            RemoveSwitchPredecessor(other!, block);
            if (unreachable is not null) RemoveSwitchPredecessor(unreachable, block);
            selection.EraseFromParent(); compare.EraseFromParent();
            if (unreachable is not null) DominatorUpdater?.ApplyUpdates([new(LlvmCfgUpdateKind.Delete, block, unreachable)]);
            Count("switch.cmp_intrinsic.converted");
            return true;
        }
    }
}
