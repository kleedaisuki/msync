using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private const int DirectSourceWarmup = 64;
    private DirectSourceSite?[]? directSources;

    private bool TryDirectSourceCall<TFrame>(Module image, int target, FunctionValue function,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken, out double result)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        result = default;
        if (!executionTargets.IsDirectSourceCandidate(target)) return false;
        var body = image.Functions[target];
        if ((function.Captures?.Count ?? 0) != body.CaptureCount) return false;

        var sites = directSources ??= new DirectSourceSite?[image.Functions.Count];
        var site = sites[target] ??= new();
        if (site.Visits < DirectSourceWarmup && ++site.Visits < DirectSourceWarmup) return false;
        if (site.Code(image, target) is not { } worker) return false;
        if (body.CodeSignature?.RequiredStatics is { Count: > 0 } required)
            staticScopes.RequireReady(required);

        // BorrowArguments normalizes at most one Cell layer; a nested Cell
        // must retain the additional LoadLocal dereference of the real frame.
        if (!stack.TryPeekNumber(0, out double argument))
        {
            if (stack.PeekReference(0) is not CellValue { Data: NumberValue passed }) return false;
            argument = passed.Data;
        }

        double captured = 0;
        CellValue? cell = null;
        if (body.CaptureCount != 0)
        {
            var value = function.Captures![0];
            cell = value as CellValue;
            if ((cell?.Data ?? value) is not NumberValue number) return false;
            captured = number.Data;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (profile is not { } recorder)
        {
            RuntimeProfile.DisabledFrame frame = default;
            return worker.TryRunSource(body, captured, argument, cell, ref frame, cancellationToken, out result);
        }
        var observed = recorder.DirectSourceFrame(image, target);
        if (!worker.TryRunSource(body, captured, argument, cell, ref observed, cancellationToken, out result)) return false;
        observed.DirectSourceCompleted();
        return true;
    }

    private sealed class DirectSourceSite
    {
        internal int Visits;
        private bool imported;
        private NumericLoop? worker;

        internal NumericLoop? Code(Module image, int target)
        {
            if (imported) return worker;
            imported = true;
            if (ExecutionRegionImporter.TryImport(image, target, out var region, out _))
                worker = NumericLoop.ImportSource(image, region);
            return worker;
        }
    }
}
