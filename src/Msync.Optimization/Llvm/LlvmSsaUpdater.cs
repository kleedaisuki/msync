// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940:
// Transforms/Utils/SSAUpdater.h and lib/Transforms/Utils/SSAUpdater.cpp.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed partial class LlvmSsaUpdater
{
    private readonly Dictionary<LlvmBasicBlock, LlvmValue?> available = [];
    private readonly List<LlvmPhiInstruction>? insertedPhis;
    private readonly CompilationTrace? trace;
    private readonly Dictionary<string, ulong>? metrics;
    private LlvmType prototypeType = null!;
    private string prototypeName = "";
    private int logicalType = -1;
    public uint PhiSearchLimit { get; set; } = 80;

    public LlvmSsaUpdater(List<LlvmPhiInstruction>? insertedPhis = null, CompilationTrace? trace = null,
        Dictionary<string, ulong>? metrics = null)
        => (this.insertedPhis, this.trace, this.metrics) = (insertedPhis, trace?.Detail, metrics);
    public void Initialize(LlvmType type, string name) => Initialize(type, name, -1);
    public void Initialize(LlvmType type, string name, int logicalType)
    {
        available.Clear(); prototypeType = type; prototypeName = name; this.logicalType = logicalType;
    }
    public bool HasValueForBlock(LlvmBasicBlock block) => available.ContainsKey(block);
    public LlvmValue? FindValueForBlock(LlvmBasicBlock block) => available.GetValueOrDefault(block);
    public void AddAvailableValue(LlvmBasicBlock block, LlvmValue value)
    {
        Debug.Assert(prototypeType is not null && ReferenceEquals(value.Type, prototypeType));
        available[block] = value;
    }
    public LlvmValue GetValueAtEndOfBlock(LlvmBasicBlock block)
    {
        if (trace is null) return GetValueAtEndInternal(block);
        return trace.MeasureDetailed("optimizer.llvm-ssa-updater.value-at-end", (Updater: this, Block: block),
            static state => state.Updater.GetValueAtEndInternal(state.Block), attributes: Attributes(block));
    }
    private LlvmValue GetValueAtEndInternal(LlvmBasicBlock block)
    {
        if (available.TryGetValue(block, out LlvmValue? value) && value is not null)
        { Record("available.cache_hit", 1); return value; }
        available.TryAdd(block, null);
        Record("available.cache_miss", 1);
        return new Solver(this).GetValue(block);
    }
    public LlvmValue GetValueInMiddleOfBlock(LlvmBasicBlock block)
    {
        if (trace is null) return GetValueInMiddleInternal(block);
        return trace.MeasureDetailed("optimizer.llvm-ssa-updater.value-in-middle", (Updater: this, Block: block),
            static state => state.Updater.GetValueInMiddleInternal(state.Block), attributes: Attributes(block));
    }
    private LlvmValue GetValueInMiddleInternal(LlvmBasicBlock block)
    {
        if (!HasValueForBlock(block)) return GetValueAtEndOfBlock(block);
        var predecessorValues = new List<(LlvmBasicBlock Block, LlvmValue Value)>();
        LlvmValue? singular = null;
        foreach (LlvmBasicBlock predecessor in Predecessors(block))
        {
            LlvmValue value = GetValueAtEndOfBlock(predecessor);
            if (predecessorValues.Count == 0) singular = value;
            else if (!ReferenceEquals(value, singular)) singular = null;
            predecessorValues.Add((predecessor, value));
        }
        if (predecessorValues.Count == 0) { Record("middle.no_predecessors", 1); return LlvmPoisonValue.Get(prototypeType); }
        if (singular is not null) { Record("middle.singular", 1); return singular; }
        if (block.FirstInstruction is LlvmPhiInstruction)
        {
            var mapping = new Dictionary<LlvmBasicBlock, LlvmValue>();
            foreach (var predecessor in predecessorValues) mapping[predecessor.Block] = predecessor.Value;
            for (LlvmInstruction? instruction = block.FirstInstruction; instruction is LlvmPhiInstruction phi; instruction = instruction.Next)
                if (IsEquivalentPhi(phi, mapping)) { Record("middle.reused_phi", 1); return phi; }
        }
        LlvmPhiInstruction inserted = CreateEmptyPhi(block, predecessorValues.Count);
        foreach (var predecessor in predecessorValues) inserted.AddIncoming(predecessor.Value, predecessor.Block);
        LlvmValue? simplified = LlvmInstructionSimplify.SimplifyInstruction(inserted);
        if (simplified is not null)
        { inserted.EraseFromParent(); Record("middle.simplified_phi", 1); return simplified; }
        for (LlvmInstruction? instruction = block.FirstInstruction; instruction is not null; instruction = instruction.Next)
            if (instruction is not LlvmPhiInstruction) { inserted.Source = instruction.Source; break; }
        insertedPhis?.Add(inserted);
        Record("middle.inserted_phi", 1);
        return inserted;
    }
    private static bool IsEquivalentPhi(LlvmPhiInstruction phi, Dictionary<LlvmBasicBlock, LlvmValue> mapping)
    {
        if (phi.IncomingCount != mapping.Count) return false;
        for (int index = 0; index < phi.IncomingCount; ++index)
            if (!ReferenceEquals(mapping.GetValueOrDefault(phi.GetIncomingBlock(index)), phi.GetIncomingValue(index))) return false;
        return true;
    }
    public void RewriteUse(LlvmUse use) => RewriteUseCore(use, afterInsertions: false);
    public void RewriteUseAfterInsertions(LlvmUse use) => RewriteUseCore(use, afterInsertions: true);
    private void RewriteUseCore(LlvmUse use, bool afterInsertions)
    {
        if (trace is null) { RewriteUseInternal(use, afterInsertions); return; }
        LlvmInstruction user = (LlvmInstruction)use.User;
        trace.Run("optimizer.llvm-ssa-updater.rewrite-use", (Updater: this, Use: use, After: afterInsertions),
            static state => state.Updater.RewriteUseInternal(state.Use, state.After), Attributes(user.Parent!));
    }
    private void RewriteUseInternal(LlvmUse use, bool afterInsertions)
    {
        LlvmInstruction user = (LlvmInstruction)use.User;
        LlvmValue value = user is LlvmPhiInstruction phi
            ? GetValueAtEndOfBlock(phi.GetIncomingBlock(use.OperandNo))
            : afterInsertions ? GetValueAtEndOfBlock(user.Parent!) : GetValueInMiddleOfBlock(user.Parent!);
        use.Set(value);
        Record("uses.rewritten", 1);
    }
    private LlvmPhiInstruction CreateEmptyPhi(LlvmBasicBlock block, int predecessorCount)
    {
        LlvmPhiInstruction phi = new(prototypeType, predecessorCount) { LogicalType = logicalType };
        phi.SetName(prototypeName);
        block.InsertBefore(phi, block.FirstInstruction);
        return phi;
    }
    private void Record(string name, ulong value = 1)
    {
        if (metrics is null && trace is null) return;
        name = "ssa_updater." + name;
        if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + value;
        trace?.Metric(name, (long)value);
    }
    private Dictionary<string, string> Attributes(LlvmBasicBlock block) => new()
    {
        ["function.name"] = block.Parent!.Name, ["block.name"] = block.Name,
        ["ssa.name"] = prototypeName, ["block.number"] = block.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };
    private static IEnumerable<LlvmBasicBlock> Predecessors(LlvmBasicBlock block)
    {
        if (block.FirstInstruction is LlvmPhiInstruction phi)
        {
            for (int index = 0; index < phi.IncomingCount; ++index) yield return phi.GetIncomingBlock(index);
            yield break;
        }
        foreach (LlvmBasicBlock predecessor in LlvmDominatorTree.Predecessors(block)) yield return predecessor;
    }
}
