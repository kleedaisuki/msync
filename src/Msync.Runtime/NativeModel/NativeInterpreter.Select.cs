using System.Runtime.CompilerServices;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private NativeDatum SelectDatum(NativeFrame frame, NativeInstruction instruction, RuntimeProfile.NativeFrameCounters counters)
    {
        NativeDatum condition = frame.GetDatum(instruction.A);
        if (condition.IsReady)
        { counters.Selection(false); return frame.GetDatum(condition.Value.AsBool() ? instruction.B : instruction.C); }
        int type = frame.Type(instruction.Result);
        NativeDatum whenTrue = frame.GetDatum(instruction.B), whenFalse = frame.GetDatum(instruction.C);
        counters.Selection(true, (whenTrue.Future is null ? 0 : 1) + (whenFalse.Future is null ? 0 : 1));
        return Track(NativeDatum.Select(condition, whenTrue, whenFalse,
            instruction.Source, image.OptionalSourceType(type), image.Type(type)));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static NativeStep ExecuteSelect(NativeInterpreter owner, Activation activation,
        in NativeExecutionInstruction instruction, int pc, CancellationToken token)
    {
        activation.Frame.SetDatum(instruction.Result, owner.SelectDatum(activation.Frame, activation.Execution.Metadata[pc], activation.Work.Counters));
        return new(pc + instruction.Span);
    }
}
