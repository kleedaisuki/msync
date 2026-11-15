// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// ADT/GenericCycleImpl.h GenericCycleInfoCompute::dfs/tagLoopHeader/run.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmCycleInfo
{
    private sealed partial class Discovery
    {
        private const int None = -1;
        private struct BlockInfo
        {
            internal LlvmBasicBlock? Block;
            internal bool IsHeader;
            internal int Position, LoopIndex, LoopHeader;
        }
        private struct CycleBuild { internal int ChildHead, NextSibling, OwnCount; }
        private struct Frame(int block, int next) { internal int Block = block, Next = next; }
        private readonly LlvmCycleInfo result;
        private readonly CompilationTrace? trace;
        internal readonly Dictionary<string, ulong>? Metrics;
        private readonly Dictionary<string, ulong>? parentMetrics;
        private readonly BlockInfo[] blocks;
        private readonly List<int> preorder = [];
        private readonly List<(int Header, int Block)> reentries = [];
        private int headerCount;
        internal Discovery(LlvmCycleInfo result, CompilationTrace? trace, Dictionary<string, ulong>? parentMetrics)
        {
            this.result = result; this.trace = trace; this.parentMetrics = parentMetrics;
            Metrics = trace is null ? null : [];
            blocks = new BlockInfo[result.Function.GetMaxBlockNumber()];
            result.blockMap = new int[blocks.Length]; Array.Fill(result.blockMap, None);
        }
        private void Count(string name, ulong value = 1)
        {
            if (Metrics is null && parentMetrics is null) return;
            name = "cycle_info." + name;
            if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + value;
            if (parentMetrics is not null) parentMetrics[name] = parentMetrics.GetValueOrDefault(name) + value;
        }
        internal void Run()
        {
            if (trace is null) Search();
            else trace.Run("optimizer.cycle-info.reverse-successor-dfs", this, static state => state.Search());
            Count("cfg.blocks", (ulong)result.Function.BasicBlocks.Count);
            Count("cfg.reachable", (ulong)preorder.Count);
            Count("cfg.unreachable", (ulong)(result.Function.BasicBlocks.Count - preorder.Count));
            if (headerCount == 0) return;
            if (trace is null) BuildForest();
            else trace.Run("optimizer.cycle-info.forest-and-euler-layout", this, static state => state.BuildForest());
            if (reentries.Count != 0)
            {
                if (trace is null) AddReentries();
                else trace.Run("optimizer.cycle-info.irreducible-entries", this, static state => state.AddReentries());
            }
            PublishViews();
        }
        private void TagLoopHeader(int block, int header)
        {
            while (block != header)
            {
                Count("header_chain.steps");
                int inner = blocks[block].LoopHeader;
                if (inner == None) { blocks[block].LoopHeader = header; return; }
                if (blocks[inner].Position >= blocks[header].Position) block = inner;
                else { blocks[block].LoopHeader = header; block = header; header = inner; }
            }
        }
        private void Search()
        {
            var stack = new List<Frame>();
            int counter = 0;
            void Open(LlvmBasicBlock block)
            {
                int number = (int)block.Number;
                preorder.Add(number);
                blocks[number] = new BlockInfo { Block = block, Position = ++counter, LoopHeader = None };
                stack.Add(new Frame(number, block.GetTerminator().SuccessorCount - 1)); Count("dfs.opened");
            }
            Open(result.Function.BasicBlocks[0]);
            while (stack.Count != 0)
            {
                Frame top = stack[^1];
                if (top.Next < 0)
                {
                    blocks[top.Block].Position = 0; stack.RemoveAt(stack.Count - 1);
                    if (stack.Count != 0 && blocks[top.Block].LoopHeader != None) TagLoopHeader(stack[^1].Block, blocks[top.Block].LoopHeader);
                    continue;
                }
                LlvmBasicBlock successorBlock = blocks[top.Block].Block!.GetTerminator().GetSuccessor(top.Next--);
                int successor = (int)successorBlock.Number;
                stack[^1] = top; Count("dfs.edges");
                if (blocks[successor].Block is null) { Count("dfs.tree_edges"); Open(successorBlock); }
                else if (blocks[successor].Position > 0)
                {
                    Count("dfs.retreating_edges");
                    if (!blocks[successor].IsHeader) { blocks[successor].IsHeader = true; ++headerCount; }
                    TagLoopHeader(top.Block, successor);
                }
                else
                {
                    Count("dfs.off_path_edges");
                    for (int header = blocks[successor].LoopHeader; header != None; header = blocks[header].LoopHeader)
                    {
                        Count("reentry.chain_steps");
                        if (blocks[header].Position > 0) { TagLoopHeader(top.Block, header); break; }
                        reentries.Add((header, successor)); Count("reentry.edges");
                    }
                }
            }
        }
    }
}
