// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: SSAUpdater.h/.cpp
// LoadAndStorePromoter constructor, run, notification/retention extension hooks.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public partial class LlvmLoadAndStorePromoter
{
    protected readonly LlvmSsaUpdater SSA;
    private readonly CompilationTrace? trace;
    private readonly Dictionary<string, ulong>? metrics;

    public LlvmLoadAndStorePromoter(IReadOnlyList<LlvmInstruction> instructions, LlvmSsaUpdater ssa,
        string name = "", CompilationTrace? trace = null, Dictionary<string, ulong>? metrics = null)
    {
        SSA = ssa; this.trace = trace?.Detail;
        this.metrics = metrics ?? (this.trace is null ? null : new Dictionary<string, ulong>());
        if (instructions.Count == 0) return;
        LlvmInstruction first = instructions[0];
        LlvmValue someValue = first is LlvmLoadInstruction ? first : ((LlvmStoreInstruction)first).ValueOperand;
        if (name.Length == 0) name = someValue.Name;
        int logicalType = first is LlvmLoadInstruction ? first.LogicalType : first.GetOperandUse(0).LogicalType;
        if (logicalType < 0) logicalType = someValue.LogicalType;
        SSA.Initialize(someValue.Type, name, logicalType);
    }
    public void Run(IReadOnlyList<LlvmInstruction> instructions)
    {
        if (trace is null) { RunCore(instructions); return; }
        trace.MeasureDetailed("optimizer.llvm-load-store-promoter", (Promoter: this, Instructions: instructions),
            static state => { state.Promoter.RunCore(state.Instructions); return 0; },
            attributes: SourceAttributes(instructions),
            metrics: _ => metrics!);
    }
    private void RunCore(IReadOnlyList<LlvmInstruction> instructions)
    {
        Dictionary<LlvmBasicBlock, List<LlvmInstruction>> usesByBlock = [];
        List<LlvmLoadInstruction> liveInLoads = [];
        Dictionary<LlvmValue, LlvmValue> replacedLoads = [];
        if (trace is null) Bucket(instructions, usesByBlock);
        else trace.Run("optimizer.llvm-load-store-promoter.bucket", (Self: this, Instructions: instructions, Uses: usesByBlock),
            static state => state.Self.Bucket(state.Instructions, state.Uses));
        if (trace is null) ProcessBlocks(instructions, usesByBlock, liveInLoads, replacedLoads);
        else trace.Run("optimizer.llvm-load-store-promoter.block-definitions",
            (Self: this, Instructions: instructions, Uses: usesByBlock, LiveIn: liveInLoads, Replaced: replacedLoads),
            static state => state.Self.ProcessBlocks(state.Instructions, state.Uses, state.LiveIn, state.Replaced));
        if (trace is null) RewriteLiveInLoads(liveInLoads, replacedLoads);
        else trace.Run("optimizer.llvm-load-store-promoter.live-in-rewrite", (Self: this, LiveIn: liveInLoads, Replaced: replacedLoads),
            static state => state.Self.RewriteLiveInLoads(state.LiveIn, state.Replaced));
        if (trace is null) DoExtraRewritesBeforeFinalDeletion();
        else trace.Run("optimizer.llvm-load-store-promoter.client-rewrites", this,
            static state => state.DoExtraRewritesBeforeFinalDeletion());
        if (trace is null) DeleteInstructions(instructions, replacedLoads);
        else trace.Run("optimizer.llvm-load-store-promoter.deletion", (Self: this, Instructions: instructions, Replaced: replacedLoads),
            static state => state.Self.DeleteInstructions(state.Instructions, state.Replaced));
    }
    private void Bucket(IReadOnlyList<LlvmInstruction> instructions, Dictionary<LlvmBasicBlock, List<LlvmInstruction>> usesByBlock)
    {
        foreach (LlvmInstruction instruction in instructions)
        {
            LlvmBasicBlock block = instruction.Parent!;
            if (!usesByBlock.TryGetValue(block, out List<LlvmInstruction>? uses))
            { uses = []; usesByBlock.Add(block, uses); Count("blocks.bucketed"); }
            uses.Add(instruction); Count("instructions.bucketed");
        }
    }
    private void ProcessBlocks(IReadOnlyList<LlvmInstruction> instructions,
        Dictionary<LlvmBasicBlock, List<LlvmInstruction>> usesByBlock,
        List<LlvmLoadInstruction> liveInLoads, Dictionary<LlvmValue, LlvmValue> replacedLoads)
    {
        // Source walks the original use-list order, not the hash buckets.
        foreach (LlvmInstruction instruction in instructions)
        {
            LlvmBasicBlock block = instruction.Parent!;
            List<LlvmInstruction> uses = usesByBlock[block];
            if (uses.Count == 0) { Count("blocks.already_processed"); continue; }
            Count("blocks.processed");
            if (uses.Count == 1)
            {
                if (instruction is LlvmStoreInstruction store)
                {
                    UpdateDebugInfo(store);
                    SSA.AddAvailableValue(block, store.ValueOperand); Count("definitions.singleton_store");
                }
                else if (instruction is LlvmAllocaInstruction alloca)
                {
                    SSA.AddAvailableValue(block, GetValueToUseForAlloca(alloca)!); Count("definitions.singleton_alloca");
                }
                else { liveInLoads.Add((LlvmLoadInstruction)instruction); Count("loads.singleton_live_in"); }
                uses.Clear();
                continue;
            }
            bool hasStore = false;
            foreach (LlvmInstruction use in uses)
                if (use is LlvmStoreInstruction or LlvmAllocaInstruction) { hasStore = true; break; }
            if (!hasStore)
            {
                foreach (LlvmInstruction use in uses) { liveInLoads.Add((LlvmLoadInstruction)use); Count("loads.all_load_block"); }
                uses.Clear();
                continue;
            }
            uses.Sort(CompareOrder);
            Count("blocks.sorted");
            LlvmValue? stored = null;
            foreach (LlvmInstruction use in uses)
            {
                if (use is LlvmLoadInstruction load)
                {
                    if (stored is null) { liveInLoads.Add(load); Count("loads.before_first_definition"); }
                    else
                    {
                        ReplaceLoadWithValue(load, stored);
                        if (ReferenceEquals(stored, load)) { stored = LlvmPoisonValue.Get(load.Type); Count("loads.self_poisoned"); }
                        load.ReplaceAllUsesWith(stored);
                        replacedLoads[load] = stored; Count("loads.local_replaced");
                    }
                    continue;
                }
                if (use is LlvmStoreInstruction store)
                {
                    UpdateDebugInfo(store); stored = store.ValueOperand; Count("definitions.local_store");
                }
                else if (use is LlvmAllocaInstruction alloca)
                { stored = GetValueToUseForAlloca(alloca); Count("definitions.local_alloca"); }
            }
            Debug.Assert(stored is not null);
            SSA.AddAvailableValue(block, stored!);
            uses.Clear();
        }
    }
    private int CompareOrder(LlvmInstruction left, LlvmInstruction right)
    {
        Count("order.comparisons");
        if (ReferenceEquals(left, right)) return 0;
        return left.ComesBefore(right, trace) ? -1 : 1;
    }
    private static Dictionary<string, string> SourceAttributes(IReadOnlyList<LlvmInstruction> instructions)
    {
        Dictionary<string, string> attributes = [];
        if (instructions.Count == 0) return attributes;
        LlvmInstruction first = instructions[0];
        attributes["function.name"] = first.Parent!.Parent!.Name;
        attributes["block.name"] = first.Parent!.Name;
        if (first.Source is { } source)
        {
            attributes["source.name"] = source.Source;
            attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return attributes;
    }
    private void Count(string name)
    {
        if (metrics is null) return;
        name = "load_store_promoter." + name;
        metrics[name] = metrics.GetValueOrDefault(name) + 1;
    }

    // These default notification hooks intentionally do nothing in upstream.
    // A client overrides only the behavior it owns; absence of a debug override
    // does not claim LLVM debug metadata was translated.
    public virtual void DoExtraRewritesBeforeFinalDeletion() { }
    public virtual void ReplaceLoadWithValue(LlvmLoadInstruction load, LlvmValue value) { }
    public virtual void InstructionDeleted(LlvmInstruction instruction) { }
    public virtual void UpdateDebugInfo(LlvmInstruction instruction) { }
    public virtual bool ShouldDelete(LlvmInstruction instruction) => true;
    public virtual LlvmValue? GetValueToUseForAlloca(LlvmInstruction alloca) => null;
}
