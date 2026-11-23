using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHirLowerer
{
    private sealed partial class Lowering
    {
        private HashSet<LlvmFunction>? completionVectors;

        private void SelectCompletionVectors(ReadinessFlow flow)
        {
            if (flow.ScalarSnapshots is not { } candidates) return;
            Dictionary<LlvmFunction, LlvmFunction>? targets = null;
            int selected = 0;
            foreach (var call in candidates)
            {
                LlvmFunction original = call.CalledFunction!;
                LlvmType type = call.GetArgument(0).Type;
                if (call.Arguments.Any(argument => argument.Type != type) || !call.Arguments.Any(flow.IsDeferred)) continue;
                targets ??= [];
                if (!targets.TryGetValue(original, out var target))
                {
                    target = LlvmFunction.Create(original.FunctionType, LlvmLinkage.ExternalLinkage,
                        "msync.snapshot.completion", module);
                    constructors.Add(target, NativeKind.Range);
                    targets.Add(original, target);
                    (completionVectors ??= []).Add(target);
                }
                call.CalledOperand = target;
                ++selected;
            }
            trace?.Detail?.Metric("flow.snapshot_completion_sites", selected);
        }
    }
}
