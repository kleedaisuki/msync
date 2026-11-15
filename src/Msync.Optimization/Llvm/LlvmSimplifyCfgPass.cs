// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Scalar/SimplifyCFGPass.cpp function pass and tail merging.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed record LlvmSimplifyCfgOverrides
{
    public int? BonusInstThreshold { get; init; }
    public bool? ForwardSwitchCondToPhi { get; init; }
    public bool? ConvertSwitchRangeToICmp { get; init; }
    public bool? ConvertSwitchToLookupTable { get; init; }
    public bool? NeedCanonicalLoop { get; init; }
    public bool? HoistCommonInsts { get; init; }
    public bool? HoistLoadsStoresWithCondFaulting { get; init; }
    public bool? SinkCommonInsts { get; init; }
    public bool? SpeculateUnpredictables { get; init; }

    internal LlvmSimplifyCfgOptions Apply(LlvmSimplifyCfgOptions options) => options with
    {
        BonusInstThreshold = BonusInstThreshold ?? options.BonusInstThreshold,
        ForwardSwitchCondToPhi = ForwardSwitchCondToPhi ?? options.ForwardSwitchCondToPhi,
        ConvertSwitchRangeToICmp = ConvertSwitchRangeToICmp ?? options.ConvertSwitchRangeToICmp,
        ConvertSwitchToLookupTable = ConvertSwitchToLookupTable ?? options.ConvertSwitchToLookupTable,
        NeedCanonicalLoop = NeedCanonicalLoop ?? options.NeedCanonicalLoop,
        HoistCommonInsts = HoistCommonInsts ?? options.HoistCommonInsts,
        HoistLoadsStoresWithCondFaulting = HoistLoadsStoresWithCondFaulting ?? options.HoistLoadsStoresWithCondFaulting,
        SinkCommonInsts = SinkCommonInsts ?? options.SinkCommonInsts,
        SpeculateUnpredictables = SpeculateUnpredictables ?? options.SpeculateUnpredictables
    };
}

public static class LlvmSimplifyCfgPass
{
    public static bool Run(LlvmFunction function, LlvmTargetTransformInfo target,
        LlvmSimplifyCfgOptions? options = null, LlvmDominatorTree? tree = null,
        CompilationTrace? trace = null, LlvmSimplifyCfgTuning? tuning = null,
        LlvmSimplifyCfgOverrides? overrides = null)
    {
        trace = trace?.Detail;
        options ??= new();
        using var ownedAssumptions = options.AC is null ? new LlvmAssumptionCache(function, trace) : null;
        options = options with { AC = options.AC ?? ownedAssumptions };
        if (overrides is not null) options = overrides.Apply(options);
        var work = new Work(function, target, tree, options, tuning, trace);
        return trace is null ? work.Run() : trace.MeasureDetailed("optimizer.pass.llvm-simplifycfg", work,
            static state => state.Run(), attributes: new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["dominators.preserved"] = (tree is not null).ToString(),
                ["hoist.common"] = options.HoistCommonInsts.ToString(),
                ["sink.common"] = options.SinkCommonInsts.ToString()
            }, metrics: _ => work.Metrics!);
    }

    private sealed class Work(LlvmFunction function, LlvmTargetTransformInfo target,
        LlvmDominatorTree? tree, LlvmSimplifyCfgOptions options, LlvmSimplifyCfgTuning? tuning,
        CompilationTrace? trace)
    {
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        private readonly LlvmDomTreeUpdater? updater = tree is null ? null :
            new(tree, LlvmDomTreeUpdateStrategy.Eager, trace, trace is null ? null : []);

        internal bool Run()
        {
            try { return RunCore(); }
            finally { updater?.Dispose(); }
        }

        private bool RunCore()
        {
            bool changed = LlvmLocalCfg.RemoveUnreachableBlocks(function, updater, trace: trace, metrics: Metrics);
            changed |= TailMerge();
            changed |= Iterate();
            if (!changed) return false;
            if (LlvmLocalCfg.RemoveUnreachableBlocks(function, updater, foldInstsToUnreachable: false, trace: trace, metrics: Metrics))
            {
                bool local;
                do
                {
                    local = Iterate();
                    local |= LlvmLocalCfg.RemoveUnreachableBlocks(function, updater,
                        foldInstsToUnreachable: false, trace: trace, metrics: Metrics);
                } while (local);
            }
            function.RenumberBlocks();
            tree?.UpdateBlockNumbers(Metrics);
            Count("blocks.renumbered", (ulong)function.BasicBlocks.Count);
            return true;
        }

        private bool Iterate()
        {
            List<LlvmWeakValueHandle> headers = [];
            foreach (LlvmBasicBlock header in FindBackedges().Select(edge => edge.To).Distinct()) headers.Add(new(header));
            try
            {
                bool changed = false, local = true;
                int iteration = 0;
                while (local)
                {
                    Debug.Assert(iteration++ < 2000, "Iterative simplification did not converge");
                    Count("iterations"); local = false;
                    LlvmBasicBlock? next = function.BasicBlocks[0];
                    while (next is { } block)
                    {
                        next = block.NextNode;
                        Debug.Assert(updater?.IsBlockPendingDeletion(block) != true);
                        while (next is not null && updater?.IsBlockPendingDeletion(next) == true) next = next.NextNode;
                        Count("blocks.visited");
                        if (!LlvmSimplifyCfg.SimplifyCFG(block, target, updater, options, headers, trace, tuning)) continue;
                        local = true; Count("blocks.simplified");
                    }
                    changed |= local;
                }
                return changed;
            }
            finally { foreach (var header in headers) header.Dispose(); }
        }

        private bool TailMerge()
        {
            Dictionary<LlvmOpcode, List<LlvmBasicBlock>> groups = [];
            foreach (var block in function.BasicBlocks)
            {
                if (updater?.IsBlockPendingDeletion(block) == true) continue;
                LlvmInstruction terminator = block.GetTerminator();
                if (terminator.SuccessorCount != 0 || terminator is not LlvmReturnInstruction result) continue;
                if (terminator.Previous is LlvmCallInstruction { TailCallKind: LlvmTailCallKind.MustTail } previous
                    && (result.ReturnValue is null || ReferenceEquals(result.ReturnValue, previous))) continue;
                if (terminator.Operands.Any(operand => operand!.Type.IsTokenTy())) continue;
                if (!groups.TryGetValue(terminator.Opcode, out var blocks)) groups.Add(terminator.Opcode, blocks = []);
                blocks.Add(block);
            }
            List<LlvmCfgUpdate>? updates = updater is null ? null : [];
            bool changed = false;
            foreach (var blocks in groups.Values) changed |= MergeTails(blocks, updates);
            if (updates is not null) updater!.ApplyUpdates(updates);
            return changed;
        }

        private bool MergeTails(IReadOnlyList<LlvmBasicBlock> blocks, List<LlvmCfgUpdate>? updates)
        {
            if (blocks.Count < 2) return false;
            LlvmInstruction first = blocks[0].GetTerminator();
            var canonical = new LlvmBasicBlock(function.Context, "common." + first.Opcode.ToString().ToLowerInvariant(),
                function, blocks[0]);
            LlvmPhiInstruction[] phis = new LlvmPhiInstruction[first.OperandCount];
            for (int index = 0; index < phis.Length; ++index)
            {
                LlvmUse use = first.GetOperandUse(index);
                var phi = new LlvmPhiInstruction(use.Value!.Type, blocks.Count)
                {
                    LogicalType = use.LogicalType >= 0 ? use.LogicalType : use.Value.LogicalType,
                    Source = first.Source
                };
                canonical.Append(phi); phis[index] = phi;
            }
            LlvmInstruction terminator = first.Clone();
            canonical.Append(terminator);
            for (int index = 0; index < phis.Length; ++index) terminator.SetOperand(index, phis[index]);
            foreach (var block in blocks)
            {
                LlvmInstruction old = block.GetTerminator();
                for (int index = 0; index < phis.Length; ++index)
                {
                    var phi = phis[index];
                    phi.AddIncoming(old.GetOperand(index)!, block);
                    phi.GetOperandUse(phi.IncomingCount - 1).LogicalType = old.GetOperandUse(index).LogicalType;
                }
                block.InsertBefore(new LlvmUncondBrInstruction(canonical) { Source = old.Source }, old);
                old.EraseFromParent();
                updates?.Add(new(LlvmCfgUpdateKind.Insert, block, canonical));
                Count("tails.blocks_merged");
            }
            Count("tails.canonical_blocks_created");
            return true;
        }

        private List<(LlvmBasicBlock From, LlvmBasicBlock To)> FindBackedges()
        {
            List<(LlvmBasicBlock, LlvmBasicBlock)> edges = [];
            byte[] states = new byte[function.GetMaxBlockNumber()];
            LlvmBasicBlock entry = function.BasicBlocks[0]; states[entry.Number] = 2;
            List<(LlvmBasicBlock Block, int Next)> stack = [(entry, 0)];
            do
            {
                var top = stack[^1]; bool found = false;
                var terminator = top.Block.GetTerminator();
                while (top.Next < terminator.SuccessorCount)
                {
                    var successor = terminator.GetSuccessor(top.Next++); stack[^1] = top;
                    Count("backedges.successors");
                    if (states[successor.Number] == 0)
                    { states[successor.Number] = 2; stack.Add((successor, 0)); found = true; break; }
                    if (states[successor.Number] == 2) edges.Add((top.Block, successor));
                }
                if (!found) { states[top.Block.Number] = 1; stack.RemoveAt(stack.Count - 1); }
            } while (stack.Count != 0);
            return edges;
        }

        private void Count(string key, ulong amount = 1)
        { if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + amount; }
    }
}
