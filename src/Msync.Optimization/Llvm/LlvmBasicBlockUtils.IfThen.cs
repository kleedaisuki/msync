// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicBlockUtils.cpp
// SplitBlockAndInsertIfThen, SplitBlockAndInsertIfThenElse; BasicBlock.cpp splitBasicBlock.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmBasicBlockUtils
{
    public static LlvmInstruction SplitBlockAndInsertIfThen(LlvmValue condition, LlvmInstruction splitBefore,
        bool unreachable = false, LlvmDomTreeUpdater? updater = null, LlvmLoopInfo? loops = null,
        LlvmBasicBlock? thenBlock = null, Dictionary<string, ulong>? metrics = null)
    {
        LlvmBasicBlock? noElse = null;
        SplitBlockAndInsertIfThenElse(condition, splitBefore, ref thenBlock, ref noElse,
            createThen: true, createElse: false, unreachableThen: unreachable, updater: updater, loops: loops, metrics: metrics);
        return thenBlock!.GetTerminator();
    }

    public static void SplitBlockAndInsertIfThenElse(LlvmValue condition, LlvmInstruction splitBefore,
        ref LlvmBasicBlock? thenBlock, ref LlvmBasicBlock? elseBlock, bool createThen = true, bool createElse = true,
        bool unreachableThen = false, bool unreachableElse = false, LlvmDomTreeUpdater? updater = null,
        LlvmLoopInfo? loops = null, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(createThen || createElse);
        Debug.Assert(!unreachableThen || !unreachableElse);
        LlvmBasicBlock head = splitBefore.Parent!;
        LlvmBasicBlock[] originalSuccessors = head.GetTerminator().Successors.Distinct().ToArray();
        LlvmBasicBlock tail = SplitBasicBlock(head, splitBefore, metrics: metrics);
        LlvmBasicBlock trueBlock = tail, falseBlock = tail;
        bool thenToTail = false, elseToTail = false;
        if (createThen) HandleIfBlock(ref thenBlock, unreachableThen, ref trueBlock, ref thenToTail, tail, splitBefore.Source);
        if (createElse) HandleIfBlock(ref elseBlock, unreachableElse, ref falseBlock, ref elseToTail, tail, splitBefore.Source);
        LlvmInstruction oldTerminator = head.GetTerminator();
        head.InsertBefore(new LlvmCondBrInstruction(condition, trueBlock, falseBlock) { Source = oldTerminator.Source }, oldTerminator);
        oldTerminator.EraseFromParent();
        if (updater is not null)
        {
            List<LlvmCfgUpdate> updates = [new(LlvmCfgUpdateKind.Insert, head, trueBlock), new(LlvmCfgUpdateKind.Insert, head, falseBlock)];
            if (thenToTail) updates.Add(new(LlvmCfgUpdateKind.Insert, trueBlock, tail));
            if (elseToTail) updates.Add(new(LlvmCfgUpdateKind.Insert, falseBlock, tail));
            foreach (LlvmBasicBlock successor in originalSuccessors) updates.Add(new(LlvmCfgUpdateKind.Insert, tail, successor));
            foreach (LlvmBasicBlock successor in originalSuccessors) updates.Add(new(LlvmCfgUpdateKind.Delete, head, successor));
            updater.ApplyUpdates(updates);
        }
        if (loops?.GetLoopFor(head) is { } loop)
        {
            if (thenToTail) loop.AddBasicBlockToLoop(trueBlock, loops);
            if (elseToTail) loop.AddBasicBlockToLoop(falseBlock, loops);
            loop.AddBasicBlockToLoop(tail, loops);
        }
        if (metrics is not null) metrics["basic_block_utils.if_split"] = metrics.GetValueOrDefault("basic_block_utils.if_split") + 1;
    }

    private static void HandleIfBlock(ref LlvmBasicBlock? supplied, bool unreachable,
        ref LlvmBasicBlock block, ref bool toTail, LlvmBasicBlock tail, SourceSpan? source)
    {
        if (supplied is not null) { block = supplied; return; }
        block = new LlvmBasicBlock(tail.Context, "", tail.Parent, tail);
        block.Append(unreachable ? new LlvmUnreachableInstruction(tail.Context) { Source = source }
            : new LlvmUncondBrInstruction(tail) { Source = source });
        toTail = !unreachable;
        supplied = block;
    }

}
