// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Utils/LoopSimplify.cpp insertUniqueBackedgeBlock and simplifyHeaderPHIs.
// Source graph-only and mutable-analysis overloads; no MSSA/SE facilities.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmLoopSimplify
{
    public static LlvmBasicBlock? InsertUniqueBackedgeBlock(LlvmLoop loop, LlvmBasicBlock? preheader,
        CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return UniqueBackedge(loop, preheader, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.unique-backedge", (Loop: loop, Preheader: preheader, Trace: trace),
            static state => UniqueBackedge(state.Loop, state.Preheader, state.Trace), attributes: Attributes(loop.Header));
    }
    public static LlvmBasicBlock? InsertUniqueBackedgeBlock(LlvmLoop loop, LlvmBasicBlock? preheader,
        LlvmDominatorTree tree, LlvmLoopInfo loops, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return UniqueBackedge(loop, preheader, tree, loops, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.unique-backedge", (Loop: loop, Preheader: preheader, Tree: tree, Loops: loops, Trace: trace),
            static state => UniqueBackedge(state.Loop, state.Preheader, state.Tree, state.Loops, state.Trace), attributes: Attributes(loop.Header));
    }
    private static LlvmBasicBlock? UniqueBackedge(LlvmLoop loop, LlvmBasicBlock? preheader, CompilationTrace? trace)
        => UniqueBackedge(loop, preheader, null, null, trace);
    private static LlvmBasicBlock? UniqueBackedge(LlvmLoop loop, LlvmBasicBlock? preheader,
        LlvmDominatorTree? tree, LlvmLoopInfo? loops, CompilationTrace? trace)
    {
        Debug.Assert(LlvmDominatorTree.Predecessors(loop.Header).Count(loop.Contains) > 1, "Must have > 1 backedge!");
        if (preheader is null) return null;
        var header = loop.Header;
        var backedges = LlvmDominatorTree.Predecessors(header).Where(predecessor => !ReferenceEquals(predecessor, preheader)).ToArray();
        var backedgeBlock = new LlvmBasicBlock(header.Context, header.Name + ".backedge", header.Parent);
        var branch = new LlvmUncondBrInstruction(header) { Source = FirstNonPhi(header).Source };
        backedgeBlock.Append(branch);
        backedgeBlock.MoveAfter(backedges[^1]);
        foreach (var phi in Phis(header))
        {
            var joined = new LlvmPhiInstruction(phi.Type, backedges.Length)
            { LogicalType = phi.LogicalType, Source = phi.Source };
            joined.SetName(phi.Name + ".be"); backedgeBlock.InsertBefore(joined, branch);
            int preheaderIndex = -1;
            bool hasUnique = true;
            LlvmValue? unique = null;
            for (int i = 0; i < phi.IncomingCount; ++i)
            {
                var incomingBlock = phi.GetIncomingBlock(i); var value = phi.GetIncomingValue(i);
                if (ReferenceEquals(incomingBlock, preheader)) preheaderIndex = i;
                else
                {
                    joined.AddIncoming(value, incomingBlock);
                    joined.GetOperandUse(joined.IncomingCount - 1).LogicalType = phi.GetOperandUse(i).LogicalType;
                    if (hasUnique)
                    {
                        if (unique is null) unique = value;
                        else if (!ReferenceEquals(unique, value)) hasUnique = false;
                    }
                    trace?.Metric("backedge.phi_inputs_moved", 1);
                }
            }
            Debug.Assert(preheaderIndex != -1, "PHI has no preheader entry??");
            if (preheaderIndex != 0)
            {
                phi.SetIncomingValue(0, phi.GetIncomingValue(preheaderIndex));
                phi.SetIncomingBlock(0, phi.GetIncomingBlock(preheaderIndex));
                phi.GetOperandUse(0).LogicalType = phi.GetOperandUse(preheaderIndex).LogicalType;
            }
            for (int i = phi.IncomingCount - 1; i > 0; --i) phi.RemoveIncomingValue(i, false);
            phi.AddIncoming(joined, backedgeBlock);
            phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = phi.LogicalType;
            if (hasUnique)
            {
                joined.ReplaceAllUsesWith(unique!); joined.EraseFromParent();
                trace?.Metric("backedge.common_phi_eliminated", 1);
            }
            else trace?.Metric("backedge.phi_created", 1);
        }
        foreach (var predecessor in backedges)
        { predecessor.GetTerminator().ReplaceSuccessorWith(header, backedgeBlock); trace?.Metric("backedge.predecessors_redirected", 1); }
        if (loops is not null) loop.AddBasicBlockToLoop(backedgeBlock, loops);
        tree?.SplitBlock(backedgeBlock);
        trace?.Metric("cfg.blocks_inserted", 1);
        return backedgeBlock;
    }
    public static bool SimplifyHeaderPhis(LlvmLoop loop, LlvmDominatorTree dominators, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return HeaderPhis(loop, dominators, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.header-phis", (Loop: loop, Tree: dominators, Trace: trace),
            static state => HeaderPhis(state.Loop, state.Tree, state.Trace), attributes: Attributes(loop.Header));
    }
    public static bool SimplifyHeaderPhis(LlvmLoop loop, LlvmDominatorTree tree, LlvmLoopInfo loops,
        bool preserveLcssa = false, CompilationTrace? trace = null)
    {
        trace = trace?.Detail;
        if (trace is null) return HeaderPhis(loop, tree, loops, preserveLcssa, null);
        return trace.MeasureDetailed("optimizer.llvm-loop-simplify.header-phis", (Loop: loop, Tree: tree, Loops: loops, Preserve: preserveLcssa, Trace: trace),
            static state => HeaderPhis(state.Loop, state.Tree, state.Loops, state.Preserve, state.Trace), attributes: Attributes(loop.Header));
    }
    private static bool HeaderPhis(LlvmLoop loop, LlvmDominatorTree dominators, CompilationTrace? trace)
        => HeaderPhis(loop, dominators, null, false, trace);
    private static bool HeaderPhis(LlvmLoop loop, LlvmDominatorTree dominators, LlvmLoopInfo? loops,
        bool preserveLcssa, CompilationTrace? trace)
    {
        bool changed = false;
        for (var instruction = loop.Header.FirstInstruction; instruction is LlvmPhiInstruction phi;)
        {
            instruction = instruction.Next;
            trace?.Metric("header.phis_visited", 1);
            if (LlvmInstructionSimplify.SimplifyPhi(phi, dominators) is not { } value) continue;
            if (preserveLcssa && !loops!.ReplacementPreservesLCSSAForm(phi, value)) continue;
            phi.ReplaceAllUsesWith(value); phi.EraseFromParent(); changed = true;
            trace?.Metric("header.phis_simplified", 1);
        }
        return changed;
    }
}
