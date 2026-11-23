using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

[StructLayout(LayoutKind.Sequential, Size = HpirRangeImports.PlanBytes)]
internal readonly record struct HpirNumericRange(double First, double Limit, double Step, ulong Skip, ulong Fence)
{
    internal static HpirNumericRange Ready(double first, double limit, double step) =>
        new(first, limit, step, 0, ulong.MaxValue);

    internal static HpirNumericRange Sequence(double first, double limit, double step, SourceSpan? source)
    {
        if (!(double.IsFinite(first) && double.IsFinite(limit) && double.IsFinite(step)))
            throw new MsyncException("ranges.sequence requires finite Number arguments", source);
        if (step == 0) throw new MsyncException("ranges.sequence requires a nonzero step", source);
        return Ready(first, limit, step);
    }

    internal HpirNumericRange Window(HpirService service, double value, SourceSpan? source)
    {
        bool take = service == HpirService.RangeTake;
        ulong count = Count(value, take ? "take" : "drop", source);
        if (take && count == 0) return this with { Skip = 0, Fence = 0 };
        ulong end = Skip + Math.Min(count, Fence - Skip);
        return take ? this with { Fence = end } : this with { Skip = end };
    }

    internal static double Finite(double value, string operation, SourceSpan? source) =>
        double.IsFinite(value) ? value : throw new MsyncException($"ranges.{operation} requires finite Number arguments", source);

    internal static ulong Count(double value, string operation, SourceSpan? source)
    {
        ulong ordinal = HpirOrdinal.FromNumber(Finite(value, operation, source));
        return ordinal != HpirOrdinal.Invalid ? ordinal :
            throw new MsyncException($"ranges.{operation} requires a nonnegative integral count representable by the platform iterator", source);
    }
}

[StructLayout(LayoutKind.Sequential, Size = HpirRangeImports.DeferredPlanBytes)]
internal struct HpirDeferredNumericRange(
    HpirScalarInput<double> first, HpirScalarInput<double> limit, HpirScalarInput<double> step,
    int function, int instruction)
{
    internal nint FirstPending = first.Pending;
    internal nint LimitPending = limit.Pending;
    internal nint StepPending = step.Pending;
    internal double First = first.Value;
    internal double Limit = limit.Value;
    internal double Step = step.Value;
    internal readonly int Function = function;
    internal readonly int Instruction = instruction;

    internal readonly int Pending => (FirstPending != 0 ? 1 : 0) + (LimitPending != 0 ? 1 : 0) + (StepPending != 0 ? 1 : 0);

    internal void Resolve(HpirNumericRange ready)
    {
        (First, Limit, Step) = (ready.First, ready.Limit, ready.Step);
        FirstPending = LimitPending = StepPending = 0;
    }
}

[StructLayout(LayoutKind.Explicit, Size = HpirRangeImports.CursorBytes)]
internal struct HpirNumericCursor
{
    [FieldOffset(HpirRangeImports.Current)] internal double Current;
    [FieldOffset(HpirRangeImports.Limit)] internal double Limit;
    [FieldOffset(HpirRangeImports.Step)] internal double Step;
    [FieldOffset(HpirRangeImports.Skip)] internal ulong Skip;
    [FieldOffset(HpirRangeImports.Fence)] internal ulong Fence;
    [FieldOffset(HpirRangeImports.Position)] internal ulong Position;
    [FieldOffset(HpirRangeImports.Stalled)] internal byte Stalled;
    [FieldOffset(HpirRangeImports.Descending)] internal byte Descending;

    internal HpirNumericCursor(HpirNumericRange plan)
    {
        (Current, Limit, Step, Skip, Fence) = plan;
        Position = 0;
        Stalled = 0;
        Descending = plan.Step < 0 ? (byte)1 : (byte)0;
    }
}
