// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Transforms/Utils/LCSSA.cpp
// computeBlocksDominatingExits, formLCSSA[Recursively] and function wrapper.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public static partial class LlvmLcssa
{
    public static bool Run(LlvmFunction function, LlvmDominatorTree tree, LlvmLoopInfo loops,
        CompilationTrace? trace = null)
    {
        Debug.Assert(ReferenceEquals(function, tree.Function) && ReferenceEquals(function, loops.Function));
        trace = trace?.Detail;
        Work work = new(tree, loops, trace);
        return trace is null ? work.Run() : trace.MeasureDetailed("optimizer.pass.llvm-lcssa", work,
            static state => state.Run(), attributes: new Dictionary<string, string> { ["function.name"] = function.Name },
            metrics: _ => work.Metrics!);
    }
    public static bool FormLCSSA(LlvmLoop loop, LlvmDominatorTree tree, LlvmLoopInfo loops,
        CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        Work work = new(tree, loops, trace);
        return trace is null ? work.FormLoop(loop) : trace.MeasureDetailed("optimizer.pass.llvm-lcssa.loop", (work, loop),
            static state => state.work.FormLoop(state.loop),
            attributes: new Dictionary<string, string> { ["function.name"] = loops.Function.Name }, metrics: _ => work.Metrics!);
    }
    public static bool FormLCSSARecursively(LlvmLoop loop, LlvmDominatorTree tree, LlvmLoopInfo loops,
        CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        Work work = new(tree, loops, trace);
        return trace is null ? work.FormRecursively(loop) : trace.MeasureDetailed("optimizer.pass.llvm-lcssa.nest", (work, loop),
            static state => state.work.FormRecursively(state.loop),
            attributes: new Dictionary<string, string> { ["function.name"] = loops.Function.Name }, metrics: _ => work.Metrics!);
    }
    public static bool FormLCSSAForInstructions(List<LlvmInstruction> worklist, LlvmDominatorTree tree,
        LlvmLoopInfo loops, List<LlvmPhiInstruction>? phisToRemove = null,
        List<LlvmPhiInstruction>? insertedPhis = null, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        Work work = new(tree, loops, trace);
        return trace is null ? work.FormInstructions(worklist, phisToRemove, insertedPhis) :
            trace.MeasureDetailed("optimizer.pass.llvm-lcssa.instructions", (work, worklist, phisToRemove, insertedPhis),
                static state => state.work.FormInstructions(state.worklist, state.phisToRemove, state.insertedPhis),
                attributes: new Dictionary<string, string> { ["function.name"] = loops.Function.Name }, metrics: _ => work.Metrics!);
    }

    private sealed partial class Work(LlvmDominatorTree tree, LlvmLoopInfo loops, CompilationTrace? trace)
    {
        private readonly Dictionary<LlvmLoop, List<LlvmBasicBlock>> exitBlocks = [];
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        internal bool Run()
        {
            bool changed = false;
            foreach (LlvmLoop loop in loops.TopLevelLoops)
            {
                exitBlocks.Clear();
                changed |= FormRecursively(loop);
            }
            return changed;
        }
        internal bool FormRecursively(LlvmLoop loop)
        {
            bool changed = false;
            foreach (LlvmLoop child in loop.SubLoops) changed |= FormRecursively(child);
            changed |= FormLoop(loop);
            return changed;
        }
        internal bool FormLoop(LlvmLoop loop)
        {
            if (trace is null) return FormLoopCore(loop);
            return trace.MeasureDetailed("optimizer.llvm-lcssa.loop", (this, loop),
                static state => state.Item1.FormLoopCore(state.loop),
                attributes: new Dictionary<string, string>
                {
                    ["function.name"] = loops.Function.Name, ["loop.header"] = loop.Header.Name,
                    ["loop.depth"] = loop.LoopDepth.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }
        private bool FormLoopCore(LlvmLoop loop)
        {
            Count("loops.visited");
            List<LlvmBasicBlock> exits = GetExitBlocks(loop);
            if (exits.Count == 0) { Count("loops.no_exit"); return false; }
            List<LlvmBasicBlock> dominating = [];
            if (trace is null) ComputeBlocksDominatingExits(loop, exits, dominating);
            else trace.Run("optimizer.llvm-lcssa.dominate-exits", (this, loop, exits, dominating),
                static state => state.Item1.ComputeBlocksDominatingExits(state.loop, state.exits, state.dominating));
            List<LlvmInstruction> worklist = [];
            if (trace is null) Scan(loop, dominating, worklist);
            else trace.Run("optimizer.llvm-lcssa.instruction-scan", (this, loop, dominating, worklist),
                static state => state.Item1.Scan(state.loop, state.dominating, state.worklist));
            return FormInstructions(worklist, null, null);
        }
        private List<LlvmBasicBlock> GetExitBlocks(LlvmLoop loop)
        {
            if (exitBlocks.TryGetValue(loop, out List<LlvmBasicBlock>? exits))
            { Count("exits.cache_hit"); return exits; }
            Count("exits.computed");
            exits = [];
            // GenericLoopInfoImpl.h getExitBlocks preserves duplicate exit edges.
            foreach (LlvmBasicBlock block in loop.Blocks)
                foreach (LlvmBasicBlock successor in LlvmDominatorTree.Successors(block))
                {
                    Count("exits.edges");
                    if (!loop.Contains(successor)) exits.Add(successor);
                }
            exitBlocks.Add(loop, exits);
            return exits;
        }
        private void ComputeBlocksDominatingExits(LlvmLoop loop, List<LlvmBasicBlock> exits, List<LlvmBasicBlock> result)
        {
            HashSet<LlvmBasicBlock> visited = [];
            List<LlvmBasicBlock> pending = new(exits);
            while (pending.Count != 0)
            {
                LlvmBasicBlock block = Pop(pending);
                Count("dominate_exits.worklist_pops");
                if (ReferenceEquals(loop.Header, block)) continue;
                LlvmBasicBlock dominator = tree.GetNode(block)!.IDom!.Block!;
                if (!loop.Contains(dominator)) continue;
                if (!visited.Add(dominator)) continue;
                result.Add(dominator);
                pending.Add(dominator);
            }
        }
        private void Scan(LlvmLoop loop, List<LlvmBasicBlock> blocks, List<LlvmInstruction> worklist)
        {
            foreach (LlvmBasicBlock block in blocks)
            {
                Count("scan.blocks");
                if (!ReferenceEquals(loops.GetLoopFor(block), loop)) { Count("scan.subloop_block"); continue; }
                foreach (LlvmInstruction instruction in block.Instructions)
                {
                    Count("scan.instructions");
                    if (instruction.UseEmpty) { Count("scan.no_uses"); continue; }
                    if (instruction.HasOneUse && instruction.FirstUse!.User is LlvmInstruction user &&
                        ReferenceEquals(user.Parent, block) && user is not LlvmPhiInstruction)
                    { Count("scan.single_local_use"); continue; }
                    if (instruction.Type.IsTokenLikeTy()) { Count("scan.token_like"); continue; }
                    worklist.Add(instruction);
                    Count("scan.worklist_entries");
                }
            }
        }
        private static T Pop<T>(List<T> list)
        {
            int index = list.Count - 1;
            T result = list[index]; list.RemoveAt(index); return result;
        }
        private void Count(string name)
        { if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + 1; }
    }
}
