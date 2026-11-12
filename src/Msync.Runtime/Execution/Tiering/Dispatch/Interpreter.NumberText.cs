using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private bool TryBoundNumberText<TFrame>(Module image, int functionIndex, int pc, Instruction instruction, TFrame frame,
        ref OperandStack<TFrame> stack, CancellationToken cancellationToken) where TFrame : struct, RuntimeProfile.IFrame
    {
        if (instruction.A != 2) return false;
        Value? value = stack.PeekReference(1);
        if (value is not (StringValue or PathValue)) return false;
        var targets = ReferenceEquals(image, module) ? executionTargets : ExecutionTargets.For(image);
        if (targets.NumberText(functionIndex)?.Contains(pc) != true) return false;

        using var span = frame.BeginInlineNative(image, instruction);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Canonical Return/StoreLocal expects Value. A raw result would
            // allocate a sidecar in a small callback and then rebox at exit.
            var result = NativeRegistry.CastNumberText(value);
            stack.Discard(2);
            stack.Add(result);
            return true;
        }
        catch (Exception error)
        {
            span?.Fail(error);
            if (error is not OperationCanceledException) ReportNativeFailure("cast", error);
            throw;
        }
    }
}
