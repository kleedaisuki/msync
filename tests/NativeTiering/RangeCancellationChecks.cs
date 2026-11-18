using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class RangeCancellationChecks
{
    internal static void Run()
    {
        using NativeGuestHeap heap = new(rawEdges: true);
        NativeGuestObjectTable arena = heap.References!;
        using CancellationTokenSource producer = new();
        using CancellationTokenSource consumer = new();
        nint source = heap.NewRangePlan(new()
        {
            Op = NativeRangeOp.Sequence, Depth = 1,
            Operands = new() { First = 0, Limit = 4, Step = 1 }
        }, producer.Token);
        nint selected = Window(heap, source, 2, consumer.Token);
        NativeRangeCursor cursor = NativeRangeCursor.Open(selected, arena, consumer.Token);
        try
        {
            if (!cursor.Next(arena, consumer.Token, out double value) || value != 0)
                throw new InvalidOperationException("The first demanded item did not reach the consumer.");
            producer.Cancel();
            RequireCancellation(ref cursor, arena, consumer.Token, producer.Token);
        }
        finally { cursor.Close(); }

        nint empty = Window(heap, source, 0, consumer.Token);
        cursor = NativeRangeCursor.Open(empty, arena, consumer.Token);
        try
        {
            if (cursor.Next(arena, consumer.Token, out _))
                throw new InvalidOperationException("An empty demand window pulled its cancelled producer.");
        }
        finally { cursor.Close(); }

        nint common = Window(heap, source, 2, producer.Token);
        cursor = NativeRangeCursor.Open(common, arena, producer.Token);
        try { RequireCancellation(ref cursor, arena, producer.Token, producer.Token); }
        finally { cursor.Close(); }
    }

    private static nint Window(NativeGuestHeap heap, nint source, ulong fence, CancellationToken token) =>
        heap.NewRangePlan(new()
        {
            Op = NativeRangeOp.Window, Left = source, Depth = 1,
            Operands = new() { Window = new(0, 1, fence) }
        }, token);

    private static void RequireCancellation(ref NativeRangeCursor cursor, NativeGuestObjectTable arena,
        CancellationToken consumer, CancellationToken expected)
    {
        try { _ = cursor.Next(arena, consumer, out _); }
        catch (OperationCanceledException error) when (error.CancellationToken == expected) { return; }
        throw new InvalidOperationException("The demanded range did not observe the cancelling producer.");
    }
}
