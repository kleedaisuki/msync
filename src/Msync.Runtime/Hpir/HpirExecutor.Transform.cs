using System.Runtime.InteropServices;
using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Runtime.Hpir;

internal static unsafe partial class HpirExecutor
{
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct TransformPlan(HpirNumericRange Seed, nint Environment);

    private static void CallTransform(HpirProgram program, ref HpirHeap? heap, HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> args, byte* frame, int destination, ExecutionOptions? options, CancellationToken token)
    {
        nint source = Read<nint>(frame, args[0]);
        switch (import.Service)
        {
            case HpirService.RangeTransformOpen:
                *(HpirNumericCursor*)Read<nint>(frame, args[1]) = new(((TransformPlan*)source)->Seed);
                return;
            case HpirService.RangeBound:
                ReadRangeBound(program, heap, work, active, diagnostics, counts, pc, import, args, frame, destination, options, token);
                return;
            case HpirService.RangePackWords:
                PackRangeWords(program, ref heap, active, diagnostics, counts, pc, import, args, frame, destination);
                return;
            case HpirService.RangeBufferGrow:
                GrowRangeBuffer(program, ref heap, active, diagnostics, counts, pc, import, args, frame, destination, source);
                return;
        }

        int function = active->Function;
        var text = new HpirTextDemandContext(program, heap!, active, pc, diagnostics, counts);
        TransformPlan plan;
        int layout;
        if (import.Service == HpirService.RangeTransform)
        {
            layout = import.Binding;
            HpirNumericRange seed = ReadPlan(source, Read<int>(frame, args[2]), work, text, diagnostics, options?.Timeline,
                function, pc, token);
            plan = new(seed, Read<nint>(frame, args[1]));
        }
        else
        {
            (layout, HpirFutureInput mode) = HpirRangeImports.DecodeWindow(import.Binding);
            plan = *(TransformPlan*)source;
            int index = 1;
            var count = FutureInput<double>(frame, args, mode, ref index);
            var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(function, pc), function, pc);
            double length = count.Pending == 0 ? count.Value : HpirFuture.Demand<double>(count.Pending, work, site, token, text);
            plan = plan with { Seed = plan.Seed.Window(HpirService.RangeTake, length, site.Source) };
        }
        nint address = Allocate(program, ref heap, active, pc, layout, 1, diagnostics, counts, clear: false);
        *(TransformPlan*)address = plan;
        Write(frame, destination, address);
    }

    private static void ReadRangeBound(HpirProgram program, HpirHeap? heap, HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> args, byte* frame, int destination, ExecutionOptions? options, CancellationToken token)
    {
        var (operation, mode) = HpirTransformImports.DecodeBound(import.Binding);
        int index = 0;
        var input = FutureInput<double>(frame, args, mode, ref index);
        double value = RangeCount(program, heap, work, active, diagnostics, counts, pc, options, token, input);
        string name = HpirTransformImports.BoundName(operation);
        SourceSpan? source = diagnostics.Source(active->Function, pc);
        if (operation == HpirRangeBound.EnumerateStart)
            Write(frame, destination, HpirNumericRange.Finite(value, name, source));
        else Write(frame, destination, HpirNumericRange.Count(value, name, source));
    }

    private static double RangeCount(HpirProgram program, HpirHeap? heap, HpirWork? work, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, ExecutionOptions? options,
        CancellationToken token, HpirScalarInput<double> count)
    {
        if (count.Pending == 0) return count.Value;
        var site = new HpirWorkSite(options?.Timeline, diagnostics.Source(active->Function, pc), active->Function, pc);
        return HpirFuture.Demand<double>(count.Pending, work, site, token, new(program, heap!, active, pc, diagnostics, counts));
    }

    private static void GrowRangeBuffer(HpirProgram program, ref HpirHeap? heap, HpirStack.Frame* active,
        HpirDiagnostics diagnostics, RuntimeProfile.HpirFunctionCensus? counts, int pc, HpirImport import,
        IReadOnlyList<int> args, byte* frame, int destination, nint source)
    {
        ulong length = Read<ulong>(frame, args[1]), capacity = Read<ulong>(frame, args[2]);
        if (capacity > (ulong)int.MaxValue)
            throw new MsyncException("ranges.materialize exceeded the addressable Range extent", diagnostics.Source(active->Function, pc));
        nint address = Allocate(program, ref heap, active, pc, import.Binding, (int)capacity, diagnostics, counts,
            clear: program.Layouts[import.Binding].BorrowOffsets.Count != 0);
        if (length != 0)
            Buffer.MemoryCopy((void*)source, (void*)address, capacity * sizeof(ulong), length * sizeof(ulong));
        counts?.RangeBufferGrow(length, capacity);
        Write(frame, destination, address);
    }
}
