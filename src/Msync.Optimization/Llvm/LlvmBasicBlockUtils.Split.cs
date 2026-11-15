// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// BasicBlockUtils.cpp SplitBlockImpl; BasicBlock.cpp splitBasicBlock.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmBasicBlockUtils
{
    public static LlvmBasicBlock SplitBlock(LlvmBasicBlock block, LlvmInstruction splitBefore,
        LlvmDomTreeUpdater? updater, LlvmLoopInfo? loops = null, LlvmMemorySsaUpdater? memoryUpdater = null,
        string name = "", CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
        => SplitBlockCore(block, splitBefore, updater, null, loops, memoryUpdater, name, trace?.Detail, metrics);

    public static LlvmBasicBlock SplitBlock(LlvmBasicBlock block, LlvmInstruction splitBefore,
        LlvmDominatorTree? tree = null, LlvmLoopInfo? loops = null, LlvmMemorySsaUpdater? memoryUpdater = null,
        string name = "", CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
        => SplitBlockCore(block, splitBefore, null, tree, loops, memoryUpdater, name, trace?.Detail, metrics);

    private static LlvmBasicBlock SplitBlockCore(LlvmBasicBlock block, LlvmInstruction splitBefore,
        LlvmDomTreeUpdater? updater, LlvmDominatorTree? tree, LlvmLoopInfo? loops,
        LlvmMemorySsaUpdater? memoryUpdater, string name, CompilationTrace? trace, Dictionary<string, ulong>? metrics)
    {
        while (splitBefore is LlvmPhiInstruction || splitBefore.IsEHPad)
        {
            splitBefore = splitBefore.Next!;
            Debug.Assert(splitBefore is not null);
        }
        var split = SplitBasicBlock(block, splitBefore, name.Length == 0 ? block.Name + ".split" : name, trace, metrics);
        if (loops?.GetLoopFor(block) is { } loop)
        {
            loop.AddBasicBlockToLoop(split, loops);
            Count(trace, metrics, "cfg.split_block.loop_memberships_added");
        }
        if (updater is not null)
        {
            List<LlvmCfgUpdate> updates = [new(LlvmCfgUpdateKind.Insert, block, split)];
            var unique = new HashSet<LlvmBasicBlock>();
            foreach (var successor in split.GetTerminator().Successors)
            {
                if (!unique.Add(successor)) continue;
                updates.Add(new(LlvmCfgUpdateKind.Insert, split, successor));
                updates.Add(new(LlvmCfgUpdateKind.Delete, block, successor));
            }
            updater.ApplyUpdates(updates);
            Count(trace, metrics, "cfg.split_block.dominator_updates", (ulong)updates.Count);
        }
        else if (tree?.GetNode(block) is { } oldNode)
        {
            var children = oldNode.Children.ToArray();
            var newNode = tree.AddNewBlock(split, block, metrics);
            foreach (var child in children) tree.ChangeImmediateDominator(child, newNode, metrics);
            Count(trace, metrics, "cfg.split_block.dominator_children_reparented", (ulong)children.Length);
        }
        if (memoryUpdater is not null)
        {
            memoryUpdater.MoveAllAfterSpliceBlocks(block, split, split.FirstInstruction!);
            Count(trace, metrics, "cfg.split_block.memory_accesses_spliced");
        }
        return split;
    }

    internal static LlvmBasicBlock SplitBasicBlock(LlvmBasicBlock block, LlvmInstruction splitBefore,
        string name = "", CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        Debug.Assert(block.HasTerminator && ReferenceEquals(splitBefore.Parent, block));
        var split = new LlvmBasicBlock(block.Context, name, block.Parent);
        split.MoveAfter(block);
        var source = splitBefore.Source;
        for (var instruction = splitBefore; instruction is not null;)
        {
            var next = instruction.Next;
            instruction.RemoveFromParent();
            split.Append(instruction);
            instruction = next;
            Count(trace, metrics, "cfg.split_block.instructions_moved");
        }
        block.Append(new LlvmUncondBrInstruction(split) { Source = source });
        foreach (var successor in split.GetTerminator().Successors)
        {
            for (var instruction = successor.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
            {
                for (int index = 0; index < phi.IncomingCount; ++index)
                {
                    if (!ReferenceEquals(phi.GetIncomingBlock(index), block)) continue;
                    phi.SetIncomingBlock(index, split);
                    Count(trace, metrics, "cfg.split_block.successor_phi_edges_redirected");
                }
            }
        }
        Count(trace, metrics, "cfg.split_block.blocks_inserted");
        return split;
    }
}
