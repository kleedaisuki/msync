// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// LLVM a55e64432a516fd6507d3ecfba7407cfe425f940: BasicAliasAnalysis.cpp
// SimpleCaptureAnalysis::getCapturesBefore; AliasAnalysis.h SimpleCaptureAnalysis.
using Msync.Core.Llvm;

namespace Msync.Optimization.Llvm;

public sealed class LlvmSimpleCaptureAnalysis : LlvmCaptureAnalysis
{
    private readonly Dictionary<LlvmValue, LlvmCaptureResult> captured = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, ulong>? metrics;
    public LlvmSimpleCaptureAnalysis(Dictionary<string, ulong>? metrics = null) => this.metrics = metrics;

    public override LlvmCaptureComponents GetCapturesBefore(LlvmValue value, LlvmInstruction? instruction,
        bool orAt, bool returnCaptures)
    {
        Count("capture.simple.queries");
        if (!LlvmBasicAliasAnalysis.IsIdentifiedFunctionLocal(value)) return LlvmCaptureComponents.Provenance;
        if (!captured.TryGetValue(value, out var result))
        {
            Count("capture.simple.cache_miss");
            result = LlvmCaptureTracking.PointerMayBeCaptured(value, LlvmCaptureComponents.Provenance,
                LlvmCaptureTracking.CapturesFullProvenance, metrics: metrics);
            captured.Add(value, result);
        }
        else Count("capture.simple.cache_hit");
        return returnCaptures ? result.WithRet : result.WithoutRet;
    }

    private void Count(string name)
    { if (metrics is not null) metrics[name] = metrics.GetValueOrDefault(name) + 1; }
}
