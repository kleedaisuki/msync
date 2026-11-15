// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// PromoteMemoryToRegister.cpp PromoteMem2Reg::run, ComputeLiveInBlocks and QueuePhiNode.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public static partial class LlvmPromoteMemoryToRegister
{
    private sealed partial class Promotion
    {
        private readonly List<LlvmAllocaInstruction> allocas;
        private readonly LlvmDominatorTree tree;
        private readonly CompilationTrace? trace;
        private readonly LlvmAssumptionCache? assumptions;
        private readonly Dictionary<string, ulong>? metrics;
        private readonly Dictionary<LlvmAllocaInstruction, int> allocaLookup = [];
        private readonly Dictionary<(uint Block, int Alloca), LlvmPhiInstruction> newPhis = [];
        private readonly Dictionary<LlvmPhiInstruction, int> phiToAlloca = [];
        private readonly Dictionary<LlvmBasicBlock, int> numPreds = [];
        private readonly List<LlvmType?> valueTypes;
        private readonly HashSet<LlvmBasicBlock> visited = [];
        private readonly Stack<(LlvmBasicBlock Block, LlvmBasicBlock? Pred, int Undo)> worklist = [];
        private readonly List<(int Index, LlvmValue Old)> undo = [];
        private LlvmValue[] incoming = [];
        internal Promotion(IReadOnlyList<LlvmAllocaInstruction> allocas, LlvmDominatorTree tree,
            CompilationTrace? trace, Dictionary<string, ulong>? metrics, LlvmAssumptionCache? assumptions)
        {
            this.allocas = new(allocas); this.tree = tree; this.trace = trace; this.metrics = metrics; this.assumptions = assumptions;
            valueTypes = Enumerable.Repeat<LlvmType?>(null, allocas.Count).ToList();
        }
        internal void Run()
        {
            // Lifetime resets are inserted together before any instruction-index cache scan.
            if (trace is null) RemoveIntrinsicUsers(); else Phase("intrinsic-users", RemoveIntrinsicUsers);
            var blockInfo = new LargeBlockInfo(metrics);
            var idf = new LlvmForwardIdfCalculator(tree, metrics);
            for (int index = 0; index < allocas.Count; ++index)
            {
                var alloca = allocas[index];
                Debug.Assert(IsAllocaPromotable(alloca));
                bool handled = trace is null ? AnalyzeAlloca(index, blockInfo, idf) :
                    trace.MeasureDetailed("optimizer.llvm-mem2reg.alloca", (Self: this, Index: index, BlockInfo: blockInfo, Idf: idf),
                        static state => state.Self.AnalyzeAlloca(state.Index, state.BlockInfo, state.Idf), attributes: Attributes(alloca));
                if (!handled) continue;
                allocas[index] = allocas[^1]; allocas.RemoveAt(allocas.Count - 1); --index;
            }
            if (allocas.Count == 0) return;
            blockInfo.Clear();
            incoming = new LlvmValue[allocas.Count];
            for (int i = 0; i < incoming.Length; ++i) incoming[i] = LlvmUndefValue.Get(valueTypes[i]!);
            if (trace is null) Rename(); else Phase("rename", Rename);
            foreach (var alloca in allocas)
            {
                if (!alloca.UseEmpty)
                { alloca.ReplaceAllUsesWith(LlvmPoisonValue.Get(alloca.Type)); Count(metrics, "unreachable.alloca_uses_poisoned"); }
                alloca.EraseFromParent(); Count(metrics, "removed.allocas");
            }
            if (trace is null) SimplifyPhis(); else Phase("phi-simplification", SimplifyPhis);
            if (trace is null) CompletePhis(); else Phase("unreachable-phi-edges", CompletePhis);
            newPhis.Clear();
        }
        private bool AnalyzeAlloca(int index, LargeBlockInfo blockInfo, LlvmForwardIdfCalculator idf)
        {
            var alloca = allocas[index];
            if (alloca.UseEmpty)
            {
                alloca.EraseFromParent(); Count(metrics, "fastpath.dead"); Count(metrics, "removed.allocas");
                return true;
            }
            var info = new AllocaInfo(alloca, metrics);
            if (info.DefiningBlocks.Count == 1 && RewriteSingleStore(alloca, info, blockInfo)) return true;
            if (info.OnlyUsedInOneBlock && PromoteSingleBlock(alloca, blockInfo)) return true;
            valueTypes[index] = info.ValueType;
            allocaLookup[alloca] = index;
            var definitions = info.DefiningBlocks.ToHashSet();
            var live = trace is null ? ComputeLiveInBlocks(alloca, info, definitions) :
                trace.MeasureDetailed("optimizer.llvm-mem2reg.liveness", (Self: this, Alloca: alloca, Info: info, Definitions: definitions),
                    static state => state.Self.ComputeLiveInBlocks(state.Alloca, state.Info, state.Definitions));
            idf.DefiningBlocks = definitions; idf.LiveInBlocks = live;
            var phiBlocks = new List<LlvmBasicBlock>();
            if (trace is null) idf.Calculate(phiBlocks);
            else trace.Run("optimizer.llvm-mem2reg.idf", (Idf: idf, Blocks: phiBlocks), static state => state.Idf.Calculate(state.Blocks));
            phiBlocks.Sort(static (a, b) => a.Number.CompareTo(b.Number));
            uint version = 0;
            foreach (var block in phiBlocks) QueuePhi(block, index, ref version);
            Count(metrics, "promotion.general");
            return false;
        }
        private int GetNumPreds(LlvmBasicBlock block)
        {
            if (!numPreds.TryGetValue(block, out int count))
                numPreds.Add(block, count = LlvmDominatorTree.Predecessors(block).Count());
            return count;
        }
        private HashSet<LlvmBasicBlock> ComputeLiveInBlocks(LlvmAllocaInstruction alloca, AllocaInfo info,
            HashSet<LlvmBasicBlock> definitions)
        {
            var work = new List<LlvmBasicBlock>(info.UsingBlocks);
            for (int i = 0; i < work.Count; ++i)
            {
                var block = work[i];
                if (!definitions.Contains(block)) continue;
                foreach (var instruction in block.Instructions)
                {
                    Count(metrics, "liveness.order_instructions");
                    if (instruction is LlvmStoreInstruction store && ReferenceEquals(store.PointerOperand, alloca))
                    {
                        work[i] = work[^1]; work.RemoveAt(work.Count - 1); --i;
                        Count(metrics, "liveness.store_before_use"); break;
                    }
                    if (instruction is LlvmLoadInstruction load && ReferenceEquals(load.PointerOperand, alloca)) break;
                }
            }
            var live = new HashSet<LlvmBasicBlock>();
            while (work.Count != 0)
            {
                var block = work[^1]; work.RemoveAt(work.Count - 1); Count(metrics, "liveness.work_pops");
                if (!live.Add(block)) continue;
                Count(metrics, "liveness.live_blocks");
                foreach (var predecessor in LlvmDominatorTree.Predecessors(block))
                {
                    Count(metrics, "liveness.predecessor_edges");
                    if (!definitions.Contains(predecessor)) work.Add(predecessor);
                }
            }
            return live;
        }
        private void QueuePhi(LlvmBasicBlock block, int allocaIndex, ref uint version)
        {
            var key = (block.Number, allocaIndex);
            if (newPhis.ContainsKey(key)) return;
            var phi = new LlvmPhiInstruction(valueTypes[allocaIndex]!, GetNumPreds(block))
            { LogicalType = allocas[allocaIndex].AllocatedLogicalType };
            phi.SetName(allocas[allocaIndex].Name + "." + (version++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            block.InsertBefore(phi, block.FirstInstruction);
            newPhis.Add(key, phi); phiToAlloca.Add(phi, allocaIndex); Count(metrics, "phi.inserted");
        }
        private void SimplifyPhis()
        {
            bool eliminated;
            do
            {
                eliminated = false; Count(metrics, "phi.simplification_rounds");
                foreach (var pair in newPhis.ToArray())
                {
                    Count(metrics, "phi.simplification_visits");
                    var phi = pair.Value;
                    if (LlvmInstructionSimplify.SimplifyPhi(phi, tree, metrics: metrics, assumptions: assumptions) is not { } value) continue;
                    phi.ReplaceAllUsesWith(value); phi.EraseFromParent(); newPhis.Remove(pair.Key);
                    eliminated = true; Count(metrics, "phi.simplified");
                }
            } while (eliminated);
        }
        private void CompletePhis()
        {
            foreach (var phi in newPhis.Values)
            {
                var block = phi.Parent!;
                if (!ReferenceEquals(block.FirstInstruction, phi) || phi.IncomingCount == GetNumPreds(block)) continue;
                var predecessors = LlvmDominatorTree.Predecessors(block).ToList();
                predecessors.Sort(static (a, b) => a.Number.CompareTo(b.Number));
                for (int i = 0; i < phi.IncomingCount; ++i)
                {
                    var predecessor = phi.GetIncomingBlock(i);
                    int position = predecessors.FindIndex(candidate => ReferenceEquals(candidate, predecessor));
                    Debug.Assert(position >= 0); predecessors.RemoveAt(position);
                }
                int incomingCount = phi.IncomingCount;
                for (var instruction = block.FirstInstruction; instruction is LlvmPhiInstruction current && current.IncomingCount == incomingCount; instruction = instruction.Next)
                    foreach (var predecessor in predecessors)
                    { current.AddIncoming(LlvmPoisonValue.Get(current.Type), predecessor); Count(metrics, "phi.unreachable_edges_added"); }
            }
        }
        private void ReplaceLoad(LlvmLoadInstruction load, LlvmValue value, string phase)
        {
            load.ReplaceAllUsesWith(value); load.EraseFromParent();
            Count(metrics, "removed.loads");
            if (metrics is not null) Count(metrics, phase + ".loads_rewritten");
        }
        private void Phase(string name, Action action)
        { if (trace is null) action(); else trace.Run("optimizer.llvm-mem2reg." + name, action, static callback => callback()); }
        private Dictionary<string, string> Attributes(LlvmAllocaInstruction alloca)
        {
            var attributes = new Dictionary<string, string>
            { ["function.name"] = alloca.Parent!.Parent!.Name, ["block.name"] = alloca.Parent.Name, ["alloca.name"] = alloca.Name };
            if (alloca.Source is { } source)
            {
                attributes["source.name"] = source.Source;
                attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }
    }
}
