// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Support/GenericLoopInfoImpl.h analyzeImpl lines 469–807.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmLoopInfo
{
    private sealed partial class Discovery
    {
        private const int NoBlock = -1, OffPath = 1, IsHeader = 2, IsReentered = 3, FirstOnPath = 4;
        private struct BlockInfo { internal int Pos, Header; }
        private struct Frame(LlvmBasicBlock block) { internal LlvmBasicBlock Block = block; internal int Next; }
        private readonly LlvmLoopInfo result;
        private readonly LlvmDominatorTree dominators;
        private readonly CompilationTrace? trace;
        private readonly Dictionary<string, ulong>? metrics;
        private readonly BlockInfo[] info;
        private readonly List<LlvmBasicBlock> postorder = [];
        private readonly List<int> headers = [];
        private readonly HashSet<int> reentries = [];

        internal Discovery(LlvmLoopInfo result, LlvmDominatorTree dominators, CompilationTrace? trace)
        {
            this.result = result; this.dominators = dominators; this.trace = trace;
            metrics = trace is null ? null : [];
            int maxNumber = 0;
            foreach (LlvmBasicBlock block in result.Function.BasicBlocks) maxNumber = Math.Max(maxNumber, (int)block.Number + 1);
            info = new BlockInfo[maxNumber];
        }

        internal void Run()
        {
            if (result.Function.IsDeclaration) return;
            if (trace is null) Search();
            else trace.Run("optimizer.llvm-loop-info.forward-dfs", this, static state => state.Search());
            if (headers.Count != 0)
            {
                foreach (int header in headers) info[header].Pos = IsHeader;
                if (reentries.Count != 0)
                {
                    if (trace is null) ReduceReentries();
                    else trace.Run("optimizer.llvm-loop-info.irreducible-reduction", this, static state => state.ReduceReentries());
                }
                if (headers.Any(header => info[header].Pos == IsHeader))
                {
                    if (trace is null) BuildLayout();
                    else trace.Run("optimizer.llvm-loop-info.contiguous-layout", this, static state => state.BuildLayout());
                }
            }
            Count("cfg.blocks", (ulong)result.Function.BasicBlocks.Count);
            Count("cfg.reachable", (ulong)postorder.Count);
            Count("cfg.unreachable", (ulong)(result.Function.BasicBlocks.Count - postorder.Count));
            Count("loops.top_level", (ulong)result.roots.Count);
            if (metrics is not null) foreach (var metric in metrics) trace!.Metric(metric.Key, (long)metric.Value);
        }

        private void Count(string name, ulong value = 1)
        {
            if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + value;
        }

        private void TagLoopHeader(int block, int header)
        {
            while (block != header)
            {
                Count("header_chain.steps");
                int inner = info[block].Header;
                if (inner == NoBlock) { info[block].Header = header; return; }
                if (info[inner].Pos >= info[header].Pos) block = inner;
                else { info[block].Header = header; block = header; header = inner; }
            }
        }

        private void Search()
        {
            var stack = new List<Frame>();
            int counter = FirstOnPath;
            void Open(LlvmBasicBlock block)
            {
                info[(int)block.Number] = new BlockInfo { Pos = counter++, Header = NoBlock };
                stack.Add(new Frame(block));
                Count("dfs.opened");
            }
            Open(result.Function.BasicBlocks[0]);
            while (stack.Count != 0)
            {
                int last = stack.Count - 1;
                Frame top = stack[last];
                LlvmInstruction terminator = top.Block.GetTerminator();
                int source = (int)top.Block.Number;
                if (top.Next == terminator.SuccessorCount)
                {
                    info[source].Pos = OffPath; postorder.Add(top.Block); stack.RemoveAt(last);
                    if (stack.Count != 0 && info[source].Header != NoBlock)
                        TagLoopHeader((int)stack[^1].Block.Number, info[source].Header);
                    continue;
                }
                LlvmBasicBlock successor = terminator.GetSuccessor(top.Next++);
                stack[last] = top;
                int target = (int)successor.Number;
                Count("cfg.edges");
                if (info[target].Pos == 0) { Count("dfs.tree_edges"); Open(successor); }
                else if (info[target].Pos >= FirstOnPath)
                {
                    Count("dfs.retreating_edges"); headers.Add(target); TagLoopHeader(source, target);
                }
                else
                {
                    Count("dfs.off_path_edges");
                    for (int header = info[target].Header; header != NoBlock; header = info[header].Header)
                    {
                        Count("reentry.chain_steps");
                        if (info[header].Pos >= FirstOnPath) { TagLoopHeader(source, header); break; }
                        if (reentries.Add(header)) Count("reentry.headers");
                    }
                }
            }
        }

        private LlvmLoop AllocateLoop(LlvmBasicBlock block, LlvmLoop? enclosing)
        {
            if (trace is null) return new LlvmLoop(result, block, enclosing);
            var attributes = new Dictionary<string, string>
            {
                ["function.name"] = result.Function.Name, ["header.name"] = block.Name,
                ["header.number"] = block.Number.ToString(), ["loop.depth"] = ((enclosing?.LoopDepth ?? 0) + 1).ToString()
            };
            if (block.FirstInstruction?.Source is { } source)
            {
                attributes["source.name"] = source.Source;
                attributes["source.start"] = source.Start.ToString();
                attributes["source.length"] = source.Length.ToString();
            }
            return trace.MeasureDetailed("optimizer.llvm-loop-info.loop", (Owner: result, Block: block, Parent: enclosing),
                static state => new LlvmLoop(state.Owner, state.Block, state.Parent), attributes: attributes);
        }

        private void BuildLayout()
        {
            result.blockMap = new LlvmLoop?[info.Length];
            for (int index = postorder.Count - 1; index >= 0; --index)
            {
                LlvmBasicBlock block = postorder[index];
                int number = (int)block.Number, header = info[number].Header;
                LlvmLoop? enclosing = header == NoBlock ? null : result.blockMap[header];
                if (info[number].Pos == IsHeader)
                {
                    enclosing = AllocateLoop(block, enclosing);
                    Count("loops.discovered");
                    if (metrics is not null) Count("loops.depth_sum", (ulong)enclosing.LoopDepth);
                }
                result.blockMap[number] = enclosing;
            }
            var blocksPO = new List<(LlvmBasicBlock Block, LlvmLoop Loop)>();
            var loopsPO = new List<LlvmLoop>();
            foreach (LlvmBasicBlock block in postorder)
            {
                LlvmLoop? loop = result.GetLoopFor(block);
                if (loop is null) continue;
                blocksPO.Add((block, loop)); ++loop.BlockLength;
                if (!ReferenceEquals(block, loop.PendingHeader)) continue;
                loopsPO.Add(loop);
                if (loop.ParentLoop is { } parent) parent.BlockLength += loop.BlockLength;
                else result.roots.Add(loop);
            }
            var layout = new LlvmBasicBlock[blocksPO.Count];
            int rootCursor = 0;
            for (int index = blocksPO.Count - 1; index >= 0; --index)
            {
                (LlvmBasicBlock block, LlvmLoop loop) = blocksPO[index];
                if (loop.Cursor == 0)
                {
                    if (loop.ParentLoop is { } parent)
                    {
                        Debug.Assert(parent.Cursor != 0);
                        loop.BlockOffset = parent.BlockOffset + parent.Cursor;
                        parent.Cursor += loop.BlockLength; parent.Children.Add(loop);
                    }
                    else { loop.BlockOffset = rootCursor; rootCursor += loop.BlockLength; }
                }
                layout[loop.BlockOffset + loop.Cursor++] = block;
                Count("layout.blocks_written");
            }
            foreach (LlvmLoop loop in loopsPO)
            {
                Debug.Assert(loop.Cursor == loop.BlockLength);
                loop.SetBorrowedBlocks(layout, loop.BlockOffset, loop.BlockLength);
                Count("layout.loop_memberships", (ulong)loop.BlockLength);
            }
        }
    }
}
