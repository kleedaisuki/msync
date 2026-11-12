using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Optimization;
using Msync.Optimization.Execution;

namespace Msync.Runtime;

public sealed partial class Interpreter
{
    private ConditionalWeakTable<LiteralScopeItems, ScopeFoldRows>? scopeFoldRows;

    private bool TryReadonlyScopeFold<TFrame, TLayout>(Module image, int function, int pc,
        ref FrameSlots slots, ref OperandStack<TFrame> stack, ref TFrame frame, CancellationToken cancellationToken,
        out int next)
        where TFrame : struct, RuntimeProfile.IFrame where TLayout : struct
    {
        next = 0;
        if (!TryFoldInput(image, stack.PeekReference(0), cancellationToken,
            out var producer, out var demand)) return false;
        var plan = executionTargets.ScopeFold(function, pc);
        if (slots.Peek<TLayout>(plan.Parity) is not BoolValue initial
            || !slots.TryNumber(plan.X, readCell: false, out double x)
            || !slots.TryNumber(plan.Y, readCell: false, out double y)) return false;
        foreach (int slot in plan.StoreSlots)
            if (slots.Peek<TLayout>(slot) is CellValue) return false;
        var cache = (scopeFoldRows ??= new()).GetValue(producer, static _ => new ScopeFoldRows());
        if (cache.Project(plan, producer, BoundScopePlaces.For(image)) is not { } rows) return false;

        bool parity = initial.Data;
        foreach (ref readonly var row in rows.AsSpan())
        {
            demand.ThrowIfCancellationRequested();
            if ((row.Ay > y) == (row.By > y)) continue;
            double dy = row.By - row.Ay;
            double lhs = (x - row.Ax) * dy;
            double rhs = (row.Bx - row.Ax) * (y - row.Ay);
            if (dy > 0 ? lhs < rhs : lhs > rhs) parity = !parity;
        }

        cancellationToken.ThrowIfCancellationRequested();
        stack.Discard();
        if (parity != initial.Data) slots.Initialize(plan.Parity, parity ? Value.True : Value.False, ref frame);
        next = plan.Exit;
        return true;
    }

    // Both bound fold plans require a private iterator slot. A borrowed frame
    // has no slots beyond the captures and arguments.
    private bool TryFoldInput(Module image, Value? input, CancellationToken cancellationToken,
        [NotNullWhen(true)] out LiteralScopeItems? producer, out CancellationToken demand)
    {
        producer = sessionImage is null && input is RangeValue range ? ReadonlyLiteralItems(image, range) : null;
        demand = producer?.ProducerToken ?? default;
        if (!demand.CanBeCanceled) demand = cancellationToken;
        // The canonical iterator owns the linked token when both differ.
        return producer is not null && (!cancellationToken.CanBeCanceled || demand == cancellationToken);
    }

    private static bool ResolveFoldFields(BoundScopePlaces places, ScopeLayout layout, ReadOnlySpan<int> admissions,
        ReadOnlySpan<int> projections, Span<int> ordinals)
    {
        foreach (int type in admissions)
            if (!places.IsAdmitted(layout, type)) return false;
        for (int i = 0; i < projections.Length; i++)
            if (!places.TryField(layout, projections[i], out ordinals[i])) return false;
        return true;
    }

    private readonly record struct ScopeFoldRow(double Ax, double Ay, double Bx, double By);

    private sealed class ScopeFoldRows
    {
        private readonly Dictionary<ReadonlyScopeFold, ScopeFoldRow[]?> projections = new(ReferenceEqualityComparer.Instance);

        internal ScopeFoldRow[]? Project(ReadonlyScopeFold plan, LiteralScopeItems producer, BoundScopePlaces places)
        {
            if (!projections.TryGetValue(plan, out var rows))
                projections.Add(plan, rows = Build(plan, producer, places));
            return rows;
        }

        private static ScopeFoldRow[]? Build(ReadonlyScopeFold plan, LiteralScopeItems producer, BoundScopePlaces places)
        {
            var rows = new ScopeFoldRow[producer.Count];
            ReadOnlySpan<int> projections = [plan.Ax, plan.Ay, plan.Bx, plan.By];
            Span<int> ordinals = stackalloc int[4];
            ScopeLayout? layout = null;
            for (int i = 0; i < rows.Length; i++)
            {
                var row = producer.Template(i);
                if (!ReferenceEquals(layout, row.Layout))
                {
                    layout = row.Layout;
                    if (!ResolveFoldFields(places, layout, plan.Admissions.AsSpan(), projections, ordinals))
                        return null;
                }
                if (!row.TryGetNumber(ordinals[0], out double ax) || !row.TryGetNumber(ordinals[1], out double ay)
                    || !row.TryGetNumber(ordinals[2], out double bx) || !row.TryGetNumber(ordinals[3], out double by)) return null;
                rows[i] = new(ax, ay, bx, by);
            }
            return rows;
        }
    }
}
