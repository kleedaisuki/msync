using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    // The canonical effect has already completed. Declining the suffix leaves
    // the original stack and the next bytecode PC untouched.
    private void RunEffectTail<TFrame>(ExecutionTargets.EffectTails tails, Module image, int functionIndex,
        IReadOnlyList<Instruction> code, ref FrameSlots locals, ref NumericLoopSites? sites,
        ref OperandStack<TFrame> stack, ref TFrame frame, ref int pc, CancellationToken cancellationToken)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        var site = tails.After(pc);
        if (site.End == 0) return;
        if (site.CaptureProbe >= 0 && !locals.TryNumber(site.CaptureProbe, !site.WritesCapture, out _))
        {
            frame.JitTailRejected();
            return;
        }
        sites ??= GetNumericLoopSites(image, functionIndex);
        if (sites.RunTail(pc + 1, site.End, ref locals, code, ref stack, ref frame, cancellationToken, out int continuation))
            pc = continuation - 1;
    }
}
