// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/SimplifyCFG.cpp hoistCommonCodeFromSuccessors and reordering helpers.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmSimplifyCfg
{
    internal sealed partial class Work
    {
        [Flags]
        private enum HoistSkipFlags : byte { None = 0, ReadMemory = 1, SideEffect = 2, ImplicitControlFlow = 4 }

        private sealed class HoistSuccessorCursor(LlvmInstruction instruction)
        {
            internal LlvmInstruction Instruction = instruction;
            internal HoistSkipFlags Skipped;
        }

        internal bool HoistCommonCodeFromSuccessors(LlvmInstruction terminator, bool allInstructionsEqualOnly = false)
        {
            if (Trace is null) return HoistCommonCode(terminator, allInstructionsEqualOnly);
            return Trace.MeasureDetailed("optimizer.llvm-simplifycfg.hoist-common",
                (Self: this, Terminator: terminator, AllEqual: allInstructionsEqualOnly),
                static state => state.Self.HoistCommonCode(state.Terminator, state.AllEqual),
                attributes: Attributes(terminator));
        }

        private bool HoistCommonCode(LlvmInstruction terminator, bool allInstructionsEqualOnly)
        {
            Count("hoist.queries");
            if (terminator.SuccessorCount < 2) return HoistRetained("less-than-two-successors");
            List<LlvmBasicBlock> successors = [];
            HashSet<LlvmBasicBlock> seen = [];
            foreach (LlvmBasicBlock successor in terminator.Successors)
                if (seen.Add(successor)) successors.Add(successor);
            List<HoistSuccessorCursor> cursors = [];
            foreach (LlvmBasicBlock successor in successors)
            {
                Count("hoist.successors.visited");
                // BlockAddress is not represented; every successor has the
                // ordinary no-address-taken source property.
                if (!HasUniqueHoistPredecessor(successor) && successor.FirstInstruction is not LlvmUnreachableInstruction)
                    return HoistRetained("multiple-predecessors");
                if (successor.FirstInstruction is LlvmPhiInstruction) return HoistRetained("successor-phi");
                cursors.Add(new(successor.FirstInstruction!));
            }
            if (allInstructionsEqualOnly && !AllSuccessorInstructionsEqual(successors))
                return HoistRetained("not-all-instructions-equal");
            if (cursors.Count > 2)
            {
                cursors.RemoveAll(static cursor => cursor.Instruction is LlvmUnreachableInstruction);
                if (cursors.Count < 2) return HoistRetained("less-than-two-reachable-successors");
            }
            bool changed = false;
            uint skipped = 0;
            for (;;)
            {
                LlvmInstruction first = cursors[0].Instruction;
                bool identical = true;
                bool hasTerminator = first.IsTerminator;
                for (int index = 1; index < cursors.Count; ++index)
                {
                    LlvmInstruction other = cursors[index].Instruction;
                    Count("hoist.lockstep.comparisons");
                    hasTerminator |= other.IsTerminator;
                    if (identical && !AreIdenticalUpToCommutativity(first, other)) identical = false;
                }
                if (hasTerminator)
                {
                    if (skipped != 0 || !identical)
                    {
                        Count("hoist.terminator.retained");
                        return changed;
                    }
                    return HoistSuccessorIdenticalTerminator(terminator, first,
                        cursors.Skip(1).Select(static cursor => cursor.Instruction).ToArray(), successors) || changed;
                }
                if (identical) identical = CanHoistLockstep(cursors);
                if (identical && first is LlvmCallInstruction { TailCallKind: LlvmTailCallKind.MustTail })
                {
                    identical = skipped == 0 && cursors.All(static cursor => cursor.Instruction.Next is LlvmReturnInstruction);
                    if (!identical) Count("hoist.musttail.retained");
                }
                if (identical)
                {
                    HoistLockstep(terminator, cursors);
                    if (!changed) Count("hoist.common-code", (ulong)cursors.Count);
                    changed = true;
                    Count("hoist.instructions", (ulong)cursors.Count);
                    continue;
                }
                if (skipped >= Tuning.HoistCommonSkipLimit)
                { Count("hoist.skip-limit"); return changed; }
                foreach (HoistSuccessorCursor cursor in cursors)
                {
                    LlvmInstruction current = cursor.Instruction;
                    cursor.Instruction = current.Next!;
                    cursor.Skipped |= SkippedInstructionFlags(current);
                }
                ++skipped;
                Count("hoist.lockstep.skipped");
            }
        }

        private bool CanHoistLockstep(IReadOnlyList<HoistSuccessorCursor> cursors)
        {
            LlvmInstruction first = cursors[0].Instruction;
            if (!IsSafeToHoistInstruction(first, cursors[0].Skipped)) return false;
            for (int index = 1; index < cursors.Count; ++index)
                if (!IsSafeToHoistInstruction(cursors[index].Instruction, cursors[index].Skipped) ||
                    !ShouldHoistCommonInstructions(first, cursors[index].Instruction)) return false;
            return true;
        }

        private void HoistLockstep(LlvmInstruction terminator, IReadOnlyList<HoistSuccessorCursor> cursors)
        {
            LlvmInstruction first = cursors[0].Instruction;
            cursors[0].Instruction = first.Next!;
            first.RemoveFromParent();
            terminator.Parent!.InsertBefore(first, terminator);
            for (int index = 1; index < cursors.Count; ++index)
            {
                LlvmInstruction other = cursors[index].Instruction;
                cursors[index].Instruction = other.Next!;
                Debug.Assert(other != first);
                if (!other.UseEmpty) other.ReplaceAllUsesWith(first);
                LlvmLocal.CombineMetadataForCse(first, other, doesMove: true, Metrics);
                first.AndIRFlags(other);
                if (first is LlvmCallBase call)
                {
                    bool intersected = call.TryIntersectAttributes((LlvmCallBase)other);
                    Debug.Assert(intersected);
                }
                other.EraseFromParent();
            }
            HoistSkipFlags skipped = HoistSkipFlags.None;
            foreach (HoistSuccessorCursor cursor in cursors) skipped |= cursor.Skipped;
            if ((skipped & HoistSkipFlags.ImplicitControlFlow) != 0)
            {
                // Current call attributes only model function properties, not
                // UB-implying parameter/return attrs. There is nothing to drop.
                Count("hoist.speculated.no-ub-attributes");
            }
        }

        private static bool HasUniqueHoistPredecessor(LlvmBasicBlock block)
        {
            using IEnumerator<LlvmBasicBlock> predecessors = LlvmDominatorTree.Predecessors(block).GetEnumerator();
            if (!predecessors.MoveNext()) return false;
            LlvmBasicBlock first = predecessors.Current;
            while (predecessors.MoveNext()) if (predecessors.Current != first) return false;
            return true;
        }

        private bool AllSuccessorInstructionsEqual(IReadOnlyList<LlvmBasicBlock> successors)
        {
            LlvmInstruction terminator = successors[0].GetTerminator();
            int size = successors[0].Instructions.Count;
            for (int index = 1; index < successors.Count; ++index)
                if (!successors[index].GetTerminator().IsIdenticalTo(terminator) || successors[index].Instructions.Count != size)
                    return false;
            LlvmInstruction?[] reverse = successors.Select(static successor => successor.GetTerminator().Previous).ToArray();
            while (reverse.All(static instruction => instruction is not null))
            {
                LlvmInstruction first = reverse[0]!;
                for (int index = 1; index < reverse.Length; ++index)
                {
                    Count("hoist.all-equal.comparisons");
                    if (!AreIdenticalUpToCommutativity(first, reverse[index]!)) return false;
                }
                for (int index = 0; index < reverse.Length; ++index) reverse[index] = reverse[index]!.Previous;
            }
            return true;
        }

        private static bool AreIdenticalUpToCommutativity(LlvmInstruction first, LlvmInstruction second)
        {
            if (first.IsIdenticalToWhenDefined(second, intersectAttrs: true)) return true;
            if (first is LlvmCmpInstruction compare && second is LlvmCmpInstruction otherCompare)
                return compare.Predicate == LlvmCmpInstruction.GetSwappedPredicate(otherCompare.Predicate) &&
                    compare.Left == otherCompare.Right && compare.Right == otherCompare.Left;
            if (first is not LlvmBinaryOperator { IsCommutative: true } || !first.IsSameOperationAs(second)) return false;
            if (first.GetOperand(0) != second.GetOperand(1) || first.GetOperand(1) != second.GetOperand(0)) return false;
            for (int index = 2; index < first.OperandCount; ++index)
                if (first.GetOperand(index) != second.GetOperand(index)) return false;
            return true;
        }

        private HoistSkipFlags SkippedInstructionFlags(LlvmInstruction instruction)
        {
            HoistSkipFlags flags = HoistSkipFlags.None;
            if (instruction.MayReadFromMemory()) flags |= HoistSkipFlags.ReadMemory;
            if (instruction.MayHaveSideEffects() || instruction is LlvmAllocaInstruction) flags |= HoistSkipFlags.SideEffect;
            if (!LlvmValueTracking.IsGuaranteedToTransferExecutionToSuccessor(instruction, Metrics)) flags |= HoistSkipFlags.ImplicitControlFlow;
            return flags;
        }

        private bool IsSafeToHoistInstruction(LlvmInstruction instruction, HoistSkipFlags flags)
        {
            if ((flags & HoistSkipFlags.ReadMemory) != 0 && instruction.MayWriteToMemory())
                return HoistRetained("reorder-store-over-read");
            if ((flags & HoistSkipFlags.SideEffect) != 0 &&
                (instruction.MayReadFromMemory() || instruction.MayHaveSideEffects() || instruction is LlvmAllocaInstruction))
                return HoistRetained("reorder-over-effects");
            if ((flags & HoistSkipFlags.ImplicitControlFlow) != 0 &&
                !LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction, metrics: Metrics))
                return HoistRetained("reorder-over-control-flow");
            // llvm.experimental.deoptimize is not a represented intrinsic.
            foreach (LlvmValue? operand in instruction.Operands)
                if (operand is LlvmInstruction producer && producer.Parent == instruction.Parent)
                    return HoistRetained("same-block-operand");
            return true;
        }

        private bool ShouldHoistCommonInstructions(LlvmInstruction first, LlvmInstruction second)
        {
            if (first is LlvmCallInstruction left && second is LlvmCallInstruction right &&
                (left.TailCallKind == LlvmTailCallKind.MustTail) != (right.TailCallKind == LlvmTailCallKind.MustTail))
                return HoistRetained("different-musttail");
            if (!TargetInfo.IsProfitableToHoist(first) || !TargetInfo.IsProfitableToHoist(second))
                return HoistRetained("target-profitability");
            if (first is LlvmCallBase firstCall && (firstCall.CannotMerge || firstCall.IsConvergent))
                return HoistRetained("first-call-nomerge-or-convergent");
            if (second is LlvmCallBase secondCall && (secondCall.CannotMerge || secondCall.IsConvergent))
                return HoistRetained("second-call-nomerge-or-convergent");
            return true;
        }

        private bool HoistRetained(string reason)
        {
            if (Metrics is not null) Count("hoist.retained." + reason);
            return false;
        }
    }
}
