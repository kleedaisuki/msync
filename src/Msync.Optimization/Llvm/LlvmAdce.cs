// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Scalar/ADCE.cpp;
// ScalarOptions.td defaults adce-remove-control-flow=true/adce-remove-loops=false.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed record LlvmAdceOptions(bool RemoveControlFlow = true, bool RemoveLoops = false);
public readonly record struct LlvmAdceChanged(bool ChangedAnything, bool ChangedNonDebugInstruction, bool ChangedControlFlow);

public static partial class LlvmAdce
{
    private static readonly LlvmAdceOptions DefaultOptions = new();
    public static LlvmAdceChanged Run(LlvmFunction function, LlvmDominatorTree postDominators,
        LlvmDominatorTree? dominators = null, LlvmAdceOptions? options = null, CompilationTrace? trace = null)
    {
        Debug.Assert(postDominators.IsPostDominator && ReferenceEquals(function, postDominators.Function));
        Debug.Assert(dominators is null || !dominators.IsPostDominator && ReferenceEquals(function, dominators.Function));
        trace = trace?.Detail;
        var work = new Work(function, postDominators, dominators, options ?? DefaultOptions, trace);
        if (trace is null) return work.Run();
        return trace.MeasureDetailed("optimizer.pass.llvm-adce", work, static state => state.Run(),
            attributes: new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["adce.remove_control_flow"] = work.Options.RemoveControlFlow.ToString(),
                ["adce.remove_loops"] = work.Options.RemoveLoops.ToString()
            }, metrics: _ => work.Metrics!);
    }
    private struct BlockInfo
    {
        internal bool Live, HasLivePhiNodes, CfLive;
        internal uint PostOrder;
    }
    private sealed partial class Work(LlvmFunction function, LlvmDominatorTree postDominators,
        LlvmDominatorTree? dominators, LlvmAdceOptions options, CompilationTrace? trace)
    {
        internal LlvmAdceOptions Options => options;
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        private BlockInfo[] blockInfo = [];
        private readonly HashSet<LlvmInstruction> liveInstructions = [];
        private readonly List<LlvmInstruction> worklist = [];
        private readonly List<LlvmBasicBlock> deadTerminators = [];
        private readonly HashSet<LlvmBasicBlock> deadTerminatorMembership = [];
        private readonly HashSet<LlvmBasicBlock> newLiveBlocks = [];
        private ref BlockInfo Info(LlvmBasicBlock block) => ref blockInfo[block.Number];
        internal LlvmAdceChanged Run()
        {
            if (trace is null) { Initialize(); MarkLiveInstructions(); return RemoveDeadInstructions(); }
            trace.Run("optimizer.llvm-adce.initialize", this, static state => state.Initialize());
            trace.Run("optimizer.llvm-adce.mark-live", this, static state => state.MarkLiveInstructions());
            return trace.MeasureDetailed("optimizer.llvm-adce.remove-dead", this, static state => state.RemoveDeadInstructions());
        }
        private void Count(string key, ulong amount = 1)
        { if (Metrics is not null) Metrics[key] = Metrics.GetValueOrDefault(key) + amount; }
        private Dictionary<string, string> Attributes(LlvmInstruction instruction)
        {
            var attributes = new Dictionary<string, string>
            {
                ["function.name"] = function.Name,
                ["block.name"] = instruction.Parent!.Name,
                ["instruction.name"] = instruction.Name,
                ["instruction.opcode"] = instruction.Opcode.ToString()
            };
            if (instruction is LlvmCallBase { CalledFunction: { } target }) attributes["callee.name"] = target.Name;
            if (instruction.Source is { } source)
            {
                attributes["source.file"] = source.Source;
                attributes["source.offset"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
                attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return attributes;
        }
    }
}
