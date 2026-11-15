// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Analysis/CFG.cpp FindFunctionBackedges; ADCE.cpp computeReversePostOrder
// using ADT/PostOrderIterator.h inverse_post_order_ext and IR/CFG.h predecessors.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmAdce
{
    private sealed partial class Work
    {
        private List<(LlvmBasicBlock From, LlvmBasicBlock To)> FindFunctionBackedges()
        {
            var result = new List<(LlvmBasicBlock, LlvmBasicBlock)>();
            var states = new byte[function.GetMaxBlockNumber()];
            var entry = function.BasicBlocks[0]; states[entry.Number] = 2;
            var stack = new List<(LlvmBasicBlock Block, int Next)> { (entry, 0) };
            do
            {
                var top = stack[^1]; bool foundNew = false;
                var terminator = top.Block.GetTerminator();
                while (top.Next < terminator.SuccessorCount)
                {
                    var successor = terminator.GetSuccessor(top.Next++); stack[^1] = top;
                    Count("backedge.successor_edges");
                    if (states[successor.Number] == 0)
                    { states[successor.Number] = 2; stack.Add((successor, 0)); foundNew = true; break; }
                    if (states[successor.Number] == 2) result.Add((top.Block, successor));
                }
                if (!foundNew) { states[top.Block.Number] = 1; stack.RemoveAt(stack.Count - 1); }
            } while (stack.Count != 0);
            return result;
        }
        private void ComputeReversePostOrder()
        {
            var visited = new HashSet<LlvmBasicBlock>(); uint order = 0;
            var stack = new List<(LlvmBasicBlock Block, LlvmUse? Next)>();
            foreach (var exit in function.BasicBlocks)
            {
                if (exit.GetTerminator().SuccessorCount != 0 || !visited.Add(exit)) continue;
                stack.Add((exit, exit.FirstUse));
                while (stack.Count != 0)
                {
                    var top = stack[^1];
                    if (top.Next is null)
                    {
                        stack.RemoveAt(stack.Count - 1); Info(top.Block).PostOrder = order++;
                        Count("reverse_cfg.blocks_numbered"); continue;
                    }
                    var use = top.Next; stack[^1] = (top.Block, use.Next);
                    Debug.Assert(use.User is LlvmInstruction { IsTerminator: true });
                    var predecessor = ((LlvmInstruction)use.User).Parent!;
                    Count("reverse_cfg.predecessor_edges");
                    if (visited.Add(predecessor)) stack.Add((predecessor, predecessor.FirstUse));
                }
            }
        }
    }
}
