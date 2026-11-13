using Msync.Core.Native;

namespace Msync.Runtime.Native;

public sealed partial class NativeInterpreter
{
    private DirectoryScope EnterDeferredDirectory(NativeRuntimeContext origin)
    {
        var previous = deferredContext.Value;
        deferredContext.Value = origin;
        return new(this, previous);
    }

    private readonly struct DirectoryScope(NativeInterpreter owner, NativeRuntimeContext? previous) : IDisposable
    {
        public void Dispose() => owner.deferredContext.Value = previous;
    }

    // Iterators must restore the consumer's context before yielding, not only at disposal.
    private IAsyncEnumerable<NativeDatum> CaptureDatums(IAsyncEnumerable<NativeDatum> items) =>
        new ContextDatums(this, items, context.CaptureDirectory());

    private sealed class ContextDatums(NativeInterpreter owner, IAsyncEnumerable<NativeDatum> items,
        NativeRuntimeContext origin) : IAsyncEnumerable<NativeDatum>
    {
        public IAsyncEnumerator<NativeDatum> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new ContextIterator(owner, items.GetAsyncEnumerator(cancellationToken), origin);
    }

    private sealed class ContextIterator(NativeInterpreter owner, IAsyncEnumerator<NativeDatum> inner,
        NativeRuntimeContext origin) : IAsyncEnumerator<NativeDatum>
    {
        private readonly NativeRuntimeContext execution = origin.ForkDirectory();
        public NativeDatum Current => inner.Current;

        public ValueTask<bool> MoveNextAsync()
        {
            NativeRuntimeContext? previous = owner.deferredContext.Value;
            bool switchDir = !ReferenceEquals(previous, execution);
            if (switchDir) owner.deferredContext.Value = execution;
            ValueTask<bool> next;
            try { next = inner.MoveNextAsync(); }
            catch { if (switchDir) owner.deferredContext.Value = previous; throw; }
            if (next.IsCompleted)
            {
                if (switchDir) owner.deferredContext.Value = previous;
                return next;
            }
            return FinishMoveNextAsync(next, previous, switchDir);
        }

        private async ValueTask<bool> FinishMoveNextAsync(ValueTask<bool> next, NativeRuntimeContext? previous, bool switchDir)
        {
            try { return await next; }
            finally { if (switchDir) owner.deferredContext.Value = previous; }
        }

        public ValueTask DisposeAsync()
        {
            NativeRuntimeContext? previous = owner.deferredContext.Value;
            bool switchDir = !ReferenceEquals(previous, execution);
            if (switchDir) owner.deferredContext.Value = execution;
            ValueTask dispose;
            try { dispose = inner.DisposeAsync(); }
            catch { if (switchDir) owner.deferredContext.Value = previous; throw; }
            if (dispose.IsCompleted)
            {
                if (switchDir) owner.deferredContext.Value = previous;
                return dispose;
            }
            return FinishDisposeAsync(dispose, previous, switchDir);
        }

        private async ValueTask FinishDisposeAsync(ValueTask dispose, NativeRuntimeContext? previous, bool switchDir)
        {
            try { await dispose; }
            finally { if (switchDir) owner.deferredContext.Value = previous; }
        }
    }
}
