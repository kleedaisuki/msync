using System.Diagnostics;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;
using static Msync.Optimization.Execution.ReadonlyNumberFold.Operation;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private const int NumberFoldWarmRows = 512;
    private const int NumberFoldChunk = 256;
    private ConditionalWeakTable<LiteralScopeItems, OrderedFoldRows>? orderedFoldRows;
    private Dictionary<ReadonlyNumberFold, NumberFoldSite>? numberFoldSites;

    private bool TryReadonlyNumberFold<TFrame, TLayout>(Module image, int function, int pc, Instruction[] code,
        ref FrameSlots slots, ref OperandStack<TFrame> stack, ref TFrame frame, CancellationToken cancellationToken,
        out int next)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        next = 0;
        if (!TryFoldInput(image, stack.PeekReference(0), cancellationToken,
            out var producer, out var demand)) return false;
        var plan = executionTargets.NumberFold(function, pc);
        if (!slots.TryNumber(plan.Accumulator, readCell: false, out double initial)) return false;
        foreach (int slot in plan.StoreSlots)
            if (slots.Peek<TLayout>(slot) is CellValue) return false;

        int inputCount = plan.InputSlots.Length;
        Span<double> inputs = inputCount <= 64 ? stackalloc double[inputCount] : new double[inputCount];
        for (int i = 0; i < inputs.Length; i++)
            if (!slots.TryNumber(plan.InputSlots[i], readCell: false, out inputs[i])) return false;
        var cache = (orderedFoldRows ??= new()).GetValue(producer, static _ => new OrderedFoldRows());
        if (cache.Project(plan, producer, BoundScopePlaces.For(image)) is not { } rows) return false;

        if (producer.Count != 0) demand.ThrowIfCancellationRequested();
        var page = SelectNumberFold(plan, producer.Count, ref frame);
        int processed = 0, chunks = 0;
        double result;
        if (typeof(TFrame) == typeof(RuntimeProfile.DisabledFrame))
            result = ExecuteNumberFold(plan, rows, producer.Count, inputs, initial, page, demand,
                ref frame, ref processed, ref chunks);
        else
        {
            try
            {
                result = ExecuteNumberFold(plan, rows, producer.Count, inputs, initial, page, demand,
                    ref frame, ref processed, ref chunks);
            }
            finally
            {
                frame.NumberFold(code.AsSpan(pc + 3, plan.BodyLength), processed, chunks,
                    exhausted: processed == producer.Count);
                if (profile is { } recorder)
                    for (int i = 0; i < processed; i++) recorder.FoldedLiteralRow(image, producer.FunctionId(i));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        stack.Discard();
        if (BitConverter.DoubleToInt64Bits(initial) != BitConverter.DoubleToInt64Bits(result))
            slots.CommitNumber(plan.Accumulator, result, ref frame);
        next = plan.Exit;
        return true;
    }

    private NativeCodePage? SelectNumberFold<TFrame>(ReadonlyNumberFold plan, int rows, ref TFrame frame)
        where TFrame : struct, RuntimeProfile.IFrame
    {
        if (rows == 0 || !NumericLoop.NativeEnabled) return null;
        var sites = numberFoldSites ??= new(ReferenceEqualityComparer.Instance);
        if (!sites.TryGetValue(plan, out var site)) sites.Add(plan, site = new());
        if (site.Attempted) return site.Page;
        site.Remaining -= rows;
        if (site.Remaining > 0) return null;

        site.Attempted = true;
        bool observed = typeof(TFrame) != typeof(RuntimeProfile.DisabledFrame);
        long start = observed ? Stopwatch.GetTimestamp() : 0;
        site.Page = NativeScopeRecurrence.Compile(plan, out int bytes);
        if (observed && site.Page is not null)
            frame.JitCompiled(Stopwatch.GetElapsedTime(start).Ticks * 100, bytes);
        return site.Page;
    }

    private static unsafe double ExecuteNumberFold<TFrame>(ReadonlyNumberFold plan, double[] rows, int count,
        ReadOnlySpan<double> inputs, double accumulator, NativeCodePage? page, CancellationToken demand,
        ref TFrame frame, ref int processed, ref int chunks) where TFrame : struct, RuntimeProfile.IFrame
    {
        int stride = plan.Fields.Length;
        if (page is not null)
        {
            fixed (double* fields = rows)
            fixed (double* invariants = inputs)
            {
                while (processed < count)
                {
                    demand.ThrowIfCancellationRequested();
                    int size = Math.Min(NumberFoldChunk, count - processed);
                    bool observed = typeof(TFrame) != typeof(RuntimeProfile.DisabledFrame);
                    long start = observed ? Stopwatch.GetTimestamp() : 0;
                    accumulator = page.RunRows(fields + processed * stride, size, invariants, accumulator);
                    processed += size;
                    chunks++;
                    if (observed) frame.JitExecuted(Stopwatch.GetElapsedTime(start).Ticks * 100, 0, tail: false);
                }
            }
            return accumulator;
        }

        var steps = plan.Steps.AsSpan();
        Span<double> values = steps.Length <= 128 ? stackalloc double[steps.Length] : new double[steps.Length];
        for (; processed < count; processed++)
        {
            demand.ThrowIfCancellationRequested();
            var fields = rows.AsSpan(processed * stride, stride);
            for (int index = 0; index < steps.Length; index++)
            {
                ref readonly var step = ref steps[index];
                values[index] = step.Op switch
                {
                    Accumulator => accumulator,
                    Field => fields[step.Left],
                    Input => inputs[step.Left],
                    Constant => step.Constant,
                    Add => values[step.Left] + values[step.Right],
                    Subtract => values[step.Left] - values[step.Right],
                    Multiply => values[step.Left] * values[step.Right],
                    Divide => values[step.Left] / values[step.Right],
                    Negate => -values[step.Left],
                    _ => throw new InvalidOperationException("Unsupported ordered Number operation.")
                };
            }
            accumulator = values[plan.Result];
        }
        return accumulator;
    }

    private sealed class NumberFoldSite
    {
        internal int Remaining = NumberFoldWarmRows;
        internal bool Attempted;
        internal NativeCodePage? Page;
    }

    private sealed class OrderedFoldRows
    {
        private readonly Dictionary<ReadonlyNumberFold, double[]?> projections = new(ReferenceEqualityComparer.Instance);

        internal double[]? Project(ReadonlyNumberFold plan, LiteralScopeItems producer, BoundScopePlaces places)
        {
            if (!projections.TryGetValue(plan, out var rows))
                projections.Add(plan, rows = Build(plan, producer, places));
            return rows;
        }

        private static double[]? Build(ReadonlyNumberFold plan, LiteralScopeItems producer, BoundScopePlaces places)
        {
            int stride = plan.Fields.Length;
            long length = (long)producer.Count * stride;
            if (length > Array.MaxLength) return null;
            var rows = new double[(int)length];
            Span<int> ordinals = stride <= 64 ? stackalloc int[stride] : new int[stride];
            ScopeLayout? layout = null;
            for (int row = 0; row < producer.Count; row++)
            {
                var template = producer.Template(row);
                if (!ReferenceEquals(layout, template.Layout))
                {
                    layout = template.Layout;
                    if (!ResolveFoldFields(places, layout, plan.Admissions.AsSpan(), plan.Fields.AsSpan(), ordinals))
                        return null;
                }
                for (int field = 0; field < stride; field++)
                    if (!template.TryGetNumber(ordinals[field], out rows[row * stride + field])) return null;
            }
            return rows;
        }
    }
}
