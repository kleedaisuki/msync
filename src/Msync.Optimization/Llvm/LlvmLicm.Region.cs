// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// LICM.cpp sinkRegion / hoistRegion / isSafeToExecuteUnconditionally.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLicm
{
    private sealed partial class Work
    {
        private bool SinkRegion(LlvmLoop loop, SinkAndHoistFlags flags, LlvmLoop? outermost)
        {
            if (Trace is null) return SinkRegionCore(loop, flags, outermost);
            return Trace.MeasureDetailed("optimizer.llvm-licm.sink-region", (Work: this, loop, flags, outermost),
                static state => state.Work.SinkRegionCore(state.loop, state.flags, state.outermost), attributes: Attributes(loop.Header));
        }

        private bool SinkRegionCore(LlvmLoop loop, SinkAndHoistFlags flags, LlvmLoop? outermost)
        {
            var work = CollectChildrenInLoop(loop);
            bool changed = false;
            for (int blockIndex = work.Count - 1; blockIndex >= 0; --blockIndex)
            {
                LlvmBasicBlock block = work[blockIndex];
                if (LoopInfo.GetLoopFor(block) != loop) continue;
                for (LlvmInstruction? instruction = block.LastInstruction; instruction is not null;)
                {
                    LlvmInstruction? previous = instruction.Previous;
                    Count("sink.visited");
                    if (LlvmDeadCodeElimination.IsInstructionTriviallyDead(instruction, Metrics))
                    { EraseInstruction(instruction); changed = true; Count("sink.dead_erased"); instruction = previous; continue; }
                    bool foldable = false;
                    if (!instruction.MayHaveSideEffects() && NotUsedOrFoldableInLoop(instruction, outermost ?? loop,
                            ref foldable, outermost is not null) && CanSinkOrHoist(instruction, loop, true, flags) &&
                        Sink(instruction, loop))
                    {
                        if (!foldable) EraseInstruction(instruction);
                        changed = true;
                    }
                    instruction = previous;
                }
            }
            return changed;
        }

        private bool HoistRegion(SinkAndHoistFlags flags)
        {
            if (Trace is null) return HoistRegionCore(flags);
            return Trace.MeasureDetailed("optimizer.llvm-licm.hoist-region", (Work: this, flags),
                static state => state.Work.HoistRegionCore(state.flags), attributes: Attributes(Loop.Header));
        }

        private bool HoistRegionCore(SinkAndHoistFlags flags)
        {
            bool changed = false;
            LlvmBasicBlock preheader = Loop.GetLoopPreheader()!;
            foreach (LlvmBasicBlock block in LoopReversePostOrder())
            {
                if (!LoopNestMode && LoopInfo.GetLoopFor(block) != Loop) continue;
                for (LlvmInstruction? instruction = block.FirstInstruction; instruction is not null;)
                {
                    LlvmInstruction? next = instruction.Next;
                    Count("hoist.visited");
                    if (Loop.HasLoopInvariantOperands(instruction) && CanSinkOrHoist(instruction, Loop, true, flags) &&
                        SafeToExecuteUnconditionally(instruction, preheader.GetTerminator()))
                    { Hoist(instruction, preheader); changed = true; instruction = next; continue; }
                    if (instruction is LlvmBinaryOperator { Opcode: LlvmOpcode.FDiv } division &&
                        (division.FastMathFlags & LlvmFastMathFlags.AllowReciprocal) != 0 && Loop.IsLoopInvariant(division.Right))
                    {
                        HoistReciprocal(division, preheader);
                        changed = true; instruction = next; continue;
                    }
                    // invariant.start/guard and vector insert-element patterns have
                    // no represented intrinsic/instruction identity in this scalar IR.
                    changed |= LlvmLicmArithmetic.HoistArithmetics(instruction, Loop, Dominators, erase,
                        ScalarOptions.MaxIntReassociations, ScalarOptions.MaxFpReassociations, Metrics);
                    instruction = next;
                }
            }
            return changed;
        }

        private bool SafeToExecuteUnconditionally(LlvmInstruction instruction, LlvmInstruction context)
        {
            Count("execution.queries");
            if (Options.AllowSpeculation && LlvmValueTracking.IsSafeToSpeculativelyExecute(instruction,
                    context, Dominators, metrics: Metrics))
            { Count("execution.speculatable"); return true; }
            bool guaranteed = Safety.IsGuaranteedToExecute(instruction, Dominators);
            Count(guaranteed ? "execution.must_execute" : "execution.conditionally_executed");
            return guaranteed;
        }

        private void HoistReciprocal(LlvmBinaryOperator division, LlvmBasicBlock preheader)
        {
            LlvmBasicBlock block = division.Parent!;
            var reciprocal = new LlvmBinaryOperator(LlvmOpcode.FDiv,
                LlvmConstantFP.Get(division.Right.Type, 1.0), division.Right)
            { FastMathFlags = division.FastMathFlags, Source = division.Source, LogicalType = division.LogicalType };
            reciprocal.GetOperandUse(1).LogicalType = division.GetOperandUse(1).LogicalType;
            Safety.InsertInstructionTo(reciprocal, block);
            block.InsertBefore(reciprocal, division);
            var product = new LlvmBinaryOperator(LlvmOpcode.FMul, division.Left, reciprocal)
            { FastMathFlags = division.FastMathFlags, Source = division.Source, LogicalType = division.LogicalType };
            product.GetOperandUse(0).LogicalType = division.GetOperandUse(0).LogicalType;
            Safety.InsertInstructionTo(product, block);
            block.InsertBefore(product, division.Next);
            division.ReplaceAllUsesWith(product);
            EraseInstruction(division);
            Hoist(reciprocal, preheader);
            Count("hoist.reciprocals");
        }
    }
}
