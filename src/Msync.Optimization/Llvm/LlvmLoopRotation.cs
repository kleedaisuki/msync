// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: Scalar/LoopRotation.cpp/.h,
// ScalarOptions.td; Utils/LoopRotationUtils.cpp processLoop and public utility.
using System.Diagnostics;
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed record LlvmLoopRotationOptions
{
    public uint MaxHeaderSize { get; init; } = 16;
    public bool EnableHeaderDuplicationAtMinSize { get; init; }
    public bool PrepareForLto { get; init; }
}

public sealed class LlvmLoopRotatePass(bool enableHeaderDuplication = true, bool prepareForLto = false, bool checkExitCount = false)
{
    public bool Run(LlvmLoop loop, LlvmLoopInfo loops, LlvmTargetTransformInfo target, LlvmDominatorTree tree,
        LlvmMemorySsaUpdater? memoryUpdater = null, LlvmLoopRotationOptions? options = null, CompilationTrace? trace = null,
        LlvmAssumptionCache? assumptions = null)
    {
        options ??= new();
        uint threshold = enableHeaderDuplication && (!loop.Header.Parent!.HasMinSize || options.EnableHeaderDuplicationAtMinSize)
            ? options.MaxHeaderSize : 0;
        return LlvmLoopRotation.Rotate(loop, loops, target, tree, memoryUpdater, rotationOnly: false,
            threshold: threshold, isUtilMode: false, prepareForLto: prepareForLto || options.PrepareForLto,
            checkExitCount: checkExitCount, trace: trace, assumptions: assumptions);
    }
}

public static partial class LlvmLoopRotation
{
    public static bool Rotate(LlvmLoop loop, LlvmLoopInfo loops, LlvmTargetTransformInfo target, LlvmDominatorTree tree,
        LlvmMemorySsaUpdater? memoryUpdater = null, bool rotationOnly = true, uint threshold = uint.MaxValue,
        bool isUtilMode = true, bool prepareForLto = false, bool checkExitCount = false, CompilationTrace? trace = null,
        LlvmAssumptionCache? assumptions = null)
    {
        trace = trace?.Detail;
        Work work = new(loop, loops, target, tree, memoryUpdater, rotationOnly, threshold, isUtilMode, prepareForLto, checkExitCount, trace, assumptions);
        if (trace is null) return work.ProcessLoop();
        Dictionary<string, string> attributes = Attributes(loop.Header);
        attributes["rotation.only"] = rotationOnly.ToString();
        attributes["header.cost.limit"] = threshold.ToString(System.Globalization.CultureInfo.InvariantCulture);
        attributes["utility.mode"] = isUtilMode.ToString();
        attributes["prepare.for.lto"] = prepareForLto.ToString();
        attributes["check.exit.count"] = checkExitCount.ToString();
        attributes["assumption.cache"] = (assumptions is not null).ToString();
        return trace.MeasureDetailed("optimizer.pass.llvm-loop-rotate", work,
            static state => state.ProcessLoop(), attributes: attributes, metrics: _ => work.Metrics!);
    }
    private sealed partial class Work(LlvmLoop loop, LlvmLoopInfo loops, LlvmTargetTransformInfo target,
        LlvmDominatorTree tree, LlvmMemorySsaUpdater? memoryUpdater, bool rotationOnly, uint maxHeaderSize,
        bool isUtilMode, bool prepareForLto, bool checkExitCount, CompilationTrace? trace, LlvmAssumptionCache? assumptions)
    {
        internal readonly Dictionary<string, ulong>? Metrics = trace is null ? null : [];
        internal bool ProcessLoop()
        {
            // Source loop metadata is absent in this representation; there is
            // no fabricated loop-ID save/restore callback.
            bool simplifiedLatch = false;
            if (!rotationOnly)
                simplifiedLatch = trace is null ? SimplifyLoopLatch() : trace.MeasureDetailed("optimizer.llvm-loop-rotate.simplify-latch", this,
                    static state => state.SimplifyLoopLatch());
            bool changed = trace is null ? RotateLoop(simplifiedLatch) :
                trace.MeasureDetailed("optimizer.llvm-loop-rotate.rotate-header", (Self: this, Simplified: simplifiedLatch),
                    static state => state.Self.RotateLoop(state.Simplified));
            Debug.Assert(!changed || loop.IsLoopExiting(loop.GetLoopLatch()!));
            Count(changed ? "loops.rotated" : "loops.not_rotated");
            return changed || simplifiedLatch;
        }
        private static void InsertMap(Dictionary<LlvmValue, LlvmValue> map, LlvmValue key, LlvmValue value)
        {
            bool inserted = map.TryAdd(key, value);
            Debug.Assert(inserted);
        }
        private void Count(string name, ulong amount = 1)
        { if (Metrics is not null) Metrics[name] = Metrics.GetValueOrDefault(name) + amount; }
    }
    private static Dictionary<string, string> Attributes(LlvmBasicBlock header)
    {
        Dictionary<string, string> attributes = new()
        { ["function.name"] = header.Parent!.Name, ["loop.header"] = header.Name };
        if (header.FirstInstruction?.Source is { } source)
        {
            attributes["source.name"] = source.Source;
            attributes["source.start"] = source.Start.ToString(System.Globalization.CultureInfo.InvariantCulture);
            attributes["source.length"] = source.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return attributes;
    }
}
