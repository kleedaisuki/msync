using Msync.Core;
using Msync.Core.Native;
using Msync.Runtime.Native;

internal static class IteratorLifecycleChecks
{
    internal static async Task<int> RunAsync()
    {
        NativeProgram program = new([], [new("iterators", [new(1, NativeSlotRole.Parameter), new(1, NativeSlotRole.Parameter)],
            [new(NativeOp.OpenIterator, A: 0, B: 0), new(NativeOp.OpenIterator, A: 1, B: 1), new(NativeOp.Return)],
            new([1, 1], 2, []), ParameterCount: 2, IteratorCount: 2)],
            [new(TypeKind.Number), new(TypeKind.Range, Element: 0), new(TypeKind.Null)], []);
        TrackingItems first = new(false), second = new(true);
        await using NativeInterpreter interpreter = new(program);
        try
        {
            await interpreter.InvokeAsync(NativeValue.FromFunction(new(0)),
                [NativeValue.FromRange(new(first, TypeRef.Number)), NativeValue.FromRange(new(second, TypeRef.Number))]);
            throw new Exception("Throwing iterator disposal was ignored");
        }
        catch (MsyncException error) when (error.Message == "selected iterator disposal failed") { }
        if (first.Disposals != 1 || second.Disposals != 1)
            throw new Exception($"Iterator failure skipped cleanup: first={first.Disposals}, second={second.Disposals}");
        Console.WriteLine("PASS iterator-lifetime: all cursors dispose despite selected cleanup failure");
        return 1;
    }

    private sealed class TrackingItems(bool fail) : IAsyncEnumerable<NativeValue>
    {
        internal int Disposals { get; private set; }
        public IAsyncEnumerator<NativeValue> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Cursor(this, fail);

        private sealed class Cursor(TrackingItems owner, bool fail) : IAsyncEnumerator<NativeValue>
        {
            public NativeValue Current => NativeValue.Null;
            public ValueTask<bool> MoveNextAsync() => new(false);
            public ValueTask DisposeAsync()
            {
                owner.Disposals++;
                return fail ? ValueTask.FromException(new MsyncException("selected iterator disposal failed")) : ValueTask.CompletedTask;
            }
        }
    }
}
