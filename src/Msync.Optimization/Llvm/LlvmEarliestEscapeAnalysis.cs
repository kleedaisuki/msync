// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp 227..289;
// AliasAnalysis.h EarliestEscapeAnalysis.
using System.Diagnostics;
using Msync.Core.Llvm;
namespace Msync.Optimization.Llvm;

public sealed class LlvmEarliestEscapeAnalysis(LlvmDominatorTree tree, LlvmLoopInfo? loops = null,
    LlvmCycleInfo? cycles = null, Dictionary<string, ulong>? metrics = null) : LlvmCaptureAnalysis
{
    private readonly Dictionary<LlvmValue, (LlvmInstruction? Earliest, LlvmCaptureResult Captures)> earliest = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LlvmInstruction, List<LlvmValue>> instructionObjects = new(ReferenceEqualityComparer.Instance);
    private bool? returnsTwice;

    public override LlvmCaptureComponents GetCapturesBefore(LlvmValue value, LlvmInstruction? instruction,
        bool orAt, bool returnCaptures)
    {
        Count("escape.earliest.queries");
        if (!LlvmBasicAliasAnalysis.IsIdentifiedFunctionLocal(value)) return LlvmCaptureComponents.Provenance;
        if (!earliest.TryGetValue(value, out var result))
        {
            Count("escape.earliest.cache_miss");
            result = LlvmCaptureTracking.FindEarliestCapture(value, tree.RootNode!.Block!.Parent!, tree,
                LlvmCaptureComponents.Provenance, metrics: metrics);
            earliest.Add(value, result);
            if (result.Earliest is { } capture)
            {
                if (!instructionObjects.TryGetValue(capture, out var objects)) instructionObjects.Add(capture, objects = []);
                objects.Add(value);
            }
        }
        else Count("escape.earliest.cache_hit");
        if (returnCaptures)
        {
            Debug.Assert(instruction is null);
            return result.Captures.WithRet;
        }
        if (result.Earliest is null) return LlvmCaptureComponents.None;
        if (instruction is null || CallsReturnsTwice()) return result.Captures.WithoutRet;
        if (ReferenceEquals(result.Earliest, instruction))
            return !orAt && IsNotInCycle(instruction) ? LlvmCaptureComponents.None : result.Captures.WithoutRet;
        Count("escape.earliest.reachability_queries");
        return LlvmCfgReachability.IsPotentiallyReachable(result.Earliest, instruction,
            tree: tree, loops: loops, metrics: metrics, cycles: cycles) ? result.Captures.WithoutRet : LlvmCaptureComponents.None;
    }

    public void RemoveInstruction(LlvmInstruction instruction)
    {
        if (!instructionObjects.Remove(instruction, out var objects)) return;
        foreach (var value in objects) { earliest.Remove(value); Count("escape.earliest.invalidated_objects"); }
        Count("escape.earliest.invalidated_instructions");
    }

    private bool CallsReturnsTwice()
    {
        if (returnsTwice.HasValue) return returnsTwice.Value;
        Count("escape.earliest.returns_twice_scans");
        returnsTwice = tree.RootNode!.Block!.Parent!.BasicBlocks.SelectMany(block => block.Instructions)
            .OfType<LlvmCallBase>().Any(call => call.HasReturnsTwice);
        return returnsTwice.Value;
    }

    private bool IsNotInCycle(LlvmInstruction instruction)
    {
        if (cycles is not null) return cycles.GetCycle(instruction.Parent!) is null;
        var successors = LlvmDominatorTree.Successors(instruction.Parent!).ToList();
        return successors.Count == 0 || !LlvmCfgReachability.IsPotentiallyReachableFromMany(successors,
            instruction.Parent!, tree: tree, loops: loops, metrics: metrics);
    }
    private void Count(string name)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1; }
}
